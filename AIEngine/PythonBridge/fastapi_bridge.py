"""
Arbiter AI — FastAPI Python Bridge
Handles chat requests, LLM inference, TTS voice output, model management, STT, and build/run/test.
"""

# ── Dependency bootstrap ──────────────────────────────────────────────────────
# Must run before any third-party imports so the server can self-heal when the
# user hasn't yet installed the requirements.
import subprocess as _subprocess
import sys as _sys
import os as _os

def _ensure_dependencies() -> None:
    """Install requirements.txt dependencies if they are not already present."""
    try:
        import fastapi  # noqa: F401 — presence check only
        return  # already installed
    except ImportError:
        pass

    _req = _os.path.join(_os.path.dirname(__file__), "requirements.txt")
    if not _os.path.exists(_req):
        print(
            f"[Arbiter] requirements.txt not found at: {_req}\n"
            "Cannot auto-install dependencies. "
            "Run: pip install fastapi uvicorn pydantic",
            flush=True,
        )
        _sys.exit(1)
    print(
        "[Arbiter] Required Python packages are missing. "
        "Installing from requirements.txt — please wait...",
        flush=True,
    )
    result = _subprocess.run(
        [_sys.executable, "-m", "pip", "install", "-r", _req],
        capture_output=False,  # stream pip output so the C# console tab shows progress
    )
    if result.returncode != 0:
        print(
            "[Arbiter] pip install failed (see output above). "
            "Run manually:  pip install -r AIEngine/PythonBridge/requirements.txt",
            flush=True,
        )
        _sys.exit(1)
    print("[Arbiter] Dependencies installed successfully.", flush=True)

_ensure_dependencies()
# ─────────────────────────────────────────────────────────────────────────────

import re
import subprocess
import tempfile
import os
from contextlib import asynccontextmanager
from pathlib import Path


def _load_env_file() -> None:
    """
    Load variables from the repository-root ``.env`` file into the process
    environment *without* overriding values that were already set externally.

    This lets ``setup_arbiter.py`` write ``ARBITER_MODEL_PATH`` once and have
    it picked up automatically on every subsequent server start.
    """
    env_path = Path(__file__).resolve().parent.parent.parent / ".env"
    if not env_path.exists():
        return
    try:
        with open(env_path, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, _, value = line.partition("=")
                key = key.strip()
                # Strip optional surrounding quotes from the value
                value = value.strip().strip('"').strip("'")
                if key and key not in os.environ:
                    os.environ[key] = value
    except OSError:
        pass


# Load .env before importing llm_interface so ARBITER_MODEL_PATH / OLLAMA_HOST
# are in the environment when those modules evaluate their module-level constants.
_load_env_file()

from fastapi import FastAPI, HTTPException, UploadFile, File
from fastapi.responses import HTMLResponse
from pydantic import BaseModel
import sqlite3
from llm_interface import generate_response, get_model_status, preload_model
from VoiceManager import speak
from persona_manager import (
    get_active_persona,
    set_active_persona,
    get_system_prompt,
    list_personas,
)
from model_downloader import (
    detect_vram_gb,
    recommend_model,
    list_downloaded_models,
    start_background_download,
    get_download_status,
    DEFAULT_MODEL_DIR,
)

# Resolve paths relative to this script's location
SCRIPT_DIR = Path(__file__).parent
MEMORY_ROOT = SCRIPT_DIR.parent.parent / "Memory" / "ConversationLogs"
PROJECTS_ROOT = SCRIPT_DIR.parent.parent / "Projects"

# Valid project name pattern: alphanumeric, spaces, dashes, underscores,
# and parentheses — covers names such as "Arbiter (Self)".
# Dots and path-separators are intentionally excluded to prevent traversal.
_PROJECT_NAME_RE = re.compile(r"^[\w ()\-]+$")

# Build/run/test constants
_BUILD_ERROR_MAX_CHARS = 1000
_RUN_TIMEOUT_SECONDS = 60


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Pre-load the LLM in a background thread so the first chat request is fast."""
    import threading
    threading.Thread(target=preload_model, daemon=True, name="llm-preload").start()
    yield


app = FastAPI(title="Arbiter AI Bridge", version="0.1.0", lifespan=lifespan)


class UserMessage(BaseModel):
    message: str
    project: str
    use_voice: bool = False
    voice: str = "British_Female"


class PersonaRequest(BaseModel):
    persona: str


class StatusResponse(BaseModel):
    status: str
    model: str
    gpu: str
    vram_gb: float
    max_tokens: int


class DownloadRequest(BaseModel):
    repo_id: str = ""
    filename: str = ""
    auto: bool = True


class BuildRequest(BaseModel):
    project: str
    command: str = ""   # if empty, auto-detected from project files


def _validate_project_name(project_name: str) -> None:
    """Raise HTTPException if project_name contains path traversal or invalid chars."""
    if not _PROJECT_NAME_RE.match(project_name):
        raise HTTPException(status_code=400, detail="Invalid project name.")


def _resolve_project_dir(project_name: str) -> Path:
    """Return the project directory path, raising 404 if it does not exist."""
    _validate_project_name(project_name)
    project_dir = PROJECTS_ROOT / project_name
    if not project_dir.exists():
        raise HTTPException(status_code=404, detail=f"Project '{project_name}' not found.")
    return project_dir


def _auto_detect_command(project_dir: Path, action: str) -> str:
    """
    Infer a build / run / test command from the project directory contents.
    Returns an empty string when the project type cannot be determined.
    """
    # .NET / C#
    if list(project_dir.glob("*.csproj")) or list(project_dir.glob("*.sln")):
        return {"build": "dotnet build", "run": "dotnet run", "test": "dotnet test"}.get(action, "")
    # Node.js
    if (project_dir / "package.json").exists():
        return {"build": "npm run build", "run": "npm start", "test": "npm test"}.get(action, "")
    # Python
    if any((project_dir / f).exists() for f in ("pyproject.toml", "setup.py", "requirements.txt")):
        build_cmd = (
            "pip install -r requirements.txt"
            if (project_dir / "requirements.txt").exists()
            else "pip install -e ."
        )
        return {"build": build_cmd, "run": "python main.py", "test": "pytest"}.get(action, "")
    # Makefile
    if (project_dir / "Makefile").exists():
        return {"build": "make", "run": "make run", "test": "make test"}.get(action, "")
    return ""


def _run_command(command: str, cwd: Path, timeout: int = 120) -> dict:
    """Run a shell command in *cwd* and return stdout, stderr, exit_code."""
    try:
        proc = subprocess.run(
            command,
            shell=True,
            cwd=str(cwd),
            capture_output=True,
            text=True,
            timeout=timeout,
        )
        return {
            "stdout": proc.stdout,
            "stderr": proc.stderr,
            "exit_code": proc.returncode,
            "success": proc.returncode == 0,
        }
    except subprocess.TimeoutExpired:
        return {
            "stdout": "",
            "stderr": f"Command timed out after {timeout}s.",
            "exit_code": -1,
            "success": False,
        }


def get_db(project_name: str) -> sqlite3.Connection:
    _validate_project_name(project_name)
    db_path = MEMORY_ROOT / project_name
    db_path.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(db_path / "session.db")
    conn.execute(
        """CREATE TABLE IF NOT EXISTS conversation (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            role      TEXT,
            message   TEXT,
            timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
        )"""
    )
    conn.commit()
    return conn


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/", response_class=HTMLResponse)
def web_ui():
    """Serve the Arbiter AI ChatGPT-style web interface."""
    html_path = SCRIPT_DIR / "static" / "index.html"
    if html_path.exists():
        return HTMLResponse(content=html_path.read_text(encoding="utf-8"))
    return HTMLResponse(
        content="<h1>Arbiter AI</h1><p>Web UI not found — ensure static/index.html exists.</p>",
        status_code=200,
    )


@app.get("/projects")
def list_projects():
    """Return a sorted list of all project directories."""
    if not PROJECTS_ROOT.exists():
        return {"projects": []}
    return {
        "projects": [d.name for d in sorted(PROJECTS_ROOT.iterdir()) if d.is_dir()]
    }


@app.get("/status", response_model=StatusResponse)
def status():
    try:
        import torch
        gpu = torch.cuda.get_device_name(0) if torch.cuda.is_available() else "CPU"
        vram = (
            torch.cuda.get_device_properties(0).total_memory / (1024 ** 3)
            if torch.cuda.is_available()
            else 0.0
        )
    except ImportError:
        gpu = "CPU (torch not installed)"
        vram = 0.0

    # Adaptive model selection based on available VRAM
    if vram >= 20:
        model, max_tokens = "13B-fp16", 2048
    elif vram >= 12:
        model, max_tokens = "13B-8bit", 1024
    elif vram >= 6:
        model, max_tokens = "7B-8bit", 512
    elif vram >= 4:
        model, max_tokens = "7B-4bit", 256
    else:
        model, max_tokens = "CPU-fallback", 128

    return StatusResponse(
        status="ok",
        model=model,
        gpu=gpu,
        vram_gb=round(vram, 2),
        max_tokens=max_tokens,
    )


@app.get("/llm/status")
def llm_status():
    """
    Return the current LLM backend status.

    Possible ``backend`` values:
    - ``"gguf"``       — a local GGUF model is loaded via llama-cpp-python
    - ``"ollama"``     — a local Ollama instance is providing inference
    - ``"stub"``       — no model configured; responses are placeholders
    - ``"not_loaded"`` — model load not yet attempted (server just started)
    """
    return get_model_status()


# ── Persona endpoints ─────────────────────────────────────────────────────────

@app.get("/personas")
def get_personas():
    """Return the list of all available personas."""
    return {"personas": list_personas()}


@app.get("/persona/{project_name}")
def get_project_persona(project_name: str):
    """Return the active persona for *project_name*."""
    conn = get_db(project_name)
    persona = get_active_persona(conn)
    conn.close()
    return {"persona": persona}


@app.post("/persona/{project_name}")
def set_project_persona(project_name: str, req: PersonaRequest):
    """Set the active persona for *project_name*.

    The persona name must be one of the built-in personas returned by
    ``GET /personas``.  Returns ``{"persona": "<new_name>"}`` on success.
    """
    conn = get_db(project_name)
    try:
        set_active_persona(conn, req.persona)
    except ValueError as exc:
        conn.close()
        raise HTTPException(status_code=400, detail=str(exc))
    conn.close()
    return {"persona": req.persona}


# ─────────────────────────────────────────────────────────────────────────────

@app.post("/chat")
def chat(msg: UserMessage):
    conn = get_db(msg.project)
    c = conn.cursor()
    c.execute(
        "INSERT INTO conversation (role, message) VALUES (?, ?)",
        ("User", msg.message),
    )
    conn.commit()

    persona = get_active_persona(conn)
    system_prompt = get_system_prompt(persona, msg.project)
    response = generate_response(msg.message, msg.project, system_prompt=system_prompt)

    c.execute(
        "INSERT INTO conversation (role, message) VALUES (?, ?)",
        ("Arbiter", response),
    )
    conn.commit()
    conn.close()

    if msg.use_voice:
        speak(response, msg.voice)

    return {"response": response, "persona": persona}


@app.get("/history/{project_name}")
def history(project_name: str):
    conn = get_db(project_name)
    rows = conn.execute(
        "SELECT role, message, timestamp FROM conversation ORDER BY id"
    ).fetchall()
    conn.close()
    return [{"role": r, "message": m, "timestamp": t} for r, m, t in rows]


@app.get("/models")
def list_models():
    """Return recommended model for detected hardware and list of already-downloaded models."""
    vram = detect_vram_gb()
    return {
        "vram_gb": round(vram, 2),
        "recommended": recommend_model(vram),
        "downloaded": list_downloaded_models(DEFAULT_MODEL_DIR),
    }


@app.post("/models/download")
def download_model_endpoint(req: DownloadRequest):
    """
    Start an async model download in the background.

    - If ``auto`` is ``true`` (default) the best model for the current hardware
      is selected automatically.
    - Otherwise supply ``repo_id`` and ``filename`` explicitly.

    Poll ``GET /models/download/status`` for progress.
    """
    started = start_background_download(
        repo_id=req.repo_id,
        filename=req.filename,
        auto=req.auto,
        destination_dir=DEFAULT_MODEL_DIR,
    )
    if not started:
        return {"status": "already_running", "detail": get_download_status()}
    return {"status": "started"}


@app.get("/models/download/status")
def download_status_endpoint():
    """Return the current model download progress and status."""
    return get_download_status()


@app.post("/build")
def build_project(req: BuildRequest):
    """
    Run the build command for a project.

    Auto-detects the command from the project's files (.csproj → ``dotnet build``,
    ``package.json`` → ``npm run build``, etc.) unless ``command`` is provided.
    On failure, the LLM is asked to suggest a fix — included as ``"suggestion"``.
    """
    project_dir = _resolve_project_dir(req.project)
    command = req.command or _auto_detect_command(project_dir, "build")
    if not command:
        raise HTTPException(
            status_code=400,
            detail="Cannot auto-detect build command. Provide one explicitly via the 'command' field.",
        )
    result = _run_command(command, project_dir)
    if not result["success"]:
        error_text = (result["stderr"] or result["stdout"])[:_BUILD_ERROR_MAX_CHARS]
        result["suggestion"] = generate_response(
            f"The build failed with this error:\n{error_text}\nSuggest a concise fix.",
            req.project,
        )
    return result


@app.post("/run")
def run_project(req: BuildRequest):
    """
    Run the project's main entry point.

    Auto-detects the command unless ``command`` is provided.
    The process is killed after 60 seconds to prevent indefinite blocking.
    """
    project_dir = _resolve_project_dir(req.project)
    command = req.command or _auto_detect_command(project_dir, "run")
    if not command:
        raise HTTPException(
            status_code=400,
            detail="Cannot auto-detect run command. Provide one explicitly via the 'command' field.",
        )
    return _run_command(command, project_dir, timeout=_RUN_TIMEOUT_SECONDS)


@app.post("/test")
def test_project(req: BuildRequest):
    """
    Run the project's test suite.

    Auto-detects the command unless ``command`` is provided.
    On failure, the LLM is asked to suggest a fix — included as ``"suggestion"``.
    """
    project_dir = _resolve_project_dir(req.project)
    command = req.command or _auto_detect_command(project_dir, "test")
    if not command:
        raise HTTPException(
            status_code=400,
            detail="Cannot auto-detect test command. Provide one explicitly via the 'command' field.",
        )
    result = _run_command(command, project_dir)
    if not result["success"]:
        error_text = (result["stderr"] or result["stdout"])[:_BUILD_ERROR_MAX_CHARS]
        result["suggestion"] = generate_response(
            f"The test run failed with this output:\n{error_text}\nSuggest a concise fix.",
            req.project,
        )
    return result


_whisper_model = None


def _get_whisper_model():
    """Load and cache the Whisper base model."""
    global _whisper_model
    if _whisper_model is None:
        import whisper  # type: ignore
        _whisper_model = whisper.load_model("base")
    return _whisper_model


# The /stt route requires python-multipart (for UploadFile / File).
# FastAPI 0.115+ validates this dependency at route-registration time, so if the
# package is missing the entire server would crash on startup.  We wrap the
# registration in a try/except so the server still starts and returns a clear
# 503 error when /stt is called without the optional dependency installed.
try:
    @app.post("/stt")
    async def speech_to_text(file: UploadFile = File(...)):
        """
        Transcribe uploaded audio to text using OpenAI Whisper.

        Accepts any audio format supported by Whisper (wav, mp3, m4a, ogg, etc.).
        Returns ``{"text": "<transcription>"}`` on success.
        Requires ``openai-whisper`` to be installed (see requirements.txt).
        """
        try:
            import whisper  # type: ignore  # noqa: F401
        except ImportError:
            raise HTTPException(
                status_code=501,
                detail=(
                    "openai-whisper is not installed. "
                    "Install it with: pip install openai-whisper"
                ),
            )

        suffix = Path(file.filename or "audio.wav").suffix or ".wav"
        audio_bytes = await file.read()
        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
            tmp.write(audio_bytes)
            tmp_path = tmp.name

        try:
            model = _get_whisper_model()
            result = model.transcribe(tmp_path)
            text: str = result.get("text", "").strip()
        finally:
            os.unlink(tmp_path)

        return {"text": text}

except RuntimeError as _stt_reg_err:
    # python-multipart (or another required package) is missing.
    # Register a stub that explains what to install instead of crashing.
    _stt_detail = (
        f"Speech-to-text unavailable: {_stt_reg_err}. "
        "Run: pip install python-multipart"
    )
    print(f"[Bridge] /stt route registration failed — {_stt_detail}")

    @app.post("/stt")
    async def speech_to_text():  # type: ignore[misc]
        raise HTTPException(status_code=503, detail=_stt_detail)


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8000)

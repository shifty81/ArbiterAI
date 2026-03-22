"""
Arbiter AI — FastAPI Python Bridge
Handles chat requests, LLM inference, TTS voice output, model management, STT, and build/run/test.
"""

import re
import subprocess
import tempfile
import os
from pathlib import Path
from fastapi import FastAPI, HTTPException, UploadFile, File
from pydantic import BaseModel
import sqlite3
from llm_interface import generate_response
from VoiceManager import speak
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

# Valid project name pattern: alphanumeric, dash, underscore only
_PROJECT_NAME_RE = re.compile(r"^[\w\-]+$")

# Build/run/test constants
_BUILD_ERROR_MAX_CHARS = 1000
_RUN_TIMEOUT_SECONDS = 60

app = FastAPI(title="Arbiter AI Bridge", version="0.1.0")


class UserMessage(BaseModel):
    message: str
    project: str
    use_voice: bool = False
    voice: str = "British_Female"


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


@app.post("/chat")
def chat(msg: UserMessage):
    conn = get_db(msg.project)
    c = conn.cursor()
    c.execute(
        "INSERT INTO conversation (role, message) VALUES (?, ?)",
        ("User", msg.message),
    )
    conn.commit()

    response = generate_response(msg.message, msg.project)

    c.execute(
        "INSERT INTO conversation (role, message) VALUES (?, ?)",
        ("Arbiter", response),
    )
    conn.commit()
    conn.close()

    if msg.use_voice:
        speak(response, msg.voice)

    return {"response": response}


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

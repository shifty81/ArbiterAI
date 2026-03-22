"""
Arbiter AI — FastAPI Python Bridge
Handles chat requests, LLM inference, TTS voice output, and model management.
"""

import re
from pathlib import Path
from fastapi import FastAPI, HTTPException
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

# Resolve memory path relative to this script's location
SCRIPT_DIR = Path(__file__).parent
MEMORY_ROOT = SCRIPT_DIR.parent.parent / "Memory" / "ConversationLogs"

# Valid project name pattern: alphanumeric, dash, underscore only
_PROJECT_NAME_RE = re.compile(r"^[\w\-]+$")

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


def _validate_project_name(project_name: str) -> None:
    """Raise HTTPException if project_name contains path traversal or invalid chars."""
    if not _PROJECT_NAME_RE.match(project_name):
        raise HTTPException(status_code=400, detail="Invalid project name.")


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


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8000)

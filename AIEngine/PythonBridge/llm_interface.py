"""
Arbiter AI — LLM Interface
Handles hardware-aware model loading and response generation.
Supports local LLaMA-style models via llama-cpp-python or transformers.
"""

import os

_model = None
_tokenizer = None

# VRAM threshold (GB) for enabling GPU acceleration in llama-cpp-python
MIN_VRAM_FOR_GPU_GB = 6.0


def _detect_vram_gb() -> float:
    try:
        import torch
        if torch.cuda.is_available():
            return torch.cuda.get_device_properties(0).total_memory / (1024 ** 3)
    except ImportError:
        pass
    return 0.0


def _find_model_in_default_dir() -> str:
    """
    Scan the default model directory for .gguf files.
    Prefers the file that matches the hardware-recommended model name;
    falls back to the first .gguf found.
    Returns an empty string if none exist.
    """
    try:
        from model_downloader import DEFAULT_MODEL_DIR, recommend_model, detect_vram_gb
    except ImportError:
        return ""

    if not DEFAULT_MODEL_DIR.exists():
        return ""

    gguf_files = sorted(DEFAULT_MODEL_DIR.glob("*.gguf"))
    if not gguf_files:
        return ""

    # Prefer the hardware-recommended model if it is present
    vram = detect_vram_gb()
    preferred = recommend_model(vram)["filename"]
    preferred_path = DEFAULT_MODEL_DIR / preferred
    if preferred_path.exists():
        return str(preferred_path)

    return str(gguf_files[0])


def _load_model():
    global _model, _tokenizer
    if _model is not None:
        return

    # 1. Explicit override via environment variable
    model_path = os.environ.get("ARBITER_MODEL_PATH", "")

    # 2. Auto-discover any .gguf in the standard model folder
    if not model_path or not os.path.exists(model_path):
        model_path = _find_model_in_default_dir()

    vram = _detect_vram_gb()

    # Try llama-cpp-python first (most efficient for local LLMs)
    if model_path and os.path.exists(model_path):
        try:
            from llama_cpp import Llama
            n_gpu_layers = -1 if vram >= MIN_VRAM_FOR_GPU_GB else 0
            _model = Llama(model_path=model_path, n_gpu_layers=n_gpu_layers, n_ctx=2048)
            print(f"[LLM] Loaded GGUF model from {model_path} (GPU layers: {n_gpu_layers})")
            return
        except ImportError:
            pass

    # Fallback: stub responder (no model installed)
    print("[LLM] No model configured — using stub responder. "
          "Run setup_arbiter.py or POST /models/download to download a model automatically.")
    _model = "stub"


def generate_response(message: str, project: str, max_tokens: int = 512) -> str:
    """Generate a response from Arbiter's LLM."""
    _load_model()

    if _model == "stub" or _model is None:
        return (
            f"[Stub] Arbiter received: '{message}' for project '{project}'. "
            "Configure ARBITER_MODEL_PATH to enable real LLM inference."
        )

    # llama-cpp-python inference
    try:
        system_prompt = (
            "You are Arbiter, a personal autonomous AI development assistant. "
            "You are precise, technical, and explain your reasoning like a teacher. "
            f"You are currently working on the project: {project}."
        )
        prompt = f"<s>[INST] <<SYS>>\n{system_prompt}\n<</SYS>>\n\n{message} [/INST]"
        output = _model(prompt, max_tokens=max_tokens, stop=["</s>", "[INST]"])
        return output["choices"][0]["text"].strip()
    except Exception as e:
        return f"[LLM Error] {str(e)}"

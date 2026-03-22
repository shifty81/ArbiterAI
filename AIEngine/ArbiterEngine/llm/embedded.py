"""Embedded (in-process) LLM backend using llama-cpp-python.

Runs GGUF models directly inside the Python process — no external server
(Ollama, LM Studio, llama.cpp server, etc.) required.

Install:
    pip install llama-cpp-python

For GPU/CUDA acceleration:
    CMAKE_ARGS="-DLLAMA_CUDA=on" pip install llama-cpp-python --force-reinstall

For Apple Metal:
    CMAKE_ARGS="-DLLAMA_METAL=on" pip install llama-cpp-python --force-reinstall

Then set in configs/config.toml:
    [agent]
    default_llm_backend = "embedded"

    [llm.embedded]
    model_path  = "path/to/model.gguf"
    n_ctx       = 4096
    n_gpu_layers = -1   # -1 = all layers on GPU, 0 = CPU only
"""
from __future__ import annotations

import json
from typing import Any, Iterator

from core.logger import get_logger
from llm.base import BaseLLM

logger = get_logger(__name__)


class EmbeddedLLM(BaseLLM):
    """In-process GGUF model inference via llama-cpp-python.

    The first call to any inference method triggers a one-time model load.
    Subsequent calls reuse the loaded model (singleton per instance).
    """

    def __init__(
        self,
        model_path: str,
        n_ctx: int = 4096,
        n_gpu_layers: int = -1,
        n_threads: int = 0,
        verbose: bool = False,
    ) -> None:
        self.model_path = model_path
        self._llm: Any = None
        self._load_kwargs: dict[str, Any] = {
            "model_path": model_path,
            "n_ctx": n_ctx,
            "n_gpu_layers": n_gpu_layers,
            "verbose": verbose,
            "chat_format": "chatml",
        }
        if n_threads > 0:
            self._load_kwargs["n_threads"] = n_threads

    # ── Internal helpers ──────────────────────────────────────────────────────

    def _get_llm(self) -> Any:
        """Return the loaded Llama instance, loading it on first call."""
        if self._llm is not None:
            return self._llm

        try:
            from llama_cpp import Llama  # type: ignore[import]
        except ImportError as exc:
            raise RuntimeError(
                "llama-cpp-python is not installed.\n"
                "Run:  pip install llama-cpp-python\n"
                "GPU:  CMAKE_ARGS=\"-DLLAMA_CUDA=on\" pip install llama-cpp-python"
            ) from exc

        if not self.model_path:
            raise RuntimeError(
                "No model_path configured for the embedded backend.\n"
                "Set llm.embedded.model_path in configs/config.toml to the full\n"
                "path of a GGUF model file (e.g. models/llama-3-8b.Q4_K_M.gguf)."
            )

        logger.info("EmbeddedLLM: loading model from %s", self.model_path)
        self._llm = Llama(**self._load_kwargs)
        logger.info("EmbeddedLLM: model loaded successfully.")
        return self._llm

    @staticmethod
    def _unavailable_msg(exc: Exception) -> str:
        return (
            f"⚠️ **Embedded LLM is not ready**: {exc}\n\n"
            "To enable in-process inference without Ollama or any external server:\n\n"
            "1. Install llama-cpp-python:\n"
            "   ```\n"
            "   pip install llama-cpp-python\n"
            "   ```\n"
            "2. Download a GGUF model (e.g. from https://huggingface.co/TheBloke).\n"
            "3. Set the path in `configs/config.toml`:\n"
            "   ```toml\n"
            "   [agent]\n"
            "   default_llm_backend = \"embedded\"\n\n"
            "   [llm.embedded]\n"
            "   model_path = \"/path/to/model.gguf\"\n"
            "   ```\n"
        )

    # ── BaseLLM interface ─────────────────────────────────────────────────────

    def chat(self, messages: list[dict[str, str]]) -> str:
        try:
            llm = self._get_llm()
            result = llm.create_chat_completion(
                messages=messages,
                max_tokens=2048,
                temperature=0.7,
                stream=False,
            )
            return result["choices"][0]["message"]["content"]
        except RuntimeError as exc:
            logger.error("EmbeddedLLM setup error: %s", exc)
            return self._unavailable_msg(exc)
        except Exception as exc:
            logger.error("EmbeddedLLM chat error: %s", exc)
            return f"[ERROR] {exc}"

    def stream_chat(self, messages: list[dict[str, str]]) -> Iterator[str]:
        try:
            llm = self._get_llm()
            stream = llm.create_chat_completion(
                messages=messages,
                max_tokens=2048,
                temperature=0.7,
                stream=True,
            )
            for chunk in stream:
                delta = chunk["choices"][0].get("delta", {})
                content = delta.get("content", "")
                if content:
                    yield content
        except RuntimeError as exc:
            logger.error("EmbeddedLLM setup error: %s", exc)
            yield self._unavailable_msg(exc)
        except Exception as exc:
            logger.error("EmbeddedLLM stream_chat error: %s", exc)
            yield f"[ERROR] {exc}"

    def generate(self, messages: list[dict[str, str]]) -> str:
        return self.chat(messages)

    def tool_call(
        self, messages: list[dict[str, str]], tools: list[dict[str, Any]]
    ) -> list[dict[str, Any]]:
        """Instruct the model to emit a JSON tool-call array."""
        tool_lines: list[str] = []
        for t in tools:
            props = t.get("arguments", {}).get("properties", {})
            required = set(t.get("arguments", {}).get("required", []))
            params = ", ".join(
                f"{k}: {v.get('type', 'any')}{'*' if k in required else ''}"
                for k, v in props.items()
            )
            tool_lines.append(f"- {t['name']}({params}): {t.get('description', '')}")

        system_content = (
            "You are a tool-calling assistant. "
            "Respond ONLY with a valid JSON array of tool calls — no explanation, no markdown.\n"
            "Format: [{\"name\": \"tool_name\", \"arguments\": {\"arg\": \"value\"}}]\n"
            "If no tool is needed respond with exactly: []\n\n"
            "Available tools (* = required arg):\n" + "\n".join(tool_lines)
        )

        non_system = [m for m in messages if m.get("role") != "system"]
        augmented = [{"role": "system", "content": system_content}] + non_system

        response = self.chat(augmented)
        try:
            start = response.find("[")
            end = response.rfind("]") + 1
            if start >= 0 and end > start:
                parsed = json.loads(response[start:end])
                if isinstance(parsed, list):
                    return parsed
        except Exception as exc:
            logger.debug("EmbeddedLLM tool-call parse failed: %s", exc)
        return []

    def list_models(self) -> list[str]:
        """Return the configured model path as a single-element list."""
        return [self.model_path] if self.model_path else []

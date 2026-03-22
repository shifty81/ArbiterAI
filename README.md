# ArbiterAI

**Arbiter** is a personal autonomous AI development assistant — a local-first, modular agent that can plan projects, write and edit code, run builds, manage files, and communicate through both chat and voice.

---

## Vision

Arbiter is not just a chatbot — it is a controllable, self-iterating AI agent that works alongside you across the full software development lifecycle:

```
Idea → Planning → Code Generation → Build → Run → Test → Report → Fix → Repeat → Working EXE
```

---

## Architecture

```
ArbiterAI/
├── Arbiter.sln                        # Visual Studio solution
│
├── HostApp/                           # C# WPF Windows application
│   ├── MainWindow.xaml(.cs)           # Launch screen
│   ├── WorkspaceWindow.xaml(.cs)      # Project list + drag-and-drop
│   ├── ProjectWindow.xaml(.cs)        # Chat, suggestions, file tree, Git, TTS
│   ├── VoiceInterface/
│   │   ├── VoiceManager.cs            # C# TTS bridge
│   │   └── VoiceManager.py            # Python TTS helper
│   ├── GitInterface/
│   │   └── GitManager.cs              # LibGit2Sharp integration
│   └── Config/
│       └── settings.json              # App configuration
│
├── AIEngine/
│   ├── LLaMA2-13B/                    # Local LLM model folder (GGUF files go here)
│   └── PythonBridge/
│       ├── fastapi_bridge.py          # FastAPI server — chat, TTS, history
│       ├── llm_interface.py           # Hardware-aware LLM loading + inference
│       ├── VoiceManager.py            # TTS (pyttsx3 / Coqui TTS)
│       └── requirements.txt           # Python dependencies
│
├── Memory/
│   └── ConversationLogs/              # Per-project SQLite chat logs
│
├── Projects/                          # User project workspaces
│   └── ExampleProject/
│       └── roadmap.json               # Phase and task tracking
│
├── Temp/                              # Temporary build/run files
│
└── archive/
    └── webhook-integration/           # Previous Jira/Azure Boards/Linear bridge (archived)
```

---

## Key Features

| Feature | Status |
|---|---|
| Chat UI (ChatGPT-style) | ✅ Phase 0 |
| Voice output (TTS) | ✅ Phase 0 |
| Voice input (STT) | ✅ Phase 0 — Windows System.Speech |
| Project & workspace management | ✅ Phase 0 |
| Drag-and-drop file workflow | ✅ Phase 0 |
| Git integration (commit, branch, push, pull, log) | ✅ Phase 0 |
| Hardware-aware LLM loading | ✅ Phase 0 |
| Code generation & approval | ✅ Phase 0 |
| Roadmap / task planning | ✅ Phase 0 |
| Automated model download | ✅ Phase 0 |
| Local LLM inference (GGUF) | ✅ Auto-discovered from model folder |
| Build + run + test loop | ✅ Phase 1 |
| **Persona system** (Arbiter / Coder / Teacher / Organizer) | ✅ **Phase 1** |
| Google Drive workspace | 📋 Phase 2 |
| Visual Studio VSIX extension | 📋 Phase 3 |
| Knowledge archive (PDF, docs) | 📋 Phase 3 |
| Image & audio generation | 📋 Phase 4 |
| Multi-agent system | 📋 Phase 5 |

---

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** — for building the WPF host app
- **Visual Studio 2022** (Community or higher) with WPF workload
- **Python 3.11+** — for the AI engine bridge
- *(Optional)* A GGUF language model — for real LLM inference

---

## Quick Start

### 1. Clone the repo

```bash
git clone https://github.com/shifty81/ArbiterAI.git
cd ArbiterAI
```

### 2. One-click automated setup *(recommended)*

```bash
python setup_arbiter.py
```

This single command will:
- Install all Python dependencies
- Detect your GPU / available VRAM
- Download the best-fit GGUF model automatically to `AIEngine/LLaMA2-13B/`
- Write the model path to a local `.env` file

### 3. Start the Python bridge

```bash
cd AIEngine/PythonBridge
python fastapi_bridge.py
```

The bridge starts at `http://127.0.0.1:8000`.  
If a model was downloaded in step 2 it will be loaded automatically — no environment variable needed.

### 4. Build and run the Windows app

Open `Arbiter.sln` in Visual Studio 2022, restore NuGet packages, and press **F5**.

---

### Manual model setup *(alternative)*

If you prefer to download a model yourself:

1. Download a GGUF file from [HuggingFace TheBloke](https://huggingface.co/TheBloke)
2. Place it in `AIEngine/LLaMA2-13B/`
3. *(Optional)* Set the environment variable in your `.env` file (repo root) to override auto-detection:
   ```
   ARBITER_MODEL_PATH=C:\path\to\your-model.gguf
   ```
   The `.env` file is loaded automatically on server start — no need to set a system environment variable.

See `AIEngine/LLaMA2-13B/README.md` for the full model compatibility table.

---

### Ollama *(easiest local LLM setup)*

[Ollama](https://ollama.com) provides a one-command local LLM installation that Arbiter
detects automatically — no GGUF download or environment variable required.

```bash
# 1. Install Ollama from https://ollama.com
# 2. Pull a model (Mistral is a good starting point)
ollama pull mistral
# 3. Start Arbiter — it will detect Ollama automatically
python AIEngine/PythonBridge/fastapi_bridge.py
```

To point Arbiter at a non-default Ollama host, set `OLLAMA_HOST` in your `.env` file:
```
OLLAMA_HOST=http://192.168.1.10:11434
```

---

### llama-cpp-python *(for GGUF models)*

For GGUF model inference, install `llama-cpp-python` (after `setup_arbiter.py` downloads the
model):

```bash
# CPU-only (always works):
pip install llama-cpp-python

# GPU-accelerated (CUDA — replace cu121 with your CUDA version):
CMAKE_ARGS="-DGGML_CUDA=on" pip install llama-cpp-python
# or on Windows:
pip install llama-cpp-python --extra-index-url https://abetlen.github.io/llama-cpp-python/whl/cu121
```

---

## Python Bridge API

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Health check |
| `/status` | GET | GPU, VRAM, model, token limit info |
| `/llm/status` | GET | Active LLM backend: `gguf`, `ollama`, or `stub` |
| `/personas` | GET | List all available personas |
| `/persona/{project}` | GET | Get the active persona for a project |
| `/persona/{project}` | POST | Set the active persona for a project |
| `/chat` | POST | Send a message, get Arbiter's response + TTS |
| `/history/{project}` | GET | Retrieve conversation history for a project |
| `/models` | GET | List recommended + already-downloaded models |
| `/models/download` | POST | Start async model download (auto or explicit) |
| `/models/download/status` | GET | Poll current download progress |
| `/build` | POST | Build the project (auto-detects command) |
| `/run` | POST | Run the project entry point |
| `/test` | POST | Run the project's test suite |
| `/stt` | POST | Transcribe audio to text via Whisper |

### Chat request example

```json
POST /chat
{
  "message": "Add a docking system to my ship class",
  "project": "SpaceGame",
  "use_voice": true,
  "voice": "British_Female"
}
```

### Persona switching example

```json
POST /persona/SpaceGame
{ "persona": "Coder" }
```

Built-in personas: **Arbiter** (default), **Coder**, **Teacher**, **Organizer**.
Each persona shapes Arbiter's system prompt and response style.
The active persona is persisted per project in the project's SQLite database.

### Model download example

```json
POST /models/download
{ "auto": true }
```

Auto-selects and downloads the best model for your hardware.
Poll `GET /models/download/status` until `"running": false`.

---

## Open-Source Technology Stack

| Layer | Technology |
|---|---|
| Windows UI | C# WPF / .NET 8 |
| AI Bridge | Python FastAPI |
| LLM Inference | llama-cpp-python (GGUF) |
| TTS | pyttsx3 / Coqui TTS |
| STT | Windows System.Speech / openai-whisper |
| Git | LibGit2Sharp |
| Vector Search | Chroma / FAISS (planned) |
| Agent Framework | LangChain / AutoGen (planned) |

---

## Roadmap

```
Phase 0  — Chat + Voice + Workspace + Git                  ✅ Complete
Phase 1  — Build loop + error fix + test runner            ✅ Complete
           Persona system (Coder / Teacher / Organizer)    ✅ Complete
Phase 2  — Google Drive workspace + cloud sync
Phase 3  — Knowledge archive + PDF + VS VSIX
Phase 4  — Image / audio generation + multimodal
Phase 5  — Multi-agent system
```

---

## Archive

The previous webhook integration (Jira → Azure Boards → Linear → GitHub Copilot) has been archived to `archive/webhook-integration/` and is no longer the active development focus.

---

## License

MIT

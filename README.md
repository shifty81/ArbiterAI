# ArbiterAI — Base AI Engine

**ArbiterAI** is the AI engine layer for the [Arbiter](https://github.com/shifty81/Arbiter) project.

It provides a fully local, free, and open-source ChatGPT-style chat interface for **code planning, brainstorming, and roadmap generation** — running 100% offline on Windows with no cloud dependency.

> **This repo does NOT push or execute code.** It is a planning and ideation engine.
> Generated snippets and roadmaps are for integration into the Arbiter tooling repo.

---

## Purpose

```
You ──► Chat with Arbiter ──► Plan code, brainstorm features, generate roadmaps
                                  │
                         Auto-indexed to chat_log.md
                         Roadmaps saved to roadmap.md / roadmap.json
                                  │
                         Arbiter tooling repo reads the plan and implements it
```

ArbiterAI sits between the developer and the Arbiter tooling layer:

| What ArbiterAI does | What Arbiter (tooling) does |
|---|---|
| LLM inference (local) | IDE, Monaco editor, file management |
| Chat UI (ChatGPT-style) | Build / run / test loop |
| Chat → Markdown indexing | Git integration |
| AI roadmap generation | Project scaffolding |
| Persona system | Plugin / module system |
| Voice I/O (TTS + STT) | Deployment, Docker, CI |
| Code snippet library | Multi-agent orchestration |

---

## Architecture

```
ArbiterAI/
├── Arbiter.sln                            # Visual Studio solution
│
├── HostApp/                               # C# WPF Windows application
│   ├── App.xaml(.cs)                      # Startup — shows LauncherWindow
│   ├── AppConfig.cs                       # Static: mode, API URL, engine process
│   ├── LauncherWindow.xaml(.cs)           # Mode picker: ArbiterAI | Arbiter Engine
│   ├── MainWindow.xaml(.cs)               # Project list, chat, export, roadmap
│   ├── ProjectWindow.xaml(.cs)            # Per-project: chat, file tree, git, TTS
│   ├── SettingsWindow.xaml(.cs)           # Settings
│   ├── BuildInterface/BuildManager.cs     # dotnet / npm / cargo build runner
│   ├── GitInterface/GitManager.cs         # LibGit2Sharp integration
│   ├── VoiceInterface/                    # TTS (System.Speech) + STT
│   ├── Utilities/                         # DarkTitleBar, PythonHelper, InputDialog
│   ├── Themes/DarkTheme.xaml              # VS Code-inspired dark palette
│   └── Config/settings.json              # App configuration
│
├── AIEngine/
│   ├── PythonBridge/                      # Primary backend (port 8000)
│   │   ├── fastapi_bridge.py              # FastAPI server — all AI endpoints
│   │   ├── llm_interface.py               # Hardware-aware LLM (GGUF + Ollama + stub)
│   │   ├── persona_manager.py             # Persona system
│   │   ├── VoiceManager.py                # TTS helper
│   │   ├── model_downloader.py            # HuggingFace Hub auto-download
│   │   ├── requirements.txt
│   │   └── static/index.html             # ChatGPT-style web UI
│   │
│   └── ArbiterEngine/                     # Full agentic backend (port 8001, optional)
│
├── Memory/
│   ├── ConversationLogs/{project}/
│   │   ├── session.db                     # SQLite chat history
│   │   └── chat_log.md                    # Auto-indexed Markdown chat log  ← NEW
│   ├── snippets.json
│   └── notes.json
│
├── Projects/{project}/
│   ├── roadmap.json                       # Phase and task tracking
│   ├── roadmap.md                         # AI-generated Markdown roadmap   ← NEW
│   └── chat_log.md                        # Mirrored chat log               ← NEW
│
└── archive/                               # Archived predecessor code
```

---

## Key Features

| Feature | Status |
|---|---|
| ChatGPT-style web chat UI | ✅ Done |
| WPF dark-theme Windows client | ✅ Done |
| **Chat auto-indexed to Markdown** (`chat_log.md`) | ✅ Done |
| **AI roadmap generation** (`roadmap.md` + `roadmap.json`) | ✅ Done |
| **Feature registry / repo diff** (`GET /repo/diff`) | ✅ Done |
| Voice output (TTS) | ✅ Done |
| Voice input (STT — Whisper + Windows Speech) | ✅ Done |
| Persona system (Arbiter / Coder / Teacher / Organizer) | ✅ Done |
| Per-project SQLite conversation history | ✅ Done |
| Project workspace management + file tree | ✅ Done |
| Git integration (commit, push, pull, branch, log) | ✅ Done |
| Hardware-aware LLM loading (GGUF + Ollama + stub) | ✅ Done |
| Build / run / test loop (dotnet, npm, Python, Make) | ✅ Done |
| Automated model download (HuggingFace Hub) | ✅ Done |
| Monaco IDE web UI | ✅ Done |
| Arbiter Engine — 12 LLM backends + agentic loop | ✅ Done |

---

## Quick Start

### Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Python 3.10+

### 1 — Install Python dependencies

```bash
cd AIEngine/PythonBridge
pip install -r requirements.txt
```

### 2 — (Optional) Install Ollama for best AI quality

```
https://ollama.com  →  ollama pull llama3
```

### 3 — Run the Python bridge

```bash
python AIEngine/PythonBridge/fastapi_bridge.py
# Bridge starts at http://127.0.0.1:8000
```

### 4 — Build and run the WPF app

```bash
cd HostApp && dotnet build && dotnet run
# OR open Arbiter.sln in Visual Studio and press F5
```

---

## New Features

### Chat → Markdown Indexing

Every message sent through Arbiter is automatically appended to:

- `Memory/ConversationLogs/{project}/chat_log.md`
- `Projects/{project}/chat_log.md` (mirrored)

Use the **📥 Export** button in the chat panel (or the web UI header) to regenerate the full log from SQLite at any time.

### AI Roadmap Generation

Click **🗺 Roadmap** (web UI header or WPF chat panel) to ask Arbiter to generate a structured implementation roadmap from your conversation history.

Saved to:
- `Projects/{project}/roadmap.md` — human-readable Markdown
- `Projects/{project}/roadmap.json` — machine-readable for the WPF phase selector

API: `POST /roadmap/generate/{project}`

### Arbiter Repo Feature Diff

`GET /repo/diff` returns the canonical feature registry for ArbiterAI — which features are done, in progress, or planned.  The Arbiter tooling repo calls this endpoint to discover available capabilities and surface them in the IDE.

---

## Python Bridge API

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Health check |
| `/status` | GET | GPU, VRAM, model, token limit |
| `/llm/status` | GET | Active LLM backend |
| `/personas` | GET | List personas |
| `/persona/{project}` | GET/POST | Get / set active persona |
| `/chat` | POST | Send message → auto-indexes to MD |
| `/history/{project}` | GET | Conversation history |
| `/chat/export/{project}` | GET | Re-export full chat to `chat_log.md` |
| `/roadmap/generate/{project}` | POST | Generate roadmap from chat history |
| `/repo/diff` | GET | Feature registry + integration report |
| `/models` | GET | Recommended + downloaded models |
| `/models/download` | POST | Start model auto-download |
| `/build` | POST | Build project |
| `/run` | POST | Run project |
| `/test` | POST | Run test suite |
| `/stt` | POST | Transcribe audio (Whisper) |
| `/assistant/chat` | POST | IDE chat panel endpoint |
| `/ai/complete` | POST | Code completion |
| `/ai/action` | POST | explain / fix / refactor / tests |
| `/brainstorm/session` | POST | Brainstorm ideas |
| `/scaffold/module` | POST | Generate scaffolded code |
| `/snippets` | GET | Saved snippet library |
| `/snippet` | POST | Save a snippet |

---

## Integration with Arbiter Repo

ArbiterAI runs as a service (`http://localhost:8000`) that the Arbiter tooling layer calls.

Arbiter should:
1. Check `GET /health` at startup to confirm ArbiterAI is running
2. Call `GET /repo/diff` to discover available features
3. Use `POST /chat` for all AI interactions
4. Read `chat_log.md` and `roadmap.md` from project folders for context

---

## Architecture Decision: Two Repos

**ArbiterAI** and **Arbiter** are kept as separate repos intentionally:

- **ArbiterAI** = AI service layer (swappable, independently deployable)
- **Arbiter** = Tooling/application layer (calls ArbiterAI via HTTP)
- The FastAPI bridge is the integration contract — Arbiter never needs to know how the AI works
- Each repo can evolve at its own pace without merge conflicts

---

## Open-Source Stack

| Layer | Technology |
|---|---|
| Windows UI | C# WPF / .NET 8 |
| AI Bridge | Python FastAPI |
| LLM Inference | llama-cpp-python (GGUF) + Ollama |
| TTS | pyttsx3 / System.Speech |
| STT | Windows System.Speech / openai-whisper |
| Chat persistence | SQLite + Markdown |
| Git | LibGit2Sharp |

---

## Roadmap

See [`roadmap.json`](roadmap.json) for the full milestone breakdown.

| Milestone | Status |
|---|---|
| M0 — Foundation (chat, voice, projects, git, build) | ✅ Done |
| M1 — Monaco IDE + chat indexing + roadmap generation | ✅ Done |
| M2 — Arbiter Engine (full agentic backend) | 🔄 In Progress |
| M3 — Archive & Library (knowledge codex) | 📋 Planned |
| M4 — WPF IDE (full native client) | 📋 Planned |
| M5 — Advanced AI (RAG, multi-agent, self-build) | 📋 Planned |
| M6 — Distribution (installer, CLI, VS Code ext) | 📋 Planned |

---

## Archive

Superseded code lives in [`archive/`](archive/) and is never deleted.

---

## License

MIT

# ArbiterAI

ArbiterAI bridges your project management tools — **Jira**, **Azure Boards**, and **Linear** — with the [GitHub Copilot coding agent](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/coding-agent). When a ticket is labeled for Copilot in your PM tool, ArbiterAI automatically creates a GitHub issue and assigns it to `copilot-swe-agent`, triggering autonomous implementation.

---

## How It Works

```
PM Tool (Jira / Azure Boards / Linear)
           │
           │  Webhook (on create/update with trigger label)
           ▼
      ArbiterAI Server
           │
           │  GitHub REST API
           ▼
   Create Issue in target repo
   Assign → copilot-swe-agent
           │
           ▼
   Copilot coding agent starts working 🤖
```

1. You add a trigger label (default: `copilot`) to a ticket in your PM tool.
2. Your PM tool fires a webhook to ArbiterAI.
3. ArbiterAI creates a corresponding GitHub issue in the configured repository.
4. The issue is assigned to `copilot-swe-agent`, which triggers the Copilot coding agent to start working autonomously.
5. Copilot opens a draft pull request once work is done for you to review.

---

## Prerequisites

- Node.js 20+
- A GitHub repository with [Copilot coding agent enabled](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/coding-agent)
- A GitHub Personal Access Token (PAT) with `repo` and `issues` scopes
- Webhook URLs configured in your PM tool(s)

---

## Installation

```bash
git clone https://github.com/shifty81/ArbiterAI.git
cd ArbiterAI
npm install
cp .env.example .env
```

Edit `.env` with your credentials (see [Configuration](#configuration)).

---

## Configuration

All configuration is done via environment variables. Copy `.env.example` to `.env` and fill in the values:

| Variable | Required | Description |
|---|---|---|
| `GITHUB_TOKEN` | ✅ | GitHub PAT with `repo` + `issues` scopes |
| `GITHUB_OWNER` | ✅ | Repo owner (user or org) where issues will be created |
| `GITHUB_REPO` | ✅ | Repo name where issues will be created |
| `PORT` | — | HTTP port (default: `3000`) |
| `JIRA_WEBHOOK_SECRET` | — | Secret for verifying Jira webhook signatures |
| `JIRA_TRIGGER_LABEL` | — | Jira label that triggers delegation (default: `copilot`) |
| `AZURE_BOARDS_WEBHOOK_SECRET` | — | Secret for verifying Azure Boards webhook signatures |
| `AZURE_BOARDS_TRIGGER_TAG` | — | Azure Boards tag that triggers delegation (default: `copilot`) |
| `LINEAR_WEBHOOK_SECRET` | — | Secret for verifying Linear webhook signatures |
| `LINEAR_TRIGGER_LABEL` | — | Linear label that triggers delegation (default: `copilot`) |

---

## Running the Server

```bash
# Development (uses ts-node)
npm run dev

# Production
npm run build
npm start
```

The server exposes:
- `GET  /health` — Health check
- `POST /webhooks/jira` — Jira webhook endpoint
- `POST /webhooks/azure-boards` — Azure Boards webhook endpoint
- `POST /webhooks/linear` — Linear webhook endpoint

---

## PM Tool Setup

### Jira

1. In your Jira project, go to **Project Settings → Webhooks → Create webhook**.
2. Set the URL to `https://<your-server>/webhooks/jira`.
3. Select events: **Issue created**, **Issue updated**.
4. Optionally set a secret and put it in `JIRA_WEBHOOK_SECRET`.
5. Add the label `copilot` (or your custom `JIRA_TRIGGER_LABEL`) to any Jira issue you want delegated to Copilot.

### Azure Boards

1. In Azure DevOps, go to **Project Settings → Service hooks → Create subscription**.
2. Choose **Web Hooks** and select **Work item created** / **Work item updated**.
3. Set the URL to `https://<your-server>/webhooks/azure-boards`.
4. Add the tag `copilot` (or your custom `AZURE_BOARDS_TRIGGER_TAG`) to any work item you want delegated.

### Linear

1. In Linear, go to **Settings → API → Webhooks → New webhook**.
2. Set the URL to `https://<your-server>/webhooks/linear`.
3. Select **Issues** events.
4. Copy the webhook signing secret into `LINEAR_WEBHOOK_SECRET`.
5. Add the label `copilot` (or your custom `LINEAR_TRIGGER_LABEL`) to any Linear issue you want delegated.

---

## Development

```bash
# Run tests
npm test

# Run tests with coverage
npm run test:coverage

# Lint
npm run lint

# Build
npm run build
```

---

## Project Structure

```
src/
├── index.ts                        # Entry point
├── config.ts                       # Configuration loading
├── server.ts                       # Express server and routing
├── github/
│   └── client.ts                   # GitHub API client (creates + assigns issues)
├── integrations/
│   ├── types.ts                    # Shared WorkItem type
│   ├── jira/
│   │   ├── handler.ts              # Jira webhook handler
│   │   └── types.ts                # Jira webhook payload types
│   ├── azure-boards/
│   │   ├── handler.ts              # Azure Boards webhook handler
│   │   └── types.ts                # Azure Boards payload types
│   └── linear/
│       ├── handler.ts              # Linear webhook handler
│       └── types.ts                # Linear payload types
└── utils/
    ├── logger.ts                   # Structured logger
    └── webhook-security.ts         # HMAC signature verification
```

---

## License

MIT


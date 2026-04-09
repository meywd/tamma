# Config-Driven Agent Management -- Overview

## Architecture: Unified API

The key architectural principle is **one unified system in the TypeScript API (Fastify)**. Both the TypeScript engine and Elsa workflows consume the same API. No duplicate logic in C#.

```
┌─────────────────────────────────┐
│        Fastify API              │
│   (packages/api/)               │
│                                 │
│  ┌──────────┐ ┌──────────────┐ │
│  │  Config   │ │ Diagnostics  │ │
│  │  Store    │ │   Store      │ │
│  └──────────┘ └──────────────┘ │
│  ┌──────────┐ ┌──────────────┐ │
│  │  Health   │ │ Sanitization │ │
│  │  Store    │ │   Store      │ │
│  └──────────┘ └──────────────┘ │
│  ┌──────────┐ ┌──────────────┐ │
│  │  Chain    │ │   Agent      │ │
│  │  Resolver │ │  Resolver    │ │
│  └──────────┘ └──────────────┘ │
│          │                      │
│     PostgreSQL                  │
└─────────┬───────────────────────┘
          │
    ┌─────┴─────┐
    │           │
TS Engine   Elsa Workflows
(in-process)  (HTTP API calls)
```

## Provider Config Model

A "provider config" = provider + model + credentials. Examples:

| Label | Provider | Model | Notes |
|-------|----------|-------|-------|
| OpenRouter + z.ai | `openrouter` | `z-ai/z1-mini` | OpenRouter as gateway to z.ai |
| OpenRouter + Claude Opus | `openrouter` | `anthropic/claude-opus-4` | OpenRouter as gateway to Anthropic |
| Claude Code CLI | `claude-code` | `claude-sonnet-4-5` | Subprocess agent |
| OpenCode + ZenMCP | `opencode` | (default) | OpenCode CLI with ZenMCP backend |

Each agent role (architect, implementer, reviewer, etc.) has an ordered **provider chain** -- a priority list of these provider configs with automatic fallback.

## What Changed from the Original Design

### Before (duplicated logic)
- TypeScript classes in `packages/providers/src/` handled chain resolution, health tracking, diagnostics
- C# activities in `apps/tamma-elsa/src/Tamma.Activities/LlmCall/` re-implemented the same:
  - `CheckCircuitBreakerActivity.cs` -- circuit breaker with consecutive failure counting
  - `RecordDiagnosticsActivity.cs` -- diagnostics recording, budget tracking
  - `ResolveAgentConfigActivity.cs` -- agent config resolution from ELSA Agents DB
  - `CallLlmActivity.cs` -- direct HTTP calls to Anthropic/OpenAI APIs
- Two independent state stores that never shared data

### After (unified API)
- TypeScript classes remain as the core implementations
- Postgres-backed services wrap the classes with persistence
- Fastify API exposes all services as endpoints
- C# activities become thin HTTP callers (~50 lines instead of ~200-600 lines each)
- One circuit breaker state, one diagnostics store, one config store

## API Endpoints Summary

| Method | Path | Story | Description |
|--------|------|-------|-------------|
| GET | `/api/v1/agents/config` | 9-1 | Get agent config for account |
| PUT | `/api/v1/agents/config` | 9-1 | Update agent config |
| POST | `/api/v1/agents/config/validate` | 9-1 | Validate config |
| POST | `/api/v1/diagnostics` | 9-2 | Record diagnostics event |
| GET | `/api/v1/diagnostics` | 9-2 | Query diagnostics |
| GET | `/api/v1/diagnostics/report` | 9-2 | Cost/usage report |
| GET | `/api/v1/diagnostics/budget/:accountId` | 9-2 | Budget status |
| GET | `/api/v1/health/providers` | 9-3 | All provider health |
| GET | `/api/v1/health/providers/:key` | 9-3 | Specific provider health |
| POST | `/api/v1/health/providers/:key/reset` | 9-3 | Reset circuit breaker |
| POST | `/api/v1/providers/create` | 9-4 | Create provider instance |
| POST | `/api/v1/providers/:handle/execute` | 9-4 | Execute task on provider |
| DELETE | `/api/v1/providers/:handle` | 9-4 | Dispose provider |
| POST | `/api/v1/providers/chain/resolve` | 9-5 | Resolve provider chain |
| POST | `/api/v1/sanitize` | 9-7 | Sanitize content |
| GET | `/api/v1/sanitize/rules` | 9-7 | Get sanitization rules |
| PUT | `/api/v1/sanitize/rules` | 9-7 | Update sanitization rules |
| GET | `/api/v1/agents/:role/resolve` | 9-8 | Resolve agent for role |
| POST | `/api/v1/agents/resolve-for-phase` | 9-8 | Resolve agent for phase |

## Stories

| # | Story | Key Output |
|---|-------|------------|
| 1 | [Config Schema + API](../story-9-1/9-1-configuration-schema.md) | Postgres-backed config store + CRUD API |
| 2 | [Diagnostics Service + API](../story-9-2/9-2-provider-diagnostics.md) | Postgres diagnostics + report/budget API |
| 3 | [Health Tracker Service + API](../story-9-3/9-3-provider-health-tracker.md) | Persistent circuit breaker + health API |
| 4 | [Provider Factory API](../story-9-4/9-4-agent-provider-factory.md) | Session-based provider creation API |
| 5 | [Provider Chain API](../story-9-5/9-5-provider-chain.md) | Chain resolution API with health/budget filtering |
| 6 | ~~Prompt Registry~~ | **SUPERSEDED by Epic 27** |
| 7 | [Sanitization Service + API](../story-9-7/9-7-content-sanitization.md) | Per-account sanitization rules + API |
| 8 | [Unified Agent Resolver API](../story-9-8/9-8-role-based-agent-resolver.md) | Top-level resolution API |
| 9 | [Engine Integration](../story-9-9/9-9-engine-integration.md) | Engine uses store-backed resolver |
| 10 | [CLI Wiring](../story-9-10/9-10-cli-wiring.md) | CLI uses resolver, new commands |
| 11 | [Diagnostics Queue + Elsa Integration](../story-9-11/9-11-diagnostics-queue-mcp-interceptors.md) | Elsa simplified to thin API callers |

## Dependency Order

```
Story 1 (config + API)     ──────────────────────────────────────────┐
Story 7 (sanitization + API) ─────────────────────┐                  │
Story 3 (health + API)      ─────────┐            │                  │
Story 4 (factory API)       ─────────┤            │                  │
                                      ↓            │                  │
Story 2 (diagnostics + API)           │            │                  │
                         ↓            │            │                  │
Story 5 (chain API) ← needs 2,3,4    │            │                  │
                                      ↓            ↓                  ↓
                  Story 11 ← needs 2,3      Story 8 ← needs 1,3,4,5,7,27
                                                                       ↓
                                                  Story 9 (engine) ← needs 8
                                                                       ↓
                                                  Story 10 (CLI) ← needs 1,9,11
```

**Parallel groups:**
- Group 1 (no deps): Stories 1, 3, 4, 7
- Group 2: Story 2
- Group 3 (needs 2,3,4): Story 5. Also: Story 11 (needs 2,3)
- Group 4 (needs 1,3,4,5,7,Epic 27): Story 8
- Group 5 (needs 8): Story 9
- Group 6 (needs 1,9,11): Story 10

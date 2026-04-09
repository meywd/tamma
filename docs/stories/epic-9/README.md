# Epic 9: Unified Agent Management API & Diagnostics

## Overview

This epic builds a **unified API layer** (Fastify) for config-driven multi-agent management. Both the TypeScript engine and Elsa workflows consume the same API, eliminating duplicate logic in C#.

**Architecture change from the original design:** The original Epic 9 stories defined in-process TypeScript classes (ProviderChain, HealthTracker, Factory, etc.) that lived only in `packages/providers/`. Elsa's `LlmCallWorkflow` re-implemented the same concerns in C# (circuit breaker in `CheckCircuitBreakerActivity.cs`, diagnostics in `RecordDiagnosticsActivity.cs`, config in `ResolveAgentConfigActivity.cs`). This caused:

- **Duplicated logic** -- circuit breaker, cost tracking, provider chains implemented twice
- **No shared state** -- TS health tracker and C# circuit breaker states were independent
- **Divergent behavior** -- TS sliding-window failures vs. C# consecutive-failure counting

**New architecture:** One unified system in the TypeScript API (Fastify at `packages/api/`). The API owns:

- **Provider chain resolution** -- which providers to try, in what order
- **Health tracking / circuit breaker state** -- shared across all callers via Postgres/Redis
- **Diagnostics collection** -- costs, tokens, latency written to one store via API
- **Content sanitization rules** -- configurable per account
- **Agent role-to-provider resolution** -- single resolution logic

Elsa's `LlmCallWorkflow` becomes a thin orchestrator that calls the API for each step. C# activities (`CheckCircuitBreakerActivity`, `RecordDiagnosticsActivity`, `ResolveAgentConfigActivity`) are replaced with simple HTTP calls to the Fastify API.

### Existing TypeScript Implementations

The following in-process classes already exist in `packages/providers/src/` and will be promoted to API-backed services:

| File | Class | Tests |
|------|-------|-------|
| `provider-chain.ts` | `ProviderChain` | `provider-chain.test.ts` |
| `provider-health.ts` | `ProviderHealthTracker` | `provider-health.test.ts` |
| `agent-provider-factory.ts` | `AgentProviderFactory` | `agent-provider-factory.test.ts` |
| `role-based-agent-resolver.ts` | `RoleBasedAgentResolver` | `role-based-agent-resolver.test.ts` |
| `secure-agent-provider.ts` | `SecureAgentProvider` | `secure-agent-provider.test.ts` |
| `instrumented-agent-provider.ts` | `InstrumentedAgentProvider` | `instrumented-agent-provider.test.ts` |
| `agent-prompt-registry.ts` | `AgentPromptRegistry` | `agent-prompt-registry.test.ts` |

### Existing C# Activities (to be simplified)

| File | Activity | Replacement |
|------|----------|-------------|
| `CallLlmActivity.cs` | Direct HTTP to LLM providers | Calls Fastify API which delegates to TS providers |
| `CheckCircuitBreakerActivity.cs` | In-workflow circuit breaker | Calls `GET /api/v1/health/:provider` |
| `RecordDiagnosticsActivity.cs` | In-workflow diagnostics | Calls `POST /api/v1/diagnostics` |
| `ResolveAgentConfigActivity.cs` | Agent config from ELSA DB | Calls `GET /api/v1/agents/:role/resolve` |
| `CheckBudgetActivity.cs` | In-workflow budget check | Calls `GET /api/v1/diagnostics/budget/:accountId` |

## Stories

| Story | Title | Package(s) | Priority | Status |
|-------|-------|-----------|----------|--------|
| 9-0 | [Overview](story-9-0/9-0-overview.md) | -- | -- | Reference |
| 9-1 | [Config Schema + API](story-9-1/9-1-configuration-schema.md) | shared, api, cli | P0 | Planned |
| 9-2 | [Diagnostics Service + API](story-9-2/9-2-provider-diagnostics.md) | shared, api | P0 | Planned |
| 9-3 | [Health Tracker Service + API](story-9-3/9-3-provider-health-tracker.md) | providers, api | P0 | Planned |
| 9-4 | [Provider Factory API](story-9-4/9-4-agent-provider-factory.md) | providers, api | P0 | Planned |
| 9-5 | [Provider Chain API](story-9-5/9-5-provider-chain.md) | providers, api | P0 | Planned |
| 9-6 | ~~Prompt Registry~~ | -- | -- | **SUPERSEDED by Epic 27** |
| 9-7 | [Sanitization Service + API](story-9-7/9-7-content-sanitization.md) | shared, api | P1 | Planned |
| 9-8 | [Unified Agent Resolver API](story-9-8/9-8-role-based-agent-resolver.md) | providers, api | P0 | Planned |
| 9-9 | [Engine Integration](story-9-9/9-9-engine-integration.md) | orchestrator | P0 | Planned |
| 9-10 | [CLI Wiring](story-9-10/9-10-cli-wiring.md) | cli | P0 | Planned |
| 9-11 | [Diagnostics Queue + Elsa Integration](story-9-11/9-11-diagnostics-queue-mcp-interceptors.md) | shared, api, tamma-elsa | P0 | Planned |

## Dependency Graph

```
Epic 16 (accounts/tenants) ──────────────────────────────────────────┐
Epic 17 (multi-tenant auth) ──────────────────────────────────────── │
Epic 27 (prompt store) ────────────── supersedes Story 9-6          │
                                                                     │
Story 9-1 (config schema + API)  ────────────────────────────────── │─┐
Story 9-7 (sanitization + API)   ─────────────────┐                 │ │
Story 9-3 (health tracker + API) ────────┐        │                 │ │
Story 9-4 (factory API)          ────────┤        │                 │ │
                                         ↓        │                 │ │
Story 9-2 (diagnostics + API)            │        │                 ↓ ↓
                         ↓               │        │      Story 9-8 (resolver API) ← 1,3,4,5,7,27
Story 9-5 (chain API) ← needs 2,3,4     │        │                 ↓
                                         │        │      Story 9-9 (engine) ← needs 8
                                         ↓        ↓                 ↓
                      Story 9-11 (diagnostics queue + Elsa) ← 2,3  Story 9-10 (CLI) ← 1,9,11
```

**Parallel groups:**
- Group 1 (no deps): Stories 1, 3, 4, 7
- Group 2 (needs shared infra): Story 2
- Group 3 (needs 2,3,4): Story 5. Also: Story 11 (needs 2,3)
- Group 4 (needs 1,3,4,5,7,Epic 27): Story 8
- Group 5 (needs 8): Story 9
- Group 6 (needs 1,9,11): Story 10

## Cross-Epic Dependencies

| Dependency | Direction | Notes |
|-----------|-----------|-------|
| Epic 16 (Accounts) | 9 depends on 16 | Account IDs for per-account config storage |
| Epic 17 (Multi-tenant Auth) | 9 depends on 17 | JWT/session auth for API endpoints |
| Epic 27 (Prompt Store) | 27 supersedes 9-6 | Prompt resolution moved to Postgres-backed store with provider dimension |

## API Endpoints Summary

All endpoints are prefixed with `/api/v1/` and scoped to `accountId` from the JWT.

| Method | Path | Story | Description |
|--------|------|-------|-------------|
| GET | `/agents/config` | 9-1 | Get agent config for account |
| PUT | `/agents/config` | 9-1 | Update agent config for account |
| POST | `/agents/config/validate` | 9-1 | Validate config without saving |
| GET | `/diagnostics` | 9-2 | Query diagnostics (cost, tokens, latency) |
| POST | `/diagnostics` | 9-2 | Record a diagnostics event |
| GET | `/diagnostics/report` | 9-2 | Generate cost/usage report |
| GET | `/diagnostics/budget/:accountId` | 9-2 | Check budget status |
| GET | `/health/providers` | 9-3 | Get all provider health statuses |
| GET | `/health/providers/:key` | 9-3 | Get specific provider health |
| POST | `/health/providers/:key/reset` | 9-3 | Manually reset circuit breaker |
| POST | `/providers/create` | 9-4 | Create a provider instance |
| POST | `/providers/chain/resolve` | 9-5 | Resolve provider chain for role |
| POST | `/sanitize` | 9-7 | Sanitize content |
| GET | `/sanitize/rules` | 9-7 | Get sanitization rules for account |
| PUT | `/sanitize/rules` | 9-7 | Update sanitization rules |
| GET | `/agents/:role/resolve` | 9-8 | Resolve agent config for role |
| POST | `/agents/resolve-for-phase` | 9-8 | Resolve agent for workflow phase |

## Estimated Total Effort

| Story | Estimate |
|-------|----------|
| 9-1 Config Schema + API | 16 hours |
| 9-2 Diagnostics Service + API | 20 hours |
| 9-3 Health Tracker Service + API | 16 hours |
| 9-4 Provider Factory API | 12 hours |
| 9-5 Provider Chain API | 14 hours |
| 9-6 ~~Prompt Registry~~ | 0 hours (superseded by Epic 27) |
| 9-7 Sanitization Service + API | 14 hours |
| 9-8 Unified Agent Resolver API | 18 hours |
| 9-9 Engine Integration | 14 hours |
| 9-10 CLI Wiring | 12 hours |
| 9-11 Diagnostics Queue + Elsa Integration | 20 hours |
| **Total** | **156 hours** |

---

**Last Updated**: 2026-04-08
**Epic Owner**: Platform Engineering

# Epic 9: Config-Driven Multi-Agent Management

**Status:** Mostly complete. 9-1..9-11 landed in TypeScript + API form; 9-12 (cross-epic integration test) and the Unified Agent Resolver leg depend on Epic 27 prompt-store landing.
**Stories:** 12 (9-1..9-12). Story 9-6 is superseded by [Epic 27 Prompt Store](Epic-27-Prompt-Store.md).
**Primary code:** `packages/shared/`, `packages/providers/`, `packages/mcp-client/`, `packages/orchestrator/`, `packages/cli/`, Fastify API routes under the api package.

## Overview

Epic 9 replaces Tamma's original hard-coded single-agent setup with a config-driven multi-agent system and promotes it to a shared API tier. Every agent *role* (architect, implementer, reviewer, tester, analyst, scrum-master, researcher, planner, documenter) has its own ordered provider chain with automatic fallback. Every call is instrumented for cost, token, and latency tracking. Every input and output passes through content sanitization, URL validation, and action gating. Unhealthy providers are skipped by a circuit breaker; budgets are enforced fail-closed.

The second half of the epic lifts the whole thing from an in-process concern to a Fastify API. Before, `packages/providers/` owned chains, health, and diagnostics, while ELSA's C# `LlmCallWorkflow` re-implemented the same concerns independently — leading to divergent behaviour between the TS engine and the ELSA server. Now, chain resolution, circuit breaker state, diagnostics recording, sanitization rules, and role-to-provider resolution all live behind one API; C# activities (`CheckCircuitBreakerActivity`, `RecordDiagnosticsActivity`, `ResolveAgentConfigActivity`) become thin HTTP clients of the same service. Per-tenant scoping is built into every route via the `accountId` claim on the JWT.

The epic also lands a defense-in-depth security layer (Story 9-7) used by the orchestrator, by ELSA via Epic 11, and by the MCP tool layer via Story 9-11's interceptor chain.

## Architecture

```
                CLI config (agents + security sections)
                                |
                                v
+-------------------------------------------------------------------+
|                  RoleBasedAgentResolver (9-8)                      |
|   Entry facade. Maps phase → role → prompt + provider chain.       |
+-------------------------------------------------------------------+
   |                     |                    |                |
   v                     v                    v                v
AgentPromptRegistry  ProviderChain       TaskConfig merge   SecureAgentProvider
(9-6 / Epic 27       (9-5)               (defaults < role   (wraps result with
 prompt store)          |                 < overrides,       ContentSanitizer)
                        |                 clamped)
                        v
             +---------------------+
             | ProviderHealthTracker (9-3)    — 3-state CB, sliding window |
             | ICostTracker / BudgetCheck     — fail-closed                |
             | AgentProviderFactory  (9-4)    — create by name             |
             | InstrumentedAgentProvider      — emit diagnostics           |
             +---------------------+
                        |
                        v
                IAgentProvider.executeTask()
        +----------------+----------------+
        v                v                v
   ClaudeAgent    OpenCode  (native agents, tool loops)
   OpenRouter     ZenMCP    (LLM providers; wrapAsAgent())

                DiagnosticsQueue (9-2, 9-11)   -->   DiagnosticsEventProcessor
                                                      (persist + metrics + alerts)

                MCP Client layer
                        |
                        v
                ToolInterceptorChain (9-11)
                  pre-interceptors:   URL validation → block or replace
                  post-interceptors:  ContentSanitizer.sanitizeOutput()

+------------------------------------------------------------------+
|                   Fastify API (unified, /api/v1)                 |
|  GET/PUT /agents/config             POST /diagnostics            |
|  GET    /agents/:role/resolve       GET /diagnostics/report      |
|  POST   /agents/resolve-for-phase   GET /diagnostics/budget/:id  |
|  GET    /health/providers           POST /providers/create       |
|  POST   /providers/chain/resolve    POST /sanitize               |
|  GET    /sanitize/rules             PUT  /sanitize/rules         |
|                                                                  |
|  Consumers: TS engine, ELSA LlmCallWorkflow, CLI, Studio.        |
+------------------------------------------------------------------+
```

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `IAgentsConfig` / validators | Typed multi-agent + security config | `packages/shared/src/types/agent-config.ts`, `security-config.ts` | 9-1 / Done |
| `DiagnosticsEvent` / queue | Discriminated union of provider events + async batch drain | `packages/shared/src/telemetry/diagnostics-event.ts`, `diagnostics-queue.ts` | 9-2 / Done |
| `ProviderHealthTracker` | 3-state circuit breaker with sliding-window failure count | `packages/providers/src/provider-health.ts` | 9-3 / Done |
| `AgentProviderFactory` | Name → `IAgentProvider` with env-var keyed credentials; locks built-ins | `packages/providers/src/agent-provider-factory.ts` | 9-4 / Done |
| `ProviderChain` | Iterates entries applying health + budget + factory + probe | `packages/providers/src/provider-chain.ts` | 9-5 / Done |
| `AgentPromptRegistry` | 6-level resolution: per-role-per-provider → per-role → global → built-in → fallback | `packages/providers/src/agent-prompt-registry.ts` | 9-6 / superseded by Epic 27 |
| `ContentSanitizer` + friends | Null-byte / HTML / zero-width removal + injection detection; URL validator; action gating; secure fetch | `packages/shared/src/security/*.ts` | 9-7 / Done |
| `RoleBasedAgentResolver` | Integration facade wiring all above; also does task-config merge + prompt rendering | `packages/providers/src/role-based-agent-resolver.ts` | 9-8 / Done |
| Engine integration | Orchestrator calls resolver at each phase | `packages/orchestrator/src/engine.ts` | 9-9 / Done |
| CLI wiring | Validates config, builds subsystems, wires into engine | `packages/cli/src/index.tsx`, `config.ts` | 9-10 / Done |
| `ToolInterceptorChain` | Pre/post hooks around MCP tool calls (URL validation, output sanitize) | `packages/mcp-client/src/interceptors.ts` | 9-11 / Done |
| Fastify API routes | Config / health / diagnostics / sanitize / agents / providers endpoints with per-tenant scoping | api package + ELSA C# HTTP client replacements | 9-1..9-8 / Done (Layer 3 via `0e97a5d6`) |
| Cross-epic integration test | End-to-end scenario across Epics 7-12 | `docs/stories/epic-9/story-9-12/` | 9-12 / Planned |

## Class / type structure

```
packages/shared/src/types/
  agent-config.ts
    type WorkflowPhase                 — 8 phases
    interface IProviderChainEntry      — { provider, model?, apiKeyRef?, config? }
    interface IAgentRoleConfig         — providerChain + allowedTools + maxBudgetUsd + ...
    interface IAgentsConfig            — { defaults, roles?, phaseRoleMap? }
    fn validateAgentsConfig(cfg)       — throws on invalid
  security-config.ts
    interface SecurityConfig           — sanitize/validate/gate toggles + limits
    fn validateSecurityConfig(cfg)

packages/shared/src/telemetry/
  diagnostics-event.ts
    type DiagnosticsEvent              — call | complete | error
    fn sanitizeErrorMessage(msg)
  diagnostics-queue.ts
    class DiagnosticsQueue             — emit() / drain() / dispose()
    interface IDiagnosticsQueue

packages/shared/src/security/
  content-sanitizer.ts
    class ContentSanitizer : IContentSanitizer
      sanitize(input) -> { output, warnings }
      sanitizeOutput(input)
  url-validator.ts
    fn validateUrl(url) -> { valid, warnings }
  action-gating.ts
    fn evaluateAction(command, opts?) -> { allowed, reason? }
  secure-fetch.ts
    fn secureFetch(url, opts?) -> { ok, status, body, headers, warnings }

packages/providers/src/
  agent-types.ts
    interface IAgentProvider           — executeTask(), isAvailable(), dispose()
  provider-health.ts
    class ProviderHealthTracker : IProviderHealthTracker
      isHealthy(key), recordSuccess(key), recordFailure(key, retryable)
      static buildKey(provider, model?)
  agent-provider-factory.ts
    class AgentProviderFactory : IAgentProviderFactory
      create(entry) : IAgentProvider
  provider-chain.ts
    class ProviderChain : IProviderChain
      getProvider(context) : Promise<IAgentProvider>
  agent-prompt-registry.ts
    class AgentPromptRegistry
      resolve(role, providerName, vars?) : string
  instrumented-agent-provider.ts
    class InstrumentedAgentProvider : IAgentProvider
  secure-agent-provider.ts
    class SecureAgentProvider : IAgentProvider  (Decorator)
  role-based-agent-resolver.ts
    class RoleBasedAgentResolver : IRoleBasedAgentResolver
      getAgentForPhase / getAgentForRole / getTaskConfig / getPrompt / getRoleForPhase
  diagnostics-processor.ts
    class DiagnosticsEventProcessor     — persist + emit metrics

packages/mcp-client/src/
  interceptors.ts
    class ToolInterceptorChain
      addPreInterceptor / addPostInterceptor / runPre / runPost
    fn createSanitizationInterceptor(sanitizer)
    fn createUrlValidationInterceptor(validateUrl)

Fastify API (via package api):
  routes/agents-config.ts, diagnostics.ts, health.ts, providers.ts,
  sanitize.ts, agents-resolve.ts   — all scoped by accountId from JWT.
```

## Sequence — CODE_GENERATION phase, primary provider healthy

```
Engine             Resolver                 ProviderChain      HealthTracker   Provider           Sanitizer        DiagQueue
  |                   |                          |                  |             |                   |                |
  | getAgentForPhase('CODE_GENERATION', ctx) --->|                  |             |                   |                |
  |                   | phase -> role 'implementer'                 |             |                   |                |
  |                   | chain.getProvider(ctx) --->                  |             |                   |                |
  |                   |                          | for each entry:  |             |                   |                |
  |                   |                          | isHealthy? ----> |             |                   |                |
  |                   |                          | <-- yes          |             |                   |                |
  |                   |                          | budget ok?       |             |                   |                |
  |                   |                          | factory.create --|-----------> | new ClaudeAgent   |                |
  |                   |                          | isAvailable? --- |-----------> |                   |                |
  |                   |                          | <-- ok           |             |                   |                |
  |                   |                          | recordSuccess -->|             |                   |                |
  |                   |                          | wrap in Instrumented + Secure                      |                |
  |                   | <-- agent ----------------|                  |             |                   |                |
  | getPrompt('implementer', 'claude-code', vars)|                  |             |                   |                |
  |                   | registry.resolve -> "You are an autonomous coding agent..."                  |                |
  | <-- prompt -------|                                                                               |                |
  | getTaskConfig('implementer', overrides)      |                                                   |                |
  | <-- merged config                                                                                 |                |
  |                                                                                                                   |
  | agent.executeTask({ prompt, ...merged })                                                                         |
  |      |                                                                                                            |
  |      | Secure wrapper: sanitizer.sanitize(prompt) ----------------------------> |                                  |
  |      | <-- { output, warnings }                                                                                   |
  |      | Instrumented wrapper: emit('provider:call') ------------------------->                                      |
  |      | call inner provider ...                                                                                    |
  |      | on result: emit('provider:complete') ------------------------------->                                      |
  |      | sanitizer.sanitizeOutput(result.text) --------------------------------->|                                  |
  |      | <-- sanitized result                                                                                       |
  | <-- AgentTaskResult                                                                                                |
  |                                                                                                                   |
  |                                                                          DiagnosticsQueue drains in background --> |
  |                                                                          processor persists + emits metrics ------>|
```

## Use cases

- **Fallback to backup provider on primary outage** — Anthropic returns 529 rate-limit. HealthTracker records failure; after threshold (5 in 60 s), circuit opens. Next call skips claude-code and falls through to `openrouter` with `z-ai/z1-mini`. Five minutes later, `half-open` lets one probe through; success closes the circuit.
- **Per-role budget enforcement** — implementer has `maxBudgetUsd: 20`, reviewer has `3`. A review step with a 500 k-token prompt fails budget check fail-closed and falls through to a cheaper provider or errors out — it never silently exceeds.
- **Prompt override for a specific provider** — tester role uses OpenRouter for cheap bulk generation; `providerPrompts.openrouter` supplies a tester-specialized prompt that differs from the Claude prompt, without having to fork the whole role.
- **MCP tool returns adversarial HTML** — MCP server returns an issue body containing `<script>` + zero-width characters + `ignore previous instructions`. Post-interceptor chain sanitizes the payload before it reaches the LLM; pre-interceptor blocked a linked `169.254.169.254/latest/meta-data` URL from firing at all.
- **Tenant A changing sanitization rules independently of Tenant B** — the API stores sanitization rules keyed by accountId; Tenant A loosens HTML stripping on specific fields without affecting anyone else.
- **ELSA and TS engine sharing circuit-breaker state** — both call `GET /api/v1/health/:provider` and both record failures into the same store, so a provider that trips the breaker from an ELSA call stays open for the TS engine too.

## Dependencies

**Upstream**
- Epic 1 — `IAgentProvider`, `IAIProvider`, `wrapAsAgent()`, built-in provider classes.
- Epic 17 — tenants; every Epic 9 API route scopes by `accountId` from JWT.
- Epic 18 — multi-tenant auth; JWT validation middleware on the Fastify routes.
- Epic 27 — prompt store; supersedes Story 9-6; 9-8 delegates prompt resolution to it.

**Downstream**
- Epic 7 — `LlmCallWorkflow` uses the chain/budget/circuit/diagnostics API.
- Epic 10 — engine brain uses the resolver for the orchestrator role.
- Epic 11 — reuses `ContentSanitizer` (C# port) + action-gate logic.
- Epic 12 — agentic tool loop is a consumer of resolved agents.
- Epic 19 — agent dispatch routes stories to a role-resolved provider chain.

## Current state

Landed:
- `5041847d docs: rewrite Epic 9 as unified API architecture + update Epic 27`
- `8b61737f docs(epic-9): add stories and tasks for config-driven agent management`
- `42bd99f0 docs: implementation plans for 10 Epic 9 stories (167h)`
- `78e60b9d fix(security): address 12 critical/high findings from Epic 9 code review`
- `5e394c84 docs: update README, guides, wiki for Epic 9; fix 5 remaining code review issues`
- `f163fc7f feat: Story 9-2 — diagnostics service gap-fill`
- `bdef3456 feat: Story 9-4 — provider factory API gap-fill`
- `1b0f7d46 feat: Story 9-3 — health tracker service gap-fill`
- `7ddb3a69 merge: Story 9-5 provider chain API with budget + recommendation`
- `c6697c88 feat: Story 9-8 — Unified Agent Resolver API`
- `0e97a5d6 feat: Layer 3 — Epic 9 services, prompt store API, auth backend`

All TypeScript classes landed with ≥80% test coverage; API layer shipped under `0e97a5d6`; MCP interceptor chain integrated in `@tamma/mcp-client`.

Outstanding:
- Story 9-12 (cross-epic integration test) is scoped but not merged; depends on Epic 27 prompt store fully landing for the final scenario.
- Story 9-6 stays in place as a migration shim (`AgentPromptRegistry` delegates to the prompt-store API when available).
- Circuit breaker state is in-memory only today; distributed coordination (Redis-backed) is a follow-up not on the Epic 9 roadmap.

Stubs / deferrals:
- Diagnostics processor persists to Postgres; a metrics forwarder (Prometheus / OTel) is planned but not in Epic 9.
- Rate limiting on API routes uses Fastify in-memory; Redis-backed rate limiting ships with the SaaS deployment path.

## See also

- [Agent Dispatch](../Agent-Dispatch.md)
- [Security](../Security.md)
- [Architecture](../Architecture.md)
- [Epic 11: Security](Epic-11-Security.md) — C# port of `ContentSanitizer`.
- [Epic 12: Tool Loop](Epic-12-Tool-Loop.md) — agentic loop consumes resolved agents.
- [Epic 27: Prompt Store](Epic-27-Prompt-Store.md) — supersedes Story 9-6.
- [Epic 17: Multi-Tenancy](Epic-17-Multi-Tenancy.md) — account scoping.
- [Epic 18: User Auth](Epic-18-User-Auth.md) — JWT for API routes.
- Impl plans: [`docs/stories/epic-9/`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-9).
- Source: `packages/shared/src/security/`, `packages/shared/src/telemetry/`, `packages/providers/src/`, `packages/mcp-client/src/interceptors.ts`, `packages/orchestrator/src/engine.ts`, `packages/cli/src/`.

---

_Last refreshed 2026-04-22._

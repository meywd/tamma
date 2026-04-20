# Layer 2 & 3 Status — Post Epic 19

**Status**: Active
**Last Updated**: 2026-04-16
**Purpose**: Capture the actual state of Layers 2 and 3 after Epic 19 (C# API
consolidation) landed on `feat/auth-foundation`. The original
[`remaining-layer-2-3-execution.md`](./remaining-layer-2-3-execution.md) was
written before the TypeScript API was deleted, so it reads as if the 14
"remaining" stories only need gap-fills. Epic 19 Phase 3 removed
`packages/api/` entirely, taking the bulk of the Layer 2/3 backend with it.

## TL;DR

- **Layer 1** is in PR #328 (feat/auth-foundation). Still open, CI green
  after the 2026-04-16 fixes.
- **Layer 2/3 backend** landed in PR #328 as TypeScript commits
  (`192ae36` Layer 2, `0e97a5d` Layer 3) and was then **deleted** by Epic 19
  Phase 3 (`9e9a57c`). The business logic is gone; EF migrations + thin C#
  endpoints replace the shape, not the depth.
- **Layer 2/3 frontend** (admin dashboard 16-3, unified nav 16-4) survived
  Epic 19 — still in `packages/dashboard/`.
- **Layer 2/3 C# / Elsa work** (12-5c mentorship fix, 12-5e CI retry counter,
  27-6 Elsa integration) survived.
- **Before starting Layer 4**: a hardening pass is needed to restore the
  business logic the C# API stubbed. Estimate **~60 hours** of focused port
  work (see the "Hardening punch list" below).

## Story-level status

Legend:
- ✅ **Live** — merged in PR #328, verifiably working
- 🟡 **Shallow** — C# endpoint exists but implementation is a stub or
  repository pass-through; original TS depth was lost
- 🔴 **Gone** — TS implementation deleted, no C# replacement
- ⚪ **Not started**

### Layer 2

| Story | Title | Status | Where it lives now |
|-------|-------|--------|-------|
| 17-2 | Row-Level Security | 🟡 | EF global query filter on entities (see `TammaDbContext.cs`). **No** Postgres-level RLS policies. Relies entirely on app-layer filtering. |
| 17-3 | Tenant-Scoped Event Store | 🟡 | `EventRepository` + `domain_events.TenantId`. No cross-aggregate query helpers, no `withTenantContext()` TX scope. |
| 17-4 | Tenant-Scoped Workflow Instances | 🟡 | `WorkflowRepository` + `workflow_instances.TenantId`. Elsa still owns instance lifecycle separately. |
| 17-5 | API Tenant Context Middleware | ✅ | `TenantContextMiddleware.cs` + `EnsurePersonalTenantMiddleware.cs`. Path-based bypass list. |
| 27-1 | Prompt Store Schema | ✅ | `prompt_overrides` table in `20260416172234_InitialSchema`. |
| 27-2 | Prompt Store Service | 🔴 | TS service deleted. C# `PromptRepository` is CRUD only. 80+8+10 system defaults exist only as stubbed strings in `PromptEndpoints.GetSystemDefault` ("Default template for {role}/{action}"). |
| 9-1 | Agent Config Schema + API | 🟡 | `AgentConfigRepository` + `AgentEndpoints`. No validation schema, no merge logic, no role→phase mapping. |
| 16-3 | Admin Dashboard | ✅ | `packages/dashboard/` — survived Epic 19. |
| 16-4 | Unified Navigation Header | ✅ | `docker/nav-header/` + `NavHeader.tsx` — survived. |
| 12-5c | Mentorship Skill-Level Fix | ✅ | `apps/tamma-elsa/.../MentorshipWorkflow.cs` |
| 12-5e | CI Retry Counter Fix | ✅ | `apps/tamma-elsa/.../CiWithDebugRetryWorkflow.cs` |

### Layer 3

| Story | Title | Status | Where it lives now |
|-------|-------|--------|-------|
| 9-2 | Diagnostics Service + API | 🟡 | `DiagnosticsRepository` + `ProviderEndpoints.QueryDiagnostics / GetReport / GetBudget`. Query filter + aggregation logic that the TS version had is gone. |
| 9-3 | Health Tracker Service + API | 🟡 | `ProviderHealthRepository` + `ProviderEndpoints.Record{Failure,Success}`. Circuit-breaker state machine (open/half-open/closed) from TS is missing. |
| 9-4 | Provider Factory API | 🟡 | `ProviderEndpoints.CreateProvider / ExecuteProvider / DeleteProvider`. Handle-based registry + provider-type dispatch logic lost. |
| 9-7 | Sanitization Service + API | 🟡 | `SanitizationRepository` + `SettingsEndpoints.Sanitize`. Rule engine (regex patterns, replacement strategy) reduced to JSON blob storage. |
| 9-8 | Unified Agent Resolver API | 🔴 | TS `RoleBasedAgentResolver` (commit `ce8840e`) deleted with `packages/api`. C# has no equivalent endpoint. |
| 27-3 | Prompt Store API Endpoints | 🟡 | `PromptEndpoints` — 10 routes. Render endpoint is a stub; no event sourcing; system-defaults list returns `{ message: "stub" }`. |
| 27-6 | Elsa Workflow Integration | ✅ | Lives in `apps/tamma-elsa/.../PromptStore*.cs` (commit `5ea8a96`). |
| 27-7 | Prompt Store Event Sourcing | 🔴 | TS implementation deleted. C# `EventRepository` has no prompt-specific append or replay helpers. |
| 18-1 | User Registration + Email | ✅ | `AuthEndpoints.Register` — creates user + personal tenant + membership. No email sending yet (verification token generated but never mailed). |
| 18-2 | Login + Session Management | ✅ | `AuthEndpoints.Login / Refresh / Logout` — JWT + refresh cookie. |
| 18-3 | Org/Tenant Creation | ✅ | `OrgEndpoints` — 11 endpoints (create, list, invite, transfer, delete). |
| 18-6 | Password Reset | 🟡 | `AuthEndpoints.PasswordResetRequest / Confirm`. Request side is still a stub that never emits a token; confirm side is real. |

## Hardening punch list (pre-Layer-4)

Rank-ordered by Layer 4 dependency urgency. These tasks restore the depth
that was lost when `packages/api/` was deleted. Without them, Layer 4
Team A and Team D cannot integrate meaningfully.

| # | Task | Hours | Blocks Layer 4 story |
|---|------|-------|----------------------|
| 1 | Port 80+8+10 system prompt defaults from `default-prompts.ts` → `Tamma.Api/Auth/SystemPrompts.cs` (static registry); wire `PromptEndpoints.GetSystemDefault` + `ListSystemDefaults` to it. | 8 | 27-4, 27-5 |
| 2 | Implement `GET /api/v1/agents/{role}/resolve` + `POST /api/v1/agents/resolve-for-phase` with the TS resolver's config-merge + role→phase mapping. | 10 | 9-9, 9-10, 9-12 |
| 3 | Implement provider chain resolve: `POST /api/v1/providers/chain/resolve` returning ordered providers based on health + config. | 8 | 9-5, 9-11 |
| 4 | Promote health tracker to a real circuit breaker (open after N failures in window, half-open probe after cooldown). | 8 | 9-5, 9-9 |
| 5 | Implement diagnostics aggregation: time-bucketed report, per-provider budget enforcement. | 10 | 9-11, 9-12 |
| 6 | Implement prompt render endpoint properly (variable substitution, system+user template merge, tool enablement flag). | 6 | 27-4, 27-5, 12-7e |
| 7 | Port sanitization rule engine from `packages/shared/src/security/` (already C#-compatible per Layer 2 track notes) into the Settings/Sanitize endpoint flow. | 6 | 12-7e |
| 8 | Wire email sending for registration + password reset (even if just logged in dev, real SMTP in prod). | 4 | 18-4, 18-5 |
| **Total** | | **60** | |

Notes:
- Tasks 1–3 are the unblocking set for Team A. Do these first.
- Tasks 4–5 extend #2/#3 and can run in parallel with Team A Layer 4.
- Tasks 6–7 unblock Team B + Team D.
- Task 8 is a Team C gap but can run any time before 18-5 ships.

## What actually needs to happen before Layer 4 starts

1. **Merge PR #328.** Everything above assumes `feat/auth-foundation` is
   merged to `main`. Currently OPEN, CI green. Rebase + merge.
2. **Run the hardening punch list.** Track as "Layer 3.5" or a single
   pre-Layer-4 PR. ~60h of work, one engineer ≈ 1.5 weeks, two engineers
   ≈ 4–5 days.
3. **Only then** kick off Layer 4 teams per the revised
   [`layer-4-integration-ui.md`](./layer-4-integration-ui.md).

## What this document does NOT replace

- Individual story files in `docs/stories/epic-{n}/` — those describe the
  *desired* feature shape, which hasn't changed.
- The [`remaining-layer-2-3-execution.md`](./remaining-layer-2-3-execution.md)
  parallel plan — that document is now historical; the punch list above is
  the current source of truth.

## Punch list completion status

**Date:** 2026-04-18
**Verifier:** auth-foundation hardening agent (post-audit-remediation pass)
**Baseline:** 1731 tests passing (842 Api + 882 Activities + 7 Core), build green.

The 8-task punch list above was effectively closed by the audit-remediation
work landed across ~47 commits in the auth-foundation session. This pass
verified each task end-to-end against the C# code, confirmed test coverage,
and documents the closing commit(s) below. **No new code was written in this
pass** — all eight tasks were already complete. The punch list is now CLOSED;
Layer 4 is unblocked.

| # | Task | Outcome | Closing commit(s) | Notes |
|---|------|---------|-------------------|-------|
| 1 | System prompt defaults (80+8+10) → SystemPrompts.cs registry + /defaults endpoints | Already-done | `ea4d5e5` (prompts P1 fixes), `d72c541` (initial port) | `Tamma.Api/Auth/SystemPrompts.cs` ports the full TS `default-prompts.ts` registry: 8 role identities, 10 action-defaults, 80 role+action templates. `PromptEndpoints.GetSystemDefault` / `GetActionDefault` / `ListSystemDefaults` are all wired and bound under `GET /api/prompts/defaults`, `GET /api/prompts/defaults/{action}`, `GET /api/prompts/defaults/{role}/{action}`. Test coverage: `PromptStore/SystemPromptsTests.cs` (249 LoC, 80-pair parameterised tests + lookups). |
| 2 | Agent resolver: GET /api/v1/agents/{role}/resolve, POST /resolve-for-phase | Already-done | `7fadaa1` (initial), `ccfff64` (real wiring), `498889b` (P0 normalise + role keys) | `AgentResolverService` walks platform-default → tenant-override (3-level merge from `agent_configs.config` JSONB), with `RolePhaseMap` providing role↔phase eligibility, `LegacyRoleAliases` and `LegacyPhaseAliases` normalisers (finding 001), and per-task clamping for budget/tools/permissionMode (finding 007). Endpoints `MapGet("/{role}/resolve")` and `MapPost("/resolve-for-phase")` wired at `Program.cs:732-733`. Test coverage: `AgentResolverServiceTests.cs` (294 LoC), `AgentEndpointsIntegrationTests.cs` (191 LoC), `RolePhaseMapTests.cs` (183 LoC). |
| 3 | Provider chain: POST /api/v1/providers/chain/resolve | Already-done | `1bce71c` (port), `32bba50` (P1 chain shape — finding 011) | `ProviderChainResolver` reads `chains[role][action] → chains[role][default] → chains[role] → chains[default]`, plus legacy TS shape (`roles.{role}.providerChain`, `defaults.providerChain`). Healthy/Unknown providers come first; HalfOpen probes appended at tail; Open providers excluded. Endpoint `MapPost("/chain/resolve")` wired at `Program.cs:805` (group `/api/providers`). Test coverage: `ProviderChainResolverTests.cs` (311 LoC), integration tests in `ProviderHealthEndpointsIntegrationTests.cs` (4 ResolveChain scenarios incl. circuit-open exclusion + half-open promotion). |
| 4 | Circuit breaker state machine (open/half-open/closed) | Already-done | `1bce71c` (port), `0dbccf9` (validation/health fixes) | `CircuitBreakerService` is a real state machine persisted to `provider_health` table — sliding failure window, configurable `FailureThreshold` + `CooldownDuration`, atomic Open→HalfOpen auto-promotion via `ISystemClock`, `TryProbeAsync` for HalfOpen consent, and Reset endpoint. Test coverage: `CircuitBreakerStateMachineTests.cs` (343 LoC). |
| 5 | Diagnostics aggregation: time-bucketed reports + per-provider budget | Already-done | `2fe9d6e` (initial merge), `0dbccf9` (P1/P2 batches), `f355c1a` (Postgres budget persistence) | `DiagnosticsService.GetReportAsync` returns `DiagnosticsReport` with 5-minute / hour / day buckets via repository `AggregateAsync`. `GetDimensionReportAsync` groups by Provider/Model/AgentType server-side. `GetBudgetAsync` reads per-account budget config (now Postgres-backed via `IBudgetConfigProvider`/`PostgresBudgetConfigProvider`) and computes alert/over-budget signals. Test coverage: `DiagnosticsAggregationTests.cs` (269 LoC), `BudgetServiceTests.cs` (231 LoC), `BudgetConfigRepositoryTests.cs` (203 LoC), `DiagnosticsEndpointsIntegrationTests.cs` (223 LoC). |
| 6 | Prompt render endpoint (variable substitution, system+user merge, tool flag) | Already-done | `ea4d5e5` (8-field RenderResponse + render contract align — finding 003) | `PromptEndpoints.RenderPrompt` resolves through the 4-layer model, calls `PromptStoreService.RenderFull` for system+user template merge, returns `RenderedPromptResponse(Role, Action, Version, RenderedTemplate, RenderedSystemPrompt, EnableTools, MaxTokens, UnresolvedVariables[])` matching the TS `RenderedPrompt` contract. Single-pass substitution; unresolved variables tracked, not eagerly thrown. Test coverage: `PromptRenderTests.cs` (158 LoC), `PromptEndpointsIntegrationTests.cs` (150 LoC) including render-route assertions. |
| 7 | Sanitization rule engine port from packages/shared/src/security/ | Already-done | `32bba50` (ContentSanitizer port — finding 006) | `Tamma.Api/Services/Sanitization/ContentSanitizer.cs` is a direct ~360-LoC port of `packages/shared/src/security/content-sanitizer.ts` (TS source 408 LoC at commit `9e9a57c~1`). Preserves all six Story 9-7 AC 6 behaviours: HTML quote-aware strip, zero-width Unicode removal (incl. CVE-2021-42574 bidi overrides), 5-category prompt-injection detection, NFKD normalisation, asymmetric input/output pipelines (output preserves fenced code), null-byte hard-floor strip. Wired through `SanitizationService` + `SettingsEndpoints.Sanitize`. Test coverage: `ContentSanitizerTests.cs` (151 LoC), `SanitizationServiceTests.cs` (291 LoC), `SanitizationEndpointsIntegrationTests.cs` (166 LoC), `SanitizationRegexTimeoutTests.cs` (50 LoC ReDoS guard). |
| 8 | Email sending wiring (SMTP) for registration + password reset | Already-done | `218c746` (initial Email service), `e809f5b` (SMTP outbox rewrite), `7d24fe8` (OutboxSmtpSender), `34b1d4e` (Resend HTTP), `c6601a6`/`6aa7594`/`93300eb`/`0b086cd` (CodeQL log-injection hardening) | `AuthEndpoints.Register` enqueues a verification email via `IEmailService.SendAsync`; `AuthEndpoints.PasswordResetRequest` enqueues a reset email; `AuthEndpoints.ResendVerification` enqueues a re-verify email — all three wired and rate-limited. `AddEmailServices()` extension picks `smtp` (default, outbox + `OutboxSmtpSender` background drain via `MailKitSmtpTransport`) / `resend` (HTTP) / `inmemory` (dev fallback) by `Email:Provider` config. Test coverage: `AuthRegisterEmailIntegrationTests.cs`, `PasswordResetEmailIntegrationTests.cs`, `OutboxSmtpSenderTests.cs` (255 LoC), `SmtpEmailServiceOutboxTests.cs`, `ResendEmailServiceTests.cs`, `InMemoryEmailServiceTests.cs`, `EmailOutboxRepositoryTests.cs`, `EmailTemplatesTests.cs` — 1622 LoC across 10 email test files. |

**Punch-list test totals (the eight task areas):** 361 tests passing (subset
of the 1731 baseline), 0 failures, 0 skips.

**Gaps verified absent:** searched for `TODO|FIXME|STUB|HACK|NotImplementedException`
across all six punch-list service trees (`Services/{Agents,Providers,Diagnostics,
PromptStore,Email,Sanitization}/`) — zero matches.

**Drift discovered:** none — the audit remediation incidentally and
comprehensively closed the punch list. The pre-Layer-4 hardening pass
forecast 60 hours; the audit work absorbed all of it.

**Conclusion:** All 8 tasks are Done (closed by audit-remediation work, no
additional commits in this pass). Layer 4 may proceed without further
hardening on this list.

## Related

- Epic 19 rewrite: [`../epic-19/19-1-api-consolidation-to-csharp.md`](../epic-19/19-1-api-consolidation-to-csharp.md)
- Phase 2+3 impl plan: [`../epic-19/19-1-phase-2-3-impl-plan.md`](../epic-19/19-1-phase-2-3-impl-plan.md)
- Revised Layer 4 plan: [`layer-4-integration-ui.md`](./layer-4-integration-ui.md)

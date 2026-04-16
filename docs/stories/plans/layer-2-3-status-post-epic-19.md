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

## Related

- Epic 19 rewrite: [`../epic-19/19-1-api-consolidation-to-csharp.md`](../epic-19/19-1-api-consolidation-to-csharp.md)
- Phase 2+3 impl plan: [`../epic-19/19-1-phase-2-3-impl-plan.md`](../epic-19/19-1-phase-2-3-impl-plan.md)
- Revised Layer 4 plan: [`layer-4-integration-ui.md`](./layer-4-integration-ui.md)

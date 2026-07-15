# Epic 4: Event Sourcing & Audit Trail

**Status:** Near Complete (7/8 done; 4-2 in progress; 4-8 black-box replay landed 2026-07-05 as an API endpoint)
**Stories:** 8 (4-1 through 4-8)
**Tech Spec:** [tech-spec-epic-4.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-4/tech-spec-epic-4.md)

## Overview

Epic 4 gives Tamma a **100% audit trail**. Every user action, every AI call, every Git operation, every approval, every escalation is captured as an immutable event with millisecond-precision timestamps and enough context to reconstruct why the system did what it did. The event stream is the source of truth for compliance (SOC2, ISO27001, GDPR), for **time-travel debugging** (show me the state of issue #123 at 14:32:07 yesterday), and for **black-box replay** (reproduce this bug from the exact sequence of events that led to it).

The epic adopts the **DCB (Dynamic Consistency Boundary)** event-sourcing pattern rather than aggregate-per-stream. DCB uses a *single* event stream with JSONB `tags` per event — `{ tenantId, issueId, prId, userId, provider, mode }` — so any cross-aggregate query ("all events for issue 123 across all tenants / all providers involved") is a single indexed query rather than a fan-out across aggregate streams. This keeps the audit log simple (one table), keeps cross-cutting queries fast, and keeps extensions cheap — new event types just start setting new tags.

Storage is PostgreSQL. The original brief called for the TypeScript **Emmett** library, but the production deployment landed on a custom EF Core implementation in the .NET tree: `Tamma.Data.Entities.DomainEvent` + `EventRepository` + `TammaActivity` auto-emission. The TypeScript `@tamma/events` package is a placeholder; `@tamma/shared/event-store.ts` ships an `InMemoryEventStore` for tests. In production every Elsa activity that inherits `TammaActivity` / `TammaAsyncActivity` / `TammaOutcomeActivity` emits start + end events automatically, so the engineer writing an activity never has to remember to emit — it's free with the base class.

## Architecture

Events flow through three layers. At the bottom, `Tamma.Activities.Core.TammaActivity` wraps every Elsa activity with a `try { emit START } ... finally { emit END }` block. The emitted `TammaEvent` carries activity id, workflow instance id, duration, status (success / failed), and an activity-specific data dictionary. The C# `IEventRepository` persists those events as `DomainEvent` rows; the row has a `Tags` JSONB column that lets the query API filter without a schema migration.

The middle layer is the **query API** (Story 4-7): `GET /api/v1/events?since=&until=&type=&correlationId=&issueNumber=` returns chronologically-ordered pages (default 100 events). The `TenantDbContext` applies a row-level-security filter per tenant so operators querying across tenants only see their own data.

The top layer is **replay** (Story 4-8, landed 2026-07-05): `GET /api/engine/runs/{correlationId}/replay?upTo={seq|timestamp}&from={seq}` reconstructs a run's point-in-time state as a **pure, deterministic left-fold** over the run's ordered DCB event slice (`ReplayReconstructor` — no I/O, no clock, no re-execution, no writes). `upTo` slices by sequence number or ISO-8601 timestamp (omitted = whole run; a point before the run began is a valid empty-state view, not a 404); `from` adds a `ReplayDelta` diff of everything after that point. The read is tenant-scoped and fails closed — a null tenant or another tenant's correlationId returns 404, never leaked events.

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `DomainEvent` entity | Immutable event row: `id`, `type`, `tenantId`, `issueNumber`, JSONB tags / metadata / data, `createdAt` | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | Done (4-1) |
| `IEventRepository` | Append + query API over `DomainEvent` | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` | Done (4-7) |
| `EventRepository` | EF Core implementation (PostgreSQL) | `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` | Done |
| `TammaActivity` base class | Auto-emits start/end events for every inheriting activity | `Tamma.Activities/Core/TammaActivity.cs` | Done |
| `TammaAsyncActivity` | Async variant | `Tamma.Activities/Core/TammaActivity.cs` | Done |
| `TammaOutcomeActivity` | Multi-outcome variant | `Tamma.Activities/Core/TammaActivity.cs` | Done |
| `TammaEvent` DTO | Shared event shape (type, status, duration, data) | `Tamma.Activities/Core/TammaActivity.cs` | Done |
| Tenant-scoped query filter | Row-level filter by `TenantId` in `TenantDbContext` | `Tamma.Data/TenantDbContext.cs` | Done |
| Query endpoints | `GET /api/v1/events`, `GET /api/v1/events/:id` | `apps/tamma-elsa/src/Tamma.Api/Endpoints/Events/*` | Done (4-7) |
| TypeScript `IEventStore` | In-process store used by `TammaEngine` for the CLI path | `packages/shared/src/event-store.ts`, `types/index.ts` | Done (partial) |
| `@tamma/events` package | Placeholder — full TS DCB impl not landed | `packages/events/src/index.ts` | Stub |
| Issue-selection events | `ISSUE.SELECTED.SUCCESS`, `ISSUE.ANALYZED.SUCCESS` | emitted by `SelectWorkItemActivity`, `ValidateWorkItemActivity` | Done (4-3) |
| AI-provider events | `AI.REQUEST.*`, `AI.RESPONSE.*`, token usage, cost | emitted by `InstrumentedAgentProvider`, `InstrumentedLlmProvider` | Done (4-4) |
| Code / Git events | `CODE.GENERATED.*`, `CODE.REFACTORED.*`, `COMMIT.CREATED.*`, `BRANCH.CREATED.*`, `PR.CREATED.*`, `PR.MERGED.*` | `Tamma.Activities/TDD/CodeEvents.cs`, `CommitEvents.cs` + `WriteTestsActivity`, `WriteImplementationActivity`, `ApplyRefactoringActivity`, `CommitChangesActivity`; plus `CreateBranchActivity`, `CreatePullRequestActivity`, `MergePullRequestActivity` | Done (4-5) |
| Approval / escalation events | `APPROVAL.REQUESTED`, `APPROVAL.PROVIDED`, `ESCALATION.TRIGGERED`, `ESCALATION.RESOLVED` | emitted from `WaitForPlanApprovalActivity`, `EscalateToSeniorActivity` | Done (4-6) |
| Replay endpoint | `GET /api/engine/runs/{correlationId}/replay` — point-in-time reconstruction via pure fold | `Tamma.Api/Services/Engine/Replay/*` (`ReplayReconstructor`, `ReplayService`, `ReplayModels`), `Tamma.Api/Endpoints/EngineEndpoints.cs` | Done (4-8) |
| Replay CLI / HTML export | `tamma replay --correlation-id ...` interactive mode + formatted report | Drafted | Drafted (4-8 follow-on) |

## Class diagram

```
   DomainEvent  (entity)
   + Id : Guid
   + Type : string              e.g. "CODE.GENERATED.SUCCESS"
   + TenantId? : Guid
   + IssueNumber? : int
   + Tags : string (JSONB)      { issueId, prId, userId, provider, mode, ... }
   + Metadata : string (JSONB)  { workflowVersion, eventSource }
   + Data : string (JSONB)      payload specific to event type
   + CreatedAt : DateTime

   IEventRepository  <<interface>>
   + AppendAsync(evt) : Task<DomainEvent>
   + GetByIdAsync(id) : Task<DomainEvent?>
   + QueryAsync(tenantId?, type?, issueNumber?, limit) : Task<List<DomainEvent>>
   + GetLastByTypeAsync(tenantId, type) : Task<DomainEvent?>
        ^
        |
   EventRepository  (EF Core + PostgreSQL)
   - db : TammaDbContext


   ITammaActivity  <<interface>>
   + EventType : string?        e.g. "CONTEXT.GATHER"
   + BuildStartData(ctx) : Dict<string, object?>
   + BuildEndData(ctx) : Dict<string, object?>
         ^
         |
   +--- TammaActivity (sync)
   +--- TammaAsyncActivity (async, wraps try/finally around ExecuteAsync)
   +--- TammaOutcomeActivity (multi-outcome branching)
         |
         |  on execute: emit START event
         |  run body
         |  emit END event (success|failed + duration + data)
         v
     DomainEvent row inserted via IEventRepository


   Query API (Fastify/.NET endpoints)
   GET /api/v1/events?since=&until=&type=&correlationId=&issueNumber=
   GET /api/v1/events/:id
        |
        | applies row-level tenant filter
        v
   TenantDbContext  (global query filter on TenantId)


   TypeScript mirror (CLI mode)
   IEventStore <<interface>>
   + record(evt)
   + getEvents(tenantId, issueNumber?)
   + getLastEvent(tenantId, type)
   + clear(tenantId)
        ^
        |
   InMemoryEventStore   (packages/shared — tests + CLI mode)
   @tamma/events        (stub — production EF path is authoritative)
```

## Data flow — "one issue cycle emits its event trail" sequence

```
Elsa Workflow                TammaActivity             IEventRepository       Postgres domain_events
     |                             |                         |                        |
     | begin activity              |                         |                        |
     |---------------------------> |                         |                        |
     |                             | emit START              |                        |
     |                             | { type: "CODE.GENERATE",|                        |
     |                             |   status: "start",      |                        |
     |                             |   activityId,           |                        |
     |                             |   workflowInstanceId,   |                        |
     |                             |   tags: { issueNumber: 123, tenantId: ... }  }  |
     |                             |------------------------>| AppendAsync(evt)       |
     |                             |                         |-----------------------> INSERT row
     |                             |                         |<----------------------- ok
     |                             |<------------------------|                        |
     |                             |                         |                        |
     |                             | [body runs]             |                        |
     |                             |  - calls IAgentProvider |                        |
     |                             |  - writes files         |                        |
     |                             |  - commits              |                        |
     |                             |                         |                        |
     |                             | emit END                |                        |
     |                             | { type: "CODE.GENERATED.SUCCESS",                |
     |                             |   status: "success",    |                        |
     |                             |   duration: 12.3s,      |                        |
     |                             |   data: { filesChanged: [...], tokens: {...} } } |
     |                             |------------------------>| AppendAsync(evt)       |
     |                             |                         |-----------------------> INSERT row
     |                             |                         |<----------------------- ok
     |<----------------------------|                         |                        |


 Later:  Operator queries for the issue timeline
     |
     |  GET /api/v1/events?issueNumber=123&since=2026-04-22T14:00Z
     |
     v
  Tamma.Api.Endpoints.Events ─── IEventRepository.QueryAsync(tenantId, null, 123, 100)
                                  (TenantDbContext applies tenant scoping)
     |
     v
  JSON list:
  [
    { type: "ISSUE.SELECTED.SUCCESS",   timestamp, tags, data },
    { type: "CONTEXT.GATHER.SUCCESS",   timestamp, tags, data },
    { type: "PLAN.GENERATED.SUCCESS",   timestamp, tags, data },
    { type: "APPROVAL.REQUESTED",       timestamp, tags, data },
    { type: "APPROVAL.PROVIDED",        timestamp, tags, data },
    { type: "BRANCH.CREATED.SUCCESS",   timestamp, tags, data },
    { type: "CODE.GENERATED.SUCCESS",   timestamp, tags, data },
    { type: "PR.CREATED.SUCCESS",       timestamp, tags, data },
    { type: "CI.FAILED.ATTEMPT_1",      timestamp, tags, data },
    { type: "AI.DIAGNOSIS.SUCCESS",     timestamp, tags, data },
    { type: "CI.PASSED.SUCCESS",        timestamp, tags, data },
    { type: "PR.MERGED.SUCCESS",        timestamp, tags, data }
  ]
```

## Use cases

- **Compliance officer** wants **to prove a PR was merged only after human approval**: query `/api/v1/events?prId=456&type=APPROVAL.PROVIDED` → confirm event exists → query same PR for `PR.MERGED.SUCCESS` → confirm the approval timestamp is before the merge timestamp.
- **Incident responder** wants **to reconstruct the state of a failing issue at 14:32:07**: query `/api/v1/events?issueNumber=123&until=2026-04-22T14:32:07Z` → project the event sequence into state → identify which retry attempt was running, what provider was selected, what tokens were spent.
- **Developer debugging a flaky bug** wants **to replay events exactly**: `GET /api/engine/runs/<correlationId>/replay?upTo=<seq>` → the pure fold reconstructs the run's state at that exact event → see which step was running, what succeeded/failed and the recorded payloads → diagnose without re-running cost-bearing AI calls (Story 4-8; a `from=` param adds a delta diff between two points).
- **Cost auditor** wants **monthly AI spend by provider**: query `?type=AI.RESPONSE.SUCCESS&since=<start-of-month>` → sum `data.usage.cost` grouped by `tags.provider` → cross-check against cost-monitor storage.
- **Platform admin** wants **to see everything a specific user did this week**: query `?userId=<uuid>&since=<7-days-ago>` → timeline of their actions across issues, PRs, approvals — all from the one table, no joins.
- **Plugin developer** wants **to add a custom event type**: set `EventType = "PLUGIN.MY_ACTION.SUCCESS"` on the activity class + set tags — no schema migration needed; the JSONB columns accept arbitrary shapes.

## Dependencies

**Upstream:**
- [Epic 1](Epic-1-Foundation.md) — interfaces to instrument.
- [Epic 2](Epic-2-Autonomous-Loop.md) — every loop step emits events.
- [Epic 3](Epic-3-Quality-Gates.md) — gate outcomes + escalations emit events.

**Downstream:**
- [Epic 5](Epic-5-Observability.md) — dashboards consume the event query API; Event Trail Exploration UI (5-5) reads from here.
- [Epic 10](Epic-10-Engine-Core.md) — owns the production event-store implementation and the JSONB indexing scheme.
- [Epic 11](Epic-11-Security.md) — audit events are the foundation of security monitoring.
- [Epic 15](Epic-15-Log-Aggregation.md) — OpenSearch optionally indexes events for search.
- [Epic 17](Epic-17-Multi-Tenancy.md) — tenant scoping on event rows is enforced by the `TenantDbContext` filter.

## Current state

**Landed:**

- Event schema (4-1) — `DomainEvent` EF entity with JSONB `tags` / `metadata` / `data` columns.
- Event store (partial, 4-2) — EF / PostgreSQL path via `EventRepository`. The brief picked Emmett; actual impl is EF + Postgres with the same shape.
- Issue-selection + analysis events (4-3) — emitted by `SelectWorkItemActivity`, `ValidateWorkItemActivity`.
- AI-provider events (4-4) — `InstrumentedAgentProvider` and `InstrumentedLlmProvider` decorators in `@tamma/providers` capture request/response with token usage.
- Code + Git events (4-5) — completed 2026-07-05: the TDD activities (`WriteTestsActivity`, `WriteImplementationActivity`, `ApplyRefactoringActivity`, `CommitChangesActivity`) now emit typed `CODE.GENERATED.SUCCESS/FAILED`, `CODE.REFACTORED.SUCCESS/FAILED` (with an `operation` tag: implementation / testing / refactoring) and `COMMIT.CREATED.SUCCESS/FAILED` events via `TDD/CodeEvents.cs` + `CommitEvents.cs`, alongside the existing branch/PR/merge events from `CreateBranchActivity`, `CreatePullRequestActivity`, `MergePullRequestActivity`.
- Approval + escalation events (4-6) — `WaitForPlanApprovalActivity`, `EscalateToSeniorActivity`.
- Event query API (4-7) — `GET /api/v1/events` endpoints in `Tamma.Api/Endpoints/Events/`; `IEventRepository.ListByCorrelationIdAsync` returns a run's full ordered event slice (with an empty-tenant guard added in the 2026-07-05 read-endpoint hardening).
- Black-box replay (4-8) — landed 2026-07-05: `ReplayReconstructor` (pure deterministic fold over the DCB event slice) + `ReplayService` + `GET /api/engine/runs/{correlationId}/replay`. Tenant-scoped, read-only, supports `upTo` (sequence or timestamp) point-in-time views and `from` delta diffs. A follow-up hardening pass (same day) added UTC date-bound normalization, `from > upTo` → 400 validation, and bounded run-event fetches.

**Stubbed / drafted:**

- 4-8 CLI surface (`tamma replay --interactive`) and the HTML report export are not implemented — replay shipped as the API endpoint above.
- `@tamma/events` TypeScript package is a placeholder. `@tamma/shared/event-store.ts` ships `InMemoryEventStore` for the CLI path.

**Drift from briefs:**

- The original 4-2 picked Emmett as the event-store library. Actual implementation uses EF Core + PostgreSQL directly — same DCB shape (single stream + JSONB tags), different plumbing. This matches the broader "single source of truth in C# / Elsa" project decision.
- The brief references `workflowVersion` metadata; implementation uses Elsa's workflow version concept rather than a hand-rolled metadata field, and adds `activityId` + `workflowInstanceId` to every event automatically via `TammaActivity`.
- Epic 10 Story 10-3 is where the production event store actually landed; Epic 4 stories 4-1..4-7 are the *spec*. The overlap is documented but the work lives in the Epic 10 tree.
- The 4-8 brief described replay as a CLI command with interactive stepping and HTML export; what shipped is an HTTP endpoint over a pure reconstruction core (`ReplayReconstructor`) — same "project events, never re-execute" semantics, different surface.
- Retention policy (brief mentions 6 months hot + 2 years archival) is not yet enforced — retention story is implicit in Epic 10 ops scope.

## See also

- **Docs:** [docs/stories/epic-4/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-4) — all 8 story briefs + context XML.
- **Tech spec:** [tech-spec-epic-4.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-4/tech-spec-epic-4.md).
- **Related wiki pages:**
  - [Architecture](Architecture) — DCB event-sourcing pattern.
  - [Epic 10: Engine Core](Epic-10-Engine-Core.md) — production event-store implementation.
  - [Epic 5: Observability](Epic-5-Observability.md) — consumer of the event query API.
  - [Epic 11: Security](Epic-11-Security.md) — audit-trail use.
  - [Epic 17: Multi-Tenancy](Epic-17-Multi-Tenancy.md) — tenant scoping on events.
- **Code paths:**
  - `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` — event schema.
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` — EF Core event store.
  - `apps/tamma-elsa/src/Tamma.Activities/Core/TammaActivity.cs` — auto-emission base class.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/Events/` — query API.
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/Replay/` — black-box replay (4-8).
  - `apps/tamma-elsa/src/Tamma.Activities/TDD/CodeEvents.cs`, `CommitEvents.cs` — code/git event coverage (4-5).
  - `packages/shared/src/event-store.ts` — TS interface + in-memory impl.
  - `packages/providers/src/instrumented-agent-provider.ts`, `instrumented-llm-provider.ts` — AI event instrumentation.

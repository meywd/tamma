# Event Schema & Catalog (Epic 4 — Story 4-1)

_Last updated: 2026-07-15._

This page is the maintainable reference for Tamma's **DCB (Dynamic Consistency Boundary)** event schema and the catalog of event **types** the running system emits. It is the shipped answer to [Story 4-1: Event Schema Design](https://github.com/meywd/tamma/blob/main/docs/stories/epic-4/story-4-1/4-1-event-schema-design.md) — re-based on the **current C# stack**. The original brief sketched a TypeScript `BaseEvent`/`Emmett` design; production landed on a custom EF Core implementation. Where the two differ, this page describes what the code actually does and maps it back to the story's acceptance criteria in [§9](#9-acceptance-criteria--shipped-schema).

Every claim here is grounded in a file you can open. Pair it with [Architecture](Architecture) for the wider system map and the [Epic 4: Event Sourcing](Epics/Epic-4-Event-Sourcing) page for "why it exists".

---

## 1. Where it lives

| Concern | File |
|---|---|
| Event row (entity) | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` |
| Table + JSONB + index config | `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (`domain_events` block) |
| Tenant-scoped store (write/read) | `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` + `IEventRepository.cs` |
| Platform-scope store (tenant-less) | `apps/tamma-elsa/src/Tamma.Data/Repositories/PlatformEventRepository.cs` (`platform_events`) |
| Workflow event model + emitter | `apps/tamma-elsa/src/Tamma.Activities/Core/TammaActivity.cs` (`TammaEvent`, `TammaEventEmitter`) |
| Engine drain ingestion | `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs` (`POST /api/engine/events`) |
| Time-travel read | `EngineEndpoints.GetHistory` → `GET /api/engine/history` (Story 4-7) |
| Event-type catalogs | `**/*Events.cs` (33 classes) + `**/*EventTypes.cs` (21 classes) + inline audit catalogs |
| Black-box replay (fold + endpoint) | `apps/tamma-elsa/src/Tamma.Api/Services/Engine/Replay/{ReplayReconstructor,ReplayService,ReplayModels}.cs` → `GET /api/engine/runs/{correlationId}/replay` (Story 4-8) |

**Two physical streams, one logical model.** Tenant-scoped events live in each tenant's `t_<hex>.domain_events` table (schema-per-tenant isolation). Platform-lifecycle events that have no tenant (`TenantId == null` — e.g. `TENANT.PROVISIONED.SUCCESS`, `EMAIL.QUEUED.SUCCESS`) live in the control-plane `platform_events` table and are projected back into `DomainEvent` shape on read so callers see one model. See the class comment on `EventRepository` for the routing matrix (Story 28-1 Decision #2).

---

## 2. The `DomainEvent` row

The persisted event (`Tamma.Data.Entities.DomainEvent`) is deliberately narrow: a few first-class columns for the hot query paths, and three JSONB blobs for everything flexible.

| Field | Type | Notes |
|---|---|---|
| `Id` | `uuid` (PK) | Stable per-event id. Minted engine-side at emit time (currently **UUID v4** — .NET 8 has no `Guid.CreateVersion7`), or server-side `gen_random_uuid()` default. Idempotency key: the append is `ON CONFLICT (Id) DO NOTHING`. |
| `Type` | `text` (≤255) | The event type, `AGGREGATE.ACTION.STATUS` (see [§3](#3-naming-convention)). |
| `TenantId` | `uuid?` | Authoritative tenant scope. `null` = platform-scope (routed to `platform_events`). |
| `IssueNumber` | `int?` | First-class column (not just a tag) — the issue-scoped audit view filters on it directly. |
| `Tags` | `jsonb` | Flexible, queryable DCB index keys (see [§4](#4-tags-taxonomy)). Default `'{}'`. |
| `Metadata` | `jsonb` | Envelope: `workflowVersion`, `eventSource`, optional `error` (see [§5](#5-metadata)). |
| `Data` | `jsonb` | Event-specific payload. Default `'{}'`. |
| `CreatedAt` | `timestamptz` | Stamped **server-side** by the repository for a monotonic store clock. The engine's own emit timestamp is preserved in `Tags.emittedAt`. |
| `SequenceNumber` | `bigint` (BIGSERIAL) | Monotonic per-stream total order. The tiebreak cursor for pagination/replay — **never** `Id`, and never `CreatedAt` (which collides within a millisecond). |

The in-flight workflow event (`TammaEvent`) is a richer shape (`ActivityId`, `WorkflowInstanceId`, `Duration`, `Status`) that the drain flattens into the row above — the activity/workflow identifiers and status land in `Tags`, the duration in `Tags.durationMs`, the payload in `Data`.

---

## 3. Naming convention

Every event type follows **`AGGREGATE.ACTION.STATUS`**:

```
DEPLOY.STAGE.SUCCESS       AGGREGATE=DEPLOY  ACTION=STAGE     STATUS=SUCCESS
PR.CREATED.FAILED          AGGREGATE=PR      ACTION=CREATED   STATUS=FAILED
AGENT.TASK.PARTIAL         AGGREGATE=AGENT   ACTION=TASK      STATUS=PARTIAL
AUDIT.CHAIN.TAMPER_DETECTED
```

Rules the codebase holds to:

- **SCREAMING_SNAKE segments**, dot-separated. Multi-word segments use `_` (`ISSUE_STATUS`, `RESULTS_COLLECTED`, `TRIAL_ENDING`).
- **Terminal status** is normally `SUCCESS` / `FAILED` (and sometimes `PARTIAL`, `SKIPPED`, `INVALID`, `EMPTY`, `ESCALATED`, `STARTED`). A few lifecycle events are two-segment (`CYCLE.STARTED`, `MERGE.REQUESTED`, `AUDIT.QUERIED`) — the "status" is implicit in the action.
- **`*.FAILED` / `*.REJECTED` are loud** — a failed operation is always its own audit row, never a silent success. `DeployEvents.IsFailureType` is the pattern: catalogs expose a helper that classifies which types are failures so no code path can report a false success.
- **Aggregate prefixes are stable** — dashboards, the audit prefix-query (`TENANT.MEMBER` matches every `TENANT.MEMBER_*`), and alert rules key off them.

As of this writing the source defines **365 distinct event-type constants across 62 aggregate prefixes** (grep of `const string … = "AGGREGATE.ACTION…"` under `apps/tamma-elsa/src`, migrations excluded). [§10](#10-event-catalog) enumerates them by domain.

---

## 4. Tags taxonomy

`Tags` is a flat JSON object of **string → string** values. It carries the *queryable* cross-cutting keys — the whole point of DCB is that a cross-aggregate question ("everything for issue #123", "every event in this workflow run", "this agent's whole action trail") is one indexed query against one table, not a fan-out across per-aggregate streams. Rich metrics and blob references belong in `Data`, **not** here.

Common tags (not every event sets every tag):

| Tag | Meaning | Set by |
|---|---|---|
| `issueId` / `issueNumber` | The issue the work is for | ADL activities |
| `prNumber` | Pull request number | PR / merge activities |
| `repository` | `owner/repo` | Git-facing activities |
| `tenantId` | Tenant (defence-in-depth; the row column is authoritative) | Drain + services |
| `userId` | Acting user (single-user / audit) | Auth + audit emitters |
| `correlationId` | Groups every event of one workflow run (a.k.a. workflow-instance id) | Agent trail + workflows |
| `workflowInstanceId` | Elsa workflow instance | Engine drain |
| `activityId` / `activityName` | Emitting activity | Engine drain |
| `status` | `started` / `success` / `error` / `partial` | Engine drain (from `TammaEvent.Status`) |
| `emittedAt` | Engine emit time (ISO 8601) — store clock lives in `CreatedAt` | Engine drain |
| `durationMs` | Activity duration | Engine drain |
| `provider` / `model` | AI provider + model on an LLM/agent event | Agent trail, LLM proxy |
| `role` | Agent role (developer, reviewer, …) | Agent trail |
| `agentId` / `agentVersion` | Which agent + config version ran | `AgentTrailTags.Build` |
| `promptRef` | **Reference** to the prompt (never the prompt body — AC6 of Story 32-6) | Agent trail |
| `iteration` | Loop iteration counter | Agent trail |
| `sessionId` | Unguessable per-run session id of an assessment/TDD sub-workflow run | Research / Ambiguity / Clarify / Design / Decomposition emit activities, TDD `CodeEvents`/`CommitEvents` |
| `storyId` | Story being implemented by the TDD cycle | TDD `CodeEvents`/`CommitEvents` builders |
| `operation` | Code-change discriminator: `implementation` / `testing` / `refactoring` | `CodeEvents` (Story 4-5) |
| `branch` / `sha` | Git branch / commit SHA of an atomic TDD commit (`sha` on success only) | `CommitEvents.BuildCreated` |
| `channel` | Delivery channel for questions / design proposals (e.g. the issue) | Clarify / Design emit activities |
| `credentialSource` | Which credential plane served the call (platform vs BYOK) | Agent trail |
| `billing_mode` | `platform` vs `byok` — lets metering split billable from non-billable with no join | `BillingModeEvents` (Story 35-2) |
| `stripeEventId` / `eventType` / `stripeObjectId` | Stripe webhook forensics (no body leaked) | Billing webhook projections |

The single shared builder for agent-trail tags is `AgentTrailTags.Build` (`apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentTrailTags.cs`) — every agent emission routes through it so the flat-string contract is identical across the trail.

**Indexed tag lookups.** Two hot tag predicates are backed by Postgres **expression indexes** so they never seq-scan the 100%-audit stream:

- `ix_domain_events_tags_agentid` on `((Tags->>'agentId'))` — the per-agent action trail (`QueryAgentTrailAsync`, Story 32-6).
- `ix_domain_events_tags_correlationid` on `((Tags->>'correlationId'))` — run existence + replay (`ExistsByCorrelationIdAsync` / `ListByCorrelationIdAsync`, Story 32-23; migration `AddDomainEventsCorrelationIdIndex`).

---

## 5. Metadata

`Metadata` is the envelope every event carries regardless of type. The engine drain stamps:

```json
{ "workflowVersion": "1.0.0", "eventSource": "system", "error": null }
```

- **`workflowVersion`** — the schema/workflow version of the emitting surface. This is the shipped realization of the story's "schema version" acceptance criterion (event evolution is handled by versioning the envelope + append-only new types, not in-place edits).
- **`eventSource`** — `system` or `plugin` (who produced the event).
- **`error`** — the failure message on a `*.FAILED` event (null otherwise).

Service-side emitters build the same envelope inline; e.g. `BillingWebhookDcbEvents` writes `{"workflowVersion":"1.0.0","eventSource":"system"}` verbatim.

---

## 6. How an event gets written (two paths)

**Path A — workflow / activity (the common case).** A Tamma activity never touches the database. It appends a `TammaEvent` to the workflow's in-memory `tamma:events` transient list via `TammaEventEmitter.Emit` (base classes `TammaActivity` / `TammaAsyncActivity` also auto-emit `.STARTED` / `.COMPLETED` / `.FAILED` around every activity — emission is free with the base class). The merged engine drain (`EventPersistenceMiddleware` + `EventDrain`) POSTs the pending slice to `POST /api/engine/events` after the activity runs; that endpoint projects each `TammaEvent` → `DomainEvent`, stamps `Tags`/`Metadata`, resolves the tenant from the workflow scope, and calls `IEventRepository.AppendAsync`.

```
Activity → TammaEventEmitter.Emit → workflow "tamma:events" list
        → EventDrain (at-least-once) → POST /api/engine/events
        → project to DomainEvent → IEventRepository.AppendAsync → domain_events
```

No activity holds an `IEventRepository` — none is registered in the Elsa engine, so a directly-injected repo would be **inert and silently drop the event**. The drain is the only durable path.

**Path B — API service (direct).** A service that already has request context builds a `DomainEvent` and calls `IEventRepository.AppendAsync` itself (or `IPlatformEventRepository.AppendAsync` for a tenant-less platform event). Example: `BillingWebhookDcbEvents.Projection(...)` returns a fully-formed `DomainEvent` with a **deterministic** Id (UUIDv5 over `(dcbType, stripeEventId)`) so a webhook re-dispatch dedups instead of double-counting money events.

**Idempotency & at-least-once.** The drain re-sends the whole pending slice on any non-2xx. `AppendAsync` is idempotent on the stable `Id` (pre-check + `ON CONFLICT (Id) DO NOTHING`), so a partial-batch failure + full retry can never duplicate audit rows.

---

## 7. Storage, indexes & isolation

From the `domain_events` block in `TammaModelConfiguration.cs`:

- **Columns**: `Id uuid PK default gen_random_uuid()`, `Type` (≤255, required), `Tags`/`Metadata`/`Data` `jsonb default '{}'`, `CreatedAt timestamptz default now()`, `SequenceNumber` BIGSERIAL identity.
- **Indexes**: `(Type, CreatedAt)`; unique `UX_domain_events_SequenceNumber`; `TenantId`; partial `(TenantId, IssueNumber) WHERE IssueNumber IS NOT NULL`; plus the two tag expression indexes from [§4](#4-tags-taxonomy).
- **Isolation**: reads/writes route through `ITenantDbContextFactory` on a per-tenant Npgsql connection whose `search_path` is the tenant schema — the connection *is* the isolation plane. Explicit `TenantId` predicates are defence-in-depth for the transitional shared-DB phase. Cross-tenant tenant-scoped search is intentionally **not** implemented (`QueryAsync(tenantId: null, issueNumber: …)` throws) — see `EventRepository`'s class comment.
- **Append-only**: no update path. `ClearAsync` (test-only) is the sole delete.

---

## 8. Reading events (query surface)

| Purpose | Repository method | HTTP surface |
|---|---|---|
| Time-travel / dashboard timeline (paginated, exact-type) | `QueryWithPaginationAsync` | `GET /api/engine/history` (Story 4-7; `WorkflowsView`) |
| Ad-hoc tenant query (exact-type, issue filter) | `QueryAsync` | internal |
| Tenant audit log (prefix-match, cursor) | `ListByTenantAsync` | `GET /api/v1/orgs/{tenantId}/audit` (Story 18-7; `MemberAccess`) |
| Per-agent action trail (Tags JSONB predicates) | `QueryAgentTrailAsync` | agent endpoints (Story 32-6) |
| Run existence / replay by correlation id | `ExistsByCorrelationIdAsync` / `ListByCorrelationIdAsync` | run-tap (Story 32-23) |
| Black-box replay — point-in-time state reconstruction (pure fold, read-only) | `ListByCorrelationIdAsync(maxEvents)` → `ReplayService` / `ReplayReconstructor` | `GET /api/engine/runs/{correlationId}/replay?upTo={seq\|timestamp}&from={seq}` (Story 4-8; `WorkflowsView`) |
| Platform-lifecycle scan (tenant-less) | `QueryAsync(tenantId: null, type: prefix)` | admin, via `platform_events` |
| Audit-chain integrity | — | `GET /api/v1/orgs/{tenantId}/audit/verify`, `GET /api/v1/admin/audit/verify`, `POST /api/v1/admin/audit/checkpoint` |

Pagination and replay page on `SequenceNumber` (immune to same-millisecond `CreatedAt` collisions); the exact `total` on a trail read is opt-in (`includeTotal`) because it is an unbounded `COUNT(*)` over the audit stream.

**Black-box replay (Story 4-8).** `ReplayReconstructor` is a **pure, deterministic left-fold** over a run's ordered event slice — no I/O, no clock, no Elsa runtime, no writes; the same slice always yields the same `ReplayResult`. It categorizes events into AI decisions, code changes, approval points and errors, derives the step reached + terminal status, and (with `from`) returns a `ReplayDelta` diff of two folds. Tenant-scoped and null-tenant fail-closed: no resolved tenant or a foreign/unknown `correlationId` → `404` (no IDOR). An `upTo` before the run began is a known-but-empty state (`200`, `eventsReplayed = 0`).

**Read-endpoint hardening (2026-07)** — defensive fixes shared by the replay + run-detail reads:

- **UTC-pinned timestamp bounds**: a timestamp `upTo` is parsed with `AssumeUniversal | AdjustToUniversal` — an offset-less ISO-8601 string is pinned to UTC (never treated as server-local) and an explicit offset is converted, so the boundary is the same instant on every host.
- **`from > upTo` is a loud `400`** (`ReplayRangeException` → BadRequest), not a silent `200` with a meaningless empty delta. Bad/non-positive `upTo`/`from` values are also `400`.
- **Bounded run-event fetch**: `ListByCorrelationIdAsync(tenantId, correlationId, maxEvents)` fetches `maxEvents + 1` to detect overflow; replay caps at **10,000 events** (`ReplayService.MaxReplayEvents`) and a run over the cap returns the capped oldest-first slice with `truncated: true` — no silent drop, no unbounded materialisation.
- **Empty-tenant guard**: `ListByCorrelationIdAsync` throws on `Guid.Empty` (parity with `QueryEventsAsync` / `QueryAgentTrailAsync`), so an unresolved tenant can never widen a read.

---

## 9. Acceptance criteria → shipped schema

The Story 4-1 brief was written against an aspirational TypeScript `BaseEvent`. Here is how each acceptance criterion maps onto the shipped C# `DomainEvent`:

| Story 4-1 AC | Shipped realization |
|---|---|
| **AC1** base fields `eventId, timestamp, eventType, actorType, actorId, payload, metadata` | `Id`, `CreatedAt` (+ `Tags.emittedAt`), `Type`, **actor lives in `Tags`** (`userId` / `eventSource` / `provider`) rather than dedicated `actorType`/`actorId` columns, `Data` = payload, `Metadata`. |
| **AC2** types for issue selection, AI req/resp, code changes, Git ops, approvals, escalations, errors | Shipped as the ADL, `AGENT`/`LLM`, `GIT`, `CODE`/`COMMIT` (Story 4-5 closed the code-change/commit gaps), `MERGE_APPROVAL`/`DEPLOY.PRODUCTION`, `*.ESCALATED`, and `*.FAILED` families — see [§10](#10-event-catalog). |
| **AC3** schema versioning | `Metadata.workflowVersion` + append-only new types (no in-place event edits). |
| **AC4** correlation ids linking related events | `Tags.correlationId` (workflow-instance id), backed by an expression index; `ListByCorrelationIdAsync` returns a whole run in order. |
| **AC5** schema validated (JSON Schema / Protobuf) | Realized as **type-safe C# catalog constants** (`*Events.cs` / `*EventTypes.cs`) + the `TammaEvent → DomainEvent` projection + a build-time guardrail (`TAMMA001`). No runtime JSON-Schema validator ships. |
| **AC6** documentation / event catalog with examples | **This page** ([§10](#10-event-catalog), [§11](#11-examples)). |

---

## 10. Event catalog

Grouped by domain. Each group cites its catalog class; the listed strings are the exact `Type` values emitted. (This is the authoritative set of catalog-defined constants; a handful of one-off inline types are noted at the end of each group.)

### 10.1 Autonomous loop — ADL (`Tamma.Activities/ADL/*Events.cs`)

| Catalog | Event types |
|---|---|
| `BranchEvents` | `BRANCH.CREATED.SUCCESS`, `BRANCH.CREATED.FAILED` |
| `CycleEvents` | `CYCLE.STARTED`, `CYCLE.STEP_FAILED`, `CYCLE.COMPLETED`, `CYCLE.FAILED` |
| `PrEvents` | `PR.CREATED.SUCCESS`, `PR.CREATED.FAILED`, `PR.MARKED_READY.SUCCESS` |
| `MergeEvents` | `MERGE.SUCCESS`, `MERGE.FAILED`, `MERGE.READINESS.CHECKED`, `ISSUE.CLOSED.SUCCESS`, `ISSUE.CLOSED.FAILED`, `BRANCH.DELETED.SUCCESS`, `BRANCH.DELETED.FAILED` |
| `MergeApprovalEvents` | `MERGE_APPROVAL.DECISION.MERGED`, `MERGE_APPROVAL.DECISION.TEST`, `MERGE_APPROVAL.DECISION.REJECTED`, `MERGE_APPROVAL.DECISION.INVALID`, `MERGE_APPROVAL.ESCALATED`, `MERGE_APPROVAL.TEST_REQUESTED`, `MERGE.REQUESTED`, `MERGE.FAILED` (+ `APPROVAL.GATE*` bookmark prefix) |
| `IssueStatusEvents` | `ISSUE_STATUS.UPDATED.SUCCESS`, `ISSUE_STATUS.UPDATED.FAILED` |
| `DeployEvents` | `DEPLOY.STAGE.STARTED/SUCCESS/FAILED`, `DEPLOY.PIPELINE.SUCCESS/FAILED`, `DEPLOY.PRODUCTION.APPROVAL_REQUESTED/APPROVED/REJECTED`, `DEPLOY.ROLLBACK.STARTED/SUCCESS/FAILED`, `RELEASE.CREATED.SUCCESS/FAILED` |
| `ReviewFixEvents` | `REVIEW_FIX.ANALYZED.SUCCESS/FAILED`, `REVIEW_FIX.GENERATED.SUCCESS/FAILED`, `REVIEW_FIX.APPLIED.SUCCESS/FAILED`, `REVIEW_FIX.ESCALATED` |
| `TddDebugEvents` | `TDD_DEBUG.CYCLE.STARTED/PASSED/FAILED`, `TDD_DEBUG.DEBUG.ATTEMPTED`, `TDD_DEBUG.DEBUGGER.ESCALATED`, `TDD_DEBUG.RETRY.EXHAUSTED`, `TDD_DEBUG.COMPLETED.SUCCESS` |
| `TriageContextEvents` | `TRIAGE.CONTEXT.STARTED/COMPLETED/EMPTY/FAILED` |
| `TriageCycleEvents` | `TRIAGE.ISSUE.STARTED/COMPLETED/SKIPPED/FAILED`, `TRIAGE.LABELS.INVALID` |
| `TriageEvents` | `TRIAGE.PANEL.STARTED/COMPLETED/PARTIAL/FAILED` |
| `TriagePoDecisionEvents` | `TRIAGE.PO_DECISION.STARTED/COMPLETED/FAILED/SKIPPED` |

### 10.2 Assessment, research & design workflows (`Tamma.Activities/{Research,Ambiguity,Clarify,Design,Decomposition}`)

The Epic-2/3 assessment sub-workflows (2026-07): each mirrors the same skeleton (gather context → mediated `llm-call` → fail-closed parse → emit) and emits through a dedicated `Emit*EventActivity`. Common tags: `sessionId`, `issueId`, `tenantId` (empty/single-user → platform-scope, `TenantId` null); each catalog exposes a `StatusForEvent` helper so the `.FAILED` terminal is always a loud error-status row, never a false success.

| Catalog | Event types |
|---|---|
| `ResearchEvents` (Story 3.4, `research` workflow) | `RESEARCH.STARTED`, `RESEARCH.CONTEXT_GATHERED`, `RESEARCH.COMPLETED` (data: `findingCount`, `confidence`), `RESEARCH.FAILED` |
| `ClarifyEvents` (Story 3.5, `clarifying-questions` workflow) | `CLARIFY.QUESTIONS.GENERATED/DELIVERED/FAILED`, `CLARIFY.ANSWERS.RECEIVED/TIMED_OUT`, `CLARIFY.REQUIREMENTS.CLARIFIED`, `CLARIFY.INCORPORATION.FAILED` (tags add `channel`; data: `questionCount`, `detail`) |
| `AmbiguityEvents` (Story 3.6, `ambiguity-scoring` workflow) | `AMBIGUITY.STARTED`, `AMBIGUITY.SCORED`, `AMBIGUITY.CLARIFICATION_TRIGGERED`, `AMBIGUITY.BELOW_THRESHOLD`, `AMBIGUITY.FAILED` (data: `score` 0..1, `ambiguityCount`, `confidence`, `threshold`, `detail`) |
| `DesignEvents` (Story 3.7, `design-proposal` workflow) | `DESIGN.PROPOSAL.GENERATED/DELIVERED/APPROVED/REJECTED`, `DESIGN.PROPOSAL.FAILED`, `DESIGN.REVIEW.TIMED_OUT` (both loud error-status; tags add `channel`; data: `alternativeCount`, `reviewer`, `detail`) |
| `DecompositionEvents` (Story 2.14, `issue-decomposition` workflow) | `DECOMPOSITION.STARTED`, `DECOMPOSITION.CONTEXT_GATHERED`, `DECOMPOSITION.COMPLETED` (data: `subtaskCount`), `DECOMPOSITION.FAILED` |

### 10.3 Quality gates, testing, debugging & TDD code/commit audit (`Tamma.Activities/{Testing,Review,Blocker,Debug,TDD}`)

| Catalog | Event types |
|---|---|
| `TestingEvents` | `TEST.CI_TRIGGERED.SUCCESS/FAILED`, `TEST.RESULTS_RECEIVED.SUCCESS`, `TEST.CI_TIMED_OUT.FAILED`, `GATE.EVALUATED.SUCCESS`, `GATE.AUTOFIX_COMMITTED.SUCCESS`, `GATE.AUTOFIX_NOOP.FAILED`, `GATE.PASSED.SUCCESS`, `GATE.FAILED.FAILED`, `GATE.ESCALATED.FAILED` |
| `CodeReviewEvents` | `CODE_REVIEW.PR_CREATED.SUCCESS/FAILED`, `CODE_REVIEW.GUIDANCE_DELIVERED.SUCCESS/FAILED`, `CODE_REVIEW.ITERATION.STARTED`, `CODE_REVIEW.MERGED.SUCCESS/FAILED`, `CODE_REVIEW.ESCALATED`, `CODE_REVIEW.FAILED` |
| `BlockerEvents` | `BLOCKER.DIAGNOSED.SUCCESS/FAILED`, `BLOCKER.RESOLUTION_ATTEMPTED`, `BLOCKER.PROGRESS_DETECTED`, `BLOCKER.PROGRESS_TIMED_OUT`, `BLOCKER.ESCALATED`, `BLOCKER.RESOLVED`, `BLOCKER.TIMED_OUT` |
| `DebugEvents` | `DEBUG.SESSION.STARTED`, `DEBUG.DIAGNOSIS.SUCCESS/FAILED`, `DEBUG.HYPOTHESIS.SELECTED`, `DEBUG.FIX.ATTEMPTED`, `DEBUG.TESTS.PASSED/FAILED`, `DEBUG.REGRESSION_TEST.INVALID`, `DEBUG.RESOLVED.SUCCESS`, `DEBUG.ESCALATED.FAILED` |
| `CodeEvents` (Story 4-5 AC1, `Tamma.Activities/TDD`) | `CODE.GENERATED.SUCCESS/FAILED` (RED test authoring + GREEN implementation, told apart by the `operation` tag: `testing` / `implementation`), `CODE.REFACTORED.SUCCESS/FAILED` (REFACTOR phase, `operation=refactoring`). Tags: `storyId`, `sessionId`, `operation`; data: `operation`, `source: "ai_generated"`, `files`, `fileCount`, optional `testCount`, failure `reason`. Emitted by `WriteTestsActivity` / `WriteImplementationActivity` / `ApplyRefactoringActivity`; `IsFailureType` classifies the loud FAILED types. |
| `CommitEvents` (Story 4-5 AC2, `Tamma.Activities/TDD`) | `COMMIT.CREATED.SUCCESS/FAILED` — the atomic TDD commit (`CommitChangesActivity`), fired on all three edges (no-files, engine-callback result, exception). Tags: `storyId`, `sessionId`, `branch`, `repository` (+ `sha` on success); data: `sha`, `message`, `branch`, `fileCount`, `files`, failure `reason`. |

> Story 4-5's coverage pass confirmed branch / PR / merge / release git operations were **already** covered (`BranchEvents`, `PrEvents`, `MergeEvents`, `DeployEvents` + the server-side `GitEventTypes` `GIT.*` family in [§10.5](#105-integrations-tammaapiservicesgitcijiraemailnotifications)) — `CODE.*` and `COMMIT.*` fill the two gaps so every code change and git operation the loop makes is a DCB event. `CODE.GENERATED.FAILED` is the emitter that fulfils the `SensitiveActionCatalog` code of the same name.

### 10.4 Agents, LLM & tools (`Tamma.Api/Services/{Agents,AgentDispatch}`, `Billing/BillingModeEvents`)

| Catalog | Event types |
|---|---|
| `AgentEventTypes` | `AGENT.SELECTED_FOR_ROLE.SUCCESS`, `AGENT.RESOLVE.FAILED/DEGRADED/NO_ENABLED_DEFAULT`, `AGENT.SELECT.NOT_ENABLED` (+ inline `AGENT.RESOLVE.NO_DEFAULT/NO_TENANT/NO_USER`, `AGENT.SELECT.NOT_FOUND`) |
| `AgentEnablementEventTypes` | `AGENT.ENABLED.SUCCESS`, `AGENT.DISABLED.SUCCESS` |
| `AgentRunEventTypes` | `AGENT.RUN.STARTED`, `AGENT.RUN.SUCCESS`, `AGENT.RUN.FAILED` |
| `AgentTrailEventTypes` | `AGENT.TASK.SUCCESS/FAILED/PARTIAL`, `AGENT.TOOL_CALL.SUCCESS/FAILED`, `AGENT.ITERATION.COMPLETED`, `AGENT.PANEL.AGGREGATED`, `REVIEW.BUG.RECORDED`, `AGENT.TRAIL.WRITE_FAILED` |
| `AgentDispatchEventTypes` | `AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS/FAILED`, `AGENT_DISPATCH.RUN_POLLED.SUCCESS/FAILED`, `AGENT_DISPATCH.RESULTS_COLLECTED.SUCCESS/FAILED` |
| Agent registry (inline) | `AGENT.CREATED.SUCCESS`, `AGENT.ARCHIVED.SUCCESS`, `AGENT_CONFIG.UPDATED.SUCCESS`, `AGENT.VERSION_PUBLISHED.SUCCESS`, `AGENT.DISPATCH.SUCCESS/FAILED` |
| `BillingModeEvents` | `LLM.CALL.SUCCESS`, `LLM.CALL.FAILED`, `BILLING.MODE.MISMATCH` |
| LLM resolve (inline) | `LLM.PROMPT.RESOLVE`, `LLM.CONVENTIONS.RESOLVE`, `LLM.CONCURRENCY.CHECK` (+ `*.NO_ROW` / `*.REGISTRY_UNAVAILABLE` variants) |

> **On "BYOK":** there is no `PRICING.BYOK.*` event type. BYOK is represented as the **`billing_mode` tag** (`byok` vs `platform`) on `LLM.CALL.*`, plus `PROVIDER_KEY.CHANGED.SUCCESS` when a tenant sets/clears its own key. Metering splits billable from non-billable off that tag with no join.

### 10.5 Integrations (`Tamma.Api/Services/{Git,Ci,Jira,Email,Notifications}`)

| Catalog | Event types |
|---|---|
| `GitEventTypes` | `GIT.BRANCH_CREATED.*`, `GIT.PR_OPENED.*`, `GIT.PR_MERGED.SUCCESS`, `GIT.PR_MERGE.FAILED`, `GIT.ISSUE_UPDATED.*`, `GIT.PR_COMMENTS_READ.*`, `GIT.COMMITS_READ.*`, `GIT.FILE_CHANGES_READ.*`, `GIT.BRANCH_DELETED.*`, `GIT.RELEASE_CREATED.*` (each `.SUCCESS`/`.FAILED`) |
| `CiEventTypes` | `CI.TESTS_TRIGGERED.SUCCESS/FAILED`, `CI.BUILD_STATUS_READ.SUCCESS/FAILED` |
| `JiraEventTypes` | `JIRA.TICKET_READ.SUCCESS/FAILED`, `JIRA.TICKET_UPDATED.SUCCESS/FAILED` |
| `EmailEventTypes` | `EMAIL.QUEUED.SUCCESS`, `EMAIL.SENT.SUCCESS`, `EMAIL.SENT.FAILED` |
| `NotificationSlackEventTypes` | `NOTIFICATION.SLACK.SENT.SUCCESS`, `NOTIFICATION.SLACK.SEND.FAILED` |

### 10.6 Config: prompts, conventions, providers, credentials

| Catalog | Event types |
|---|---|
| `PromptEventsService` (inline) | `PROMPT.CREATED.SUCCESS`, `PROMPT.UPDATED.SUCCESS`, `PROMPT.DELETED.SUCCESS`, `PROMPT.RESET.SUCCESS`, `PROMPT.RENDERED.SUCCESS` |
| `ConventionEventsService` (inline) | `CONVENTION.CREATED.SUCCESS`, `CONVENTION.UPDATED.SUCCESS`, `CONVENTION.DELETED.SUCCESS`, `CONVENTION.RESET.SUCCESS` |
| `ProviderPricingEventTypes` | `PROVIDER.PRICE.VERSIONED`, `PROVIDER.REGISTERED`, `PROVIDER.STATUS_CHANGED` (+ inline `PROVIDER.PRICE.IMMUTABLE`) |
| Config-change audit (inline) | `PROVIDER_CHAIN.CHANGED.SUCCESS`, `PROVIDER_KEY.CHANGED.SUCCESS`, `INTEGRATION_CREDENTIAL.CHANGED.SUCCESS`, `SANITIZATION_RULE.CHANGED.SUCCESS`, `BUDGET.CONFIG.CHANGED.SUCCESS` |
| `PricingEventTypes` (+ inline) | `PRICING.MARGIN.UPDATED`, `PRICING.MARGIN.NO_POLICY`, `PRICING.UNKNOWN_MODEL` |

### 10.7 Billing & plans (`Tamma.Api/Services/{Billing,Pricing}`)

| Catalog | Event types |
|---|---|
| `BillingEvents` | `BILLING.CUSTOMER.CREATED`, `BILLING.PLAN_CATALOG.SYNCED`, `BILLING.SUBSCRIPTION.CREATED/UPDATED/CANCELED/TRIAL_ENDING` |
| `BillingWebhookEventTypes` | `BILLING.SUBSCRIPTION.CREATED/UPDATED/DELETED/TRIAL_ENDING`, `BILLING.INVOICE.CREATED/FINALIZED/PAID/PAYMENT_FAILED`, `BILLING.PAYMENT.SUCCEEDED/FAILED`, `BILLING.DISPUTE.OPENED`, `BILLING.WEBHOOK.SKIPPED/FAILED` |
| `PlanCatalogEventTypes` | `PLAN.VERSION.CREATED`, `PLAN.DEPRECATED`, `PLAN.CATALOG.UPDATED`, `PLAN.CUSTOM.CREATED` |
| `PlanAssignmentEventTypes` | `TENANT.PLAN.CHANGED`, `TENANT.PLAN.CANCELLED`, `PLAN.UPDATED` |
| `EntitlementEventTypes` | `ENTITLEMENT.RESOLVED.SUCCESS`, `ENTITLEMENT.RESOLVED.FAILED` |

### 10.8 Analytics (`Tamma.Activities/Analytics`, `Tamma.Api/Services/Analytics`)

| Catalog | Event types |
|---|---|
| `AnalyticsRollupEvents` | `ANALYTICS.ROLLUP.TENANT_COMPLETED/TENANT_SKIPPED/TENANT_FAILED`, `ANALYTICS.ROLLUP.PLATFORM_COMPLETED`, `ANALYTICS.ROLLUP.HOUR_COMPLETED`, `ANALYTICS.ROLLUP.DIMENSIONAL_LAG`, `ANALYTICS.PURGE.HOURLY/FAILED/USAGE_HOURLY/USAGE_HOURLY_FAILED`, `ANALYTICS.COMPACT.DAILY` |
| `CostAnalyticsEvents` | `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` |

### 10.9 Tenancy, onboarding, provisioning & platform (`Tamma.Activities/TenantLifecycle`, `Tamma.Platforms`, `Tamma.Api/Endpoints/OnboardingEndpoints.cs`)

| Catalog | Event types |
|---|---|
| `TenantLifecycleEvents` | `TENANT.PROVISIONING_REQUESTED`, `TENANT.PROVISION.STEP_STARTED/STEP_COMPLETED/STEP_FAILED`, `TENANT.CREATED.SUCCESS`, `TENANT.PROVISIONED.SUCCESS`, `TENANT.PROVISION.FAILED`, `TENANT.DELETE.REQUESTED/STARTED/STEP_STARTED/STEP_COMPLETED/STEP_FAILED/STEP_SKIPPED/ABORTED/FAILED`, `TENANT.DELETED.SUCCESS`, `TENANT.DELETE_CANCELLED` |
| `PlatformInstallationEventTypes` (in `PlatformInstallationEvents.cs`) | `PLATFORM.INSTALLATION.CONNECTED.SUCCESS`, `PLATFORM.INSTALLATION.DISCONNECTED.SUCCESS`, `TENANT.SWITCH_ORG.SUCCESS` |
| Onboarding (Story 18-4, inline in `OnboardingEndpoints.cs`) | `REPO.ACTIVATED.SUCCESS` / `REPO.DEACTIVATED.SUCCESS` — a connected repo's `IsActive` flag flipped via `PATCH /api/v1/onboarding/repos/{installationId}/{repoId}` (idempotent: a no-op flip emits nothing; tags: `tenantId`, `userId`; data: `installationId`, `repoId`, `repoFullName`, `active`). `ONBOARDING.COMPLETED.SUCCESS` — the first-run milestone via `POST /api/v1/onboarding/complete`; the append-only event **is** the record (no persisted flag column), idempotent via `GetLastByTypeAsync` (tags: `tenantId`, `userId`; data: `installationCount`, `activeRepoCount`, `completedAt`). |

### 10.10 Auth, audit, security & secrets (`Tamma.Core/Audit`, `Tamma.Api/Services/{Audit,Secrets}`, `Tamma.Activities/SecretsRotation`)

| Catalog | Event types |
|---|---|
| `SensitiveActionCatalog` (compliance audit) | `AUTH.LOGIN.SUCCESS/FAILURE`, `AUTH.TOKEN.REFRESHED`, `AUTH.REFRESH_REUSE_DETECTED`, `AUTH.PASSWORD_RESET.SUCCESS`, `AUTH.APIKEY.USED`, `USER.LOGOUT_ALL.SUCCESS`, `USER.ORG_SWITCHED.SUCCESS`, `USER.ROLE_CHANGED.SUCCESS`, `IMPERSONATION.STARTED/ENDED`, `GDPR.DSAR.REQUESTED`, `DATA.EXPORTED.SUCCESS`, `TENANT.MEMBER_INVITED/MEMBER_JOINED/MEMBER_REMOVED/MEMBER_ROLE_CHANGED.SUCCESS`, `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS`, `TENANT.CONNECTION_STRING_ROTATED.SUCCESS`, `TENANT.PURGED.SUCCESS`, `TENANT.MOVE.REQUESTED` |
| `AuditChainEventTypes` | `AUDIT.CHAIN.VERIFIED`, `AUDIT.CHAIN.TAMPER_DETECTED`, `AUDIT.CHAIN.CHECKPOINTED` |
| `AuditQueryEventTypes` | `AUDIT.QUERIED` |
| Secret access/rotation (inline) | `SECRET.READ/WRITE/REVEAL`, `SECRET.ROTATE.STARTED/SUCCESS/FAILED`, `SECRET.MIGRATED.SUCCESS/SKIPPED/FAILED`, `SECRET.VERSION.RETIRED/REVOKED`, and the `SECRET.ROTATION.*` saga family (`REQUESTED`, `STAGED`, `SWITCHED`, `ACTIVATED`, `RETIRE_SCHEDULED`, `RETIRED`, `COMPLETED`, `FAILED`, `REJECTED`, `PROBE.SUCCESS/FAILED`, `PUSH.SUCCESS/FAILED`, `POOL.DRAINED`, `COMPENSATION.STARTED/SUCCESS/FAILED`, `ROLLBACK.ROLE_DISABLED`, `CRANL.ENV_PUSHED/RELOAD_TRIGGERED/RATE_LIMIT_HIT`) |
| KEK rotation (inline) | `SECRETS.KEK.ROTATION.STARTED/COMPLETED/FAILED` |

> This is a representative map, not a line-by-line dump of all 365 constants — the giant `SECRET.ROTATION.*` saga family is summarized. The catalog **classes** named above are the authoritative source; grep `apps/tamma-elsa/src` for the exact current set.

---

## 11. Examples

**A workflow activity event (`PR.CREATED.SUCCESS`)** — projected by the drain from a `TammaEvent`:

```json
{
  "id": "0f9c7e2a-6b1d-4f2e-9a3c-1b2c3d4e5f60",
  "type": "PR.CREATED.SUCCESS",
  "tenantId": "8a1f…",
  "issueNumber": 123,
  "tags": {
    "issueId": "123",
    "issueNumber": "123",
    "repository": "meywd/tamma",
    "prNumber": "456",
    "tenantId": "8a1f…",
    "workflowInstanceId": "wf-inst-…",
    "activityName": "EmitPrEventActivity",
    "status": "success",
    "durationMs": "812.4",
    "emittedAt": "2026-07-04T12:34:56.789Z"
  },
  "metadata": { "workflowVersion": "1.0.0", "eventSource": "system", "error": null },
  "data": { "url": "https://github.com/meywd/tamma/pull/456", "isDraft": false, "filesChanged": ["src/foo.cs"] },
  "createdAt": "2026-07-04T12:34:56.802Z",
  "sequenceNumber": 90231
}
```

**A service event with a deterministic id (`BILLING.INVOICE.PAID`)** — id is UUIDv5 over `(type, stripeEventId)` so a webhook re-dispatch dedups:

```json
{
  "id": "b3d1…(v5)",
  "type": "BILLING.INVOICE.PAID",
  "tenantId": "8a1f…",
  "tags": { "tenantId": "8a1f…", "stripeEventId": "evt_1P…", "eventType": "invoice.paid", "stripeObjectId": "in_1P…" },
  "metadata": { "workflowVersion": "1.0.0", "eventSource": "system" },
  "data": { "stripeObjectId": "in_1P…" }
}
```

---

## 12. Adding a new event type

1. **Pick the aggregate + name.** Follow `AGGREGATE.ACTION.STATUS`. Reuse an existing aggregate prefix where one fits; failures end in `.FAILED` (or `.REJECTED`).
2. **Add the constant to the right catalog.** Workflow/activity events → the domain's `*Events.cs` (`Tamma.Activities/**`). Service events → the domain's `*EventTypes.cs` (`Tamma.Api/Services/**`). Never hard-code the literal at the call site.
3. **Emit it.**
   - Activity: build a `TammaEvent` (set `EventType`, `Status`, `Tags`, `Data`) and call `TammaEventEmitter.Emit` — the drain persists it. Do **not** inject `IEventRepository` into an activity.
   - Service: build a `DomainEvent` and call `IEventRepository.AppendAsync` (or `IPlatformEventRepository.AppendAsync` for a tenant-less platform event). For replayable/idempotent facts, mint a **deterministic** id (see `BillingWebhookDcbEvents.DeterministicId`).
4. **Tag it for the queries you need.** Put cross-cutting keys in `Tags` (`issueId`, `correlationId`, `agentId`, `provider`, `billing_mode`, …). If you add a new *hot* tag predicate, add a Postgres expression index for it (mirror `ix_domain_events_tags_correlationid`).
5. **Payload → `Data`; never leak secrets.** No prompt bodies, tokens, or credentials in `Tags`/`Data` — references only.
6. **If it's a failure, classify it.** Extend the catalog's `IsFailureType`-style helper so no path can read a failure as a success.
7. **Document it here** — add the type to the relevant [§10](#10-event-catalog) table.

---

## Related pages

- [Architecture](Architecture) — system map (the DCB stream in context)
- [Epic 4: Event Sourcing & Audit Trail](Epics/Epic-4-Event-Sourcing) — epic overview & story status
- [Security](Security) — tenant isolation & the audit chain
- [Secret Management](Secret-Management) — the `SECRET.*` rotation saga
- [Agent Dispatch](Agent-Dispatch) — the `AGENT.*` action trail

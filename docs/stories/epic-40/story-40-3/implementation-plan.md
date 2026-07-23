# Implementation Plan — Story 40-3: Durable Agent-Run Signal Plane + Resume Endpoint

## Scope & Deliverable

When this story is done, a finished coding run resumes the suspended workflow **durably and
across pods**. A persisted `agent_run_waits` row written at dispatch carries the
`session_id`/`bookmark_name` the `workflow_run.completed` webhook lacks; the webhook (on any
pod) matches the row and resumes the 40-2 bookmark through Elsa's persistent bookmark runtime;
an `AgentRunResumeEndpoint` exposes the same resume (404/409, tenant-folded, mirroring 39-8); and
the old in-process poll loop becomes a durable reconciler that catches missed webhooks. The
in-memory `WebhookSignalRegistry` is demoted to an optional same-process fast path — correctness
no longer depends on it.

## Pre-Reading

- `docs/stories/epic-40/story-40-3/40-3-durable-agent-run-signal-and-resume-endpoint.md` — this story (ACs are source of truth)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs` — the in-memory plane being demoted; `AgentWebhookSignalKey` (installation-id folding, review-finding-5), `AgentWebhookSignal` (payload shape)
- `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:377` — `HandleWorkflowRunEvent`: how repo/branch/run-id/installation-id/conclusion are extracted from the webhook, `PublishSignal` call site
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentMonitorService.cs` — `WaitForWebhookAsync` (registry wait), `PollAsync` (→ reconciler), mode logic (Webhook/Auto/Poll), installation-id resolution
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs` — mediated `GetRunAsync`/`DiscoverRunAsync` the reconciler polls through
- `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DesignResumeEndpoint.cs` + `ClarifyResumeEndpoint.cs` (and 39-8's `DocumentDecisionResumeEndpoint`) — the tenant-folded resume-endpoint pattern (bookmark-name recompute, 404/409 posture)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs` — `ForAgentRun` (40-2) recomputed on resume
- `docs/stories/epic-40/story-40-2/implementation-plan.md` — the `ForAgentRun` name contract + completion payload shape (lockstep)
- `apps/tamma-elsa/src/Tamma.Data/` — `TenantDbContext`, `TammaModelConfiguration`, an existing entity + repository for the row pattern (e.g. how `domain_events`/other tenant tables are wired)
- how a bookmark is resumed programmatically in-engine: the Elsa `IBookmarkQueue`/bookmark-resume API used by `DesignResumeEndpoint` (copy that call)
- **NOT FOUND (prerequisite):** `WaitForAgentRunActivity` + `ForAgentRun` (40-2), `DocumentDecisionResumeEndpoint` (39-8). See Dependencies & Sequencing.

## Design Decisions

- **D1 — The signal row is a correlation record, NOT a queue.** `agent_run_waits` exists solely
  to give the webhook the `session_id`/`bookmark_name`/`workflow_instance_id` it cannot derive
  from the `workflow_run` payload. Delivery itself rides Elsa's persistent bookmark store (any
  pod resumes a DB bookmark). No RabbitMQ, no new bus — the epic's "bookmark store is the
  backplane" principle. Written by 40-2's `Execute` at dispatch; read by the webhook/endpoint/
  reconciler.
- **D2 — Match key `(installation_id, repository, head_branch)`; disambiguate by session.** The
  webhook knows those three. Normally one pending row matches; concurrent dispatches on the same
  branch (rare — the TDD loop is sequential per branch) are disambiguated by resuming the row
  whose `dispatched_at` is the latest before the run's `created_at`, and a genuinely ambiguous
  match returns 409 on the endpoint / logs-and-skips on the webhook (never a wrong resume). The
  installation id preserves the review-finding-5 cross-tenant guard (AC7).
- **D3 — Resume through a shared `AgentRunResumer` service, called by webhook + endpoint +
  reconciler.** One `IAgentRunResumer.ResumeAsync(matchKey, completionPayload, ct)` that (a)
  loads the pending row, (b) recomputes `ForAgentRun(...)` and asserts byte-equality with the
  stored `bookmark_name` (drift guard), (c) resumes the bookmark via Elsa's bookmark runtime with
  the payload, (d) marks the row `received`. Idempotent: already-`received` row or already-burned
  bookmark ⇒ logged no-op success (AC8, GitHub redelivery). All three entry points delegate here
  so behavior is identical.
- **D4 — `AgentRunResumeEndpoint` mirrors `DocumentDecisionResumeEndpoint` exactly.** Same auth
  (the existing webhook HMAC / installation-scoped path), same recompute-name → resume → 404/409
  shape, tenant-folded. It is the manual/retry surface; the webhook path is the automatic one.
  Both call `IAgentRunResumer`.
- **D5 — The poll loop becomes a durable reconciler, not a live wait.** Delete the in-request
  ~35-min `PollAsync` loop from the wait path; keep its per-tick mediated `GetRunAsync` logic and
  move it into `AgentRunReconciler` — a hosted sweep (or driven by 40-2's `DelayFor` wake) that
  reads `pending` rows older than a short threshold, polls the run, and resumes via
  `IAgentRunResumer` if terminal. So "webhook missed" self-heals durably; no thread parks for the
  wait. `AgentMonitorService`'s discover phase (finding the run id post-204) is retained.
- **D6 — Demote, don't delete, `WebhookSignalRegistry`.** Keep it as an optional same-process
  fast-path: `IAgentRunResumer` may first try an in-memory waiter (sub-second local resume) then
  fall through to the durable bookmark resume. With the registry unregistered, the durable path is
  authoritative. Update `AgentMonitorService`'s "Webhook mode without registry → hard fail" branch
  to "→ durable path" (AC6). This bounds churn and keeps the fast local case fast.
- **D7 — The row is tenant-DB-scoped.** `agent_run_waits` lives in the tenant schema
  (`TenantDbContext`) so per-tenant isolation is structural (matches the platform's schema-per-
  tenant model); single-user mode uses the central schema like every other tenant. Migration
  against `TenantDbContext` — subject to the single-migration-author token (see Dependencies).

## Implementation Steps

1. **CREATE the entity + migration** — `apps/tamma-elsa/src/Tamma.Data/Entities/AgentRunWait.cs`
   (D1 fields), register in `TenantDbContext` + `TammaModelConfiguration` (unique
   `(tenant_id, repository, branch_name, session_id)`, index on `(installation_id, repository,
   branch_name, status)`), generate the `TenantDbContext` migration (D7 — hold the migration
   token). CREATE `IAgentRunWaitRepository` + `AgentRunWaitRepository`
   (`apps/tamma-elsa/src/Tamma.Data/Repositories/`).

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentRunResumer.cs` +
   `AgentRunResumer.cs`** (D3) — load row → recompute + assert `ForAgentRun` name → resume
   bookmark (Elsa bookmark runtime, the `DesignResumeEndpoint` call shape) → mark `received`;
   idempotent; optional in-memory fast-path (D6). Emits the loud diagnostic on unresolvable row
   (AC8) — the constant is 40-6's; use a placeholder pinned to 40-6.

3. **MODIFY 40-2's `WaitForAgentRunActivity.Execute`** — after a successful dispatch, **write the
   `agent_run_waits` pending row** (tenant, repo, branch, session, installation id resolved via
   the mediated `ResolveInstallationIdAsync`, `workflow_instance_id`, `bookmark_name =
   ForAgentRun(...)`). This is the one cross-story edit (coordinate with 40-2). Local mode writes
   no row (it never suspends externally).

4. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs`**
   (`HandleWorkflowRunEvent`) — after (or instead of) `PublishSignal`, call
   `IAgentRunResumer.ResumeAsync` with the `(installation_id, repo, head_branch)` match key +
   the completion payload (D2/D3). Keep the `PublishSignal` fast-path call (D6). Non-matching
   runs stay `Skipped`.

5. **CREATE `AgentRunResumeEndpoint`** (`apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/`, or
   Tamma.Api alongside the webhook) (D4, AC3) — recompute name → `IAgentRunResumer` → 404/409,
   tenant-folded, existing auth.

6. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentRunReconciler.cs`** (D5) —
   hosted/periodic sweep over `pending` rows, mediated poll, resume-if-terminal, mark
   `timed_out` past the row timeout. Register as a hosted service (guarded by a config flag like
   the existing worker `RunOnStartup` pattern).

7. **MODIFY `AgentMonitorService`** — relax the "Webhook-without-registry = hard fail" branch to
   route through the durable path (D6, AC6); retain discovery. (The bulk of the live poll wait is
   superseded by 40-2's suspend + this reconciler; keep the discover helper.)

8. **DI registration** (both hosts) — `IAgentRunWaitRepository`, `IAgentRunResumer`,
   `AgentRunReconciler`. **CREATE tests** (see Test Plan). Finish with
   `dotnet ef migrations has-pending-model-changes` (clean after the intended migration) +
   `dotnet test`.

## Data & Migrations

- **New table `agent_run_waits`** in `TenantDbContext` (D7): `id, tenant_id, repository,
  branch_name, session_id, installation_id, workflow_instance_id, bookmark_name, dispatched_at,
  status, updated_at`; unique `(tenant_id, repository, branch_name, session_id)`; index
  `(installation_id, repository, branch_name, status)`. One EF migration against
  `TenantDbContext` — **subject to the single-migration-author token** (coordinate with any
  concurrent tenant-context migration). No data backfill (new in-flight state only).

## Events

- **Emits (40-6 constants, placeholder-pinned here):** `AGENT_RUN.RESUME_UNRESOLVED` (loud, on a
  row with no resolvable bookmark past timeout) and reuses 40-6's `AGENT_RUN.RECEIVED`/`TIMED_OUT`
  where the resumer marks the row. If 40-6 has not merged, define a local constant and migrate it
  to 40-6's `AgentRunEventTypes` at merge (documented conscious pin).
- **Consumes:** the `workflow_run.completed` webhook payload; the mediated `GET runs/{id}` status.

## Test Plan

All NUnit + FluentAssertions (+ Moq; Testcontainers for the cross-pod scenario, shared with 40-7).

- **`AgentRunWaitRepositoryTests`** (unit/Testcontainers) — upsert pending, unique constraint,
  match by `(installation_id, repo, branch, status=pending)`, mark received/timed_out. **Covers AC1.**
- **`AgentRunResumerTests`** (unit, Moq'd repo + bookmark runtime) — resume marks received +
  resumes the named bookmark; name-drift (stored ≠ recomputed) → loud fail, no resume; already-
  received → idempotent no-op; already-burned bookmark → logged no-op; ambiguous match → 409/skip;
  installation-scoping (tenant A payload never resumes tenant B row). **Covers AC2, AC7, AC8.**
- **`AgentRunResumeEndpointTests`** (unit/API) — 404 no row, 409 ambiguous, 200 resume,
  tenant-fold rejection, auth. **Covers AC3.**
- **`AgentRunReconcilerTests`** (unit, Moq'd mediation) — pending-past-threshold + terminal run →
  resume; still-running → leave pending; past row-timeout → `timed_out` + loud event. **Covers AC4, AC8.**
- **`InstallationRouterWorkflowRunResumeTests`** (unit) — `workflow_run.completed` → resolver →
  `IAgentRunResumer` called with the right key; non-Tamma run → Skipped. **Covers AC2.**
- **Cross-pod integration** (Testcontainers, shared with 40-7 step) — dispatch+suspend on host A;
  dispose A; deliver webhook via a fresh host B on the same store; assert resume on B, registry
  unwired. **Covers AC5, AC6.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — durable tenant-scoped signal row | 1, 3 | `AgentRunWaitRepositoryTests` |
| 2 — webhook resolves row + resumes bookmark | 2, 4 | `AgentRunResumerTests`, `InstallationRouterWorkflowRunResumeTests` |
| 3 — resume endpoint (39-8 shape, 404/409) | 5 | `AgentRunResumeEndpointTests` |
| 4 — durable poll reconciler | 6 | `AgentRunReconcilerTests` |
| 5 — cross-pod delivery proven | 8 | Cross-pod integration (with 40-7) |
| 6 — in-memory registry demoted, not required | 2, 7 | Cross-pod integration (registry unwired); `AgentMonitorService` mode test |
| 7 — tenant + installation folded | 2, 5 | `AgentRunResumerTests` scoping cases |
| 8 — fail-loud, exactly-once resume | 2, 6 | `AgentRunResumerTests`, `AgentRunReconcilerTests` idempotency/timeout cases |

## Dependencies & Sequencing

- **Hard prerequisites:** 40-2 (`WaitForAgentRunActivity` + `ForAgentRun` — step 3 edits it;
  the bookmark this story resumes) and, through it, 39-10 (`LifecycleBookmarks`). Blocking.
- **Soft:** 39-8 (`DocumentDecisionResumeEndpoint` pattern for step 5) — mirrored, not consumed;
  can proceed against `DesignResumeEndpoint` if 39-8 is not yet in.
- **Migration ordering:** `agent_run_waits` is a `TenantDbContext` migration — take the single
  migration-author token; rebase its snapshot onto whatever tenant migration precedes it at merge.
- **In place, verified:** `InstallationRouterService` webhook receiver, `AgentMonitorService`
  poll/discover, `AgentDispatchMediationService` mediated reads, Elsa persistent bookmark runtime,
  `TenantDbContext`.
- **Feeds:** 40-7 (cross-pod + crash integration), 40-6 (formalizes the event constants used here).
- **Sequencing within the story:** 1 → 2 → 3/4 → 5/6 → 7 → 8.

## Risks & Mitigations

- **Ambiguous branch match resumes the wrong run.** Mitigation: D2's latest-before-created_at
  disambiguation + 409/skip on genuine ambiguity + the stored-vs-recomputed name byte-guard (D3) —
  a mismatched name never resumes.
- **Webhook redelivery double-resumes.** Mitigation: `received` status + burned-bookmark no-op
  (D3, AC8); resume is idempotent.
- **Reconciler and webhook race to resume the same row.** Mitigation: the resumer's row-status CAS
  (`pending → received`) + Elsa's single-burn bookmark make concurrent resumes converge to one
  effect; the loser is a no-op.
- **Migration-token contention with concurrent tenant migrations.** Mitigation: additive table,
  standard rebase; coordinate the token per the epic execution plan.
- **Cross-tenant regression if installation scoping is dropped.** Mitigation: AC7 carries
  `installation_id` on the row + match; `AgentRunResumerTests` scoping cases guard it.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | entity + `TenantDbContext` migration + repository | 1.0 |
| 2 | `AgentRunResumer` (resume + idempotency + name guard + fast-path) | 1.25 |
| 3 | 40-2 `Execute` row write (cross-story) | 0.5 |
| 4 | `InstallationRouterService` resolve+resume wiring | 0.75 |
| 5 | `AgentRunResumeEndpoint` | 0.75 |
| 6, 7 | reconciler + `AgentMonitorService` mode relax | 1.0 |
| 8 | DI + unit tests (repo, resumer, endpoint, reconciler, router) | 1.5 |
| **Total** | | **6.75** (story estimate: 5-7 days) |

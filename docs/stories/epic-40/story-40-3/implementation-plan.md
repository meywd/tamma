# Implementation Plan — Story 40-3: Durable Agent-Run Signal Plane + Resume Endpoint

## Scope & Deliverable

When this story is done, a finished coding run resumes the suspended workflow **durably and
across pods**, in **both** operating modes. A persisted `agent_run_waits` row — written on the
API side inside the mediated dispatch, and dual-scoped (tenant schema in SaaS, control plane in
single-user) — carries the `session_id`/`bookmark_name` the `workflow_run.completed` webhook
lacks; the webhook (on any pod, with no ambient tenant) matches the row and resumes the 40-2
bookmark through Elsa's persistent bookmark runtime; an `AgentRunResumeEndpoint` exposes the same
resume (404/409, principal-folded, mirroring the shipped 39-8 endpoint); and the old in-process
poll loop becomes a durable reconciler owned by a real hosted service with principal-folded
advisory-lock leader election. The in-memory `WebhookSignalRegistry` is demoted to an optional
same-process fast path — correctness no longer depends on it.

## Pre-Reading

- `docs/stories/epic-40/story-40-3/40-3-durable-agent-run-signal-and-resume-endpoint.md` — this story (ACs are source of truth)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs` — the in-memory plane being demoted; `AgentWebhookSignalKey` (installation-id folding, review-finding-5), `AgentWebhookSignal` (payload shape)
- `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:377` — `HandleWorkflowRunEvent`: how repo/branch/run-id/installation-id/conclusion are extracted from the webhook, `PublishSignal` call site
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentMonitorService.cs` — `WaitForWebhookAsync` (registry wait), `PollAsync` (→ reconciler), mode logic (Webhook/Auto/Poll), installation-id resolution
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs` — mediated `GetRunAsync`/`DiscoverRunAsync` the reconciler polls through
- `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DocumentDecisionResumeEndpoint.cs` (39-8, **shipped**) + `DesignResumeEndpoint.cs` / `ClarifyResumeEndpoint.cs` / `DocumentInputResumeEndpoint.cs` — the principal-folded resume-endpoint pattern (bookmark-name recompute, 404/409 posture)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs` — `Compose` (`:38`); `ForAgentRun` (added by 40-2) recomputed on resume
- `docs/stories/epic-40/story-40-2/implementation-plan.md` — the `ForAgentRun` name contract + completion payload shape (lockstep)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/AgentSelectionRepository.cs:7-18` + `PromptRepository.cs:17-23` — **the dual-scoping precedent this story copies** (CP context for single-user, tenant schema for SaaS; `principal_xor`; no method joins both planes)
- `apps/tamma-elsa/src/Tamma.Data/` — `TenantDbContext`, `ControlPlaneDbContext`, `TammaModelConfiguration` (see `ConfigureAgentRoleSelections(modelBuilder, fixedTenantId: null)` for how one entity is mapped into both contexts)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:249` (`AddTammaData`) and `:3113` (`POST /api/v1/agent-dispatch/{owner}/{repo}/runs`, `EngineServiceOnly`) — why the row is written API-side; `Tamma.ElsaServer/Program.cs` registers **no** `ITenantDbContextFactory`
- `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs` — `TenantFreePathPrefixes` includes `/api/github/webhooks`, so the webhook handler has **no ambient tenant**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — the landed hosted-service + advisory-lock leader-election shape the AC4 reconciler follows (`IRollupSchedulerLeaderLock`, `PostgresAdvisoryLeaderLock`, `pg_try_advisory_lock`); note its lock key has **no** principal component — do not copy that part
- how a bookmark is resumed programmatically in-engine: the Elsa `IBookmarkQueue`/bookmark-resume API used by `DocumentDecisionResumeEndpoint` (copy that call)
- *Corrected:* an earlier draft listed `DocumentDecisionResumeEndpoint` (39-8) as **NOT FOUND**. It exists. The only genuine forward dependency is `WaitForAgentRunActivity` + `ForAgentRun` (40-2) — see Dependencies & Sequencing.

## Design Decisions

- **D1 — The signal row is a correlation record, NOT a queue.** `agent_run_waits` exists solely
  to give the webhook the `session_id`/`bookmark_name`/`workflow_instance_id` it cannot derive
  from the `workflow_run` payload. Delivery itself rides Elsa's persistent bookmark store (any
  pod resumes a DB bookmark). No RabbitMQ, no new bus — the epic's "bookmark store is the
  backplane" principle. Written on the **API side** during the mediated dispatch (D7 — *not*
  by 40-2's `Execute`, which runs in a host with no tenant DB); read by the
  webhook/endpoint/reconciler.
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
  shape, principal-folded (tenant in SaaS, the sole user in single-user). It is the
  manual/retry surface; the webhook path is the automatic one.
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
- **D7 — The row is DUAL-SCOPED, and written on the API side.** *Corrected: the earlier D7
  ("lives in the tenant schema … single-user mode uses the central schema like every other
  tenant") does not hold. `tenantId` is `Guid?` through the whole mediation surface
  (`IAgentDispatchMediationService.cs:20`; `AgentDispatchEndpoints.cs:41`), the inbound webhook
  path is tenant-free (`TenantContextMiddleware.TenantFreePathPrefixes` contains
  `/api/github/webhooks`), and the engine host that was supposed to write the row registers no
  `ITenantDbContextFactory` at all (`AddTammaData` is called only at
  `Tamma.Api/Program.cs:249`).* So:
  - **Shape:** one `AgentRunWait` entity mapped into **both** `TenantDbContext` (SaaS,
    `tenant_id` set) and `ControlPlaneDbContext` (single-user, `user_id` set), `principal_xor`
    CHECK, parallel repository methods per mode with no cross-plane join — the landed
    `AgentSelectionRepository` / `PromptRepository` discipline.
  - **Writer:** `AgentDispatchMediationService`'s trigger path, not the engine activity. The
    engine passes `bookmarkName` + `workflowInstanceId` on the mediated request; everything
    else the API already has.
  - **Migrations:** two — one against `TenantDbContext` (subject to the single-migration-author
    token) and one against `ControlPlaneDbContext`. Budget both.
- **D8 — The AC4 reconciler is a real hosted `BackgroundService` with principal-folded
  advisory-lock leader election, registered in BOTH hosts.** Shape copied from
  `HourlyAnalyticsRollupScheduler`: `BackgroundService` + options section with `Enabled` +
  poll interval + an `IAgentRunReconcilerLeaderLock` abstraction whose production impl runs
  `SELECT pg_try_advisory_lock(@k)` on a transient `NpgsqlConnection` (tests inject a
  deterministic in-memory lock). **The lock key folds the principal**, unlike
  `ComputeAdvisoryLockKey(year, dayOfYear, hour)` which is global — a global key would let one
  principal's leader starve every other principal's sweep.

  *Placement — this closes the "who hosts it in single-user mode?" question rather than
  leaving it open.* The reconciler **class lives in `Tamma.Activities/AgentDispatch/`**, the
  lowest assembly that has everything it needs: `IAgentRunWaitRepository`
  (`Tamma.Activities → Tamma.Data`), the mediated poll
  `TammaApiClient.GetAgentRunAsync` (`Tamma.Activities/LlmCall/TammaApiClient.cs:324`), and
  `IAgentRunResumer`. It is therefore hostable from **both** processes, since
  `Tamma.ElsaServer → Tamma.Activities` while `Tamma.ElsaServer` does **not** reference
  `Tamma.Api`. Register it:
  - in **`Tamma.Api`** (SaaS) — the host that reaches both planes;
  - in **`Tamma.ElsaServer`** (single-user self-hosted) — which registers
    `ControlPlaneDbContext` whenever `ConnectionStrings:DefaultConnection` is set, i.e. exactly
    where single-user rows live, and has no tenant plane to miss.

  Both registrations are `Enabled`-gated, and `PostgresAdvisoryLeaderLock`'s connection source
  (`ConnectionStrings:DefaultConnection`) exists in both hosts, so leader election works
  unchanged. No mode is left with an unhosted sweep — which is what would have made AC8's
  "no dangling row" vacuously true.

## Implementation Steps

1. **CREATE the entity + BOTH migrations** — `apps/tamma-elsa/src/Tamma.Data/Entities/AgentRunWait.cs`
   (D1 fields + `user_id`), mapped by `TammaModelConfiguration` into **both**
   `TenantDbContext` and `ControlPlaneDbContext` (D7; follow
   `ConfigureAgentRoleSelections`): `principal_xor` CHECK, unique on the principal +
   `(repository, branch_name, session_id)`, index on `(installation_id, repository,
   branch_name, status)`. Generate the `TenantDbContext` migration (hold the migration token)
   **and** the `ControlPlaneDbContext` migration. CREATE `IAgentRunWaitRepository` +
   `AgentRunWaitRepository` (`apps/tamma-elsa/src/Tamma.Data/Repositories/`) with parallel
   per-mode methods, no cross-plane join — `AgentSelectionRepository` is the template.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentRunResumer.cs` +
   `AgentRunResumer.cs`** (D3) — load row → recompute + assert `ForAgentRun` name → resume
   bookmark (Elsa bookmark runtime, the `DocumentDecisionResumeEndpoint` call shape) → mark
   `received`; idempotent; optional in-memory fast-path (D6). It resolves its principal from
   the match key's installation id — **never** from `ITenantContext`, which is unbound on the
   webhook path. Emits the loud diagnostic on unresolvable row (AC8) — the constant is 40-6's;
   use a placeholder pinned to 40-6. *(Placement note: `Tamma.Activities` is the only assembly
   both hosts can reach — `Tamma.ElsaServer` does not reference `Tamma.Api` — so the resumer,
   like the reconciler in D8, must live here.)*

3. **WRITE the pending row on the API side, not in the engine** (D7). *Corrected: the earlier
   step 3 modified `WaitForAgentRunActivity.Execute` to write the row directly — impossible,
   the `Tamma.ElsaServer` host registers no `ITenantDbContextFactory`.* Two edits:
   (a) **MODIFY the mediated dispatch contract** — add `bookmarkName` + `workflowInstanceId` to
   `AgentDispatchRunApiRequest` (`Tamma.Activities/LlmCall/Models/TammaApiModels.cs:490-496`)
   and `DispatchAgentRunRequest` (`Tamma.Api/Services/AgentDispatch/AgentDispatchRequests.cs`);
   40-2's `Execute` populates them (its only cross-story obligation — coordinate with 40-2).
   (b) **MODIFY `AgentDispatchMediationService.TriggerRunCoreAsync`** — on a successful
   dispatch, write the `pending` row through `IAgentRunWaitRepository` using the principal it
   already holds (`Guid? tenantId`), the parsed `owner/name`, `body.Ref`, `body.CorrelationId`
   (= session id) and the resolved installation id. Local mode never reaches this path, so it
   writes no row (it never suspends externally).

4. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs`**
   — `HandleWorkflowRunEvent` (`:377`) is **synchronous** today and must become
   `HandleWorkflowRunEventAsync` (its `case "workflow_run"` arm at `:350` currently returns
   without `await`). After (or instead of) `PublishSignal` (`:477`), call
   `IAgentRunResumer.ResumeAsync` with the `(installation_id, repo, head_branch)` match key +
   the completion payload (D2/D3). Keep the `PublishSignal` fast-path call (D6). Non-matching
   runs stay `Skipped`. **The handler runs with no ambient tenant** (`/api/github/webhooks` is
   in `TenantContextMiddleware.TenantFreePathPrefixes`), so the resumer must resolve the
   principal from the installation id — never from `ITenantContext`. Also update the now-stale
   XML doc on `IAgentDispatchMediationService` that declares the inbound webhook path "out of
   scope".

5. **CREATE `AgentRunResumeEndpoint`** (`apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/`, or
   Tamma.Api alongside the webhook) (D4, AC3) — recompute name → `IAgentRunResumer` → 404/409,
   principal-folded, existing auth.

6. **CREATE the reconciler as a real hosted service** (D5 + **D8**) —
   `AgentRunReconciler : BackgroundService` in `Tamma.Api` (it needs both stores and the
   mediated reads), plus `AgentRunReconcilerOptions` (`Enabled`, poll interval, pending
   threshold, row timeout) and `IAgentRunReconcilerLeaderLock` +
   `PostgresAgentRunReconcilerLeaderLock` (`SELECT pg_try_advisory_lock(@k)` on a transient
   `NpgsqlConnection`), modelled on `HourlyAnalyticsRollupScheduler` /
   `PostgresAdvisoryLeaderLock`. **The lock key folds the principal** — do not reuse the
   rollup's global `(year, dayOfYear, hour)` key. Body: sweep `pending` rows past the
   threshold, mediated poll, resume-if-terminal via `IAgentRunResumer`, mark `timed_out` past
   the row timeout. Register with `AddHostedService` guarded by `Enabled` (the existing
   `RunOnStartup` posture) and **record D8's single-user answer** (register in
   `Tamma.ElsaServer` too). If the dual registration is not done, delete AC4 rather than ship
   an unhosted sweep.

7. **MODIFY `AgentMonitorService`** — relax the "Webhook-without-registry = hard fail" branch to
   route through the durable path (D6, AC6); retain discovery. (The bulk of the live poll wait is
   superseded by 40-2's suspend + this reconciler; keep the discover helper.)

8. **DI registration** (both hosts) — `IAgentRunWaitRepository`, `IAgentRunResumer`,
   `AgentRunReconciler`. **CREATE tests** (see Test Plan). Finish with
   `dotnet ef migrations has-pending-model-changes` (clean after the intended migration) +
   `dotnet test`.

## Data & Migrations

- **New table `agent_run_waits` in BOTH contexts** (D7): `id, tenant_id, user_id, repository,
  branch_name, session_id, installation_id, workflow_instance_id, bookmark_name, dispatched_at,
  status, updated_at`; `principal_xor` CHECK (exactly one of `tenant_id` / `user_id`); unique
  on the principal + `(repository, branch_name, session_id)`; index
  `(installation_id, repository, branch_name, status)`.
  **Two EF migrations, not one:** `TenantDbContext` (SaaS rows) — **subject to the
  single-migration-author token**, coordinate with any concurrent tenant-context migration —
  and `ControlPlaneDbContext` (single-user rows). No data backfill (new in-flight state only).
  *Corrected: the earlier plan budgeted a single tenant-context migration, which leaves
  single-user mode with nowhere to write the row.*

## Events

- **Emits (40-6 constants, placeholder-pinned here):** `AGENT_RUN.RESUME_UNRESOLVED` (loud, on a
  row with no resolvable bookmark past timeout) and reuses 40-6's `AGENT_RUN.RECEIVED`/`TIMED_OUT`
  where the resumer marks the row. If 40-6 has not merged, define a local constant and migrate it
  to 40-6's **`AgentRunWaitEventTypes`** at merge (documented conscious pin). *(Corrected: this line
  said `AgentRunEventTypes`; 32-5 owns that name — `Tamma.Api/Services/Agents/AgentRunEventTypes.cs:17`,
  `AGENT.RUN.*` — so 40-6 D1 renames the new catalogue. The wire strings are unchanged.)*
- **Consumes:** the `workflow_run.completed` webhook payload; the mediated `GET runs/{id}` status.

## Test Plan

All NUnit + FluentAssertions (+ Moq; Testcontainers for the cross-pod scenario, shared with 40-7).

- **`AgentRunWaitRepositoryTests`** (unit/Testcontainers) — upsert pending, unique constraint,
  `principal_xor` rejection (both ids set / neither set), match by
  `(installation_id, repo, branch, status=pending)`, mark received/timed_out. **Run the whole
  matrix twice: SaaS (tenant schema) and single-user (`ITenantContext.TenantId == null`, CP
  context).** *Falsifiable:* an implementation that only reaches `ITenantDbContextFactory`
  fails every single-user case. **Covers AC1.**
- **`AgentRunResumerTests`** (unit, Moq'd repo + bookmark runtime) — resume marks received +
  resumes the named bookmark; name-drift (stored ≠ recomputed) → loud fail, no resume; already-
  received → idempotent no-op; already-burned bookmark → logged no-op; ambiguous match → 409/skip;
  installation-scoping (tenant A payload never resumes tenant B row). **Covers AC2, AC7, AC8.**
- **`AgentRunResumeEndpointTests`** (unit/API) — 404 no row, 409 ambiguous, 200 resume,
  principal-fold rejection (both modes), auth. **Covers AC3.**
- **`AgentRunReconcilerTests`** (unit, Moq'd mediation + in-memory leader lock) —
  pending-past-threshold + terminal run → resume; still-running → leave pending; past
  row-timeout → `timed_out` + loud event; **two instances over one store resume each row
  exactly once**; **two different principals sweep concurrently** (both acquire their lock).
  *Falsifiable:* a global advisory-lock key (the rollup scheduler's shape) fails the last case.
  **Covers AC4, AC8.**
- **`InstallationRouterWorkflowRunResumeTests`** (unit) — `workflow_run.completed` → resolver →
  `IAgentRunResumer` called with the right key; non-Tamma run → Skipped; **resume succeeds with
  `ITenantContext.TenantId` null** (the webhook path is tenant-free). **Covers AC2.**
- **Cross-pod integration** (Testcontainers, shared with 40-7 step) — dispatch+suspend on host A;
  dispose A; deliver webhook via a fresh host B on the same store; assert resume on B, registry
  unwired. **Covers AC5, AC6.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — durable dual-scoped signal row, written API-side | 1, 3 | `AgentRunWaitRepositoryTests` (both modes) |
| 2 — webhook resolves row + resumes bookmark | 2, 4 | `AgentRunResumerTests`, `InstallationRouterWorkflowRunResumeTests` |
| 3 — resume endpoint (39-8 shape, 404/409) | 5 | `AgentRunResumeEndpointTests` |
| 4 — durable poll reconciler, hosted + leader-elected | 6 | `AgentRunReconcilerTests` (incl. two-instance and two-principal cases) |
| 5 — cross-pod delivery proven | 8 | Cross-pod integration (with 40-7) |
| 6 — in-memory registry demoted, not required | 2, 7 | Cross-pod integration (registry unwired); `AgentMonitorService` mode test |
| 7 — principal + installation folded | 2, 5 | `AgentRunResumerTests` scoping cases (incl. single-user null-tenant) |
| 8 — fail-loud, exactly-once resume | 2, 6 | `AgentRunResumerTests`, `AgentRunReconcilerTests` idempotency/timeout cases |

## Dependencies & Sequencing

- **Hard prerequisite:** 40-2 (`WaitForAgentRunActivity` + `ForAgentRun` — the bookmark this
  story resumes; step 3a asks 40-2's `Execute` to populate two new mediated-request fields).
  Blocking.
- **39-10 — LANDED.** `LifecycleBookmarks` is shipped; the resume recomputes through it.
- **39-8 — LANDED, mirrored not consumed.** *Corrected: previously "soft … can proceed against
  `DesignResumeEndpoint` if 39-8 is not yet in".* `DocumentDecisionResumeEndpoint.cs` exists —
  copy its 404/409 + fold posture directly for step 5.
- **Migration ordering:** two migrations. The `TenantDbContext` one takes the single
  migration-author token (rebase its snapshot onto whatever tenant migration precedes it at
  merge); the `ControlPlaneDbContext` one is additive and independent.
- **In place, verified:** `InstallationRouterService` webhook receiver (sync `HandleWorkflowRunEvent`
  — step 4 makes it async), `AgentMonitorService` poll/discover, `AgentDispatchMediationService`
  mediated reads (and the new row-write site), Elsa persistent bookmark runtime,
  `TenantDbContext` + `ControlPlaneDbContext`, `HourlyAnalyticsRollupScheduler`'s hosted-service
  + advisory-lock shape.
- **Closed by D8 (was open):** which host runs the reconciler in single-user mode — the class
  lives in `Tamma.Activities` and is registered in **both** hosts, so SaaS sweeps from
  `Tamma.Api` and single-user sweeps from `Tamma.ElsaServer`.
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
- **Single-user mode is designed out by accident.** The obvious design (one tenant-schema
  table, written by the engine activity) is unbuildable in single-user mode and unbuildable
  from the engine host at all. Mitigation: D7's dual-scoped entity + API-side writer, and the
  repository test matrix that must pass with a null tenant id.
- **The reconciler ships hosted nowhere.** A `BackgroundService` that is never registered
  (or is registered only where `Enabled` defaults false) makes AC8's "no dangling row"
  vacuously true. Mitigation: D8 names the host and the leader lock; the story authorises
  deleting AC4 outright rather than shipping an unhosted sweep.
- **A global advisory-lock key starves other principals.** Copying
  `ComputeAdvisoryLockKey(year, dayOfYear, hour)` verbatim would let one principal's leader
  block every other principal's sweep. Mitigation: D8's principal-folded key + the
  two-principal concurrency test.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | dual-mapped entity + **two** migrations (Tenant + CP) + dual-scoped repository | 1.5 |
| 2 | `AgentRunResumer` (resume + idempotency + name guard + fast-path) | 1.25 |
| 3 | mediated-request fields + API-side row write (cross-story with 40-2) | 0.75 |
| 4 | `InstallationRouterService` sync→async + resolve+resume wiring | 0.75 |
| 5 | `AgentRunResumeEndpoint` | 0.75 |
| 6, 7 | hosted reconciler + principal-folded leader lock + `AgentMonitorService` mode relax | 1.5 |
| 8 | DI + unit tests (repo × 2 modes, resumer, endpoint, reconciler, router) | 1.75 |
| **Total** | | **8.25** (story estimate: 5-7 days — **the dual-scoping + hosted-reconciler corrections push this over; re-estimate to 7-9 days or split the reconciler out**) |

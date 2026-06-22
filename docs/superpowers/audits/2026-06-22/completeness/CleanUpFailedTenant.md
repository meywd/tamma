# Completeness Audit — `CleanUpFailedTenantWorkflow`

**Date:** 2026-06-22
**Auditor:** automated completeness assessment (read-only)
**Verdict:** **PARTIAL** — core teardown is solid, decomposed, idempotent and audited; meaningful scope gaps remain vs the broader delete-flow spec.

---

## 1. Purpose & owner

Operator-triggered, best-effort teardown for a tenant left in a damaged state (half-provisioned, half-deleted, or failed provisioning compensation). Owned by **Epic 28 / Story 28-5 AC7** ("`CleanUpFailedTenantWorkflow` operator sidecar").

- Workflow: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CleanUpFailedTenantWorkflow.cs`
- Trigger bridge: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TenantCleanupRequestedTrigger.cs`
- Endpoint: `POST /api/admin/tenants/{id}/cleanup` in `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` (`CleanupTenant`)
- Step activities (all in `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/`):
  - `EvictTenantPoolForCleanupActivity` (`EvictTenantPoolActivity.cs`)
  - `DropTenantSchemaForCleanupActivity` (`DropTenantSchemaActivity.cs`)
  - `DropTenantRoleForCleanupActivity` (`DropTenantRoleActivity.cs`)
  - `SoftDeleteTenantRowActivity` (`SoftDeleteTenantRowActivity.cs`)
  - `EmitCleanupTerminalEventActivity` + `CleanupStepActivity` base + `CleanupWorkflowState` (`EmitCleanupTerminalEventActivity.cs`)
  - `CleanupFailureClassifier` (`CleanupFailureClassifier.cs`)

---

## 2. Maturity: **PARTIAL**

This is NOT a thin happy-path skeleton. It is a thoughtfully decomposed, replay-safe, audited workflow that fully satisfies its narrow AC7. It is rated **partial** (not complete) because the *cleanup* path diverges from the broader documented tenant-teardown scope (Doc 04 §6.3): it skips the optional pre-destroy backup and the CP-side related-row cleanup, and the documented incident-strategy defense-in-depth is never actually wired.

---

## 3. Current capabilities (what it actually does today)

- **Real event-driven trigger.** Root `Event("tenant-cleanup-requested")` starter is indexed by Elsa as a stored trigger; `TenantCleanupRequestedTrigger` (a `BackgroundService`) polls `platform_events` for `TENANT.CLEANUP.REQUESTED` rows and re-publishes via `IEventPublisher.PublishAsync` (2s cadence, 25-row cap, in-memory high-water cursor that starts at boot max so redeploys don't replay history, correlation by tenant id, cursor-not-advanced-on-dispatch-failure retry). Closes the Round-2 "M3" integration cliff where the endpoint was a no-op against the activity.
- **Input normalization with hard guard.** `InitInputs` (`SetVariable`) parses `tenantId` (Guid-or-string) into a typed workflow variable and **throws** on `Guid.Empty` — honors no-silent-failure.
- **Per-step Elsa decomposition (the H6 fix).** Four sibling best-effort steps + one terminal step under a `Sequence`, replacing the old 200-line single-activity mini-orchestrator. Each step gets its own Elsa replay/cancel/observability boundary.
- **Continue-on-error per step.** `CleanupStepActivity` base catches its own exception, classifies + redacts it, records into persisted workflow variables (`CleanupWorkflowState`), emits `TENANT.DELETE.STEP_FAILED`, and returns normally so siblings still run. Cooperative cancellation (`OperationCanceledException` on a fired CT) is re-thrown, not swallowed.
- **Idempotent destructive ops.** Evict = no-op if not cached; `DROP SCHEMA IF EXISTS … CASCADE`; `DROP OWNED BY` → `DROP ROLE IF EXISTS` with probe-before-drop; placement-aware (reads `DatabaseId`/`SchemaName` shadow, legacy central fallback). Soft-delete is idempotent and releases placement (`TenantPlacementShadow.ReleaseAsync`).
- **Single terminal-event invariant.** `EmitCleanupTerminalEventActivity` reads the accumulator and emits exactly one of `TENANT.DELETED.SUCCESS` (all 4 steps ok) or `TENANT.DELETE.FAILED` (with `failedSteps`, `succeededSteps`, redacted `stepDetails`, `requiresManualCleanup:true`). On partial failure stamps `tenants.ProvisioningState='requires_manual_cleanup'` + a length-bounded `ProvisioningDetail`; on full success sets `none`.
- **Rich, stable failure taxonomy.** `CleanupFailureClassifier` maps `(step, exception)` to a fixed vocabulary (`drop_schema_failed`, `drop_role_failed`, `network_error`, `permission_denied`, `evict_pool_failed`, `cancelled`, `step_failed`) with a redactor-scrubbed, 200-char-bounded snippet — dashboards/alerts group on these.
- **No-PII discipline.** Errors redacted via `IErrorRedactor`; event payloads carry no SQL/connection strings/emails. Endpoint enforces 2FA-lite (`X-Admin-Confirm` must echo the tenant id) and sanitizes the `X-Admin-Note` charset/length before it reaches `platform_events`/`ProvisioningDetail`.
- **No external-provider calls.** Pure CP/Postgres teardown — does not touch the LLM/agent mediation surface, so the 32-5 / Epic 38 mediation rules don't apply to this workflow.

---

## 4. Intended full scope (with citations)

- **AC7 (the explicit contract)** — `docs/stories/epic-28/story-28-5/28-5-create-delete-tenant-workflows.md` §AC7 (lines 170-177): separate idempotent global-Elsa workflow, probes before each delete step, triggered by `POST /api/admin/tenants/{id}/cleanup` (platform admin only); each step logs `TENANT.DELETE.STEP_*`; terminal success = `TENANT.DELETED.SUCCESS`, terminal failure = `TENANT.DELETE.FAILED` + `RequiresManualCleanup=true`. **All AC7 line-items are met by the current code.**
- **The broader teardown scope that cleanup is the recovery sidecar for** — AC4 / Doc 04 §6.3 (same story, lines 107-143) defines the full delete flow the cleanup is meant to *complete* when a delete went sideways:
  - Step C (optional) **pg_dump backup** when `Backup:DeletionBackup=true` (Doc 04 §9). The sibling `DeleteTenantWorkflow` *does* run this (`BackupTenantDatabaseActivity`, step B2, between evict and drop-schema). The cleanup workflow does **not**.
  - Step I **CP-side related-row cleanup**: delete `tenant_memberships`, `user_invites`, nullify `github_installations.TenantId`, delete CP rows in `platform_queued_tasks` referencing the tenant. **Neither** `DeleteTenantWorkflow` nor `CleanUpFailedTenantWorkflow` performs this today (the soft-delete activities only stamp the `tenants` row + release placement).
- **Single terminal event invariant** — Story 28-5 dashboard timeline (28-11) relies on exactly one terminal event per run; honored.
- **Domain best-practice for a "best-effort destructive teardown / recovery" flow:** snapshot-before-destroy when a backup flag is on; clean up all dependent/orphan rows so the tenant id can't dangle in membership/installation/queue tables; make the operator-facing failure path actionable (it is); avoid silent success when the CP row is unreachable (the terminal event still fires, but see gaps below for the empty-tenant-id early-return).
- **Mediation specs (`docs/superpowers/specs/2026-06-20-*.md`)**: reviewed — not applicable. This workflow performs no LLM/agent/git work; it is infrastructure teardown only.

---

## 5. Missing capabilities (gap to "complete")

| # | Capability | Priority | dependsOn |
|---|------------|----------|-----------|
| 1 | **CP-side related-row cleanup** (Doc 04 §6.3 step I): delete `tenant_memberships`, `user_invites`, nullify `github_installations.TenantId`, delete `platform_queued_tasks` for the tenant. Today these orphan — a damaged tenant gets soft-deleted but members/invites/installs/queued tasks dangle. | P1 | none (story 28-5 step I, shared with DeleteTenantWorkflow) |
| 2 | **Optional pre-destroy pg_dump backup** before `DROP SCHEMA … CASCADE` (Doc 04 §9; parity with `DeleteTenantWorkflow` step B2 / `BackupTenantDatabaseActivity`, gated by `Backup:DeletionBackup`). Cleanup currently destroys tenant data with no snapshot even when the flag is on. | P1 | none (reuse `BackupTenantDatabaseActivity` as a continue-on-error cleanup variant) |
| 3 | **Wire `WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy)`** in `Build()`. The class doc claims this as defense-in-depth, but it is never set. Functionally the per-step swallow covers it; the documented safety net is absent (a future step that forgets to inherit `CleanupStepActivity`, or an exception in `InitInputs`/terminal, would abort the run). | P2 | none |
| 4 | **Empty/unresolvable tenant-id terminal path emits no event.** `EmitCleanupTerminalEventActivity` early-returns (log-only) when `tenantId == Guid.Empty`. A cleanup run that lost its tenant id produces step failures but **no terminal record** — violates the "one terminal event per run" contract and leaves the dashboard with a run that never concludes. Should emit `TENANT.DELETE.FAILED` with an `invalid_input` reason. | P1 | none |
| 5 | **No mark-cleaning state at start.** `DeleteTenantWorkflow` flips `Status`/`ProvisioningState` to a transient "deleting/dropping" at step A; cleanup jumps straight into destructive steps with no in-flight state stamp, so the admin UX shows the old (damaged) state until the terminal event lands. Add a leading `MarkTenantCleaningActivity` (or reuse `MarkProvisioning`) writing `ProvisioningState='cleaning'`. | P2 | 28-11 (dashboard reads it) |
| 6 | **Backend-minted (Cranl/V2) hosting teardown not addressed.** For tenants placed on a Cranl-minted hosting DB (`cranl_database_url_encrypted` populated), schema-drop on the pool DB is correct, but the workflow does not consider deprovisioning/releasing backend-minted infra. May be out of scope for "schema-per-tenant on shared pool", but should be an explicit decision, not an omission. | P3 | Epic 30 / tenant-provisioning move |
| 7 | **No idempotency guard against concurrent cleanup of the same tenant.** Bridge correlates by tenant id, but two `TENANT.CLEANUP.REQUESTED` rows close in time (or a redeploy re-read) can dispatch two instances. Steps are idempotent so it's safe, but a short-lived dedup (e.g. skip if `ProvisioningState='cleaning'` set <N min ago) avoids duplicate terminal events. | P3 | depends on #5 (the cleaning state) |

---

## 6. Ordered build-out spec (to reach complete)

Honors project rules: tenant→system→error (no empty/plain fallback), no silent-failure/false-success, emit DCB/platform audit events, no external-provider calls (none needed here).

1. **Add `MarkTenantCleaningActivity` as the first step (after `InitInputs`, before `EvictTenantPool`).**
   - Continue-on-error variant of the mark-state pattern. Sets `tenants.ProvisioningState='cleaning'`, `ProvisioningUpdatedAt=now()`; emits `TENANT.CLEANUP.STARTED` (step marker, not terminal).
   - Failure edge: record into `CleanupWorkflowState` like any other step; do not abort.
   - Closes gap #5; provides the state #7's dedup can key on.

2. **Insert an optional backup step between `EvictTenantPool` and `DropTenantSchema`.**
   - Add `BackupTenantDatabaseForCleanupActivity` (continue-on-error subclass of `CleanupStepActivity` that calls the existing `BackupTenantDatabaseActivity` logic; reuse, do not fork the pg_dump implementation). Step name e.g. `backup-tenant-schema`.
   - Gated by `Backup:DeletionBackup` — no-op (records success, emits `STEP_COMPLETED` with `skipped:true`) when off, matching `DeleteTenantWorkflow` semantics.
   - Failure edge: a backup failure is recorded as a failed step → contributes to `TENANT.DELETE.FAILED` and `requires_manual_cleanup`, so an operator never silently loses the only snapshot before a CASCADE drop.
   - Closes gap #2.

3. **Add a CP-side related-row cleanup step after `SoftDeleteTenantRow`, before the terminal step.**
   - New `CleanUpTenantRelatedRowsActivity` (continue-on-error, step name `cleanup-related-rows`) executing Doc 04 §6.3 step I in one CP transaction: `DELETE tenant_memberships WHERE tenant_id=@id`, `DELETE user_invites WHERE tenant_id=@id`, `UPDATE github_installations SET tenant_id=NULL WHERE tenant_id=@id`, `DELETE platform_queued_tasks WHERE tenant_id=@id` (confirm exact table/column names against `ControlPlaneDbContext` before implementing).
   - Idempotent (set-based deletes are naturally re-runnable); failure recorded into accumulator → surfaces in the terminal failure summary.
   - Also wire this same step into `DeleteTenantWorkflow` (shared gap) so the two paths converge on the full Doc 04 §6.3.
   - Closes gap #1.

4. **Fix the empty-tenant-id terminal path in `EmitCleanupTerminalEventActivity`.**
   - Replace the log-only early `return` with: emit `TENANT.DELETE.FAILED` carrying `failedSteps`, `stepDetails`, and a synthetic `reason:"invalid_input_no_tenant_id"`, `requiresManualCleanup:true`. (Cannot stamp the row — id unknown — but the run still concludes with exactly one terminal event.)
   - Closes gap #4 + preserves the single-terminal-event invariant.

5. **Wire the incident strategy in `Build()`.**
   - Set `builder.WithWorkflowOptions(o => o.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy))` (verify the exact Elsa 3.5.x builder API — `WorkflowOptions` may be set via `builder.Options` / `WithWorkflowOptions`). This makes the doc-comment's claimed defense-in-depth real, so an uncaught throw in `InitInputs`, the terminal step, or a future non-`CleanupStepActivity` step doesn't abort the whole sequence.
   - Closes gap #3.

6. **Add concurrent-cleanup dedup (depends on step 1).**
   - In `MarkTenantCleaningActivity` (or the bridge), skip dispatch / short-circuit the run if `ProvisioningState='cleaning'` was set within a small window (e.g. 5 min). Emit a `TENANT.CLEANUP.SKIPPED_DUPLICATE` marker for the audit trail rather than a second terminal event.
   - Closes gap #7.

7. **Make an explicit decision on backend-minted (Cranl/V2) hosting teardown.**
   - Either add a guarded `DeprovisionBackendHostingActivity` for tenants with `cranl_database_url_encrypted` set, or document in the workflow XML doc that backend infra deprovisioning is owned by the provisioning/move path (Epic 30) and out of cleanup scope. Don't leave it as a silent omission.
   - Closes gap #6.

### Resulting target sequence

```
InitInputs (throws on empty id)
 → MarkTenantCleaning            (new, step 1)
 → EvictTenantPoolForCleanup
 → BackupTenantDatabaseForCleanup (new, gated, step 2)
 → DropTenantSchemaForCleanup
 → DropTenantRoleForCleanup
 → SoftDeleteTenantRow
 → CleanUpTenantRelatedRows       (new, step 3)
 → EmitCleanupTerminalEvent       (fixed empty-id path, step 4)
+ WithWorkflowOptions(IncidentStrategyType = ContinueWithIncidentsStrategy)  (step 5)
```

---

## 7. Summary

`CleanUpFailedTenantWorkflow` is well-engineered and fully satisfies its explicit AC7 contract: real event trigger, per-step Elsa decomposition with continue-on-error, idempotent placement-aware destructive ops, redacted/stable failure taxonomy, and a single terminal event with a manual-review flag. It is **not** a thin stub. It is rated **partial** because it omits two documented teardown concerns (Doc 04 §6.3 step I CP-side related-row cleanup, and the optional pre-destroy pg_dump backup the sibling delete workflow has), never actually wires the incident-strategy it documents, and has a terminal-event hole on the empty-tenant-id path that breaks the one-terminal-event invariant. Overall priority **P1**, effort **M**.

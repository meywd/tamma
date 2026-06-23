# Completeness Audit — `DeleteTenantWorkflow`

**Date:** 2026-06-22
**Workflow:** `DeleteTenantWorkflow` (DefinitionId `delete-tenant`)
**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeleteTenantWorkflow.cs`
**Maturity:** **partial** (the destructive DB-teardown core is real and idempotent; the integration trigger, the cooling-off/cancellation contract, CP-side relationship cleanup, and the terminal failure path are missing)
**Overall priority:** **P0** (an admin-initiated DELETE never runs this workflow today — the destructive teardown is effectively dead code in production; tenants left in `deleting` with no terminal event)

---

## Purpose & Owner

Story 28-5 (Epic 28 — Database-per-Tenant Isolation, now realised as unified schema-per-tenant). Tears a tenant down: flip to `deleting`, evict the LRU connection pool, optionally `pg_dump`-backup, `DROP SCHEMA … CASCADE` the tenant's `t_<hex>` schema on its assigned pool database, drop the per-tenant Postgres role, then soft-delete the CP row + release the placement slot and emit the terminal `TENANT.DELETED.SUCCESS`. Wall-clock O(1) on tenant data volume (cost is the schema drop, not a row-by-row purge — Epic 28 success metric #3).

Owning spec: `docs/stories/epic-28/story-28-5/28-5-create-delete-tenant-workflows.md` (AC4). Sibling: `CleanUpFailedTenantWorkflow` (AC7) — the operator recovery path, which is substantially more complete than this one.

---

## Current Capabilities (what it actually does today)

Root is a flat `Sequence` of seven activities (no trigger, no branches, no compensation):

1. `InitInputs` (`SetVariable`) — parses `tenantId` input (Guid or string), throws if empty/missing; clamps `attempt` to >= 1.
2. `MarkTenantDeletingActivity` — sets `Status='deleting'` + `DeleteRequestedAt=now()` (idempotent no-op if already deleting); emits `TENANT.DELETE.REQUESTED`.
3. `EvictTenantPoolActivity` — `ITenantConnectionResolver.EvictAsync(tenantId)` so the cached `NpgsqlDataSource` is forgotten before the drop. Throws on resolver failure.
4. `BackupTenantDatabaseActivity` — optional `pg_dump` (placement-scoped `--schema=t_<hex>`, or legacy whole-DB), gated by `Backup:DeletionBackup` (no-op when off); `PGPASSWORD` via env, idempotent skip when schema absent, throws on non-zero exit.
5. `DropTenantSchemaActivity` — `DROP SCHEMA IF EXISTS t_<hex> CASCADE` on the assigned pool DB; no-op when tenant has no placement; idempotent via `IF EXISTS`. Throws on connection failure.
6. `DropTenantRoleActivity` — placement-aware `DROP OWNED BY` then `DROP ROLE IF EXISTS` (pool path, or legacy central); probe-before-drop idempotency.
7. `EmitDeletedSuccessActivity` — soft-deletes the CP row (`DeletedAt`, `Status='deleted'`, null `EncryptedConnectionString`), releases the placement (pool `TenantCount` decrement + `DatabaseId`/`SchemaName` null) in the same `SaveChanges`, emits terminal `TENANT.DELETED.SUCCESS`. Suppresses the generic STEP_* envelope (`EmitStepEvents=false`).

Per-step audit: the `TenantLifecycleActivity` base emits `TENANT.PROVISION.STEP_STARTED/STEP_COMPLETED/STEP_FAILED` (note: PROVISION.* prefix, not DELETE.* — see gaps) around every step except the terminal one, tagged with `step`+`attempt` for the dedup index. Idempotency is genuinely solid throughout: every destructive step probes its target or uses `IF EXISTS`, so an Elsa replay between any two steps is safe.

---

## Intended Full Scope (with citations)

Story 28-5 **AC4** (`28-5-create-delete-tenant-workflows.md` lines 107-143) defines the complete delete flow. Steps A–K, plus the cooling-off + cancellation contract:

- **A** flip status, **B** evict pool(s), **C** optional pg_dump backup, **D** terminate backends (accepted as covered by `WITH (FORCE)` in the old db-per-tenant form; in the schema model `DROP SCHEMA CASCADE` has no equivalent lingering-backend race, so D is moot), **E**/**F**/**G** drop DBs (now collapsed to the single `DROP SCHEMA CASCADE` under unified tenancy), **H** drop role.
- **Step I — CP-side cleanup (MISSING):** "delete `tenant_memberships`, `user_invites`, nullify `github_installations.TenantId`, delete CP rows referencing this tenant in `platform_queued_tasks`." (line 129-131)
- **J** soft-delete the CP row, **K** emit `TENANT.DELETED.SUCCESS`.
- **AC4 cooling-off (MISSING):** "5-minute cooling-off window before Step A by delaying the … trigger (Doc 04 §6.5 + Doc 01 §10.1)." (line 139-140)
- **AC4 cancellation (MISSING):** "Cancellation during the cooling-off window flips `Status='active'` and emits `TENANT.DELETE_CANCELLED`." (line 141-143) — the constant `TenantLifecycleEvents.DeleteCancelled = "TENANT.DELETE_CANCELLED"` exists but is **never emitted anywhere**.
- **AC4 trigger (MISSING):** "triggered by `TENANT.DELETE_REQUESTED` published by `DELETE /api/admin/tenants/{id}`." (line 109-112)

CLAUDE.md project rules that apply: tenant→system→error (never empty/plain fallback); no silent-failure / false-success; emit DCB audit events for every operation; steps never call external providers directly. (This workflow does no LLM/agent/git work, so the 32-5 call-LLM mediation and Epic 38 do **not** apply — it is pure infrastructure teardown.)

Best-practice for a destructive teardown saga: a single durable trigger; a grace window with a cancel path; relationship/foreign-key cleanup so no dangling references survive; exactly one terminal event per run; on partial failure a `requires_manual_cleanup` quarantine state + a terminal FAILED event so the tenant never silently sticks in `deleting`. The sibling `CleanUpFailedTenantWorkflow` already implements every one of these (continue-on-error siblings, single terminal `EmitCleanupTerminalEventActivity`, `ProvisioningState='requires_manual_cleanup'`, `TenantCleanupRequestedTrigger` poller bridge) — it is the reference shape this workflow should converge toward for its failure path.

---

## Missing Capabilities

| # | Capability | Priority | Depends on |
|---|---|---|---|
| 1 | **No trigger / dispatch path.** Root `Sequence` has no Elsa `Event` starter and there is **no `TenantDeleteRequestedTrigger` poller bridge** (only `TenantCleanupRequestedTrigger` exists). `AdminTenantsEndpoints.DeleteTenant`/`ForceDeleteTenant` emit `TENANT.DELETE.REQUESTED` but nothing consumes it for `delete-tenant`. Endpoint header (lines 64-69) confirms "wired via the Elsa trigger in a follow-up". The workflow is **undispatchable in production** — admin DELETE flips the row but never runs the teardown. | P0 | none (mirror `TenantCleanupRequestedTrigger`) |
| 2 | **No terminal `TENANT.DELETE.FAILED` / quarantine.** On any mid-sequence throw the `Sequence` aborts; the tenant is left in `deleting`, no terminal event, no `ProvisioningState='requires_manual_cleanup'`. Only a step-scoped `STEP_FAILED` is emitted. Violates "no silent-failure" + "exactly one terminal event". | P0 | none |
| 3 | **AC4 Step I — CP-side relationship cleanup absent.** `tenant_memberships`, `user_invites` not deleted; `github_installations.TenantId` not nulled; `platform_queued_tasks` (and other CP `TenantId`-keyed rows: `ApiKey`, `AlertChannel`, `BillingCustomer`, etc.) left dangling against a soft-deleted tenant. | P0 | none |
| 4 | **No cooling-off window (AC4).** The 5-min grace before the destructive drop does not exist; the workflow doc claims it is "held by the trigger/queue" but no trigger/queue exists. Destructive drop would run immediately. | P1 | item 1 (lives in the trigger) |
| 5 | **No cancellation path (AC4).** `TENANT.DELETE_CANCELLED` constant defined but never emitted; no endpoint/branch flips `deleting`→`active` during the grace window. | P1 | items 1, 4 |
| 6 | **Wrong event prefix.** Base emits `TENANT.PROVISION.STEP_*` for delete steps; spec (AC4/Doc 03 §2.1) + the existing `DeleteStepStarted/Completed/Failed = TENANT.DELETE.STEP_*` constants want `DELETE.*`. Pollutes the provision timeline and the 28-11 dashboards. | P1 | none |
| 7 | **`attempt` plumbed but never advanced.** `Attempt` rides every step but no retry policy increments it; on Elsa restart it replays at `attempt=1`. AC3-style per-step retry schedule (Doc 03 §5.1) is not configured. | P2 | none |
| 8 | **Backup live path never exercised.** `Backup:DeletionBackup` defaults off; the `pg_dump` end-to-end run (binary present + flag on) is wiring-tested only, never run in CI. Restore-from-dump is undocumented/untested. | P2 | none |
| 9 | **No soft-timeout / SLOW signal.** Doc 03 §5.2 budget (soft 15 min `…SLOW`, hard 2h) is not wired for the delete path. | P3 | none |

---

## Build-out Spec (ordered)

Reach "complete" by converging on the `CleanUpFailedTenantWorkflow` shape for the failure path and closing the integration cliff. All steps emit DCB audit events; no external-provider calls (none needed); honour no-silent-failure.

1. **Add the dispatch bridge (P0, item 1).** Create `TenantDeleteRequestedTrigger : BackgroundService` in `Tamma.ElsaServer/Workflows/`, modelled on `TenantCleanupRequestedTrigger`: poll `platform_events` for `TENANT.DELETE.REQUESTED` rows past an in-process high-water cursor, and `IEventPublisher.PublishAsync(DeleteRequestedEventName, correlationId=tenantId, payload={tenantId, attempt})`. Add `public const string DeleteRequestedEventName = "tenant-delete-requested"` to `DeleteTenantWorkflow` and prepend `new Event(DeleteRequestedEventName)` to the root `Sequence` (before `InitInputs`). Register the hosted service + options in `Tamma.ElsaServer/Program.cs`. **Event:** none new (consumes existing `TENANT.DELETE.REQUESTED`). **Failure edge:** dispatch failure → log WARN, do not advance cursor (retry next tick) — same as the cleanup bridge.

2. **Cooling-off + cancellation (P1, items 4+5).** In the trigger bridge, do not dispatch until `now - DeleteRequestedAt >= CoolingOff` (default 5 min, configurable). Before dispatch, re-read the tenant: if `Status != 'deleting'` (an operator cancelled), skip dispatch and do nothing. Add `POST /api/admin/tenants/{id}/actions/cancel-delete` (platform-owner only): if `Status='deleting'` and within the window, flip `Status='active'`, clear `DeleteRequestedAt`, invalidate the status cache, and emit **`TENANT.DELETE_CANCELLED`** (use the existing constant). **Failure edge:** illegal transition (not `deleting`, or grace elapsed) → 409.

3. **Convert the body to continue-on-error + a single terminal activity (P0, item 2).** Re-shape steps 3-6 to derive from `CleanupStepActivity` semantics (catch-record-continue) OR keep them throwing but wrap the destructive span so a failure routes to a new terminal step rather than aborting. Add `EmitDeleteTerminalEventActivity` (mirror `EmitCleanupTerminalEventActivity`): read accumulated per-step state; if all destructive steps succeeded → soft-delete row + release placement + emit `TENANT.DELETED.SUCCESS` (fold today's `EmitDeletedSuccessActivity` in here); if any failed → emit **`TENANT.DELETE.FAILED`** with a `failedSteps` array and set `tenants.ProvisioningState='requires_manual_cleanup'`, leaving `Status` recoverable by `POST /cleanup`. **Events:** `TENANT.DELETED.SUCCESS` | `TENANT.DELETE.FAILED`. Exactly one terminal event per run.

4. **Add AC4 Step I — CP-side relationship cleanup (P0, item 3).** New `CleanupTenantRelationshipsActivity` slotted between `DropTenantRoleActivity` and the terminal step (idempotent, single `SaveChanges`): delete `tenant_memberships` and `user_invites` for the tenant; null `github_installations.TenantId`; delete pending `platform_queued_tasks` for the tenant; audit-disposition the other CP `TenantId`-keyed rows (`ApiKey`, `AlertChannel`, `BillingCustomer`, `TenantAgentEnablement`, `PromptOverride`, …) per data-retention policy (delete vs. keep-for-audit — decide per table, do **not** silently leave FK dangles). **Event:** rides the step `STEP_COMPLETED/STEP_FAILED` envelope. **Failure edge:** a row-delete failure records into the per-step state so the terminal step emits `DELETE.FAILED` + quarantine (item 3 above).

5. **Switch the delete step events to the `DELETE.*` prefix (P1, item 6).** Give `TenantLifecycleActivity` (or a delete-specific base) a `StepEventFamily` hook so delete steps emit `TENANT.DELETE.STEP_STARTED/COMPLETED/FAILED` (constants already exist) instead of `PROVISION.*`. **Events:** corrected per-step envelope.

6. **Per-step retry schedule + attempt advance (P2, item 7).** Configure Elsa per-step retry (Doc 03 §5.1: ~10s/30s/2m, max 3) so transient SQL-state failures retry and `attempt` actually increments — the step-dedup index + `IF EXISTS` idempotency already make replays safe.

7. **Soft-timeout signal (P3, item 9).** Emit `TENANT.DELETE.SLOW` if the run exceeds the soft budget; enforce the hard ceiling via Elsa workflow timeout → route to the `DELETE.FAILED` terminal.

8. **Exercise the backup path (P2, item 8).** Add an integration test (Testcontainers) that flips `Backup:DeletionBackup=true`, runs a real `pg_dump` of a seeded `t_<hex>` schema, and asserts a restorable dump file; document the restore runbook. Verify dump contains only the tenant's schema (no neighbour leakage on the shared pool DB).

---

## Verdict

The destructive core (evict → backup → drop schema → drop role → soft-delete + placement release) is real, idempotent, and audit-stamped — this is **not** a happy-path stub. But it is **partial**, gated by a P0 integration gap: with no trigger/bridge it never runs from the admin DELETE today, and it lacks the terminal-failure quarantine, CP relationship cleanup, and cooling-off/cancel contract that its own AC4 spec mandates and that its sibling `CleanUpFailedTenantWorkflow` already demonstrates. Closing items 1-3 is the minimum to make tenant deletion correct, safe, and non-silently-failing.

**Effort:** L (trigger bridge + terminal-event refactor + relationship-cleanup activity + cancel endpoint + tests; bounded, single-domain, reference impl exists in the cleanup sibling).

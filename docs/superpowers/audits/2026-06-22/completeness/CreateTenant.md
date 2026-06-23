# Completeness Audit — `CreateTenantWorkflow`

**Date:** 2026-06-22
**Workflow:** `CreateTenantWorkflow` (Elsa definition id `create-tenant`)
**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/*`

---

## Purpose & Owner

Provision a new SaaS tenant end-to-end: assign a `tenant_databases` pool placement by plan
tier, mint the per-tenant Postgres role + `t_<hex>` schema, migrate + seed it, encrypt and
persist the connection string, warm the pool, flip the tenant `active`, and queue the welcome
email — with a full `TENANT.*` DCB audit trail in `platform_events`.

**Owner:** Epic 28 (Database/Schema-per-Tenant Isolation), **Story 28-5**
(`docs/stories/epic-28/story-28-5/28-5-create-delete-tenant-workflows.md`). Story status reads
`DONE (2026-06-05)`, but that DONE covers AC1/AC2/AC5 follow-ups only — AC3 (compensation +
retry + timeouts) was never built into this definition, and the AC1 trigger consumer was never
wired (see below).

---

## Maturity: **partial**

The *per-step activity layer is genuinely complete and production-grade* — every step is
idempotent, replay-safe, emits `STEP_STARTED/COMPLETED/FAILED` DCB events with `step`+`attempt`
tags (dedup index), redacts secrets from failure events, and has explicit refuse-to-resurrect
guards (`MarkTenantActiveActivity` rejects flipping a `deleted`/`suspended` tenant to `active`).
This is NOT a "thin happy-path skeleton" at the activity level.

The **gap is at the workflow/orchestration level and the integration boundary**, and it is
material:

1. **The workflow is orphaned — it is never dispatched.** Unlike `CleanUpFailedTenantWorkflow`
   (which has an `Event("tenant-cleanup-requested")` trigger node at its sequence root PLUS a
   `TenantCleanupRequestedTrigger` bridge in `Tamma.Api` that republishes the platform event
   into Elsa's `IEventPublisher`), `CreateTenantWorkflow`'s `Sequence` root has **no `Event`
   trigger node** and there is **no `TenantProvisioningRequestedTrigger` bridge** anywhere in
   the repo. `grep` for `create-tenant` / `CreateTenantWorkflow` finds only the definition, doc
   comments, and the shared service interface — **zero dispatchers**. The workflow IS registered
   with Elsa (`elsa.AddWorkflowsFrom<LlmCallWorkflow>()` in `Program.cs:119` scans the assembly),
   so it is published as `create-tenant`, but nothing fires it.

2. **The verify-email "trigger" is an audit breadcrumb, not a dispatch.**
   `AuthEndpoints.TryTriggerProvisioningForOwnedTenantsAsync` flips owned `pending_verification`
   tenants to `provisioning` and emits `TENANT.PROVISIONING_REQUESTED`, but its own comments say
   the emission is *"the trigger source the future Elsa workflow listens on"* and *"the Elsa
   trigger that consumes `TENANT.PROVISIONING_REQUESTED` is not yet wired in production"*
   (story doc lines 339-341, 383-384). It is conditional precisely because wiring an
   unconditional flip would 503 live signups, since no consumer drains the event.

3. **SaaS provisioning actually runs via a parallel synchronous path.** The real implementation
   is `TenantProvisioningService.ProvisionAsync` (`Tamma.Api/Services/Provisioning/`), which
   re-implements assign-placement → create-role → create-schema → migrate → encrypt → activate
   *inline* and is what the single-user middleware and the V2/Cranl path use. A comment in that
   service even reads *"moot post-Task-4 (CreateTenantWorkflow activity deleted, zero rows in
   prod)"* — i.e. this Elsa workflow is treated as dead in prod. So Story 28-5's central
   directive ("SaaS tenant creation is owned by the async `CreateTenantWorkflow`") is **not the
   reality**; the workflow is a fully-built but disconnected artifact.

4. **No compensation, no retry schedule, no timeouts (AC3 entirely missing).** The `Sequence`
   is purely linear; failure just throws and the workflow faults. There is no reverse-order
   compensation ladder, no per-step retry policy, no `IncidentStrategy`, no soft-timeout
   `TENANT.PROVISION.SLOW`, no hard-timeout abort, and no terminal `TENANT.PROVISION.FAILED`
   emission. The class doc-comment hand-waves this as "owned by the call-site" — but the
   call-site doesn't exist, so on any mid-run failure a half-provisioned tenant is left stuck in
   `provisioning` with no terminal event and no compensation. (`CleanUpFailedTenantWorkflow` is
   operator-triggered only — `POST /api/admin/tenants/{id}/cleanup` — it is NOT auto-invoked on
   create failure.)

Net: rated **partial** rather than **thin** because the building blocks are real and robust, but
rather than **complete** because the workflow is undispatched, has no failure/compensation/timeout
contract, and the spec's headline AC3 is unimplemented.

---

## Current Capabilities

Linear `Sequence` of 11 nodes (`InitInputs` + 10 steps), all idempotent + replay-safe:

1. `InitInputs` (`SetVariable`) — parses `tenantId` (Guid|string), throws on empty; clamps `attempt` ≥ 1.
2. `MarkProvisioningActivity` — sets `Status='provisioning'`; no-op if already `active` (sets `tenant.skip_provision` property); no-op if already `provisioning`.
3. `AssignTenantPlacementActivity` — picks `tenant_databases` pool row by tier via `ITenantPlacementService`; outputs `DatabaseId` + `SchemaName`; idempotent re-assign returns existing.
4. `CreateTenantRoleActivity` — `CREATE ROLE tamma_tenant_<hex>` with a 32-byte strong password on the placement cluster; outputs `RoleName` + `GeneratedPassword` (empty on idempotent skip).
5. `CreateTenantSchemaActivity` — schema + grants on the placement DB.
6. `BuildTenantConnectionStringActivity` — mints the `Search Path=t_<hex>` connection string into an in-memory variable.
7. `MigrateTenantDatabaseActivity` — runs the tenant EF migration set (idempotent via `__TenantMigrationsHistory`).
8. `SeedTenantDefaultsActivity` — `SELECT 1` smoke-test over the tenant role + seeds default-persona enablement (Story 32-16, best-effort/non-fatal).
9. `EncryptAndPersistConnectionStringActivity` — AES-GCM seal under current KEK → `tenants.EncryptedConnectionString`+`KekVersion`; skip-reencrypt guard for replays.
10. `WarmTenantPoolActivity` — eager `ITenantConnectionResolver.GetDataSourceAsync`; **non-fatal** (catches + warns).
11. `MarkTenantActiveActivity` — `Status='active'` (only from `provisioning`; refuses other states); emits `TENANT.CREATED.SUCCESS` + `TENANT.PROVISIONED.SUCCESS`.
12. `QueueWelcomeEmailActivity` — exactly-once welcome row into the CP `platform_email_outbox`; non-fatal when no owner email.

DCB audit: base `TenantLifecycleActivity` emits `TENANT.PROVISION.STEP_STARTED/COMPLETED/FAILED`
per step (failure-event emission is best-effort, message scrubbed); `TenantLifecycleEvents` is the
single event-type catalogue.

---

## Intended Full Scope (with citations)

From Story 28-5 (`docs/stories/epic-28/story-28-5/28-5-create-delete-tenant-workflows.md`) +
`plans/db-per-tenant/03-async-tenant-provisioning.md` (Doc 03) referenced therein, and project
rules in `CLAUDE.md`:

- **AC1** — Workflow is *triggered* by `TENANT.PROVISIONING_REQUESTED` (verify-email emits; an
  Elsa trigger/correlated signal consumes it and starts the run). Idempotent against replay of
  that signal (story Risks §"Idempotency window").
- **AC2** — Eleven idempotent + **compensable** steps (the activity layer mostly satisfies the
  idempotent half).
- **AC3** — *Per-step retry schedule* (Doc 03 §5.1: 10s/30s/2min, max 3 for most steps;
  30s/2min/10min for migration steps); on exhaustion/permanent-abort run the **reverse-order
  compensation ladder** (Doc 03 §4.1); on full compensation `Status='failed'`,
  `FailureReason='clean'`, `TENANT.PROVISION.FAILED` with `compensation_outcome='cleaned'`; on
  partial compensation `RequiresManualCleanup=true`, `compensation_outcome='partial'`.
- **AC5** — Welcome email to CP outbox; after 3 retries emit success with
  `welcome_email_queued=false` rather than failing (story marks the explicit counter as not
  implemented — structurally satisfied since enqueue is a local insert).
- **AC6** — `GET /api/v1/tenants/{id}/status` folds `platform_events` into a step ladder with
  `estimatedCompletion` (rolling p50, fallback `startedAt+45s`). (Endpoint exists —
  `TenantStatusEndpoint.cs`.)
- **Timing budget** (Doc 03 §5.2): hard ceiling 2h; soft timeout 15min emits
  `TENANT.PROVISION.SLOW`.
- **No-PII invariant** (test T14): event `data`/`tags` carry no emails, no raw SQL, no
  connection strings.
- **Project rules** (`CLAUDE.md`): tenant→system→error resolution, never empty/plain fallback;
  no silent-failure / false-success; steps never call external providers directly; every
  operation emits DCB events.

(Note: the spec predates the unified-schema-per-tenant pivot — it says `CREATE DATABASE` and
two-tier Elsa DBs; the implemented schema-per-tenant model is the current correct shape, so the
DB-per-tenant step text is superseded, but the **retry/compensation/timeout** contract of AC3
remains fully applicable.)

---

## Missing Capabilities

| # | Capability | Priority | dependsOn |
|---|-----------|----------|-----------|
| 1 | **Dispatch wiring**: no `Event` trigger node in the workflow + no `TenantProvisioningRequestedTrigger` bridge republishing `TENANT.PROVISIONING_REQUESTED` into Elsa. Workflow is registered but never started. | P0 | none (mirror `CleanUpFailedTenantWorkflow` + `TenantCleanupRequestedTrigger`) |
| 2 | **Decide & document the canonical create path** (Elsa `create-tenant` vs synchronous `TenantProvisioningService.ProvisionAsync`). Two parallel implementations exist; the service comment calls the workflow "deleted/zero rows in prod". Ship one, retire/alias the other. | P0 | none (architecture decision) |
| 3 | **Compensation ladder on terminal failure** (AC3): reverse-order undo of succeeded steps (drop schema, drop role, evict pool, null the envelope), set `Status='failed'`. Today a mid-run failure strands the tenant in `provisioning`. | P0 | none (reuse Drop*/Evict* cleanup activities) |
| 4 | **Terminal `TENANT.PROVISION.FAILED` event** with `compensation_outcome` (`cleaned`/`partial`) + `FailureReason` + `RequiresManualCleanup` flip on partial cleanup (AC3). No terminal failure event is emitted today. | P0 | #3 |
| 5 | **Per-step retry policy** (AC3 / Doc 03 §5.1): bounded exponential backoff, migration steps get the longer schedule, with permanent-vs-transient SQLSTATE classification deciding retry-vs-abort. | P1 | none |
| 6 | **`InitInputs` `tenant.skip_provision` is set but never honored** — `MarkProvisioning` flags an already-active tenant for short-circuit, but the linear `Sequence` runs all downstream steps regardless. Add a guard/branch (`If`) that exits the workflow when the flag is set. | P1 | none |
| 7 | **Soft-timeout `TENANT.PROVISION.SLOW` at 15min + hard 2h ceiling** abort→compensation (Doc 03 §5.2). Not implemented. | P1 | #3 |
| 8 | **`welcome_email_queued` outcome on the success event** (AC5 §3) — `MarkTenantActive` emits success unconditionally; if welcome enqueue is to remain a downstream step its outcome isn't reflected in the terminal event. | P2 | none |
| 9 | **Workflow-journal secret scrubbing for `GeneratedPassword`/`TenantConnectionString` variables** — both are held in workflow variables that Elsa persists in the run journal. The activity doc-comments say a "platform-event sanitiser scrubs this before serialisation … until that sanitiser lands, treat as in-memory-only." Confirm/implement the journal-level scrub so the no-PII / no-secrets invariant (T14) holds for the *persisted workflow state*, not just `platform_events`. | P0 | none |
| 10 | **Integration coverage T1–T14** (Doc 03 §9.1): happy-path-under-30s, transient-retry, terminal-failure-compensation, quarantine, at-least-once replay, soft/hard timeout, reprovision, welcome-fail, no-PII scan. Story claims these but the workflow has no compensation/retry/timeout to exercise. | P1 | #3,#5,#7 |

---

## Build-out Spec (ordered)

> Honors project rules: route nothing to external providers from steps (these are DB/infra
> activities — compliant); tenant→system→error resolution; no silent-failure / no false-success;
> emit DCB events on every transition.

1. **Resolve the architecture (P0, blocks everything).** Pick the canonical SaaS create path.
   Recommended: make the Elsa `create-tenant` workflow the single source of truth and have
   `TenantProvisioningService.ProvisionAsync` be (a) the single-user synchronous path only, or
   (b) a thin facade that dispatches the workflow. Document the decision in an ADR under
   `.dev/decisions/`. Update the stale "deleted/zero rows" comment.

2. **Add the dispatch trigger (P0).** Mirror the cleanup pattern exactly:
   - Add an `Event("tenant-provisioning-requested")` node as the first child of the `Sequence`
     root in `CreateTenantWorkflow.Build` (before `InitInputs`).
   - Add `Tamma.ElsaServer/Workflows/TenantProvisioningRequestedTrigger.cs` (or extend the
     existing bridge) subscribing to `IPlatformEventBus`, filtering
     `TENANT.PROVISIONING_REQUESTED`, and calling `IEventPublisher.PublishAsync(
     "tenant-provisioning-requested", correlationId: tenantId, input: { tenantId })`.
   - Make verify-email's flip unconditional *only once* a consumer exists (today's conditional
     guard exists precisely because there was none). Add the replay test from Risks §"Idempotency
     window": double `PROVISIONING_REQUESTED` → one run, `Status='provisioning'` guard holds.

3. **Honor the already-active short-circuit (P1, #6).** Wrap steps 2-12 in an `If` keyed on the
   `tenant.skip_provision` workflow property set by `MarkProvisioningActivity` (or have
   `MarkProvisioning` set a typed `Bool` workflow variable). On skip, emit a
   `TENANT.PROVISION.STEP_COMPLETED step=already-active` breadcrumb and complete the workflow.

4. **Wrap the provisioning body in a compensation scope (P0, #3/#4).** Since Elsa 3.5.x has no
   built-in `TryCatch`, follow the team's established pattern (documented in
   `CleanUpFailedTenantWorkflow`): set `WorkflowOptions.IncidentStrategyType` and add a
   **compensation tail** that runs when a fault is detected. Concretely:
   - Track succeeded steps in a workflow variable list (the base activity already emits
     `STEP_COMPLETED`; capture step names into a `List<string>` variable on completion, or read
     them back from `platform_events`).
   - On fault, run a reverse-order ladder of the existing cleanup activities — reuse
     `EvictTenantPoolActivity` → `DropTenantSchemaActivity` → `DropTenantRoleActivity` and null
     the connection-string envelope — each `continue-on-error` (catch + record into a
     `failedCompensations` variable), exactly as `CleanUpFailedTenantWorkflow` does.
   - **Terminal event branch:** if all required compensations succeeded → `Status='failed'`,
     `FailureReason='clean'`, emit `TENANT.PROVISION.FAILED` with `compensation_outcome='cleaned'`;
     else → `Status='failed'`, `FailureReason='partial'`, `RequiresManualCleanup=true`, emit
     `TENANT.PROVISION.FAILED` with `compensation_outcome='partial'` and a `failedSteps[]` array.
     Never leave the tenant in `provisioning` with no terminal event (no silent-failure).

5. **Add per-step retry policy (P1, #5).** Apply Doc 03 §5.1 backoff via Elsa activity retry
   metadata or a small retry wrapper in `TenantLifecycleActivity.RunAsync` keyed on a
   SQLSTATE-based transient/permanent classifier (reuse/extend `CleanupFailureClassifier.cs`,
   which already does Postgres SQL-state classification). Migration steps
   (`MigrateTenantDatabaseActivity`) get the longer 30s/2min/10min schedule; most get
   10s/30s/2min, max 3. Permanent classifications skip retry and go straight to the
   compensation tail (step 4).

6. **Add soft/hard timeout (P1, #7).** Stamp a `StartedAt` on `InitInputs`. Schedule a soft-timeout
   side-path (Elsa timer/`Delay` 15min correlated to the run) that emits `TENANT.PROVISION.SLOW`
   (add the constant to `TenantLifecycleEvents`) if the run hasn't reached `MarkTenantActive`.
   Add a hard 2h ceiling that triggers the compensation tail with `FailureReason='timeout'`.

7. **Persist-journal secret scrubbing (P0, #9).** Verify whether the promised
   "platform-event sanitiser" actually scrubs `GeneratedPassword` + `TenantConnectionString` from
   the Elsa run journal. If not, register an Elsa journal/state filter (or move these to a
   non-persisted execution-context property instead of `WithVariable`) so the persisted workflow
   state carries no plaintext password or connection string. Add a test asserting the journal row
   for a completed run contains neither.

8. **Reflect welcome-email outcome (P2, #8).** Either move `QueueWelcomeEmail` before
   `MarkTenantActive` and pass a `welcome_email_queued` bool into the success event `data`, or
   have `MarkTenantActive` read the outbox row's existence. Keep enqueue non-fatal.

9. **Reprovision endpoint integration (P1).** Ensure `POST /api/admin/tenants/{id}/reprovision`
   (and the existing `RetryTenant`) dispatch the *same* trigger from step 2 (T12), so recovery
   from a `failed`/`clean` end-state re-runs the workflow idempotently.

10. **Land integration tests T1–T14 (P1, #10).** Testcontainers Postgres + Elsa: happy path
    <30s + welcome row; transient-retry (`57P03`) succeeds on attempt 2; terminal failure runs
    compensation (`RequiresManualCleanup=false`); quarantine (mid-compensation failure →
    `RequiresManualCleanup=true`, cleanup workflow clears); at-least-once replay (no dup events,
    no dup welcome); soft/hard timeout; reprovision; welcome-insert-fail still succeeds; T14
    no-PII scan over `platform_events` **and** the workflow journal.

---

## Summary

`CreateTenantWorkflow` is a **partial** workflow: an excellent, idempotent, replay-safe,
fully-audited *activity layer* sitting under an *orchestration shell that is never invoked and
has no failure contract*. The two highest-leverage gaps are both P0 and both about the seams,
not the steps: (1) it is **orphaned** — no Elsa trigger and no platform-event→Elsa bridge ever
dispatches it, and the SaaS path actually runs through a parallel synchronous service; and
(2) it has **no compensation / terminal-failure / timeout** behavior, so any mid-run fault
strands a tenant in `provisioning` forever with no `TENANT.PROVISION.FAILED` event — violating
the no-silent-failure rule and Story 28-5 AC3. Closing #1–#4 + #9 turns this from a
well-built-but-disconnected artifact into the robust, complete, auditable provisioning workflow
the story specifies.

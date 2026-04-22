# Story 28-5 Implementation Plan — `CreateTenantWorkflow` + `DeleteTenantWorkflow`

**Status**: Planned (2026-04-20)
**Story brief**: [`28-5-create-delete-tenant-workflows.md`](./28-5-create-delete-tenant-workflows.md)
**Epic 28 phase**: B (Data plane — after 28-4 and 28-6)
**Branch**: `feat/story-28-5-tenant-lifecycle-workflows`

---

## 1. Objective

Ship two global-Elsa workflows: `CreateTenantWorkflow` (11 idempotent
steps, compensable on failure, auditable via `platform_events`) and
`DeleteTenantWorkflow` (O(1) drop-database + cleanup). These are the
central tenant-lifecycle artefacts — async provisioning from the
verify-email endpoint hinges on them. Every step emits a
`TENANT.PROVISION.STEP_*` event so admins can observe progress in the
state dashboard (28-11). Failure at any step triggers compensation in
reverse order.

## 2. Dependencies

Hard blockers:

- **Story 28-1** — CP + tenant + Elsa migrations exist.
- **Story 28-3** — `TenantDbContextFactory` ready.
- **Story 28-4** — connection pool resolver live so the post-provisioning
  pool can be eagerly built.
- **Story 28-6** — `platform_events`, `platform_queued_tasks`,
  `platform_email_outbox` tables + repositories.
- **Story 28-12** — `ISecretsService` encrypts the per-tenant DB password.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/CreateTenantWorkflow.cs` | Master workflow definition. |
| `.../Workflows/DeleteTenantWorkflow.cs` | Symmetric teardown. |
| `.../Activities/Tenant/MarkProvisioningActivity.cs` | Step 1. |
| `.../Activities/Tenant/CreateRoleActivity.cs` | Step 2. |
| `.../Activities/Tenant/CreateTenantDbActivity.cs` | Step 3. |
| `.../Activities/Tenant/MigrateTenantDbActivity.cs` | Step 4. |
| `.../Activities/Tenant/CreateElsaDbActivity.cs` | Step 5. |
| `.../Activities/Tenant/MigrateElsaDbActivity.cs` | Step 6. |
| `.../Activities/Tenant/GrantAppRoleActivity.cs` | Step 7 — grants on the tenant DB to `tamma_app`. |
| `.../Activities/Tenant/EncryptAndPersistCredsActivity.cs` | Step 8 — write encrypted connection string back to `tenants.DbConnectionCiphertext`. |
| `.../Activities/Tenant/WarmPoolActivity.cs` | Step 9 — trigger `TenantConnectionResolver.DataSourceFor(tenantId)` so the first user request isn't cold. |
| `.../Activities/Tenant/EnqueueWelcomeEmailActivity.cs` | Step 10 — insert into `platform_email_outbox`. |
| `.../Activities/Tenant/MarkActiveActivity.cs` | Step 11 — flip `tenants.Status=active`; emit `TENANT.PROVISION.COMPLETED`. |
| `.../Activities/Tenant/DropTenantDbActivity.cs` | Delete step. |
| `.../Activities/Tenant/DropRoleActivity.cs` | Delete step (after `DROP OWNED BY`). |
| `.../Compensation/CompensationLedger.cs` | Tracks executed steps; driver for reverse-order compensation. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantLifecycleEndpoints.cs` | `GET /api/v1/tenants/:id/provision-status` — polls workflow progress for the frontend poller. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/CreateTenantWorkflowTests.cs` | Happy path + 11 failure-injection cases. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/DeleteTenantWorkflowTests.cs` | Happy + force + safety guards. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` | `VerifyEmail` flips status to `provisioning`, emits `TENANT.PROVISIONING_REQUESTED` (single CP txn), returns 204. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | Register workflow + activity DI. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` | Add `DbConnectionCiphertext`, `ProvisioningError`, `LastProvisionedStep` columns; add CHECK on `Status` values. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/28_5_tenant_status_cols.cs` | Migration. |
| `/home/meywd/tamma/docs/runbooks/tenant-lifecycle.md` | Ops guide: how to replay a stuck workflow, how to force-delete. |

## 5. Sequence of changes

### Step 1 — Schema migration + CompensationLedger (3h)

- Migration adds `DbConnectionCiphertext`, `ProvisioningError`,
  `LastProvisionedStep` columns with `CHECK(Status IN ('pending_verification','provisioning','active','failed','deleting','deleted'))`.
- `CompensationLedger` is a scoped service that records each step's
  handle; on workflow failure, it runs compensators in reverse.
- **Commit**: `feat(db): tenants status columns + compensation ledger`.

### Step 2 — Steps 1-3 happy path (4h)

- `MarkProvisioning`: no-op if already `active`.
- `CreateRole`: idempotent via `pg_roles` lookup.
- `CreateTenantDb`: idempotent via `pg_database`.
- Each emits `TENANT.PROVISION.STEP_N.SUCCESS`.
- **Commit**: `feat(provisioning): steps 1-3 with idempotency`.

### Step 3 — Steps 4-7 (6h)

- `MigrateTenantDb`: invoke EF migration programmatically against new DB.
- `CreateElsaDb` + `MigrateElsaDb`: same for per-tenant Elsa.
- `GrantAppRole`: SQL grants `tamma_app` CRUD on the 15 tenant tables.
- **Commit**: `feat(provisioning): steps 4-7 (migrations + grants)`.

### Step 4 — Steps 8-11 (5h)

- `EncryptAndPersistCreds`: calls `ISecretsService.EncryptConnectionString`
  and updates `tenants.DbConnectionCiphertext`.
- `WarmPool`: eagerly acquires pool handle.
- `EnqueueWelcomeEmail`: insert into `platform_email_outbox`.
- `MarkActive`: flips `Status`, emits `TENANT.PROVISION.COMPLETED`.
- **Commit**: `feat(provisioning): steps 8-11 (creds + welcome)`.

### Step 5 — Compensation wiring (4h)

- Each step registers its compensator with the ledger at success.
- Workflow `OnError` invokes `ledger.CompensateAsync()`.
- Unit tests: failure-injection at every step; assert ledger reversed
  correctly and `Status=failed`.
- **Commit**: `feat(provisioning): compensation on step failure`.

### Step 6 — DeleteTenantWorkflow (5h)

- Reverse of Create: evict pool → drop Elsa DB → drop tenant DB →
  `DROP OWNED BY tamma_tenant_<id>` → `DROP ROLE` → mark deleted.
- Pre-check: refuse to delete `Status='active'` unless `force=true`
  passed by a platform admin.
- **Commit**: `feat(provisioning): delete tenant workflow`.

### Step 7 — VerifyEmail integration (3h)

- `POST /auth/verify-email` flips status + emits
  `TENANT.PROVISIONING_REQUESTED` in single CP transaction.
- Integration test: full register → verify → poll status until
  `active` → authenticate.
- **Commit**: `feat(auth): trigger provisioning on email verify`.

### Step 8 — Progress endpoint + runbook (3h)

- `GET /api/v1/tenants/:id/provision-status` reads workflow state
  from Elsa and returns `{ status, lastStep, progressPct, error }`.
- Runbook: replay/force-delete instructions.
- **Commit**: `feat(api): tenant provision status + runbook`.

## 6. Test strategy

### Unit

- Per-activity tests: idempotency (run twice, same outcome) +
  compensation correctness.

### Integration (Testcontainers Postgres)

- Happy path: verify-email → workflow completes → tenant DB
  queryable via pool in <60s.
- Fault injection: each step fails once; assert compensation runs
  and `Status=failed`, `ProvisioningError` populated.
- Replay: after failure, run workflow again — it should either
  succeed (transient) or no-op (already failed state).
- Delete happy path: bring tenant to `active` then delete; verify
  `pg_database` clean.

### Performance

- Brief deploy gate: p95 < 60s for tenant create at concurrency=1.

## 7. Rollback plan

- **Feature flag**: `Tenants:AsyncProvisioning=true` (brief rollback
  section). Off → sync provisioning (Epic 19 era).
- **Partial provisioning rollback**: compensation ledger cleans up
  artefacts. If ledger itself fails, runbook documents manual cleanup
  (ops runs `DROP DATABASE` + `DROP ROLE`).
- **Non-reversible**: welcome email send is idempotent via
  `platform_email_outbox` UNIQUE constraint on `(tenantId, template)`.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Schema + ledger | 3 |
| 2. Steps 1-3 | 4 |
| 3. Steps 4-7 | 6 |
| 4. Steps 8-11 | 5 |
| 5. Compensation | 4 |
| 6. Delete workflow | 5 |
| 7. VerifyEmail integration | 3 |
| 8. Progress endpoint + runbook | 3 |
| **Total** | **33** |

Brief target 45h; plan comes under because compensation is structured
as a single ledger rather than per-step inverse activities.

## 9. Open questions

- **`DROP DATABASE WITH (FORCE)` kicks live connections out.** Must
  evict the pool *before* the drop. Sequence documented in step 6;
  verified with Postgres 17 docs on `WITH (FORCE)`.
- **Workflow idempotency under Elsa retries**: if Elsa restarts
  mid-workflow, does the same step re-run? Each activity is idempotent
  by design. Covered by replay integration test.
- **Per-tenant Elsa migration version drift**: 28-1 plan step notes
  this. Embed migration assembly hash in the workflow; fail-fast if
  mismatch. Implementation here.
- **Compensation for `MigrateTenantDb`**: migration runs EF's
  `__EFMigrationsHistory`; if it fails mid-migration, the DB is
  in an inconsistent state. Plan: compensate by dropping the whole
  tenant DB (step 3's compensator) rather than migration rollback.
- **Concurrent provisioning requests for the same user** (double-click
  email verify): CP transaction + unique constraint on
  `(UserId, Status='provisioning')` prevents duplicates. Returns
  409 on second click.

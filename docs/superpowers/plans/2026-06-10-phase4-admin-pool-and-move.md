# Phase 4 — Admin DB-Pool CRUD + Move-Tenant

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Operators can manage the `tenant_databases` pool (list/create/update/retire, with a
tenant→database view) and **move a tenant** to another pool database with a brief per-tenant
read-only window (locked decision 4): mark `draining` → `pg_dump -n t_<hex>` from source → restore
into target → re-point the encrypted connection string → evict the pool → drop the source schema →
bookkeeping → `active`.

**Architecture:** New admin endpoints under `/api/admin/tenant-databases` (platform-owner
`OwnerAccess` policy, mirroring `/api/admin/tenants`). `draining` becomes a real tenant Status:
TenantStatusEvaluator/middleware allow safe (GET/HEAD/OPTIONS) requests and 503+Retry-After the
mutating verbs. The move itself is a service (`TenantMoveService`) reusing Phase 2/3 seams
(`ITenantDatabasePool`, `TenantProvisioningService.CreateRoleAsync/CreateSchemaAsync`,
`IConnectionStringDecryptor`/protector, `IProcessRunner` for pg_dump/pg_restore) and runs
asynchronously off the admin endpoint via the same platform-queue pattern Cranl provisioning uses
(202 Accepted + status polling). Same-cluster moves (source Host:Port == target Host:Port) keep the
tenant role + password and only swap the Database in the conn string; cross-cluster moves create the
role on the target cluster with a fresh password.

**Tech Stack:** .NET 9 / EF Core 9 / Npgsql, pg_dump/pg_restore via the existing `IProcessRunner`
seam (PGPASSWORD env only — never argv), Testcontainers.

**Parent doc:** `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (§3 rows "Admin"/
"Move tenant", §4 Phase 4, decision 4).

---

## Environment facts (verified 2026-06-10 — do not re-derive)

- Repo `/home/meywd/tamma/apps/tamma-elsa`, branch `feat/wave-b`. Build `dotnet build Tamma.sln`;
  docker/tests `sg docker -c "..."`. Full-suite baseline: 4461 passed / 11 skipped.
- `tenant_databases` live since Phase 2 (`TenantDatabase` entity; central row seeded by
  `TenantDatabasesSeeder`, stable id `bbbbbbbb-...-01`). `ITenantDatabasePool` (Tamma.Data.Abstractions):
  GetAdminConnectionStringAsync / ExecuteOnAsync / RoleExistsOnAsync / BuildTenantConnectionStringAsync /
  GetDatabaseNameAsync / GetConnectionInfoAsync / SchemaExistsOnAsync — plus an internal per-row
  decrypt cache with `Evict`.
- Placement bookkeeping: `TenantPlacementService` stamps tenants.DatabaseId/SchemaName + TenantCount;
  `TenantPlacementShadow.LoadAsync/ReleaseAsync` (Tamma.Activities) reads/releases.
- Status machinery: tenants.Status CHECK (`ck_tenants_status`) currently allows NULL +
  pending_verification/provisioning/active/delete_requested/deleting/deleted/failed/suspended —
  **NO `draining`**; the CHECK lives in `TammaModelConfiguration.cs` AND hand-mirrored in the
  collapsed baseline `Migrations/ControlPlane/20260609205701_InitialControlPlane.cs` + Designer +
  snapshot (Phase 0 C1-fix procedure: edit all four consistently, then
  `dotnet ef migrations has-pending-model-changes -c ControlPlaneDbContext -p src/Tamma.Data -s src/Tamma.Data`
  must report no changes). The conn-string CHECK needs NO change (draining tenants have envelopes).
- `TenantStatusEvaluator` (`src/Tamma.Api/Services/TenantStatus/`) maps Status → gate result;
  `TenantContextMiddleware` enforces. There is a vestigial `StatusDropping="dropping"` case
  (read-side only, nothing writes it). HTTP method IS available to the middleware.
- Admin endpoints pattern: `src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` (OwnerAccess
  policy, list with shadow-column projections incl. KekVersion/Status; status-transition POSTs).
  Tests: `tests/Tamma.Api.Tests/Admin/` + `AdminTenantsTests`.
- Async admin-op pattern to mirror: Cranl provisioning — POST returns 202, work runs on the
  existing platform task queue, GET reports state transitions (grep `provision` under
  `src/Tamma.Api/Endpoints/Admin/` + the platform queued-task handler registry under
  `src/Tamma.Api/Services/` — find how `PlatformQueuedTasks` handlers register and mirror).
- pg_dump shelling pattern: `BackupTenantDatabaseActivity` (`src/Tamma.Activities/TenantLifecycle/`)
  — `IProcessRunner`, args builder (no password in argv; PGPASSWORD env), `-n <schema>` flavor since
  Phase 2, `GetConnectionInfoAsync` for discrete parts. Its tests use a RecordingProcessRunner.
- Encrypt/decrypt seams: `ITenantConnectionStringProtector` (encrypt + CurrentKekVersion),
  `IConnectionStringDecryptor` (decrypt with KekVersion).
- Eviction: `ITenantConnectionResolver.EvictAsync(tenantId)` (idempotent);
  `TenantStatusInvalidationListener` reacts to status changes.
- Dev/test KEK ships in appsettings.Development.json (Phase 3); fixtures provision tenants via
  `Infrastructure/TestTenantProvisioning.cs` helpers.
- `pg_dump`/`pg_restore` presence on the test host is NOT guaranteed — gate the real-move e2e on
  `which pg_dump && which pg_restore` (mirror the existing env-gated-skip pattern in
  `AppRoleRegressionTests`).

## Phase 4 boundaries (YAGNI guard)

- NO RLS removal / ProviderKey retirement (Phase 5). NO online/zero-downtime move (parent §7). NO
  cross-region/pgbouncer topology. NO Elsa workflow for the move (the platform-queue service pattern
  is the established one; workflows have no auto-dispatch subscriber anyway).
- Pool-row capacity stays advisory (Phase 2 note) — CRUD validates shape, not global invariants.

---

### Task 1: `draining` status — CHECK + read-only gate (TDD)

**Files:**
- Modify: `src/Tamma.Data/TammaModelConfiguration.cs` (`ck_tenants_status` gains `'draining'`) +
  the SAME edit hand-mirrored in `Migrations/ControlPlane/20260609205701_InitialControlPlane.cs`,
  its `.Designer.cs`, and `ControlPlaneDbContextModelSnapshot.cs` (Phase 0 C1 procedure; verify
  with `has-pending-model-changes` → none).
- Modify: `src/Tamma.Api/Services/TenantStatus/TenantStatusEvaluator.cs` — new
  `StatusDraining = "draining"` mapping to a new outcome "read-only" (safe methods allowed).
- Modify: `TenantContextMiddleware` — read-only outcome: allow GET/HEAD/OPTIONS through, other
  verbs → 503 + `Retry-After: 5` + problem body naming the move.
- Tests: extend `TenantStatusEvaluatorTests` (draining cases) + middleware tests (GET passes,
  POST 503) + a real-PG probe-style test asserting a `Status='draining'` UPDATE passes the CHECK
  (mirror how Phase 0's CHECK tests do it, or extend an existing real-PG admin test).

TDD: tests first (red), then evaluator/middleware/CHECK, then green. Commit:
`feat(tenancy-p4): draining tenant status = brief read-only window`.

---

### Task 2: Admin `tenant-databases` CRUD + tenant→DB view

**Files:**
- Create: `src/Tamma.Api/Endpoints/Admin/AdminTenantDatabasesEndpoints.cs` (mirror
  AdminTenantsEndpoints structure/policy/registration — find where admin endpoint groups register
  in Program.cs and mirror):
  - `GET /api/admin/tenant-databases` — list (Id, Label, Host, Port, PlacementClass,
    TierEligibility, TenantCapacity, TenantCount, Status, KekVersion, CreatedAt/UpdatedAt — NEVER
    the admin conn string).
  - `GET /api/admin/tenant-databases/{id}` — row + its tenants (Id, Slug, SchemaName, Status).
  - `POST /api/admin/tenant-databases` — body: label, adminConnectionString (plaintext in,
    encrypted at rest via `ITenantConnectionStringProtector`; Host/Port parsed FROM the conn string
    via NpgsqlConnectionStringBuilder — reject mismatch with explicit Host/Port body fields by NOT
    accepting them), placementClass, tierEligibility[], tenantCapacity?. Validates: label unique
    (409), placementClass/status enums, conn string parses + a live `SELECT 1` probe through a
    fresh NpgsqlConnection (reject unreachable rows with 422 + the Npgsql error).
  - `PATCH /api/admin/tenant-databases/{id}` — mutable: label, tierEligibility, tenantCapacity,
    status (active|draining|full|retired), adminConnectionString (re-encrypt + `TenantDatabasePool`
    cache evict — expose/internal-call the pool's Evict).
  - `DELETE /api/admin/tenant-databases/{id}` — 409 unless TenantCount == 0 AND no tenants
    reference it (defensive count query); hard delete (zero-data project).
- Modify: `AdminTenantsEndpoints` list/detail projections — add `DatabaseId` + `SchemaName`
  shadow-column reads (the tenant→DB view's other half).
- Tests: `tests/Tamma.Api.Tests/Admin/AdminTenantDatabasesEndpointsTests.cs` — mirror the
  AdminTenants test harness (real PG, OwnerAccess auth setup): CRUD happy paths, 409 label dup,
  409 delete-with-tenants, 422 unreachable conn string, member-role 403, conn string NEVER in any
  response (assert on serialized JSON), PATCH conn-string rotation evicts the pool cache.

Commit: `feat(tenancy-p4): admin tenant-databases CRUD + tenant→database view`.

---

### Task 3: `TenantMoveService` (TDD: orchestration unit tests + env-gated e2e)

**Files:**
- Create: `src/Tamma.Data/Abstractions/ITenantMoveService.cs` +
  `src/Tamma.Api/Services/Provisioning/TenantMoveService.cs` (DI beside the provisioning service)
- Test: `tests/Tamma.Api.Tests/Tenancy/TenantMoveServiceTests.cs`

Contract:

```csharp
namespace Tamma.Data.Abstractions;

/// <summary>
/// Unified-tenancy Phase 4 — moves a tenant's schema to another pool
/// database with a brief read-only window (parent plan decision 4):
/// draining → pg_dump -n t_&lt;hex&gt; → restore into target → re-point the
/// encrypted connection string → evict pools → drop source schema →
/// bookkeeping → active. Same-cluster moves (source Host:Port == target)
/// keep the role + password and swap only the Database; cross-cluster
/// moves create the role on the target cluster with a fresh password.
/// </summary>
public interface ITenantMoveService
{
    Task MoveAsync(Guid tenantId, Guid targetDatabaseId, CancellationToken ct = default);
}
```

Step order (each step idempotent or safely re-runnable; log step transitions with a
`tenant.move.<step>` prefix):
1. **Validate**: tenant exists, not deleted, Status == 'active' (anything else → throw with the
   state); has placement (DatabaseId+SchemaName); target row exists, Status == 'active', target !=
   source; target tier-eligibility/capacity checked (same predicate as placement — reuse/extract).
2. **Drain**: Status → 'draining' (+UpdatedAt) + `EvictAsync(tenantId)` (in-flight requests finish;
   new mutations 503 via Task 1).
3. **Dump**: `pg_dump -F c -n <schema> -f <tmpfile>` from the SOURCE row via
   `GetConnectionInfoAsync(sourceDatabaseId)` + `IProcessRunner` (mirror
   BackupTenantDatabaseActivity's arg builder + PGPASSWORD discipline; reuse/extract its helper
   rather than copy-pasting if reasonably extractable).
4. **Role on target**: same-cluster (`source.Host:Port == target.Host:Port` from the two rows) →
   skip (role exists cluster-wide); cross-cluster → `TenantProvisioningService.CreateRoleAsync`
   against the TARGET placement (fresh password; if role pre-exists on target WITHOUT a recoverable
   password → throw with the DROP OWNED BY runbook).
5. **Schema on target**: `CreateSchemaAsync` against target placement (CREATE SCHEMA AUTHORIZATION +
   GRANT CONNECT + per-DB search_path default).
6. **Restore**: `pg_restore -d <target db> --no-owner --role <tenant role> <tmpfile>` via target
   `GetConnectionInfoAsync` (objects land owned by the tenant role inside the already-created
   schema; `--no-owner --role` requires the admin to be allowed to SET ROLE — superuser-or-member;
   document in the XML doc). Verify: `__TenantMigrationsHistory` row count in target schema equals
   source's (query both via the pool) — mismatch → throw (source intact, tenant still draining →
   operator retries or reverts status).
7. **Re-point**: build the new conn string — same-cluster: decrypt current envelope, swap
   `Database` to the target row's, keep credentials, keep `Search Path`; cross-cluster:
   `BuildConnectionStringAsync(targetDatabaseId, role, freshPassword, schema)`. Encrypt + persist
   envelope + KekVersion; update tenants.DatabaseId → target; TenantCount-- on source row /
   TenantCount++ on target row; SAME SaveChanges.
8. **Evict + verify**: `EvictAsync(tenantId)`; open a TenantDbContext via the real factory and run
   a trivial query (mirrors the provisioning verify).
9. **Drop source**: `DROP SCHEMA IF EXISTS <schema> CASCADE` on the SOURCE row via the pool;
   cross-cluster additionally `DROP OWNED BY` + `DROP ROLE IF EXISTS` on the SOURCE cluster (the
   role has no other objects there); same-cluster: role stays (still owns target schema).
10. **Activate**: Status → 'active'; delete the tmp dump file in a finally.

Failure policy: steps 2-6 failures leave the tenant 'draining' with source intact (operator
re-runs MoveAsync — steps are idempotent — or PATCHes status back to active); failures after step 7
committed leave the tenant pointing at the TARGET (re-run completes drop/activate). State every
window in the XML doc.

Tests:
- Orchestration units with a RecordingProcessRunner + recording pool fake (mirror
  BackupTenantDatabaseActivityTests + DropTenantSchemaActivityTests fakes): step order, same- vs
  cross-cluster branching, password-never-in-argv, validation rejections (not-active tenant,
  ineligible target, target==source), history-count-mismatch abort.
- E2E (env-gated on `pg_dump`/`pg_restore` in PATH, NUnit `Assert.Ignore` otherwise): two physical
  DBs in one Testcontainer (same cluster) registered as two pool rows; provision tenant on A; write
  a marker row (AgentConfig); MoveAsync to B; assert: schema gone on A, present on B with marker
  row + history; envelope decrypts to B's database; resolver round-trip works; TenantCounts
  shifted; Status active.

Commit: `feat(tenancy-p4): TenantMoveService — schema move with read-only window`.

---

### Task 4: Admin move endpoint (202 + async execution + status)

**Files:**
- Modify/Create: `AdminTenantDatabasesEndpoints` or `AdminTenantsEndpoints` (pick where Cranl's
  provision endpoint lives and mirror placement): `POST /api/admin/tenants/{tenantId}/move` body
  `{ "targetDatabaseId": "..." }` → validates cheaply (tenant + target exist) → 202 + enqueues via
  the SAME mechanism Cranl provisioning uses (read it first; mirror exactly — handler registration,
  task payload shape, claim semantics). `GET /api/admin/tenants/{tenantId}/move` (or fold into the
  existing provisioning-status endpoint if that's how Cranl reports — mirror) returns the current
  Status + last move error if the handler recorded one (FailureReason shadow column is the existing
  place errors land — reuse it).
- Handler: invokes `ITenantMoveService.MoveAsync`; on exception, writes FailureReason + leaves
  Status as the failure policy dictates (draining), logs.
- Tests: endpoint auth (OwnerAccess/403), 202 + task enqueued (assert via the queue's test
  surface), 404s, handler failure writes FailureReason (fake move service throwing).

Commit: `feat(tenancy-p4): admin move-tenant endpoint (202 + queued execution)`.

---

### Task 5: Full suite + docs + execution record

- Full suite (foreground): 0 failures (baseline 4461+new).
- Docs: parent plan Phase 4 → DONE + deviations (e.g. `draining` added to the Status CHECK —
  extends the Phase 0 enumeration; move is service+queue, not an Elsa workflow — no dispatch
  subscriber exists, matching how provisioning actually runs); `wiki/Multi-Tenant-Provisioning.md`
  + `wiki/Architecture.md` (admin pool CRUD + move section); `docs/deployment/configuration-reference.md`
  if any new knob; execution record in THIS plan.
- Commit `docs(tenancy-p4): mark Phase 4 complete` (controller pushes + CI).

---

## Self-review notes

- **Spec coverage** (parent §3 Admin/Move + §4 Phase 4 + decision 4): pool CRUD ✓ (T2), tenant→DB
  view ✓ (T2), move with read-only window ✓ (T1+T3+T4), dump→restore→re-point→evict→drop ✓ (T3).
- **No placeholders**: contracts + step orders are complete; "mirror X" references name exact files
  whose patterns were verified in earlier phases.
- **Type consistency**: `ITenantMoveService.MoveAsync(Guid, Guid)`; reuses `TenantPlacement`-adjacent
  seams without changing them.
- **Known risks**: (1) pg_restore `--role` privileges vary by admin role — e2e proves the
  Testcontainers superuser path; the XML doc states the requirement for operators. (2) The
  platform-queue mirror depends on reading the Cranl handler pattern correctly — T4 instructs
  reading first and STOPPING if the pattern doesn't exist as described.

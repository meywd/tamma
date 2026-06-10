# Phase 2 — Unified Tenant Creation Path (placement + schema + mint-at-creation)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every newly created tenant — SaaS org via `CreateTenantWorkflow` AND the single-user
personal tenant via `EnsurePersonalTenantMiddleware` — gets a placement decision (`tenant_databases`
pool row chosen by plan tier), a Postgres role + `t_<hex>` schema with schema-scoped grants in the
assigned database, a minted AES-GCM-encrypted connection string (`...;Search Path=t_<hex>`), and
tenant migrations applied into the schema; the delete path drops the schema/role and releases the
pool slot.

**Architecture:** A new `TenantProvisioningService` owns the step logic (placement → role → schema →
conn-string → migrate → seed → encrypt) so the SaaS workflow activities and the single-user
middleware share ONE implementation (per the project's universal two-scoping-models rule).
`tenant_databases` becomes live: a startup seeder registers the central DB as pool member #1
(shared, all tiers) so dev/self-host and SaaS run the same code path; `ITenantDatabasePool` is the
accessor that decrypts a pool row's admin connection and builds tenant connection strings against
the TARGET database (roles are cluster-scoped — they must be created on the target cluster, not the
central one). **Access-path behavior does NOT change in this phase**: the stub resolver stays wired
(dev/test still read tenant data from the central DB public schema); Phase 3 flips access to the
minted connection strings and removes the stub. The end-to-end proof test exercises the real
`LruPooledTenantConnectionResolver` directly to demonstrate the full chain ahead of Phase 3.

**Tech Stack:** .NET 9 / EF Core 9 / Npgsql 9, Elsa activities (existing `TenantLifecycle` set),
AES-GCM via existing `ITenantConnectionStringProtector`, PostgreSQL (`pgvector/pgvector:pg16`
throwaways + Testcontainers).

**Parent doc:** `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (§3 rows
"Placement"/"Tenant schema lifecycle"/"Conn-string mint", §4 — NOTE the **phase re-order**: this is
the parent's "Phase 3 — unified creation path", pulled BEFORE stub removal because stub removal
hard-depends on every tenant having a connection string. Record the re-order in the parent doc
(Task 8).)

---

## Environment facts (verified 2026-06-09 — do not re-derive)

- C# root `/home/meywd/tamma/apps/tamma-elsa`, branch `feat/wave-b`. Build `dotnet build Tamma.sln`;
  tests/docker via `sg docker -c "..."`.
- `CreateTenantWorkflow` (`src/Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs:195-211`): linear
  sequence initInputs → MarkProvisioning → **CreateTenantRoleActivity** (outputs RoleName +
  GeneratedPassword; `CREATE ROLE tamma_tenant_<hex> WITH LOGIN PASSWORD...` via
  `ITenantAdminConnection`; idempotent via pg_roles probe, empty password on skip) →
  **CreateTenantDatabaseActivity** (`CREATE DATABASE ... OWNER role`) →
  **BuildTenantConnectionStringActivity** → MigrateTenantDatabase (Phase 1 made it schema-aware) →
  SeedTenantDefaults → EncryptAndPersistConnectionString (AES-GCM via
  `ITenantConnectionStringProtector`, persists EncryptedConnectionString + KekVersion, idempotency
  guard `ShouldSkipReencrypt`) → WarmTenantPool → MarkTenantActive → QueueWelcomeEmail. Structure
  asserted by `tests/Tamma.Activities.Tests/TenantLifecycle/CreateTenantWorkflowStructureTests.cs`.
- `DeleteTenantWorkflow` (`src/Tamma.ElsaServer/Workflows/DeleteTenantWorkflow.cs:122-134`):
  initInputs → MarkTenantDeleting → EvictTenantPool → BackupTenantDatabase (gated by
  `Backup:DeletionBackup`) → DropTenantDatabase (`DROP DATABASE ... WITH (FORCE)`) → DropTenantRole
  (`DROP OWNED BY` + `DROP ROLE`) → EmitDeletedSuccess (soft-delete + nulls envelope).
- `ITenantAdminConnection` / `NpgsqlTenantAdminConnection` (`src/Tamma.Data/Abstractions/`,
  `src/Tamma.Data/Pooling/`): admin conn from `ConnectionStrings:TenantAdmin` →
  `:DefaultConnection` → `:ControlPlane`; fresh connection per statement, no transaction; has
  `RoleExistsAsync`/`DatabaseExistsAsync`/`ExecuteAsync`/`BuildTenantConnectionString(db, role, pw)`
  (preserves Host/Port/SSL from admin builder)/`GetConnectionInfo`.
- `EnsurePersonalTenantMiddleware` (`src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs:126-175`):
  single-user mode only (SaaS no-ops). Creates the Tenant row (`u-<8hex>` slug) + membership +
  active-tenant pointer; sets NO Status / EncryptedConnectionString / SchemaName / DatabaseId.
- SaaS trigger: verify-email flips owned `pending_verification` tenants → `provisioning` and
  publishes `TENANT.PROVISIONING_REQUESTED` (`AuthEndpoints.cs:369-447`); an Elsa subscriber starts
  `create-tenant`.
- `TenantDatabase` entity + `tenant_databases` table exist (Phase 0) with **zero usages**. Columns:
  Id, Label (unique), Host, Port, AdminConnectionStringEncrypted (bytea), PlacementClass
  shared|dedicated, TierEligibility text[], TenantCapacity int?, TenantCount, Status
  active|draining|full|retired, KekVersion smallint, timestamps.
- `tenants` has shadow columns `SchemaName` (unique partial), `DatabaseId` (FK→tenant_databases,
  Restrict), `Status`, `EncryptedConnectionString`, `KekVersion` (short) — Phase 0.
- Plan tier: `tenants.Plan` = slug string; `db.Plans.FirstOrDefaultAsync(p => p.Slug == tenant.Plan)`
  → `.PlacementPolicy` ("shared"/"dedicated"). Seeded: free/team=shared, enterprise=dedicated.
- Encrypt seam: `ITenantConnectionStringProtector` (`Encrypt(string)` + `CurrentKekVersion`),
  impl `TenantSecretProtectorAdapter` over `TenantSecretProtector` (AES-256-GCM; key
  `Cranl:EncryptionKey` base64-32; dev fallback HKDF from `Cranl:ApiKey`; production hard-fail).
  Registered in `PlatformEventsServiceCollectionExtensions.cs:64-75`. Decrypt seam:
  `IConnectionStringDecryptor` (AesGcm impl in prod, `PassthroughConnectionStringDecryptor` exists).
- `TenantNaming`: `RoleName` = `tamma_tenant_<hex>`, `SchemaName` = `t_<hex>` (Phase 1), `Quote`.
- Phase 1 gave: `EfTenantDbMigrator` applies into `Search Path` schema (in-schema
  `__TenantMigrationsHistory`, CREATE SCHEMA safety net); `InitialTenant` baseline needs no
  extensions.
- Resolver: `LruPooledTenantConnectionResolver` resolves V2-directory-or-legacy-decrypt, throws
  `TenantConnectionStringMissingException` when envelope empty. Stub resolver registered via
  `TryAddSingleton` in `Tamma.Data/DependencyInjection.cs:102-106` when no CP string. **Do not
  change resolver wiring in this phase.**
- Activities resolve services from the executing host's DI (`context.GetRequiredService<T>()`);
  new services must be registered in every composition root that runs the activities (find where
  `ITenantAdminConnection`/`ITenantDbMigrator` are registered and mirror — grep
  `AddSingleton<ITenantAdminConnection` / `ITenantDbMigrator`).
- Postgres facts (don't re-litigate): roles are CLUSTER-scoped (create on the target cluster);
  `ALTER ROLE x IN DATABASE d SET search_path = s` scopes the default search_path per DB;
  PG15+ revoked PUBLIC CREATE on `public`; `DROP OWNED BY` acts per-database (must run on the
  TARGET db); pg16 image in use.

## Phase 2 boundaries (YAGNI guard)

- NO resolver/stub changes, NO ConventionStoreSeeder change, NO test-fixture re-wiring (Phase 3).
- NO admin CRUD endpoints for tenant_databases, NO move-tenant (Phase 4).
- NO RLS removal (Phase 5).
- Cranl/V2 provider path (`ProviderKey` tenants) untouched — placement applies to the standard path.

---

### Task 1: `ITenantDatabasePool` accessor + central bootstrap seeder (TDD)

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDatabasePool.cs`
- Create: `apps/tamma-elsa/src/Tamma.Data/Pooling/TenantDatabasePool.cs`
- Create: `apps/tamma-elsa/src/Tamma.Data/Seeders/TenantDatabasesSeeder.cs`
- Modify: DI registration (same composition roots as `ITenantAdminConnection` — grep and mirror) +
  the startup seeder invocation (mirror how `PlansSeeder.SeedAsync` is invoked at API startup —
  grep `PlansSeeder` in `src/Tamma.Api/`)
- Test: `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/TenantDatabasePoolTests.cs` (real PG,
  mirror the SchemaPerTenantMigrationTests harness)

- [ ] **Step 1: the contract** (complete file):

```csharp
using Npgsql;

namespace Tamma.Data.Abstractions;

/// <summary>
/// Unified-tenancy Phase 2 — accessor over the <c>tenant_databases</c>
/// registry (the operator's DB pool). Decrypts a pool row's admin
/// connection and derives tenant-facing connection strings against the
/// TARGET database. Roles are cluster-scoped, so every DDL the tenant
/// lifecycle runs (CREATE ROLE / SCHEMA / GRANT / DROP) must go through
/// the assigned row's admin connection — never the central
/// <see cref="ITenantAdminConnection"/>.
/// </summary>
public interface ITenantDatabasePool
{
    /// <summary>Decrypted admin connection string of the pool row.</summary>
    Task<string> GetAdminConnectionStringAsync(Guid databaseId, CancellationToken ct = default);

    /// <summary>
    /// Execute one statement on the pool row's admin connection
    /// (autocommit, fresh connection — mirrors ITenantAdminConnection).
    /// </summary>
    Task<int> ExecuteOnAsync(Guid databaseId, string commandText, CancellationToken ct = default);

    /// <summary>True when pg_roles on the row's cluster has the role.</summary>
    Task<bool> RoleExistsOnAsync(Guid databaseId, string roleName, CancellationToken ct = default);

    /// <summary>
    /// Tenant-facing connection string: the row's Host/Port/SSL + the
    /// row's database + the tenant role/password +
    /// <c>Search Path=&lt;schemaName&gt;</c>.
    /// </summary>
    Task<string> BuildTenantConnectionStringAsync(
        Guid databaseId, string roleName, string password, string schemaName,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: failing tests** (real PG; encrypt a known admin conn string into a seeded
  `tenant_databases` row using the registered `ITenantConnectionStringProtector` — in the test,
  construct `TenantSecretProtector` from an explicit base64 key, mirroring however
  `EncryptAndPersistConnectionStringActivityTests` builds protectors; read that test first):
  - `GetAdminConnectionString_DecryptsEnvelope` — round-trips.
  - `BuildTenantConnectionString_TargetsRowDatabaseWithSearchPath` — parse result with
    `NpgsqlConnectionStringBuilder`: Database == row's database name (from the admin CS), Username ==
    roleName, `SearchPath` == schemaName, Host == row Host.
  - `ExecuteOn_RunsOnTargetCluster` — `CREATE TABLE`/`SELECT 1` smoke through the accessor.
  Run → RED (types missing).

- [ ] **Step 3: implement `TenantDatabasePool`.** Constructor deps:
  `IDbContextFactory<ControlPlaneDbContext>`, `IConnectionStringDecryptor`, `ILogger<>?`. Internals:
  load the `TenantDatabase` row (throw `InvalidOperationException` with the databaseId when
  missing), decrypt `AdminConnectionStringEncrypted` via `IConnectionStringDecryptor.Decrypt(envelope,
  row.KekVersion)`, cache the decrypted string per databaseId in a `ConcurrentDictionary` (pool rows
  rotate rarely; add an `Evict(Guid)` internal for tests). `ExecuteOnAsync`/`RoleExistsOnAsync`:
  open a fresh `NpgsqlConnection` per call, no transaction, 300s command timeout (mirror
  `NpgsqlTenantAdminConnection`). `BuildTenantConnectionStringAsync`: start from
  `new NpgsqlConnectionStringBuilder(adminCs)`, overwrite `Username`, `Password`,
  `SearchPath = schemaName`, `ApplicationName = $"tamma-tenant;schema={schemaName}"`, drop
  `IncludeErrorDetail`; keep the admin CS's `Database` (the pool row's DB IS the target).

- [ ] **Step 4: implement `TenantDatabasesSeeder`** (static, mirror `PlansSeeder` shape exactly —
  insert-missing-only, never update):

```csharp
/// <summary>
/// Unified-tenancy Phase 2 — registers the central database as pool
/// member #1 (Label "central", shared, all tiers) when tenant_databases
/// is empty, so single-user/dev and SaaS share one placement code path.
/// Operators add real pool rows (and may retire this one) via Phase 4
/// admin CRUD. Insert-missing-only: never updates an existing row.
/// </summary>
public static class TenantDatabasesSeeder
{
    public static readonly Guid CentralDatabaseId =
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    public static async Task SeedAsync(
        ControlPlaneDbContext context,
        string adminConnectionString,
        ITenantConnectionStringProtector protector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);
        ArgumentNullException.ThrowIfNull(protector);

        if (await context.TenantDatabases.AnyAsync(cancellationToken))
            return;

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var now = DateTime.UtcNow;
        context.TenantDatabases.Add(new TenantDatabase
        {
            Id = CentralDatabaseId,
            Label = "central",
            Host = builder.Host ?? "localhost",
            Port = builder.Port,
            AdminConnectionStringEncrypted = protector.Encrypt(adminConnectionString),
            PlacementClass = "shared",
            TierEligibility = ["free", "team", "enterprise"],
            TenantCapacity = null,
            TenantCount = 0,
            Status = "active",
            KekVersion = (short)protector.CurrentKekVersion,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}
```

The `adminConnectionString` argument at the startup call site = the same chain
`NpgsqlTenantAdminConnection` uses (`ConnectionStrings:TenantAdmin` → `:DefaultConnection` →
`:ControlPlane`) — read its source and reuse the exact lookup. Add a seeder test
(`SeedAsync_InsertsCentralRowOnce`, `SeedAsync_NoopWhenRowsExist`) beside the pool tests.

- [ ] **Step 5: DI + startup wiring; build; GREEN; commit**

```bash
git add -A src/ tests/ && git commit -m "feat(tenancy-p2): tenant_databases pool accessor + central bootstrap seeder"
```

---

### Task 2: `ITenantPlacementService` (TDD)

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantPlacementService.cs` +
  `TenantPlacementService.cs`
- Modify: DI registration (Tamma.Api composition root, near the protector registration)
- Test: `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/TenantPlacementServiceTests.cs`

- [ ] **Step 1: contract** (complete):

```csharp
namespace Tamma.Api.Services.Provisioning;

/// <summary>Outcome of a placement decision (unified-tenancy Phase 2).</summary>
public sealed record TenantPlacement(Guid DatabaseId, string SchemaName);

/// <summary>
/// Assigns a tenant to a <c>tenant_databases</c> pool row by plan tier
/// (plans.PlacementPolicy: shared pool member vs dedicated DB) and
/// stamps tenants.DatabaseId + tenants.SchemaName. Idempotent: an
/// already-placed tenant returns its existing placement unchanged.
/// </summary>
public interface ITenantPlacementService
{
    Task<TenantPlacement> AssignAsync(Guid tenantId, CancellationToken ct = default);
}
```

- [ ] **Step 2: failing tests** (real PG via the existing harness; seed plans + a central pool row):
  - `Assign_FreeTenant_LandsOnSharedRow_StampsSchemaAndDatabase` — asserts tenants.DatabaseId/
    SchemaName shadow columns set, SchemaName == `TenantNaming.SchemaName(tenantId)`, TenantCount
    incremented to 1.
  - `Assign_IsIdempotent` — second call returns same placement, TenantCount stays 1.
  - `Assign_EnterpriseTenant_NoDedicatedRow_Throws` — clear `InvalidOperationException` naming the
    tier and policy (the central bootstrap row is shared; enterprise needs a dedicated row —
    operator adds one via Phase 4).
  - `Assign_SkipsFullAndNonActiveRows` — a row with TenantCapacity=1, TenantCount=1 is skipped; a
    `draining` row is skipped.

- [ ] **Step 3: implement.** Algorithm (all inside one CP DbContext save):
  1. Load tenant (IgnoreQueryFilters); if `SchemaName`+`DatabaseId` shadow props already set →
     return existing.
  2. Plan: `db.Plans.FirstOrDefault(p => p.Slug == tenant.Plan)` — **missing plan = throw**
     (tenant→system→error rule; never default silently).
  3. Candidate rows: `Status == "active"`, `PlacementClass == plan.PlacementPolicy`,
     `TierEligibility.Contains(plan.Slug)`, and (`TenantCapacity == null ||
     TenantCount < TenantCapacity`); for `dedicated` additionally `TenantCount == 0`. Order by
     `TenantCount` ascending then `CreatedAt`; take first; none → throw with tier/policy in message.
  4. Stamp shadow props `DatabaseId`/`SchemaName`; increment row.TenantCount; row.UpdatedAt + 
     tenant.UpdatedAt = UtcNow; save. (Concurrency: rely on the DB; two concurrent placements both
     succeed — capacity is advisory, exact enforcement is Phase 4's problem. Note this in a comment.)

- [ ] **Step 4: GREEN; commit**

```bash
git add -A src/ tests/ && git commit -m "feat(tenancy-p2): tier-driven tenant placement service"
```

---

### Task 3: `TenantProvisioningService` — the shared step engine (TDD, real PG)

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantProvisioningService.cs` +
  `TenantProvisioningService.cs`
- Modify: DI registration
- Test: `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/TenantProvisioningServiceTests.cs`

- [ ] **Step 1: contract** (complete):

```csharp
namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Unified-tenancy Phase 2 — the ONE implementation of the tenant
/// provisioning steps, shared by the SaaS CreateTenantWorkflow activities
/// and the single-user EnsurePersonalTenantMiddleware (universal rule:
/// one behavior, two scoping models). Steps are individually idempotent
/// so the Elsa workflow can wrap each in its own activity with retries.
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>Placement (Task 2 seam) — assign pool row + schema name.</summary>
    Task<TenantPlacement> AssignPlacementAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// CREATE ROLE on the placement row's cluster. Returns the generated
    /// password, or null when the role already existed (password
    /// unrecoverable — only the stored envelope from a prior run has it).
    /// </summary>
    Task<string?> CreateRoleAsync(Guid tenantId, TenantPlacement placement, CancellationToken ct = default);

    /// <summary>
    /// CREATE SCHEMA AUTHORIZATION role + GRANT CONNECT + default
    /// search_path, on the placement row's database. Idempotent.
    /// </summary>
    Task CreateSchemaAsync(Guid tenantId, TenantPlacement placement, CancellationToken ct = default);

    /// <summary>Tenant-facing conn string for the placement (Search Path included).</summary>
    Task<string> BuildConnectionStringAsync(
        Guid tenantId, TenantPlacement placement, string password, CancellationToken ct = default);

    /// <summary>
    /// Full pipeline for the synchronous single-user path: placement →
    /// role → schema → conn string → migrate (ITenantDbMigrator) → encrypt
    /// + persist (reusing the activity-equivalent semantics) → Status
    /// 'active'. Throws on any failure — caller decides UX.
    /// </summary>
    Task ProvisionAsync(Guid tenantId, CancellationToken ct = default);
}
```

- [ ] **Step 2: failing integration test** (THE phase proof; real PG container; explicit base64
  KEK so encryption is real):
  `Provision_PersonalTenant_EndToEnd_ResolvableByRealResolver`:
  1. Seed plans + central pool row (Task 1 seeder) pointing at the test container.
  2. Insert a tenant row (free plan).
  3. `await provisioning.ProvisionAsync(tenantId)`.
  4. Assert: `t_<hex>` schema exists in the container with `conventions` +
     `__TenantMigrationsHistory`; role `tamma_tenant_<hex>` exists; tenants row has
     EncryptedConnectionString + KekVersion + Status='active' + DatabaseId/SchemaName; pool row
     TenantCount==1.
  5. **Real resolver leg:** construct `LruPooledTenantConnectionResolver` directly (read its
     constructor/tests — `tests/.../Epic28/TenantConnectionPoolMetricsTests.cs` shows construction)
     with the real `AesGcm` decryptor for the same KEK; `GetDataSourceAsync(tenantId)`; open a
     `TenantDbContext` on it via `TenantDbContextFactory` and `AgentConfigs.ToListAsync()` succeeds
     (empty list) — proving decrypt → pool → search_path → schema end-to-end.
  6. **Isolation leg:** as the tenant ROLE (connect with the minted conn string), attempt
     `SELECT * FROM public.tenants` and `CREATE TABLE public.x(i int)` → both must FAIL
     (permission denied); `SELECT 1` and table creation inside own schema succeed.
  Run → RED.

- [ ] **Step 3: implement.** Deps: `ITenantPlacementService`, `ITenantDatabasePool`,
  `IDbContextFactory<ControlPlaneDbContext>`, `ITenantDbMigrator`,
  `ITenantConnectionStringProtector`, `ILogger<>`.
  - `CreateRoleAsync`: port the EXACT logic of `CreateTenantRoleActivity.ProcessAsync`
    (password generator included — move `GenerateStrongPassword` to a shared internal static, or
    duplicate verbatim with a cross-ref comment; prefer extracting to
    `TenantNaming`-adjacent helper `TenantRolePassword.Generate()` in Tamma.Data and have the
    activity use it too) — but execute via `ITenantDatabasePool.ExecuteOnAsync(placement.DatabaseId, ...)`
    + `RoleExistsOnAsync`. Returns null on idempotent-skip.
  - `CreateSchemaAsync` SQL (each statement via `ExecuteOnAsync`, quoted via `TenantNaming.Quote`):

```sql
CREATE SCHEMA IF NOT EXISTS "t_<hex>" AUTHORIZATION "tamma_tenant_<hex>";
GRANT CONNECT ON DATABASE "<row's database>" TO "tamma_tenant_<hex>";
ALTER ROLE "tamma_tenant_<hex>" IN DATABASE "<row's database>" SET search_path = "t_<hex>";
```

   (database name = parse from the pool row's decrypted admin CS via
   `NpgsqlConnectionStringBuilder(adminCs).Database` — expose it from `ITenantDatabasePool` if
   needed as `GetDatabaseNameAsync(databaseId)`; add to the interface in Task 1 if you reach this
   and it's missing — update Task 1's tests accordingly and note it.)
   No grants on `public` (PG15+ default already denies CREATE; do NOT revoke PUBLIC's USAGE —
   pg_catalog functions don't need it but breaking `public` USAGE cluster-wide is out of scope).
  - `BuildConnectionStringAsync`: `pool.BuildTenantConnectionStringAsync(placement.DatabaseId,
    TenantNaming.RoleName(tenantId), password, placement.SchemaName)`.
  - `ProvisionAsync`: placement → role (if password null AND tenants.EncryptedConnectionString
    empty → throw the same "DROP ROLE + retry" guidance the activity logs) → schema → conn string →
    `ITenantDbMigrator.MigrateTenantAppAsync(cs)` → encrypt+persist (same semantics as
    `EncryptAndPersistConnectionStringActivity`: skip when envelope present under current KEK; set
    KekVersion short; UpdatedAt) → set Status='active' (shadow prop) + save. Single-user path does
    NOT queue welcome emails or warm pools.

- [ ] **Step 4: GREEN (both legs); commit**

```bash
git add -A src/ tests/ && git commit -m "feat(tenancy-p2): TenantProvisioningService — shared placement/role/schema/mint pipeline"
```

---

### Task 4: SaaS workflow on the shared pipeline (schema instead of database)

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/AssignTenantPlacementActivity.cs`,
  `CreateTenantSchemaActivity.cs`
- Modify: `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/CreateTenantRoleActivity.cs`,
  `BuildTenantConnectionStringActivity.cs` (read it first — adapt to pool/`Search Path`)
- Modify: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs`
- Delete: `CreateTenantDatabaseActivity.cs` (zero data exists; no legacy tenants — remove, don't
  deprecate)
- Test: update `CreateTenantWorkflowStructureTests.cs`; add unit tests for the two new activities
  (mocked services, mirror `CreateTenantRoleActivityPasswordTests` harness style)

- [ ] **Step 1:** `AssignTenantPlacementActivity` — thin wrapper: resolves
  `ITenantPlacementService`, calls `AssignAsync`, sets workflow variables `DatabaseId` (string,
  Guid "D") + `SchemaName`. Mirror the input/output plumbing style of the existing TenantLifecycle
  activities (read `MarkProvisioningActivity` for the base-class pattern).
- [ ] **Step 2:** `CreateTenantRoleActivity` — switch from `ITenantAdminConnection` to
  `ITenantProvisioningService.CreateRoleAsync(tenantId, placement)` (placement reconstructed from
  the workflow variables). Preserve outputs (RoleName, GeneratedPassword — empty string on
  idempotent-skip, matching current contract). `CreateTenantSchemaActivity` — wraps
  `CreateSchemaAsync`. `BuildTenantConnectionStringActivity` — wraps `BuildConnectionStringAsync`.
- [ ] **Step 3:** Workflow sequence becomes: initInputs → markProvisioning → **assignPlacement** →
  createRole → **createSchema** → buildConnectionString → migrateDatabase → seedDefaults →
  encryptAndPersist → warmPool → markActive → queueWelcome. Declare the two new variables. Update
  the structure tests (activity count/order/variables).
- [ ] **Step 4:** Build; run
  `sg docker -c "dotnet test tests/Tamma.Activities.Tests/Tamma.Activities.Tests.csproj -v minimal"`
  (whole project — workflow structure + activity units live here). Expected: all pass.
- [ ] **Step 5: Commit**

```bash
git add -A src/ tests/ && git commit -m "feat(tenancy-p2): CreateTenantWorkflow provisions schema-per-tenant via shared pipeline"
```

---

### Task 5: Delete path — schema-scoped

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/DropTenantSchemaActivity.cs`
- Modify: `DropTenantRoleActivity.cs` (DROP OWNED must run on the TARGET db via the pool; DROP ROLE
  on the target cluster), `BackupTenantDatabaseActivity.cs` (when tenant has SchemaName: `pg_dump
  -n <schema>` against the target DB using the pool row's connection info; read
  `GetConnectionInfo` usage first), `EmitDeletedSuccessActivity.cs`/`SoftDeleteTenantRowActivity.cs`
  (release placement: decrement pool TenantCount, null DatabaseId/SchemaName shadow props — pick
  the activity that already mutates the CP row and do it there, same SaveChanges)
- Modify: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeleteTenantWorkflow.cs` (dropDatabase →
  dropSchema) + structure tests
- Delete: `DropTenantDatabaseActivity.cs`
- Test: unit tests for `DropTenantSchemaActivity` (mocked pool), updated structure tests

- [ ] **Step 1:** `DropTenantSchemaActivity`: resolve tenant's `DatabaseId`/`SchemaName` shadow
  props from CP (IgnoreQueryFilters); if either is null log + return (tenant predates placement —
  nothing to drop); else `DROP SCHEMA IF EXISTS "t_<hex>" CASCADE;` via
  `ITenantDatabasePool.ExecuteOnAsync`. `DropTenantRoleActivity`: same placement lookup; run
  `DROP OWNED BY "role" ;` via the pool (target db) then `DROP ROLE IF EXISTS "role";` via the
  pool; when placement is null keep the legacy central-admin path (the role may exist on central
  from pre-Phase-2 dev runs — keep `ITenantAdminConnection` fallback with a comment).
- [ ] **Step 2:** Backup: when SchemaName set → `pg_dump --schema=<schema>` flavor (add `-n` arg to
  the existing pg_dump invocation path), against the pool row's DB (extend `ITenantDatabasePool`
  with `GetConnectionInfoAsync(databaseId)` mirroring `ITenantAdminConnection.GetConnectionInfo` if
  the activity needs discrete parts — note the interface addition in your report). Legacy whole-DB
  branch stays for SchemaName-null tenants.
- [ ] **Step 3:** Placement release in the soft-delete/emit activity (whichever already saves the
  CP row): decrement the pool row's TenantCount (floor 0), null the tenant's
  DatabaseId/SchemaName shadow props in the SAME SaveChanges that nulls the envelope.
- [ ] **Step 4:** Workflow + structure tests updated; build; run the Activities test project; all
  pass. Commit:

```bash
git add -A src/ tests/ && git commit -m "feat(tenancy-p2): delete path drops tenant schema/role and releases pool slot"
```

---

### Task 6: Single-user personal tenant provisions synchronously

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs` (the
  creation block ~126-175)
- Test: extend the middleware's existing tests (grep `EnsurePersonalTenant` in tests/) + one real-PG
  integration test in `TenantProvisioningServiceTests.cs`

- [ ] **Step 1:** After membership+active-tenant bookkeeping and BEFORE the success event, resolve
  `ITenantProvisioningService` from `context.RequestServices` and:

```csharp
// Unified-tenancy Phase 2: the personal tenant is provisioned
// synchronously (placement → role → schema → minted connection string →
// migrations) so it is a first-class tenant from its first request.
// Failure policy: log + continue — the request proceeds on the
// transitional shared path (stub resolver) and the admin retry
// endpoint can re-provision; Phase 3 (stub removal) makes this
// failure hard.
try
{
    var provisioning = context.RequestServices
        .GetRequiredService<ITenantProvisioningService>();
    await provisioning.ProvisionAsync(tenant.Id, context.RequestAborted);
}
catch (Exception ex)
{
    logger.LogError(ex,
        "personal tenant provisioning failed tenantId={TenantId} — continuing on shared path",
        tenant.Id);
}
```

- [ ] **Step 2:** Tests: middleware unit tests get a fake `ITenantProvisioningService` (assert it
  was invoked with the new tenant id; assert a throwing fake does NOT fail the request). The real-PG
  integration test asserts a first-login-created tenant ends Status='active' with envelope +
  schema (reuse Task 3's assertions via a helper).
- [ ] **Step 3:** Build + run middleware/auth + tenancy test filters; commit:

```bash
git add -A src/ tests/ && git commit -m "feat(tenancy-p2): personal tenants provision schema+conn-string at first login"
```

---

### Task 7: Full suite

- [ ] `dotnet build Tamma.sln` → 0 errors; `sg docker -c "dotnet test Tamma.sln -v minimal"` →
  baseline ~4400+, 0 failures. Root-cause any failure (likely suspects: structure tests missed,
  DI registration missing in a composition root that runs activities, seeder ordering vs
  PlansSeeder at startup). Fix and re-run. No commit (next task commits docs together).

---

### Task 8: Docs + execution record

**Files:**
- Modify: `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md`:
  - Phase decomposition: mark this as `**Phase 2 (re-ordered: was "Phase 3 — unified creation
    path") — DONE <date>.**` and re-label the old Phase 2 (unified resolver / stub removal) as
    `**Phase 3 (re-ordered: was Phase 2)** — stub removal now possible because every tenant mints
    at creation`; one-line rationale (stub removal hard-depends on mint-at-creation).
  - Deviations list append: `11. **Central DB bootstraps as pool member #1** (tenant_databases
    Label='central', shared, all tiers, insert-missing-only seeder) — dev/self-host and SaaS share
    one placement path. 12. **Personal tenants provision synchronously at first login** in
    single-user mode (soft-fail until Phase 3 stub removal makes it hard).`
- Modify: `wiki/Multi-Tenant-Provisioning.md` + `wiki/Architecture.md` tenancy sections — describe
  the new creation path (placement/tier table, schema+role+grants, minted conn string), mark the
  db-per-tenant CreateDatabase description as superseded.
- Append execution record to THIS plan doc (commit range, proof-test evidence, notable findings).
- Commit + (controller pushes):

```bash
cd /home/meywd/tamma && git add -A apps/ docs/ wiki/
git commit -m "docs(tenancy-p2): mark Phase 2 complete (unified creation path; phases 2/3 re-ordered)"
```

---

## Self-review notes

- **Spec coverage** (parent §3/§4 "unified creation path"): placement service ✓ (T2),
  schema lifecycle ✓ (T3/T4/T5), mint for every tenant incl. personal ✓ (T3/T6),
  tenant_databases live ✓ (T1), delete/backup schema-scoped ✓ (T5). Resolver/stub intentionally
  untouched (now Phase 3).
- **Isolation invariant** (locked decision 1) is TESTED, not assumed: Task 3's isolation leg proves
  the tenant role cannot read `public` tables nor create outside its schema.
- **Type consistency:** `TenantPlacement(Guid DatabaseId, string SchemaName)` used by T2-T6;
  `CreateRoleAsync` returns `string?` (null = idempotent-skip) mirrored by the activity's
  empty-string output contract (conversion at the activity boundary).
- **Known risks:** (1) DI registration for activities executes in more than one host — T1/T4
  explicitly instruct grepping the existing registrations and mirroring; full suite + structure
  tests catch misses. (2) `ITenantDatabasePool` may need `GetDatabaseNameAsync`/`GetConnectionInfoAsync`
  additions discovered mid-task — T3/T5 call this out as acceptable interface growth with test
  updates. (3) KEK in pure dev (no Cranl:ApiKey): tests always pass an explicit base64 KEK; the
  middleware path soft-fails by design until Phase 3.

---

## Execution record (2026-06-10)

**Status: Phase 2 COMPLETE.** All 8 tasks executed; full suite green.

**Commit range:** `c7073248..60202650` (8 commits on `feat/wave-b`) + the Task-8 docs commit:

- `2709147e` polish(tenancy-p2): pool ExecuteOn injection contract doc + seeder KEK-version note
- `3bd67c46` feat(tenancy-p2): tier-driven tenant placement service
- `461f3e1d` fix(tenancy-p2): reject placement of soft-deleted tenants + corrupt-state coverage
- `d8235917` feat(tenancy-p2): TenantProvisioningService — shared placement/role/schema/mint pipeline
- `e402e08a` polish(tenancy-p2): correct provisioning recovery runbook text + comment fixes
- `0a1fd62a` feat(tenancy-p2): CreateTenantWorkflow provisions schema-per-tenant via shared pipeline
- `3aa04e8d` feat(tenancy-p2): delete path drops tenant schema/role and releases pool slot
- `60202650` feat(tenancy-p2): personal tenants provision schema+conn-string at first login
- (this commit) docs(tenancy-p2): mark Phase 2 complete (unified creation path; phases 2/3 re-ordered)

**Full suite (Task 7):** `dotnet build Tamma.sln` 0 errors; `dotnet test Tamma.sln` →
**4464 passed / 11 skipped / 0 failed** across 10 projects (baseline 2026-06-09 was ~4409/11 —
Phase 2 net +~55 tests):

| Project | Passed | Skipped |
|---|---:|---:|
| Tamma.Api.Tests | 2744 | 8 |
| Tamma.Activities.Tests | 1237 | 0 |
| Tamma.Platforms.GitLab.Tests | 97 | 0 |
| Tamma.Platforms.Gitea.Tests | 96 | 0 |
| Tamma.Platforms.Tests | 90 | 0 |
| Tamma.Platforms.Abstractions.Tests | 66 | 0 |
| Tamma.Platforms.GitHub.Tests | 63 | 0 |
| Tamma.Studio.Tests | 30 | 0 |
| Tamma.Core.Tests | 23 | 0 |
| Tamma.Platforms.IntegrationTests | 18 | 3 |

One Task-7 fix: 9 failures in `PlatformOwnerAccessPolicyTests` — the fixture boots a
**Production-environment** WebApplicationFactory, and the new Phase-2 startup seeder
(`TenantDatabasesSeeder` in `Program.cs`) eagerly resolves `ITenantConnectionStringProtector`,
whose `FromConfiguration` hard-fails in Production without `Cranl:EncryptionKey` (R2-H11).
Fix: the fixture now supplies a base64 32-byte `Cranl__EncryptionKey` (like a real Production
deployment must) and resets it in teardown; `Cranl__ApiKey` stays unset so the Null provisioner
seam is unaffected.

**Phase proof (Task 3 e2e, both legs):**
`Provision_PersonalTenant_EndToEnd_ResolvableByRealResolver`
(`tests/Tamma.Api.Tests/Tenancy/TenantProvisioningServiceTests.cs`):

- *Real-resolver leg:* after `ProvisionAsync`, a directly-constructed
  `LruPooledTenantConnectionResolver` + real `AesGcm` decryptor (same KEK) resolves the tenant,
  and a `TenantDbContext` opened via `TenantDbContextFactory` reads `AgentConfigs` successfully —
  decrypt → pool → `Search Path` → schema, end-to-end ahead of Phase 3.
- *Role-isolation leg:* connected as the tenant role on the minted connection string,
  `SELECT * FROM public.tenants` and `CREATE TABLE public.x(...)` both fail with SqlState
  **42501** (permission denied), while DML/DDL inside the tenant's own `t_<hex>` schema succeeds —
  the locked role-per-tenant isolation decision is tested, not assumed.

**Notable findings / deviations during execution:**

1. **PG privilege-check-before-IF-NOT-EXISTS forced a DO-block in the migrator** — Postgres
   evaluates CREATE privileges on `CREATE SCHEMA IF NOT EXISTS` even when the schema already
   exists, so the tenant-role migration path wraps the safety-net create in a `DO $$ ... $$`
   existence-checked block.
2. **Interfaces moved to `Tamma.Data.Abstractions`** — `ITenantPlacementService` /
   `ITenantProvisioningService` could not live in `Tamma.Api.Services.Provisioning` as planned:
   the activities project must reference the contracts, and project-reference direction
   (Activities → Data, never Activities → Api) forced them down into Data abstractions.
3. **Soft-deleted-tenant placement guard added in review** — `AssignAsync` rejects tenants whose
   row is soft-deleted (`461f3e1d`), with corrupt-state coverage.
4. **`SchemaExistsOnAsync` added to `ITenantDatabasePool`** for backup idempotency (skip
   `pg_dump -n` when the schema is already gone on a delete retry).
5. **Cleanup vocabulary renamed** — `drop_database_failed` → `drop_schema_failed` in
   `CleanupFailureClassifier` / terminal-event codes, matching the schema-scoped delete path.

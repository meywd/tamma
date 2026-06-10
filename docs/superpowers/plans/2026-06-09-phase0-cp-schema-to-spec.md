# Phase 0 — CP Schema to Spec + `tenant_databases` Registry + Collapse CP Migration Baseline

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the control-plane schema to the unified-tenancy spec (new `tenant_databases`
registry, `tenants.SchemaName`/`DatabaseId`, `KekVersion` smallint NOT NULL DEFAULT 1, Status +
connection-string + api_keys-scope CHECKs, `plans.PlacementPolicy` + seed), reconcile the
runtime/design-time migrations-history-table name mismatch, and collapse the 30-migration CP chain
into one `InitialControlPlane` baseline — **schema-only, no behavior change**.

**Architecture:** All changes land in `ControlPlaneDbContext`'s model (EF Core 9 + Npgsql). CHECK
constraints go into the model via `HasCheckConstraint` so they survive baseline regeneration.
Raw SQL from the old chain (RLS policies, triggers, partial indexes) is classified keep/drop and the
keepers are ported into the regenerated baseline. Validation = apply old chain and new baseline to
two throwaway Postgres containers, `pg_dump --schema-only` both, diff against an intended-changes
whitelist, plus psql INSERT probes for every new CHECK.

**Tech Stack:** .NET 9 / EF Core 9, Npgsql, PostgreSQL (throwaway: `pgvector/pgvector:pg16`, same
image as CI), `dotnet-ef` 9.0.9 (already installed globally — verified).

**Parent doc:** `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (§2, Phase 0).

---

## Environment facts (verified 2026-06-09 — do not re-derive)

- Repo root for all C# work: `/home/meywd/tamma/apps/tamma-elsa`. Build: `dotnet build Tamma.sln`
  (no wrapper). Tests need docker (Testcontainers): `sg docker -c "dotnet test ..."`. Any direct
  `docker`/`psql-in-docker` command also needs `sg docker -c "..."`.
- `ControlPlaneDbContext` owns BOTH `src/Tamma.Data/Migrations/*.cs` (17 root migrations — verified
  via `[DbContext(typeof(ControlPlaneDbContext))]` in Designer files) AND
  `src/Tamma.Data/Migrations/ControlPlane/*.cs` (13 migrations) = 30 total.
  `src/Tamma.Data/Migrations/Tenant/` belongs to `TenantDbContext` — **never touch it**.
- History-table mismatch: design-time factory (`ControlPlaneDesignTimeDbContextFactory.cs:21`) uses
  `__ControlPlaneMigrationsHistory`; runtime (`DependencyInjection.cs:76`) uses
  `__TammaMigrationsHistory`. We unify on `__ControlPlaneMigrationsHistory`.
- `TammaModelConfiguration.ConfigureControlPlaneEntities(modelBuilder, includeTenantShadowColumns)`
  has exactly ONE production call site: `ControlPlaneDbContext.cs:212` with `true`. The tenants
  shadow-column block is `TammaModelConfiguration.cs:193-247`.
- Live Status values written to the tenants shadow column (grep-verified): `pending_verification`,
  `provisioning`, `active`, `deleting`, `deleted`, `failed` (+ constants for `suspended`,
  `delete_requested` in `TenantStatusEvaluator.cs`). Nothing writes `pending`.
- The delete flow NULLS `EncryptedConnectionString` in the same save that sets `Status='deleted'`
  (`EmitDeletedSuccessActivity.cs:54-55`, `SoftDeleteTenantRowActivity.cs:80-81`). The admin
  provisioning flow sets `Status='provisioning'` BEFORE the workflow mints the connection string
  (`AuthEndpoints.cs:407`), and `failed` can be set before any mint.
- Live api_keys Scope values written to the **CP** table: `user` (`AdminEndpoints.cs:374`),
  `service` (`AdminEndpoints.cs:48`), `installation` (`ApiKeyRotationService.cs:25`), `tenant`
  (`OrgApiKeysEndpoints.cs:53` — yes, into `ControlPlaneDbContext`; physical move to tenant schemas
  is a later phase). `platform` appears in the platform_api_key_index path.
- KekVersion call sites that read the shadow prop as `EF.Property<int?>`:
  `AdminTenantsEndpoints.cs:169,653`, `KekCabinetHealthCheck.cs:76,88`,
  `KekRotationCoordinator.cs:615,619` (+ write at `:670`). Tests may have more — grep before edit.

## Documented deviations from the parent doc (decided here, intentionally)

1. **api_keys Scope CHECK is a transitional enumeration** `('platform','user','installation',
   'service','tenant')`, NOT the spec-final `('platform','user')`. Reason: three live CP code paths
   write `service`/`installation`/`tenant` today (see facts above); the spec-final CHECK lands when
   tenant-scoped keys physically move out of CP (unified-tenancy Phase 2+).
2. **Connection-string CHECK exempts `provisioning`, `failed`, `deleted`, `deleting`,
   `delete_requested`** in addition to NULL and `pending_verification` — i.e. presence is enforced
   only for `active` and `suspended`. Reason: provisioning/failed/deleted legitimately coexist with
   a NULL connection string in today's flows (mint happens mid-provisioning; failure can precede
   mint; delete nulls the envelope), and deleting/delete_requested can be entered from `failed`
   (or legacy NULL-status) rows that never got a connection string minted — force-delete
   (`AdminTenantsEndpoints.ForceDeleteTenant`, `MarkTenantDeletingActivity`) would otherwise hit
   23514 on the designed cleanup path. The spec's invariant — *active tenants always have a
   connection string* — is enforced. Tighten to spec-exact in Phase 3 when every tenant is minted
   at creation.

---

### Task 1: `TenantDatabase` entity + EF config + DbSet

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Data/Entities/TenantDatabase.cs`
- Modify: `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` (DbSet near line 33-40 block;
  `ConfigureTenantDatabases(modelBuilder);` call in `OnModelCreating` after line 234's
  `ConfigurePlatformWebhookDeliveries(modelBuilder);`; new private method near
  `ConfigureKekRotations` at line ~390)

- [ ] **Step 1: Create the entity**

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Unified-tenancy Phase 0 (plan 2026-06-09 §2.1) — one row per Postgres
/// database available for tenant-schema placement: the operator's DB pool.
/// A database hosts 1..N tenant schemas; <c>PlacementClass</c> says whether
/// it is a shared pool member or a dedicated (single-tenant) DB. The admin
/// connection string (provisioner role — creates schemas/roles) is encrypted
/// with the same AES-GCM KEK envelope used for tenant connection strings.
/// </summary>
public class TenantDatabase
{
    public Guid Id { get; set; }

    /// <summary>Operator-facing name, e.g. <c>shared-eu-1</c>, <c>dedicated-acme</c>.</summary>
    public string Label { get; set; } = null!;

    public string Host { get; set; } = null!;
    public int Port { get; set; } = 5432;

    /// <summary>AES-GCM/KEK envelope of the provisioner-role connection string.</summary>
    public byte[] AdminConnectionStringEncrypted { get; set; } = null!;

    /// <summary><c>shared</c> | <c>dedicated</c>.</summary>
    public string PlacementClass { get; set; } = "shared";

    /// <summary>Plan tiers allowed to land here, e.g. <c>{free,team}</c>.</summary>
    public string[] TierEligibility { get; set; } = [];

    /// <summary>Max tenant schemas (NULL = unbounded); used for shared pools.</summary>
    public int? TenantCapacity { get; set; }

    /// <summary>Maintained on placement/move operations.</summary>
    public int TenantCount { get; set; }

    /// <summary><c>active</c> | <c>draining</c> | <c>full</c> | <c>retired</c>.</summary>
    public string Status { get; set; } = "active";

    /// <summary>KEK version of the admin-connection envelope.</summary>
    public short KekVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Wire into `ControlPlaneDbContext`**

DbSet (in the block at lines 33-40, matching the expression-body style):

```csharp
public DbSet<TenantDatabase> TenantDatabases => Set<TenantDatabase>();
```

Call in `OnModelCreating` (after `ConfigurePlatformWebhookDeliveries(modelBuilder);`):

```csharp
ConfigureTenantDatabases(modelBuilder);
```

Private method (place near `ConfigureKekRotations`, match the surrounding doc-comment style):

```csharp
/// <summary>
/// Unified-tenancy Phase 0 — <c>tenant_databases</c> registry (the admin DB
/// pool). CHECKs pin the two closed enums; <c>Label</c> is the operator key.
/// </summary>
private static void ConfigureTenantDatabases(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<TenantDatabase>(entity =>
    {
        entity.ToTable("tenant_databases", t =>
        {
            t.HasCheckConstraint(
                "ck_tenant_databases_placement_class",
                "\"PlacementClass\" IN ('shared','dedicated')");
            t.HasCheckConstraint(
                "ck_tenant_databases_status",
                "\"Status\" IN ('active','draining','full','retired')");
        });
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        entity.Property(e => e.Label).IsRequired().HasMaxLength(255);
        entity.Property(e => e.Host).IsRequired().HasMaxLength(255);
        entity.Property(e => e.Port).HasDefaultValue(5432);
        entity.Property(e => e.AdminConnectionStringEncrypted)
            .IsRequired().HasColumnType("bytea");
        entity.Property(e => e.PlacementClass)
            .IsRequired().HasMaxLength(20).HasDefaultValue("shared");
        entity.Property(e => e.TierEligibility)
            .HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
        entity.Property(e => e.TenantCount).HasDefaultValue(0);
        entity.Property(e => e.Status)
            .IsRequired().HasMaxLength(20).HasDefaultValue("active");
        entity.Property(e => e.KekVersion).HasDefaultValue((short)1);
        entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

        entity.HasIndex(e => e.Label).IsUnique();
        entity.HasIndex(e => e.Status);
    });
}
```

- [ ] **Step 3: Build**

Run: `cd /home/meywd/tamma/apps/tamma-elsa && dotnet build Tamma.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Tamma.Data/Entities/TenantDatabase.cs src/Tamma.Data/ControlPlaneDbContext.cs
git commit -m "feat(tenancy-p0): tenant_databases registry entity + CP model config"
```

---

### Task 2: tenants — `SchemaName`, `DatabaseId`, Status CHECK, connection-string CHECK

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (the
  `if (includeTenantShadowColumns)` block at lines 193-247)

- [ ] **Step 1: Add the new shadow columns + FK + CHECKs**

Inside the `if (includeTenantShadowColumns)` block, after the existing
`entity.Property<string?>("ProviderResourceIds")...` declaration and before the
`entity.HasIndex("Status");` line, add:

```csharp
// ── Unified-tenancy Phase 0 (plan 2026-06-09 §2.2) ──
//
// SchemaName = the tenant's schema (t_<hex>) inside its assigned DB;
// DatabaseId = which tenant_databases row hosts that schema. Both stay
// NULL until the unified creation path (Phase 3) mints them — Phase 0
// is schema-only.
entity.Property<string?>("SchemaName").HasMaxLength(63);
entity.Property<Guid?>("DatabaseId");

entity.HasIndex("SchemaName").IsUnique()
    .HasFilter("\"SchemaName\" IS NOT NULL");
entity.HasIndex("DatabaseId");

entity.HasOne<TenantDatabase>()
    .WithMany()
    .HasForeignKey("DatabaseId")
    .OnDelete(DeleteBehavior.Restrict);

// CHECKs reference shadow columns, so they live inside this guard.
// Conn-string CHECK: the spec invariant is "active tenants always have
// a connection string". provisioning/failed/deleted are exempt because
// today's flows legitimately hold NULL there (mint happens mid-
// provisioning; failure can precede mint; delete nulls the envelope).
// Tighten to spec-exact (only pending_verification exempt) in Phase 3.
entity.ToTable("tenants", t =>
{
    t.HasCheckConstraint(
        "ck_tenants_status",
        "\"Status\" IS NULL OR \"Status\" IN ('pending_verification'," +
        "'provisioning','active','delete_requested','deleting'," +
        "'deleted','failed','suspended')");
    t.HasCheckConstraint(
        "ck_tenants_connection_string_present",
        "\"Status\" IS NULL OR \"Status\" IN ('pending_verification'," +
        "'provisioning','failed','deleted') " +
        "OR \"EncryptedConnectionString\" IS NOT NULL");
});
```

Note: the unconditional `entity.ToTable("tenants");` at the top of the Tenant block stays as-is;
the second `ToTable` call here only accumulates the CHECK annotations onto the same table.

- [ ] **Step 2: Build**

Run: `cd /home/meywd/tamma/apps/tamma-elsa && dotnet build Tamma.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the tenant-adjacent test projects (regression only — no new tests; CHECK
  enforcement is verified against real Postgres in Task 9)**

Run: `sg docker -c "dotnet test tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant' --no-build -v minimal"`
(if `--no-build` complains, drop it)
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Tamma.Data/TammaModelConfiguration.cs
git commit -m "feat(tenancy-p0): tenants SchemaName/DatabaseId + status & conn-string CHECKs"
```

---

### Task 3: `KekVersion` → smallint NOT NULL DEFAULT 1 (+ all call sites)

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (line ~206:
  `entity.Property<int?>("KekVersion");`)
- Modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` (lines 169, 653)
- Modify: `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/KekCabinetHealthCheck.cs` (lines ~76, ~88)
- Modify: `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/KekRotationCoordinator.cs` (lines ~615,
  ~619, ~670)
- Possibly: test files — grep first (Step 2)

- [ ] **Step 1: Change the shadow property declaration**

In `TammaModelConfiguration.cs`, replace:

```csharp
entity.Property<int?>("KekVersion");
```

with:

```csharp
// smallint NOT NULL DEFAULT 1 per spec (plan 2026-06-09 §2.2). CLR type
// short — every EF.Property<T> read of this column must use short.
entity.Property<short>("KekVersion").HasDefaultValue((short)1);
```

- [ ] **Step 2: Find ALL call sites (source + tests)**

Run: `cd /home/meywd/tamma/apps/tamma-elsa && grep -rn 'Property<int?>(t, "KekVersion")\|Property("KekVersion")\|Property<int?>("KekVersion")\|EF.Property<int?>(.*KekVersion' --include=*.cs src/ tests/`
Expected: the six known sites below plus possibly tests. Fix every hit using the same patterns.

- [ ] **Step 3: Update each call site**

`AdminTenantsEndpoints.cs:169` and `:653` — both are projections into an anonymous row whose
consumer treats KekVersion as a nullable int. Replace:

```csharp
KekVersion = EF.Property<int?>(t, "KekVersion"),
```

with (the int? cast keeps the downstream DTO shape unchanged and translates to SQL fine):

```csharp
KekVersion = (int?)EF.Property<short>(t, "KekVersion"),
```

`KekCabinetHealthCheck.cs` — the legacy-NULL ("version 0") branch is dead once the column is NOT
NULL DEFAULT 1, but keep the code shape minimal-diff. Replace at ~:76:

```csharp
.Where(t => EF.Property<int?>(t, "KekVersion") == null)
```

with:

```csharp
.Where(t => (int?)EF.Property<short>(t, "KekVersion") == null)
```

and at ~:88 replace:

```csharp
.Select(t => (int?)EF.Property<int?>(t, "KekVersion"))
```

with:

```csharp
.Select(t => (int?)EF.Property<short>(t, "KekVersion"))
```

`KekRotationCoordinator.cs` at ~:615 replace:

```csharp
.Where(t => (EF.Property<int?>(t, "KekVersion") ?? 0) < toVersion)
```

with:

```csharp
.Where(t => EF.Property<short>(t, "KekVersion") < toVersion)
```

at ~:619 replace:

```csharp
EF.Property<int?>(t, "KekVersion") ?? 0))
```

with:

```csharp
(int)EF.Property<short>(t, "KekVersion")))
```

at ~:670 replace:

```csharp
entry.Property("KekVersion").CurrentValue = toVersion;
```

with:

```csharp
entry.Property("KekVersion").CurrentValue = (short)toVersion;
```

(Adjust surrounding lambda shapes only as far as the compiler requires; preserve semantics. If a
test sets `Property("KekVersion").CurrentValue = <int literal>`, cast it `(short)`.)

- [ ] **Step 4: Build**

Run: `dotnet build Tamma.sln`
Expected: 0 errors. If a missed call site throws `InvalidCastException`-style model errors at test
time, return to Step 2 — the grep missed a pattern.

- [ ] **Step 5: Run the KEK test suites**

Run: `sg docker -c "dotnet test tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj --filter 'FullyQualifiedName~Kek' -v minimal"`
Expected: all pass (KekRotationCoordinatorTests, KekRotationMetricsTests, KekCabinetHealthCheck
tests if present).

- [ ] **Step 6: Commit**

```bash
git add -A src/ tests/
git commit -m "feat(tenancy-p0): KekVersion smallint NOT NULL DEFAULT 1 + short call sites"
```

---

### Task 4: api_keys Scope CHECK (CP) + move `users_platform_role_check` into the model

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (ApiKey block ~line 290:
  `entity.ToTable("api_keys");`; User block — find `ToTable("users"...)` near the
  `PlatformRole` property config)
- Reference (read-only): `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/20260426172707_AddUsersPlatformRole.cs`

- [ ] **Step 1: api_keys CHECK — replace `entity.ToTable("api_keys");` in the CP ApiKey block with:**

```csharp
entity.ToTable("api_keys", t =>
{
    // Phase 0 transitional enumeration (plan 2026-06-09 §2.4 deviation 1).
    // Spec target on CP is ('platform','user') — unreachable until
    // tenant-scoped keys physically move to tenant schemas (Phase 2+)
    // and the service/installation scopes are reconciled with the spec.
    t.HasCheckConstraint(
        "ck_api_keys_scope",
        "\"Scope\" IN ('platform','user','installation','service','tenant')");
});
```

(The tenant-DB api_keys table at `TammaModelConfiguration.cs:~1031` already has
`ck_api_keys_tenant_scope` — leave untouched.)

- [ ] **Step 2: users PlatformRole CHECK into the model**

Read `20260426172707_AddUsersPlatformRole.cs` and copy its exact CHECK expression
(`users_platform_role_check`). In the User entity block, add the constraint to the `ToTable` call
using constraint name `ck_users_platform_role` and the same expression, e.g. (verify expression
against the migration before writing):

```csharp
t.HasCheckConstraint(
    "ck_users_platform_role",
    "\"PlatformRole\" IN ('user','platform_admin')");
```

If the User block's `ToTable` has no build-action yet, convert it the same way as Step 1. If the
User table already declares other CHECKs via `HasCheckConstraint` (Role, AuthMethod — see
`TammaModelConfiguration.cs:96-108`), add this one alongside them in the same style.

- [ ] **Step 3: KekRotations raw index check**

Read `Migrations/ControlPlane/20260426120000_KekRotations.cs` — it creates one index via raw SQL.
If `ControlPlaneDbContext.ConfigureKekRotations` (line ~390) does not already declare an
equivalent `HasIndex`, add it there as a model-level index (same columns/filter, name preserved
via `.HasDatabaseName("<original name>")`). This keeps the regenerated baseline complete without
porting raw SQL for it.

- [ ] **Step 4: Build**

Run: `dotnet build Tamma.sln`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Tamma.Data/
git commit -m "feat(tenancy-p0): api_keys scope CHECK (transitional) + model-level users/kek constraints"
```

---

### Task 5: `plans.PlacementPolicy` + CHECK + seeder

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs`
- Modify: `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (Plan block, ~line 374)
- Modify: `apps/tamma-elsa/src/Tamma.Data/Seeders/PlansSeeder.cs`
- Test: extend the existing PlansSeeder/plans test if one exists
  (`grep -rln "PlansSeeder" tests/`); otherwise add a focused test beside the closest
  ControlPlane-seeding test, mirroring its harness.

- [ ] **Step 1: Entity property** (after `IsActive`, before `CreatedAt`):

```csharp
/// <summary>
/// Unified-tenancy placement (plan 2026-06-09 §2.3, decision 2):
/// <c>shared</c> = tenant schema lands in a shared-pool DB;
/// <c>dedicated</c> = tenant gets a single-tenant DB.
/// </summary>
public string PlacementPolicy { get; set; } = "shared";
```

- [ ] **Step 2: EF config** — in the Plan block replace `entity.ToTable("plans");` with:

```csharp
entity.ToTable("plans", t =>
{
    t.HasCheckConstraint(
        "ck_plans_placement_policy",
        "\"PlacementPolicy\" IN ('shared','dedicated')");
});
```

and add with the other property configs:

```csharp
entity.Property(e => e.PlacementPolicy)
    .IsRequired().HasMaxLength(20).HasDefaultValue("shared");
```

- [ ] **Step 3: Seeder** — in `PlansSeeder.SeedAsync`, add to each seed initializer
  (locked decision 2: free=shared, team=shared, enterprise=dedicated):

```csharp
// free plan object:
PlacementPolicy = "shared",
// team plan object:
PlacementPolicy = "shared",
// enterprise plan object:
PlacementPolicy = "dedicated",
```

- [ ] **Step 4: Failing test first** — if a PlansSeeder test exists, extend it; otherwise create
  one mirroring the harness of the nearest seeder/ControlPlane test. Assertion content:

```csharp
[Fact]
public async Task SeedAsync_SetsPlacementPolicyPerTier()
{
    // arrange: empty ControlPlaneDbContext via the suite's usual factory
    await PlansSeeder.SeedAsync(context);

    var bySlug = await context.Plans.ToDictionaryAsync(p => p.Slug);
    Assert.Equal("shared", bySlug["free"].PlacementPolicy);
    Assert.Equal("shared", bySlug["team"].PlacementPolicy);
    Assert.Equal("dedicated", bySlug["enterprise"].PlacementPolicy);
}
```

Run it BEFORE Step 3's seeder edit to see it fail (red), then after (green) — write the test, run,
apply Step 3, run again.

Run: `sg docker -c "dotnet test tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj --filter 'FullyQualifiedName~PlansSeeder' -v minimal"`
Expected: fail before seeder edit (PlacementPolicy = "shared" default on enterprise), pass after.

- [ ] **Step 5: Build + commit**

```bash
dotnet build Tamma.sln
git add src/Tamma.Data/ tests/
git commit -m "feat(tenancy-p0): plans.PlacementPolicy + tier seed (free/team=shared, enterprise=dedicated)"
```

---

### Task 6: Unify the migrations-history table name + wipe-list update

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs:76`
- Modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs` (wipe block, lines ~1955-1980)

- [ ] **Step 1: Runtime history table** — in `DependencyInjection.cs:76` replace:

```csharp
npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
```

with:

```csharp
// Must match ControlPlaneDesignTimeDbContextFactory — one history table
// for design-time and runtime (unified-tenancy Phase 0 reconciliation).
npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"));
```

- [ ] **Step 2: Wipe list** — in `Program.cs`'s `DROP TABLE IF EXISTS` raw SQL block:
  - add `tenant_databases,` (alphabetically near `tenants`)
  - add `"__ControlPlaneMigrationsHistory"` alongside the existing
    `"__TammaMigrationsHistory"` entry (keep the old name — servers deployed before this change
    still carry it).

- [ ] **Step 3: Build + commit**

```bash
dotnet build Tamma.sln
git add src/Tamma.Data/DependencyInjection.cs src/Tamma.Api/Program.cs
git commit -m "fix(tenancy-p0): unify CP migrations-history table name (runtime == design-time)"
```

---

### Task 7: Capture the OLD-chain schema dump (before any file deletion)

**Files:** none modified — produces `/tmp/cp-schema-old.sql` + `/tmp/cp-raw-sql-inventory.txt`

- [ ] **Step 1: Throwaway Postgres A**

```bash
sg docker -c "docker run -d --name pg-mig-a -e POSTGRES_USER=tamma -e POSTGRES_PASSWORD=tamma \
  -e POSTGRES_DB=tamma_control -p 5499:5432 --tmpfs /var/lib/postgresql/data pgvector/pgvector:pg16"
sg docker -c "docker exec pg-mig-a sh -c 'until pg_isready -U tamma; do sleep 1; done'"
```

- [ ] **Step 2: Apply the OLD 30-migration chain** (design-time factory reads
  `ConnectionStrings__ControlPlane`):

```bash
cd /home/meywd/tamma/apps/tamma-elsa
ConnectionStrings__ControlPlane="Host=localhost;Port=5499;Database=tamma_control;Username=tamma;Password=tamma" \
  dotnet ef database update -c ControlPlaneDbContext -p src/Tamma.Data -s src/Tamma.Data
```

Expected: applies all 30 migrations without error. (Note: the model now differs from the chain —
that's fine; `database update` replays the static migration files, not the model.)

- [ ] **Step 3: Dump schema A**

```bash
sg docker -c "docker exec pg-mig-a pg_dump -U tamma -d tamma_control --schema-only --no-owner" \
  > /tmp/cp-schema-old.sql
```

- [ ] **Step 4: Raw-SQL inventory** — extract every `migrationBuilder.Sql(` block from the 8 CP
  migrations that contain raw SQL (list verified 2026-06-09):

```
Migrations/20260417114431_EmailOutbox.cs
Migrations/20260417010625_TaskQueue.cs
Migrations/20260419015726_SchemaHardeningPhase1.cs
Migrations/20260419021119_Phase2RlsAndTriggers.cs
Migrations/20260420120000_Phase2RlsNullPolicyTightening.cs
Migrations/ControlPlane/20260426120000_KekRotations.cs        (handled in Task 4 Step 3)
Migrations/ControlPlane/20260426172707_AddUsersPlatformRole.cs (handled in Task 4 Step 2)
Migrations/ControlPlane/20260429160554_AddV2ProviderColumns.cs
```

Write each block to `/tmp/cp-raw-sql-inventory.txt` with a KEEP/DROP classification per these
rules:
- **KEEP** (port to new baseline in Task 8): RLS policies, `prevent_tenant_id_change` function +
  triggers, and partial/expression indexes **that target tables still present in the CP model**
  (`tenants`, `tenant_memberships`, `api_keys`, `users`, `refresh_tokens`,
  `password_reset_tokens`, `github_installations`, `github_installation_repos`, ...). RLS is
  ported verbatim even though Phase 5 will remove it — Phase 0 is behavior-neutral.
- **DROP**: anything referencing tables that left CP in `DropMovedEntitiesFromControlPlane`
  (`agent_configs`, `sanitization_rules`, `provider_health`, `provider_diagnostics`, ...); data
  backfills (`UPDATE ... SET` — no data exists); constraints/indexes already represented in the
  EF model after Tasks 1-5 (e.g. `users_platform_role_check`, the KekRotations index, any
  hardening index that 28-7 re-modeled — check for duplicates by name in `/tmp/cp-schema-old.sql`).

Leave container `pg-mig-a` running — Task 9 diffs against it.

---

### Task 8: Collapse the CP chain → regenerate `InitialControlPlane`

**Files:**
- Delete: all `*.cs` files directly in `apps/tamma-elsa/src/Tamma.Data/Migrations/` (17 migrations
  + Designers + `ControlPlaneDbContextModelSnapshot.cs`) — **NOT** the `ControlPlane/` and
  `Tenant/` subdirectories themselves
- Delete: all files in `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/`
- **Never touch** `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/`
- Create (generated): `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_InitialControlPlane.cs`
  + Designer + `ControlPlaneDbContextModelSnapshot.cs`

- [ ] **Step 1: Delete old chain**

```bash
cd /home/meywd/tamma/apps/tamma-elsa
git rm src/Tamma.Data/Migrations/*.cs
git rm src/Tamma.Data/Migrations/ControlPlane/*.cs
ls src/Tamma.Data/Migrations/        # expect: only ControlPlane/ and Tenant/ remain
ls src/Tamma.Data/Migrations/Tenant/ # expect: untouched (7 files incl. snapshot)
```

- [ ] **Step 2: Regenerate baseline**

```bash
dotnet ef migrations add InitialControlPlane -c ControlPlaneDbContext \
  -p src/Tamma.Data -s src/Tamma.Data -o Migrations/ControlPlane
```

Expected: three files appear under `Migrations/ControlPlane/`. The new snapshot must live there
too (it will, via `-o`).

- [ ] **Step 3: Port the KEEP raw SQL** — append ONE `migrationBuilder.Sql(...)` block at the END
  of the generated migration's `Up()` containing the KEEP entries from
  `/tmp/cp-raw-sql-inventory.txt` (Task 7 Step 4), with a comment:

```csharp
// ── Ported from the pre-collapse chain (unified-tenancy Phase 0) ──
// RLS policies + prevent_tenant_id_change triggers (Phase2RlsAndTriggers +
// NullPolicyTightening, filtered to tables still in CP) and the
// SchemaHardeningPhase1 partial/expression indexes not representable in
// the EF model. RLS removal is deliberately deferred to unified-tenancy
// Phase 5 — Phase 0 is behavior-neutral.
migrationBuilder.Sql("""
    <KEEP blocks verbatim here>
    """);
```

Mirror with the corresponding `DROP POLICY`/`DROP TRIGGER`/`DROP INDEX` statements in `Down()`
only if the generated `Down()` is non-trivial; if `Down()` just drops all tables, the ported
objects die with their tables and no extra `Down()` SQL is needed.

- [ ] **Step 4: Build**

Run: `dotnet build Tamma.sln`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A src/Tamma.Data/Migrations
git commit -m "feat(tenancy-p0)!: collapse CP migrations into InitialControlPlane baseline

30 migrations (17 root + 13 ControlPlane/) regenerated from the model.
Still-load-bearing raw SQL (RLS, tenant-id-change triggers, hardening
indexes on CP tables) ported into the baseline; objects for tables that
left CP, data backfills, and model-covered constraints dropped.
Server carries zero business data - recreate-from-model, volume-reset
at deploy (TAMMA_PRESERVE_DB unset)."
```

---

### Task 9: Validate the new baseline against throwaway Postgres B (+ CHECK probes + diff)

**Files:** none modified — produces `/tmp/cp-schema-new.sql`, `/tmp/cp-schema.diff`

- [ ] **Step 1: Throwaway Postgres B + apply new baseline**

```bash
sg docker -c "docker run -d --name pg-mig-b -e POSTGRES_USER=tamma -e POSTGRES_PASSWORD=tamma \
  -e POSTGRES_DB=tamma_control -p 5498:5432 --tmpfs /var/lib/postgresql/data pgvector/pgvector:pg16"
sg docker -c "docker exec pg-mig-b sh -c 'until pg_isready -U tamma; do sleep 1; done'"
cd /home/meywd/tamma/apps/tamma-elsa
ConnectionStrings__ControlPlane="Host=localhost;Port=5498;Database=tamma_control;Username=tamma;Password=tamma" \
  dotnet ef database update -c ControlPlaneDbContext -p src/Tamma.Data -s src/Tamma.Data
```

Expected: exactly 1 migration (`InitialControlPlane`) applies cleanly.

- [ ] **Step 2: Dump + diff**

```bash
sg docker -c "docker exec pg-mig-b pg_dump -U tamma -d tamma_control --schema-only --no-owner" \
  > /tmp/cp-schema-new.sql
diff /tmp/cp-schema-old.sql /tmp/cp-schema-new.sql > /tmp/cp-schema.diff; wc -l /tmp/cp-schema.diff
```

- [ ] **Step 3: Reconcile the diff against the intended-changes whitelist.** Every hunk must match
one of:
1. NEW `tenant_databases` table + its indexes/CHECKs/sequence.
2. tenants: new `SchemaName`/`DatabaseId` columns, their indexes, the FK, `ck_tenants_status`,
   `ck_tenants_connection_string_present`, `KekVersion` integer NULL → smallint NOT NULL DEFAULT 1.
3. plans: new `PlacementPolicy` column + `ck_plans_placement_policy`.
4. api_keys: new `ck_api_keys_scope`.
5. users: `users_platform_role_check` → `ck_users_platform_role` (rename only).
6. History table: `__TammaMigrationsHistory` absent, `__ControlPlaneMigrationsHistory` present
   (dump A may carry `__ControlPlaneMigrationsHistory` from the Task 7 design-time apply — accept).
7. Cosmetic EF-version noise (constraint/index name normalization, column ordering, sequence
   names) — judge case-by-case, but a DROPPED policy/trigger/index/constraint that is in dump A
   and absent in dump B and NOT in this whitelist is a **Task 8 Step 3 porting miss**: go back,
   add it to the KEEP block, regenerate the migration file content, re-run this task.

- [ ] **Step 4: CHECK probes (the "failing tests" for the schema)** — run each psql statement and
confirm pass/violation as labeled:

```bash
PSQL='sg docker -c "docker exec -i pg-mig-b psql -U tamma -d tamma_control -v ON_ERROR_STOP=1"'
# 1. VIOLATION expected (bad status):
echo "INSERT INTO tenants (\"Name\",\"Slug\",\"Status\") VALUES ('x','t-bad-status','bogus');" | eval $PSQL
# 2. VIOLATION expected (active without conn string):
echo "INSERT INTO tenants (\"Name\",\"Slug\",\"Status\") VALUES ('x','t-active-noconn','active');" | eval $PSQL
# 3. PASS expected (provisioning without conn string — transitional exemption):
echo "INSERT INTO tenants (\"Name\",\"Slug\",\"Status\") VALUES ('x','t-prov','provisioning');" | eval $PSQL
# 4. PASS expected (NULL status, no conn string):
echo "INSERT INTO tenants (\"Name\",\"Slug\") VALUES ('x','t-null');" | eval $PSQL
# 5. VIOLATION expected (bad api key scope):
echo "INSERT INTO api_keys (\"Scope\",\"OwnerId\",\"KeyHash\",\"KeyPrefix\",\"Label\") VALUES ('bogus','o','h1','p','l');" | eval $PSQL
# 6. VIOLATION expected (bad placement class):
echo "INSERT INTO tenant_databases (\"Label\",\"Host\",\"AdminConnectionStringEncrypted\",\"PlacementClass\") VALUES ('db1','h','\\x00','weird');" | eval $PSQL
# 7. PASS expected (defaults: shared/active/kek 1):
echo "INSERT INTO tenant_databases (\"Label\",\"Host\",\"AdminConnectionStringEncrypted\") VALUES ('db2','h','\\x00') RETURNING \"PlacementClass\",\"Status\",\"KekVersion\",\"Port\";" | eval $PSQL
```

(Column quoting matters — EF uses PascalCase quoted identifiers. If an INSERT fails for a
*different* reason than the labeled CHECK — e.g. RLS on tenants blocking the insert — read the
error; RLS-denied is acceptable for probes 1-4 ONLY if re-running them as table owner with
`SET row_security = off;` prepended reproduces the labeled outcome.)

- [ ] **Step 5: Cleanup containers**

```bash
sg docker -c "docker rm -f pg-mig-a pg-mig-b"
```

- [ ] **Step 6: Commit any reconciliation edits** (if Step 3 forced a Task-8 fix):

```bash
git add -A src/Tamma.Data/Migrations && git commit -m "fix(tenancy-p0): port missed raw SQL into InitialControlPlane baseline"
```

---

### Task 10: Full suite + docs + push + CI

**Files:**
- Modify: `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (mark Phase 0 done +
  record the two documented deviations)
- Modify: `wiki/Multi-Tenant-Provisioning.md` and/or `docs/architecture.md` ONLY if they assert
  things Phase 0 made false (e.g. "KekVersion integer NULL", missing tenant_databases). Grep:
  `grep -rn "KekVersion\|tenant_databases\|__TammaMigrationsHistory" docs/ wiki/ --include=*.md`
  and fix stale statements.

- [ ] **Step 1: Full build + full test suite**

```bash
cd /home/meywd/tamma/apps/tamma-elsa
dotnet build Tamma.sln
sg docker -c "dotnet test Tamma.sln -v minimal"
```

Expected: build 0 errors; all tests pass. Any failure → fix before proceeding (likely causes: a
missed KekVersion call site, a test asserting the old migration count, a test inserting a Status
value outside the CHECK against real Postgres).

- [ ] **Step 2: Docs updates** (parent plan: add a `**Phase 0: DONE <date>**` marker to the Phase
  decomposition entry + note deviations 1-2 from this plan's header in its Decisions section).

- [ ] **Step 3: Commit, push, watch CI**

```bash
git add docs/ wiki/
git commit -m "docs(tenancy-p0): mark Phase 0 complete + record transitional-CHECK deviations"
git push
cd /home/meywd/tamma && gh run list --branch feat/wave-b --limit 5
# watch the CI + docker runs for the pushed SHA to completion:
gh run watch <ci-run-id> --interval 30 --exit-status
```

Expected: CI green (the "Integration Tests (Postgres)" job boots the API, which applies the new
baseline via `Program.cs` `Migrate()` — this is the end-to-end gate).

---

## Self-review notes

- **Spec coverage** (parent §4 Phase 0): tenant_databases ✓ (T1), SchemaName/DatabaseId ✓ (T2),
  Status CHECK ✓ (T2), uniform conn-string CHECK ✓ (T2, transitional — deviation 2),
  api_keys Scope CHECK ✓ (T4, transitional — deviation 1), KekVersion smallint ✓ (T3),
  PlacementPolicy + seed ✓ (T5), history-table reconciliation ✓ (T6), delete + regenerate
  baseline ✓ (T7-T9), throwaway-Postgres validation ✓ (T7, T9).
- **No behavior change**: RLS/triggers ported verbatim (T8); CHECKs are satisfiable by every
  current write path (verified against grep'd Status/Scope writers and the delete-flow null-out).
- **Type consistency**: KekVersion is `short` in the model (T3), `short` on TenantDatabase (T1);
  all `EF.Property<short>` reads cast back to `int?`/`int` at DTO boundaries.
- **Known risk**: EF model ≠ old chain between T1-T6 and T8 — harmless (tests build schema from
  the model or run on the new baseline; old chain only replayed statically in T7).

---

## Execution record (2026-06-09)

All 10 tasks completed on feat/wave-b (commits 65ff281f..9ec93dd4 + this docs-closure commit).
Validation: old-chain vs collapsed-baseline schema diff fully reconciled (110 lines, all
whitelisted); 12/12 CHECK probes behaved as labeled on bare Postgres; full suite green. Notable
execution findings:
- Old chain's narrow `ck_api_keys_scope` ('user','installation','service') meant org-key creation
  (Scope='tenant' on CP) violated it — latent prod bug fixed by the transitional CHECK.
- `fk_api_keys_rotated_from` existed only as raw SQL (never modeled) — now ported; candidate for
  proper modeling later.
- uuid-ossp is NOT needed by the CP model (only mentorship configs reference uuid_generate_v4, and
  CP ignores them); the collapsed baseline applies on bare Postgres.
- Interim TenancyP0_* migrations were created during Tasks 2-5 (test suite replays the chain at API
  startup) and deleted by the Task-8 collapse, as planned.

# Phase 1 — Tenant Schema Naming + Per-Schema Migrations + Collapse Tenant Baseline

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the tenant data layer schema-per-tenant capable: `TenantNaming.SchemaName` (`t_<hex>`),
tenant migrations apply into the schema carried by the connection string's `Search Path` (with an
in-schema `__TenantMigrationsHistory`), and the 3-migration Tenant chain collapses into one
`InitialTenant` baseline — while every existing flow (no `Search Path` → `public`) behaves
identically.

**Architecture:** The schema is carried exclusively by the Npgsql `Search Path` connection-string
key — no new parameters thread through the call graph. A tiny helper parses it; every place that
configures `MigrationsHistoryTable("__TenantMigrationsHistory")` passes the parsed schema as the
history-table schema; `EfTenantDbMigrator` additionally runs `CREATE SCHEMA IF NOT EXISTS` first.
Unqualified DDL from EF migrations lands in the first `search_path` schema automatically, so the
same single baseline serves `public` (today) and `t_<hex>` (target) unchanged. Before any of that
can work, the model must shed its `public`-anchored dependencies: the two `uuid_generate_v4()`
mentorship defaults become the built-in `gen_random_uuid()` (kills the uuid-ossp extension
dependency — an extension function would NOT resolve under `Search Path=t_<hex>`), and the two raw
`NULLS NOT DISTINCT` unique indexes move into the model via EF 9's native support.

**Tech Stack:** .NET 9 / EF Core 9 / Npgsql 9 (`NpgsqlConnectionStringBuilder.SearchPath`,
`MigrationsHistoryTable(name, schema)`, `AreNullsDistinct(false)`), PostgreSQL
(`pgvector/pgvector:pg16` throwaways), dotnet-ef 9.0.9 (global).

**Parent doc:** `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (§3 rows
"Naming"/"Migrations into schema", §4 Phase 1).

---

## Environment facts (verified 2026-06-09 — do not re-derive)

- C# root: `/home/meywd/tamma/apps/tamma-elsa`, branch `feat/wave-b`. Build: `dotnet build Tamma.sln`.
  Tests/docker need `sg docker -c "..."`.
- Tenant chain: `src/Tamma.Data/Migrations/Tenant/` = 3 migrations + snapshot, owned by
  `TenantDbContext`. Raw SQL: ONLY two `CREATE UNIQUE INDEX ... NULLS NOT DISTINCT` blocks
  (`20260429152530` → `IX_prompt_overrides_UserId_TenantId_Scope_Role_Action` on
  `prompt_overrides("UserId","TenantId","Scope","Role","Action")`; `20260524143833` →
  `IX_conventions_TenantId_Role_Action` on `conventions("TenantId","Role","Action")`) + one
  `DROP INDEX` in a Down(). NO RLS, NO triggers, NO extension creation in the chain.
- uuid-ossp dependency: exactly two `HasDefaultValueSql("uuid_generate_v4()")` in
  `TammaModelConfiguration.ConfigureMentorshipEntities` (~lines 1203 and 1232; mentorship_sessions
  + mentorship_events Id columns). Everything else uses `gen_random_uuid()` (pg_catalog builtin,
  PG13+; no extension, resolves under any search_path).
- History-table config sites for tenant contexts (all currently 1-arg, no schema):
  `EfTenantDbMigrator.cs` (~line 46), `TenantDbContextFactory.cs` (both branches, ~lines 71-77),
  `ConventionStoreSeeder.cs` (~line 152), `ApiTestFixture.ApplyTenantMigrationsAsync` (~line 186).
  `TenantDesignTimeDbContextFactory.cs` stays as-is (design-time, public).
- `SearchPath` appears NOWHERE in the codebase today. `LruPooledTenantConnectionResolver.BuildDataSource`
  initializes `NpgsqlConnectionStringBuilder(connectionString)` — Npgsql preserves a `Search Path`
  key from the source string automatically, so runtime query scoping needs NO resolver change in
  Phase 1 (resolver work is Phase 2).
- `TenantNaming` (`src/Tamma.Data/Pooling/TenantNaming.cs`) has `HexOf/RoleName/DatabaseName/
  ElsaDatabaseName/Quote`; NO SchemaName. Its tests: `tests/Tamma.Activities.Tests/TenantLifecycle/TenantNamingTests.cs`.
- Real-Postgres tenant-migration test harness to mirror:
  `tests/Tamma.Api.Tests/Conventions/ConventionStoreMigrationTests.cs` and
  `tests/Tamma.Api.Tests/PromptStore/PromptOverridesPrincipalXorMigrationTests.cs` (both call
  `new EfTenantDbMigrator().MigrateTenantAppAsync(cs)` against a fixture Postgres).
- Test fixtures create `uuid-ossp` + `pgcrypto` extensions (`ApiTestFixture.EnableExtensionsAsync`
  ~line 173, called ~117-118; same pattern in `TenancySetUpFixture` ~92-101) — after this phase the
  collapsed baseline needs NEITHER for tenant tables; pgcrypto/uuid-ossp removal is Task 7 (verify
  nothing else uses them first).
- Postgres semantics that make this design work (don't re-litigate): unqualified `CREATE TABLE`/
  `CREATE INDEX` land in the FIRST `search_path` schema; function references in DDL `DEFAULT`
  expressions resolve via `search_path` at DDL time (hence the uuid_generate_v4 trap);
  `gen_random_uuid()` lives in `pg_catalog`, always resolvable.

## Phase 1 boundaries (YAGNI guard)

- NO per-tenant role creation/grants, NO placement service, NO creation-path changes (Phase 3).
- NO resolver behavior change (Phase 2). NO `tenants.SchemaName` population (Phase 3).
- `CreateTenantDatabaseActivity`/`MigrateTenantDatabaseActivity`/`DropTenantDatabaseActivity` keep
  their current db-per-tenant behavior — the conn strings they pass simply have no `Search Path`,
  which the new code treats as "public, exactly as today".

---

### Task 1: `TenantNaming.SchemaName` + `SchemaFromConnectionString` helper (TDD)

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/Pooling/TenantNaming.cs`
- Test: `apps/tamma-elsa/tests/Tamma.Activities.Tests/TenantLifecycle/TenantNamingTests.cs`

- [ ] **Step 1: Write the failing tests** (append to the existing `TenantNamingTests` class,
  matching its assertion style — read the file first):

```csharp
[Test]
public void SchemaName_Is_T_Prefixed_Hex()
{
    Assert.That(TenantNaming.SchemaName(SampleTenant),
        Is.EqualTo("t_" + TenantNaming.HexOf(SampleTenant)));
    Assert.That(TenantNaming.SchemaName(SampleTenant).Length, Is.EqualTo(34)); // < 63 limit
}

[Test]
public void SchemaFromConnectionString_ParsesFirstSearchPathSegment()
{
    var cs = "Host=h;Database=d;Username=u;Password=p;Search Path=t_abc123,public";
    Assert.That(TenantNaming.SchemaFromConnectionString(cs), Is.EqualTo("t_abc123"));
}

[Test]
public void SchemaFromConnectionString_NoSearchPath_ReturnsNull()
{
    Assert.That(
        TenantNaming.SchemaFromConnectionString("Host=h;Database=d;Username=u;Password=p"),
        Is.Null);
}

[Test]
public void SchemaFromConnectionString_RejectsUnsafeIdentifier()
{
    var cs = "Host=h;Database=d;Username=u;Password=p;Search Path=\"evil schema\"";
    Assert.Throws<ArgumentException>(() => TenantNaming.SchemaFromConnectionString(cs));
}
```

- [ ] **Step 2: Run to confirm RED**

Run: `cd /home/meywd/tamma/apps/tamma-elsa && sg docker -c "dotnet test tests/Tamma.Activities.Tests/Tamma.Activities.Tests.csproj --filter 'FullyQualifiedName~TenantNaming' -v minimal"`
Expected: compile error (`SchemaName` undefined) — that counts as red for an API-addition test.

- [ ] **Step 3: Implement** — add to `TenantNaming` (after `ElsaDatabaseName`; note the class doc
  comment says names have prefix `tamma_tenant_` — extend that doc sentence to mention the `t_`
  schema prefix too). Add `using Npgsql;` (Tamma.Data already references Npgsql):

```csharp
/// <summary>
/// Canonical per-tenant schema name — <c>t_&lt;hex&gt;</c> (unified-tenancy
/// plan 2026-06-09 §2.2). Short prefix keeps it visually distinct from the
/// role (<c>tamma_tenant_&lt;hex&gt;</c>) and comfortably under the 63-byte
/// identifier limit (34 chars).
/// </summary>
public static string SchemaName(Guid tenantId) => $"t_{HexOf(tenantId)}";

/// <summary>
/// Extracts the tenant schema from a connection string's
/// <c>Search Path</c> key: first comma-separated segment, or null when the
/// key is absent/empty (callers treat null as "default <c>public</c>
/// behavior, exactly as before Phase 1"). Rejects identifiers outside
/// <c>[a-z_][a-z0-9_]*</c> — schema names only ever come from
/// <see cref="SchemaName"/> or operator config, never user input, so
/// anything else indicates a corrupted/hostile connection string.
/// </summary>
public static string? SchemaFromConnectionString(string connectionString)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    var searchPath = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
    if (string.IsNullOrWhiteSpace(searchPath))
        return null;

    var first = searchPath.Split(',')[0].Trim();
    if (first.Length == 0)
        return null;
    if (!System.Text.RegularExpressions.Regex.IsMatch(first, "^[a-z_][a-z0-9_]*$"))
        throw new ArgumentException(
            $"Search Path schema '{first}' is not a safe identifier.",
            nameof(connectionString));
    return first;
}
```

- [ ] **Step 4: Run to confirm GREEN** (same command as Step 2). Expected: all TenantNaming tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Tamma.Data/Pooling/TenantNaming.cs tests/Tamma.Activities.Tests/TenantLifecycle/TenantNamingTests.cs
git commit -m "feat(tenancy-p1): TenantNaming.SchemaName (t_<hex>) + Search Path schema parser"
```

---

### Task 2: Shed public-schema dependencies from the tenant model

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (mentorship defaults ~1203,
  ~1232; prompt_overrides + conventions index declarations inside `ConfigureTenantEntities`)

- [ ] **Step 1: uuid_generate_v4 → gen_random_uuid.** In `ConfigureMentorshipEntities`, replace BOTH
  `HasDefaultValueSql("uuid_generate_v4()")` occurrences with
  `HasDefaultValueSql("gen_random_uuid()")` and add one comment at the first site:

```csharp
// gen_random_uuid (pg_catalog builtin, PG13+) instead of uuid-ossp's
// uuid_generate_v4: extension functions don't resolve under a
// per-tenant "Search Path=t_<hex>" and the extension dependency is
// pointless since PG13. Unified-tenancy Phase 1.
```

- [ ] **Step 2: Move the two raw NULLS NOT DISTINCT unique indexes into the model.** Read the
  `prompt_overrides` and `conventions` entity blocks in `ConfigureTenantEntities` first. EF 9 +
  Npgsql 9 express these natively. Ensure each block declares (preserving the EXACT database names
  the raw SQL used, since the collapsed baseline must reproduce them):

```csharp
// prompt_overrides block — replaces the raw-SQL index from migration
// 20260429152530 (NULLS NOT DISTINCT became model-expressible in EF 9):
entity.HasIndex("UserId", "TenantId", "Scope", "Role", "Action")
    .IsUnique()
    .AreNullsDistinct(false)
    .HasDatabaseName("IX_prompt_overrides_UserId_TenantId_Scope_Role_Action");
```

```csharp
// conventions block — replaces the raw-SQL index from migration 20260524143833:
entity.HasIndex("TenantId", "Role", "Action")
    .IsUnique()
    .AreNullsDistinct(false)
    .HasDatabaseName("IX_conventions_TenantId_Role_Action");
```

IMPORTANT reconciliation: the raw migrations may have DROPPED an older model-declared index on
prompt_overrides (legacy `(UserId, Scope, Role, Action)` unique). Check what index declarations the
blocks currently carry; remove any declaration for the dropped legacy index so the model matches
the END state of the chain. Use property-name strings exactly as the entity defines them (check
whether these are shadow or CLR properties — match the existing `HasIndex` style in those blocks).
If `AreNullsDistinct` doesn't compile, the using for `Microsoft.EntityFrameworkCore` /
`Npgsql.EntityFrameworkCore.PostgreSQL` extensions is missing in that file — check existing usings;
it is an extension method on `IndexBuilder` from the Npgsql EF provider.

- [ ] **Step 3: Build** — `dotnet build Tamma.sln` → 0 errors. (Model is now ahead of the tenant
  chain; that's expected — the chain is collapsed two tasks from now, and tenant-migration tests
  replay the OLD chain which still works because the fixtures still create uuid-ossp.)

- [ ] **Step 4: Run tenant-side regression tests:**

```bash
sg docker -c "dotnet test tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj --filter 'FullyQualifiedName~ConventionStore|FullyQualifiedName~PromptOverrides' -v minimal"
```
Expected: all pass (these run against the OLD chain; the model edits don't affect applied DDL).

- [ ] **Step 5: Commit**

```bash
git add src/Tamma.Data/TammaModelConfiguration.cs
git commit -m "feat(tenancy-p1): drop uuid-ossp dependency + model-level NULLS NOT DISTINCT indexes"
```

---

### Task 3: Capture OLD tenant-chain schema dump + raw-SQL inventory

**Files:** none — produces `/tmp/tenant-schema-old.sql`, `/tmp/tenant-raw-sql-inventory.md`;
leaves container `pg-ten-a` RUNNING for Task 5.

- [ ] **Step 1: Throwaway Postgres A** (old chain needs uuid-ossp as env prep — prod equivalent is
  docker/init-db.sql):

```bash
sg docker -c "docker run -d --name pg-ten-a -e POSTGRES_USER=tamma -e POSTGRES_PASSWORD=tamma -e POSTGRES_DB=tamma_tenant -p 5499:5432 --tmpfs /var/lib/postgresql/data pgvector/pgvector:pg16"
sg docker -c "docker exec pg-ten-a sh -c 'until pg_isready -U tamma; do sleep 1; done'"
echo 'CREATE EXTENSION IF NOT EXISTS "uuid-ossp";' | sg docker -c "docker exec -i pg-ten-a psql -U tamma -d tamma_tenant"
```

- [ ] **Step 2: Apply the OLD 3-migration chain** (TenantDesignTimeDbContextFactory reads
  `ConnectionStrings__TenantDesignTime`):

```bash
cd /home/meywd/tamma/apps/tamma-elsa
ConnectionStrings__TenantDesignTime="Host=localhost;Port=5499;Database=tamma_tenant;Username=tamma;Password=tamma" \
  dotnet ef database update -c TenantDbContext -p src/Tamma.Data -s src/Tamma.Data
```
Expected: 3 migrations apply cleanly.

- [ ] **Step 3: Dump + inventory**

```bash
sg docker -c "docker exec pg-ten-a pg_dump -U tamma -d tamma_tenant --schema-only --no-owner" > /tmp/tenant-schema-old.sql
wc -l /tmp/tenant-schema-old.sql
```

Write `/tmp/tenant-raw-sql-inventory.md`: extract every `migrationBuilder.Sql(` block from the 3
migrations (expected: exactly the 2 unique-index creations + 1 Down() drop — verify with grep, flag
anything unexpected). Classification: both indexes = **DROP from porting** (now model-level after
Task 2 — they regenerate from the model with identical names); confirm each index exists in the
dump AND will be model-generated (cross-check names against Task 2's `HasDatabaseName` values).
Expected KEEP count: **0** (no raw SQL ported into the new baseline). If you find raw SQL beyond
the 3 known blocks, STOP and report BLOCKED with the block text.

---

### Task 4: Collapse the tenant chain → regenerate `InitialTenant`

**Files:**
- Delete: all `*.cs` in `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/`
- Create (generated): `Migrations/Tenant/<ts>_InitialTenant.cs` + Designer + new
  `TenantDbContextModelSnapshot.cs`
- **Never touch** `Migrations/ControlPlane/`

- [ ] **Step 1: Delete + regenerate**

```bash
cd /home/meywd/tamma/apps/tamma-elsa
git rm src/Tamma.Data/Migrations/Tenant/*.cs
dotnet ef migrations add InitialTenant -c TenantDbContext \
  -p src/Tamma.Data -s src/Tamma.Data -o Migrations/Tenant
ls src/Tamma.Data/Migrations/Tenant/   # expect exactly 3 files
```

- [ ] **Step 2: Inspect the generated migration.** Verify: NO `uuid_generate_v4` anywhere
  (`grep uuid_generate_v4 src/Tamma.Data/Migrations/Tenant/*.cs` → empty); both NULLS NOT DISTINCT
  indexes present as `CreateIndex` with the exact legacy names; the `ck_prompt_overrides_principal_xor`
  and `ck_api_keys_tenant_scope` CHECKs present; no schema-qualified (`schema:`) arguments.

- [ ] **Step 3: Build + sanity apply on a BARE Postgres (no extensions — proves uuid-ossp is gone):**

```bash
dotnet build Tamma.sln
sg docker -c "docker run -d --name pg-ten-sanity -e POSTGRES_USER=tamma -e POSTGRES_PASSWORD=tamma -e POSTGRES_DB=tamma_tenant -p 5497:5432 --tmpfs /var/lib/postgresql/data pgvector/pgvector:pg16"
sg docker -c "docker exec pg-ten-sanity sh -c 'until pg_isready -U tamma; do sleep 1; done'"
ConnectionStrings__TenantDesignTime="Host=localhost;Port=5497;Database=tamma_tenant;Username=tamma;Password=tamma" \
  dotnet ef database update -c TenantDbContext -p src/Tamma.Data -s src/Tamma.Data
sg docker -c "docker rm -f pg-ten-sanity"
```
Expected: 1 migration applies cleanly with NO extension pre-created.

- [ ] **Step 4: Commit**

```bash
git add -A src/Tamma.Data/Migrations
git commit -m "feat(tenancy-p1)!: collapse Tenant migrations into InitialTenant baseline

3 migrations regenerated from the model. No raw SQL ported: the two
NULLS NOT DISTINCT unique indexes are now model-level (EF 9), and the
uuid-ossp dependency is gone (mentorship defaults use gen_random_uuid).
Baseline applies on bare Postgres with no extensions."
```

---

### Task 5: Validate the tenant baseline (diff + probes)

**Files:** none — produces `/tmp/tenant-schema-new.sql`, `/tmp/tenant-schema.diff`,
`/tmp/tenant-diff-reconciliation.md`.

- [ ] **Step 1: Fresh Postgres B + apply new baseline (bare — no extensions):**

```bash
sg docker -c "docker run -d --name pg-ten-b -e POSTGRES_USER=tamma -e POSTGRES_PASSWORD=tamma -e POSTGRES_DB=tamma_tenant -p 5498:5432 --tmpfs /var/lib/postgresql/data pgvector/pgvector:pg16"
sg docker -c "docker exec pg-ten-b sh -c 'until pg_isready -U tamma; do sleep 1; done'"
cd /home/meywd/tamma/apps/tamma-elsa
ConnectionStrings__TenantDesignTime="Host=localhost;Port=5498;Database=tamma_tenant;Username=tamma;Password=tamma" \
  dotnet ef database update -c TenantDbContext -p src/Tamma.Data -s src/Tamma.Data
sg docker -c "docker exec pg-ten-b pg_dump -U tamma -d tamma_tenant --schema-only --no-owner" > /tmp/tenant-schema-new.sql
diff /tmp/tenant-schema-old.sql /tmp/tenant-schema-new.sql > /tmp/tenant-schema.diff; wc -l /tmp/tenant-schema.diff
```

- [ ] **Step 2: Reconcile every hunk.** Whitelist: (1) uuid-ossp extension in A only;
  (2) `uuid_generate_v4()` defaults in A → `gen_random_uuid()` in B (mentorship_sessions +
  mentorship_events Id); (3) column-order/whitespace/pg_dump-token noise. ANYTHING else (a missing
  index — check both NULLS NOT DISTINCT indexes exist in B with identical definitions — a missing
  CHECK, a changed type) = porting/model miss → BLOCKED with specifics. Record in
  `/tmp/tenant-diff-reconciliation.md`.

- [ ] **Step 3: Probes on pg-ten-b** (adapt for unrelated NOT NULL columns by reading the error,
  fixing the probe, not the schema):

```bash
P() { sg docker -c "docker exec -i pg-ten-b psql -U tamma -d tamma_tenant -v ON_ERROR_STOP=1"; }
# 1 PASS: mentorship_sessions default id works without any extension:
echo "INSERT INTO mentorship_sessions (<minimal required cols>) VALUES (...) RETURNING id;" | P
# 2 NULLS NOT DISTINCT on conventions: two identical (NULL TenantId, Role, Action) rows —
#   second must VIOLATE IX_conventions_TenantId_Role_Action:
echo "INSERT INTO conventions (...) VALUES (... NULL TenantId ...);" | P
echo "INSERT INTO conventions (...) VALUES (... same NULL TenantId/Role/Action ...);" | P   # VIOLATION expected
# 3 prompt_overrides principal XOR CHECK: row with BOTH UserId and TenantId set → VIOLATION:
echo "INSERT INTO prompt_overrides (...) VALUES (... both set ...);" | P                    # VIOLATION expected
# 4 api_keys tenant-scope CHECK: Scope='user' → VIOLATION (ck_api_keys_tenant_scope):
echo "INSERT INTO api_keys (\"Scope\",...) VALUES ('user',...);" | P                        # VIOLATION expected
```
(The `<minimal required cols>` are determined by reading the table definitions in
`/tmp/tenant-schema-new.sql` — fill the smallest valid column lists; do not skip a probe because a
column list is fiddly.)

- [ ] **Step 4: Cleanup** — `sg docker -c "docker rm -f pg-ten-a pg-ten-b"` (only when reconciled;
  leave running + report BLOCKED otherwise). Nothing to commit.

---

### Task 6: Schema-aware migration plumbing + the two-tenants-one-DB proof test

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Data/Pooling/EfTenantDbMigrator.cs` (~line 40-55)
- Modify: `apps/tamma-elsa/src/Tamma.Data/TenantDbContextFactory.cs` (both branches ~lines 66-78)
- Modify: `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionStoreSeeder.cs` (~line 150)
- Modify: `apps/tamma-elsa/tests/Tamma.Api.Tests/Infrastructure/ApiTestFixture.cs`
  (`ApplyTenantMigrationsAsync`, ~line 184)
- Test (new): `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/SchemaPerTenantMigrationTests.cs`

- [ ] **Step 1: Write the failing proof test FIRST.** Mirror the container/fixture harness of
  `tests/Tamma.Api.Tests/Conventions/ConventionStoreMigrationTests.cs` (read it; reuse its Postgres
  acquisition pattern exactly — same base class/fixture, same cleanup). Test content:

```csharp
[Test]
public async Task MigrateTenantApp_AppliesIntoSearchPathSchema_TwoTenantsCoexistInOneDb()
{
    var tenantA = Guid.NewGuid();
    var tenantB = Guid.NewGuid();
    var schemaA = TenantNaming.SchemaName(tenantA);
    var schemaB = TenantNaming.SchemaName(tenantB);

    string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(BaseConnectionString) { SearchPath = schema }
            .ConnectionString;

    var migrator = new EfTenantDbMigrator();
    await migrator.MigrateTenantAppAsync(CsFor(schemaA));
    await migrator.MigrateTenantAppAsync(CsFor(schemaB));

    // Both schemas carry their own tables AND their own history table.
    await using var conn = new NpgsqlConnection(BaseConnectionString);
    await conn.OpenAsync();
    foreach (var schema in new[] { schemaA, schemaB })
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT count(*) FROM information_schema.tables
                WHERE table_schema = @s AND table_name = 'conventions'),
              (SELECT count(*) FROM information_schema.tables
                WHERE table_schema = @s AND table_name = '__TenantMigrationsHistory')
            """;
        cmd.Parameters.AddWithValue("s", schema);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.That(r.GetInt64(0), Is.EqualTo(1), $"conventions missing in {schema}");
        Assert.That(r.GetInt64(1), Is.EqualTo(1), $"history table missing in {schema}");
    }

    // Data isolation: a row written via schema A's context is invisible via schema B's.
    var optsA = new DbContextOptionsBuilder<TenantDbContext>()
        .UseNpgsql(CsFor(schemaA)).Options;
    var optsB = new DbContextOptionsBuilder<TenantDbContext>()
        .UseNpgsql(CsFor(schemaB)).Options;
    await using (var ctxA = new TenantDbContext(optsA, tenantA))
    {
        ctxA.AgentConfigs.Add(/* minimal valid AgentConfig — read the entity for required props */);
        await ctxA.SaveChangesAsync();
    }
    await using (var ctxB = new TenantDbContext(optsB, tenantB))
    {
        Assert.That(await ctxB.AgentConfigs.AnyAsync(), Is.False);
    }
}
```
(`BaseConnectionString` = whatever the mirrored harness exposes for its tenant Postgres; the
re-application of `MigrateTenantAppAsync` must be idempotent per schema. Adapt names/attributes to
the harness conventions — content stays.)

- [ ] **Step 2: RED** — run it:

```bash
sg docker -c "dotnet test tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj --filter 'FullyQualifiedName~SchemaPerTenantMigration' -v minimal"
```
Expected failure mode: tables land in `public`, not `t_<hex>` (history-table lookup or the
information_schema assertions fail).

- [ ] **Step 3: Implement.** In `EfTenantDbMigrator.MigrateTenantAppAsync`, replace the options
  build + migrate with:

```csharp
// Unified-tenancy Phase 1: the connection string's Search Path names the
// tenant's schema. Unqualified DDL in the baseline lands in the first
// search_path schema; the history table is pinned to the same schema so
// each tenant tracks its own applied set. No Search Path → public,
// exactly the pre-Phase-1 behavior.
var schema = TenantNaming.SchemaFromConnectionString(tenantConnectionString);

var options = new DbContextOptionsBuilder<TenantDbContext>()
    .UseNpgsql(tenantConnectionString, npgsql =>
        npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema))
    .Options;

await using var ctx = new TenantDbContext(options);
if (schema is not null)
{
    // Safety net until Phase 3's CreateTenantSchemaActivity owns schema
    // creation with role grants. Schema name is validated by
    // SchemaFromConnectionString ([a-z_][a-z0-9_]*), Quote defends in depth.
    await ctx.Database.ExecuteSqlRawAsync(
        $"CREATE SCHEMA IF NOT EXISTS {TenantNaming.Quote(schema)};", ct)
        .ConfigureAwait(false);
}
// EF's MigrateAsync is idempotent — only pending migrations
// execute, the rest are no-ops by reading __TenantMigrationsHistory.
await ctx.Database.MigrateAsync(ct).ConfigureAwait(false);
```

In `TenantDbContextFactory.CreateAsync`: resolver branch — the data source's connection string is
available as `dataSource.ConnectionString`; shared branch — `_connectionString`. Both become:

```csharp
if (_resolver is not null)
{
    var dataSource = await _resolver
        .GetDataSourceAsync(tenantId, cancellationToken)
        .ConfigureAwait(false);
    var schema = TenantNaming.SchemaFromConnectionString(dataSource.ConnectionString);
    builder.UseNpgsql(dataSource, npgsql =>
        npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema));
}
else
{
    var schema = TenantNaming.SchemaFromConnectionString(_connectionString!);
    builder.UseNpgsql(_connectionString!, npgsql =>
        npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema));
}
```

In `ConventionStoreSeeder.SeedAsync` (data-source overload) and
`ApiTestFixture.ApplyTenantMigrationsAsync`: same one-line pattern —
`npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", TenantNaming.SchemaFromConnectionString(<the cs/dataSource.ConnectionString>))`.
Add `using Tamma.Data.Pooling;` where missing.

- [ ] **Step 4: GREEN** — rerun Step 2's command; the proof test passes. Then the broader
  regression set:

```bash
sg docker -c "dotnet test tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj --filter 'FullyQualifiedName~ConventionStore|FullyQualifiedName~PromptOverrides|FullyQualifiedName~Tenancy' -v minimal"
```
Expected: all pass (no Search Path in any existing fixture conn string → behavior unchanged).

- [ ] **Step 5: Commit**

```bash
git add -A src/ tests/
git commit -m "feat(tenancy-p1): tenant migrations apply into Search Path schema with in-schema history"
```

---

### Task 7: Fixture/extension cleanup + docs + full suite

**Files:**
- Modify: `apps/tamma-elsa/tests/Tamma.Api.Tests/Infrastructure/ApiTestFixture.cs` (~173-182) and
  `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/TenancySetUpFixture.cs` (~92-101) — extension
  bootstrap
- Modify: `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (Phase 1 → DONE)
- Modify: `wiki/Architecture.md` (stale Wave-A.5 `TammaDbContext` claims in §3.3/§13 — flagged in
  the Phase 0 final review; plus any tenant-migration statements Phase 1 made false)

- [ ] **Step 1: Extension cleanup.** `grep -rn "uuid-ossp\|uuid_generate_v4\|pgcrypto\|digest(" --include=*.cs src/ tests/`.
  After the collapse NOTHING in src should need uuid-ossp. For each fixture extension bootstrap:
  remove `uuid-ossp` creation; remove `pgcrypto` ONLY if the grep shows no remaining consumer
  (note: `gen_random_uuid()` is pg_catalog since PG13 — pgvector/pg16 images need NO extension for
  it; if in doubt about pgcrypto, keep it and say why in the report). Update the fixture comments.

- [ ] **Step 2: Full suite:**

```bash
cd /home/meywd/tamma/apps/tamma-elsa
dotnet build Tamma.sln
sg docker -c "dotnet test Tamma.sln -v minimal"
```
Expected: 0 errors, 0 failures (baseline: ~4400 tests). Investigate and root-cause any failure.

- [ ] **Step 3: Docs.**
  - Parent plan: Phase 1 entry → `- **Phase 1 — DONE 2026-06-09.** ...`; add to the deviations
    list: `9. **uuid-ossp dependency eliminated** (mentorship defaults → gen_random_uuid) — an
    extension function would not resolve under a per-tenant search_path; baseline now applies on
    bare Postgres. 10. **Tenant baseline carries zero raw SQL** — the NULLS NOT DISTINCT indexes
    are model-level in EF 9.`
  - `wiki/Architecture.md`: fix the stale §3.3/§13 `TammaDbContext` "still used" claims (verify
    current reality with `grep -rn "class TammaDbContext" src/` first — describe what IS true);
    update any "3 tenant migrations"/uuid-ossp statements to the collapsed `InitialTenant` +
    schema-per-tenant capability.
  - Append an execution record to THIS plan doc (same shape as the Phase 0 one: commit range,
    validation evidence, notable findings).

- [ ] **Step 4: Commit**

```bash
cd /home/meywd/tamma && git add -A apps/ docs/ wiki/
git commit -m "docs(tenancy-p1): mark Phase 1 complete; drop dead extension bootstrap from fixtures"
```

---

## Self-review notes

- **Spec coverage** (parent §4 Phase 1): `TenantNaming.SchemaName` ✓ (T1); migrator/factory apply
  into `t_<hex>` via Search Path + in-schema history ✓ (T6); collapse tenant baseline ✓ (T3-T5).
  Extra but necessary: uuid-ossp elimination (T2) — prerequisite for any non-public search_path.
- **Behavior neutrality**: all schema plumbing keys off `Search Path`, which NO existing connection
  string carries; null schema reproduces today's exact calls (1-arg history overload ≡ 2-arg with
  null). The only semantic change is mentorship Id defaults (gen_random_uuid ≡ v4 UUIDs, zero data).
- **Type consistency**: `SchemaFromConnectionString` returns `string?` consumed by
  `MigrationsHistoryTable(string, string?)` — matches Npgsql's signature. `SchemaName` used by T6's
  test and later phases.
- **Known risk**: `dataSource.ConnectionString` on NpgsqlDataSource — verify the property exposes
  the original string (it does; password may be stripped, but Search Path survives — the helper
  only reads Search Path). If the stripped form ever loses Search Path, the proof test catches it.

---

## Execution record (2026-06-09)

All 7 tasks completed on feat/wave-b (commits 3f9e6b75..831811db + this docs-closure commit;
full suite 4409 passed / 0 failed across 10 projects). Validation: old-chain vs
collapsed-baseline diff = 31 lines, fully whitelisted (uuid-ossp env-prep + gen_random_uuid defaults
+ pg_dump noise); behavior probes all as labeled (NND indexes, principal-xor CHECK, tenant-scope
CHECK, extension-free mentorship defaults); two-tenants-one-DB proof test green (schema-pinned
history tables, cross-schema data isolation — confirmed no EF query-filter confound). Notable:
- RED failure mode for the proof test was Postgres 3F000 "no schema has been selected to create in"
  — the migrator's CREATE SCHEMA safety net is what makes Search Path-only conn strings viable
  until Phase 3's create-activity owns schema creation.
- Six test fixtures + the design-time factory still use the unpinned 1-arg MigrationsHistoryTable
  (no Search Path in their conn strings → identical behavior). Phase 2 should consolidate into a
  single UseTenantNpgsql(...) helper so future Search-Path callers can't silently split history.

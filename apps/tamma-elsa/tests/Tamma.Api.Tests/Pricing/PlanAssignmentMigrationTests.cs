using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-4 (AC1, AC2) — applies the ControlPlane migration bundle to a clean
/// Postgres testcontainer and asserts the <c>tenant_plan_assignments</c> schema
/// landed: the table, the three CHECK constraints, the partial unique
/// "one active per tenant" index (and that it rejects a second active insert),
/// and the supporting indexes. Raw Npgsql introspection — mirrors the existing
/// migration suites.
/// </summary>
[TestFixture]
public class PlanAssignmentMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tpa_migration_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<HashSet<string>> QueryStringsAsync(NpgsqlConnection conn, string sql)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        }
        return result;
    }

    private async Task<string?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : v.ToString();
    }

    [Test]
    public async Task Migration_Creates_The_Assignments_Table()
    {
        await using var conn = await OpenAsync();
        var tables = await QueryStringsAsync(conn,
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public';");

        tables.Should().Contain("tenant_plan_assignments");
    }

    [Test]
    public async Task Migration_Creates_The_Check_Constraints()
    {
        await using var conn = await OpenAsync();
        var checks = await QueryStringsAsync(conn,
            "SELECT conname FROM pg_constraint WHERE contype='c';");

        checks.Should().Contain("ck_tpa_status");
        checks.Should().Contain("ck_tpa_effective_window");
        checks.Should().Contain("ck_tpa_version_positive");
    }

    [Test]
    public async Task Migration_Creates_The_Partial_OneActivePerTenant_Index()
    {
        await using var conn = await OpenAsync();
        var def = await ScalarAsync(conn,
            "SELECT indexdef FROM pg_indexes WHERE indexname='ux_tpa_one_active_per_tenant';");

        def.Should().NotBeNull();
        def!.Should().Contain("UNIQUE");
        def.Should().Contain("active", "the index is filtered WHERE Status = 'active'");
    }

    [Test]
    public async Task PartialUniqueIndex_Rejects_A_Second_Active_Row_For_The_Same_Tenant()
    {
        var tenantId = await SeedTenantWithPlansAsync();

        await using var conn = await OpenAsync();

        // First active insert — succeeds.
        await ExecAsync(conn, tenantId, PlansSeeder.TeamPlanId, "active");

        // Second active insert for the same tenant — must violate the partial
        // unique index (23505).
        var act = () => ExecAsync(conn, tenantId, PlansSeeder.FreePlanId, "active");
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23505");
    }

    [Test]
    public async Task CheckConstraints_Reject_Bad_Status_And_Window_And_Version()
    {
        var tenantId = await SeedTenantWithPlansAsync();
        await using var conn = await OpenAsync();

        // Bad status.
        var badStatus = () => ExecAsync(conn, tenantId, PlansSeeder.TeamPlanId, "bogus");
        (await badStatus.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");

        // EffectiveTo < EffectiveFrom.
        var badWindow = () => ExecAsync(
            conn, tenantId, PlansSeeder.TeamPlanId, "cancelled",
            effectiveFrom: DateTime.UtcNow, effectiveTo: DateTime.UtcNow.AddDays(-1));
        (await badWindow.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");

        // Version < 1.
        var badVersion = () => ExecAsync(
            conn, tenantId, PlansSeeder.TeamPlanId, "cancelled", planVersion: 0);
        (await badVersion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");
    }

    private static async Task ExecAsync(
        NpgsqlConnection conn, Guid tenantId, Guid planId, string status,
        int planVersion = 1, DateTime? effectiveFrom = null, DateTime? effectiveTo = null)
    {
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO tenant_plan_assignments
                (""Id"",""TenantId"",""PlanId"",""PlanVersion"",""Status"",
                 ""EffectiveFrom"",""EffectiveTo"",""CreatedAt"",""UpdatedAt"")
              VALUES (gen_random_uuid(), @t, @p, @v, @s, @from, @to, now(), now());", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", planId);
        cmd.Parameters.AddWithValue("v", planVersion);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("from", effectiveFrom ?? DateTime.UtcNow);
        cmd.Parameters.AddWithValue("to", (object?)effectiveTo ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedTenantWithPlansAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE tenant_plan_assignments, plan_prices, plan_entitlements, plan_features, plans, tenants CASCADE;");
        await PlansSeeder.SeedAsync(ctx);

        var tenantId = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Acme",
            Slug = "acme-" + tenantId.ToString("N")[..6],
            Type = "team",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Tenants.Add(tenant);
        ctx.Entry(tenant).Property("Status").CurrentValue = "active";
        ctx.Entry(tenant).Property("PlanId").CurrentValue = PlansSeeder.FreePlanId;
        ctx.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3, 4 };
        await ctx.SaveChangesAsync();
        return tenantId;
    }
}

/// <summary>
/// Story 34-4 (AC3) — the migration back-fill produces exactly one <c>active</c>
/// assignment per existing tenant, pinning the plan's current <c>Version</c>. A
/// dedicated container migrates to the PREVIOUS migration, seeds plans + tenants,
/// then applies <c>AddTenantPlanAssignment</c> so the raw-SQL back-fill runs.
/// </summary>
[TestFixture]
public class PlanAssignmentBackfillMigrationTests
{
    private const string PreviousMigration = "20260702193642_AddBillingSubscription";

    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tpa_backfill_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        // 1. Migrate to the migration BEFORE AddTenantPlanAssignment.
        await using (var ctx = NewContext())
        {
            var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        // 2. Seed plans + three tenants: one team via PlanId, one free via slug,
        //    and one ORPHAN whose PlanId is NULL and whose (canonical) legacy slug
        //    resolves to no ACTIVE plan (Finding 3). The orphan's slug is
        //    'enterprise' (canonical → passes ck_tenants_plan) but the active
        //    enterprise plan is deprecated below, so the slug lookup misses — the
        //    tenant must STILL back-fill to the terminal `free` fallback, never
        //    silently vanish.
        await using (var ctx = NewContext())
        {
            await PlansSeeder.SeedAsync(ctx);

            // Deprecate the active 'enterprise' version so slug 'enterprise' has no
            // active row (bypasses EF plan-immutability enforcement via raw SQL).
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE plans SET \"Status\" = 'deprecated' WHERE \"Slug\" = 'enterprise';");

            AddTenant(ctx, TeamTenantId, "team-tenant", planId: PlansSeeder.TeamPlanId, planSlug: "team");
            AddTenant(ctx, FreeTenantId, "free-tenant", planId: null, planSlug: "free");
            AddTenant(ctx, OrphanSlugTenantId, "orphan-tenant", planId: null, planSlug: "enterprise");
            await ctx.SaveChangesAsync();
        }

        // 3. Apply AddTenantPlanAssignment — its back-fill runs.
        await using (var ctx = NewContext())
        {
            await ctx.Database.MigrateAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private static readonly Guid TeamTenantId = Guid.NewGuid();
    private static readonly Guid FreeTenantId = Guid.NewGuid();
    private static readonly Guid OrphanSlugTenantId = Guid.NewGuid();

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private static void AddTenant(
        ControlPlaneDbContext ctx, Guid id, string name, Guid? planId, string planSlug)
    {
        var tenant = new Tenant
        {
            Id = id,
            Name = name,
            Slug = name + "-" + id.ToString("N")[..6],
            Type = "team",
            Plan = planSlug,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Tenants.Add(tenant);
        ctx.Entry(tenant).Property("Status").CurrentValue = "active";
        ctx.Entry(tenant).Property("PlanId").CurrentValue = planId;
        ctx.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3, 4 };
    }

    [Test]
    public async Task Backfill_Creates_Exactly_One_Active_Assignment_Per_Tenant()
    {
        await using var ctx = NewContext();

        var all = await ctx.TenantPlanAssignments.AsNoTracking().ToListAsync();
        all.Should().HaveCount(3, "one active assignment per existing (non-deleted) tenant");
        all.Should().OnlyContain(a => a.Status == "active");
    }

    [Test]
    public async Task Backfill_OrphanSlug_Tenant_Falls_Back_To_Free()
    {
        // Finding 3 — a non-deleted tenant with a NULL shadow PlanId AND a legacy
        // slug that resolves to no active plan must STILL receive a `free` active
        // assignment (the guaranteed terminal fallback), never be silently skipped.
        await using var ctx = NewContext();

        var orphan = await ctx.TenantPlanAssignments.AsNoTracking()
            .SingleAsync(a => a.TenantId == OrphanSlugTenantId);
        orphan.PlanId.Should().Be(PlansSeeder.FreePlanId,
            "an unresolvable legacy slug + NULL PlanId back-fills to the active 'free' plan");
        orphan.Status.Should().Be("active");
    }

    [Test]
    public async Task Backfill_Pins_The_PlanId_From_Shadow_Column()
    {
        await using var ctx = NewContext();

        var team = await ctx.TenantPlanAssignments.AsNoTracking()
            .SingleAsync(a => a.TenantId == TeamTenantId);
        team.PlanId.Should().Be(PlansSeeder.TeamPlanId);
        team.PlanVersion.Should().Be(1, "the current active team version is pinned");
    }

    [Test]
    public async Task Backfill_Falls_Back_To_Free_Slug_When_Shadow_PlanId_Null()
    {
        await using var ctx = NewContext();

        var free = await ctx.TenantPlanAssignments.AsNoTracking()
            .SingleAsync(a => a.TenantId == FreeTenantId);
        free.PlanId.Should().Be(PlansSeeder.FreePlanId,
            "a NULL shadow PlanId back-fills via the Plan slug ('free')");
    }
}

/// <summary>
/// Story 34-4 (Finding 3) — when the active <c>free</c> plan is ABSENT and a
/// non-deleted tenant would resolve to no plan (NULL <c>PlanId</c> + a legacy slug
/// with no active version), the back-fill must FAIL LOUD (RAISE) rather than
/// silently skip the tenant and leave it assignment-less (→ Story 34-6
/// <c>NO_ASSIGNMENT</c>/404 post-deploy). fail-before: the prior
/// <c>AND COALESCE(...) IS NOT NULL</c> filter let the migration succeed while
/// omitting the tenant; pass-after: the migration throws.
/// </summary>
[TestFixture]
public class PlanAssignmentBackfillFreeAbsentMigrationTests
{
    private const string PreviousMigration = "20260702193642_AddBillingSubscription";

    private static readonly Guid OrphanTenantId = Guid.NewGuid();

    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tpa_backfill_free_absent_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        // 1. Migrate to the migration BEFORE AddTenantPlanAssignment.
        await using (var ctx = NewContext())
        {
            var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        // 2. Seed plans, then deprecate EVERY plan version (raw SQL, bypassing EF's
        //    plan-immutability guard) so there is no active `free` terminal
        //    fallback (nor any other active plan). Add an orphan tenant (NULL
        //    PlanId + a canonical slug 'free' that no longer resolves to an active
        //    plan) that therefore resolves to no plan at all.
        await using (var ctx = NewContext())
        {
            await PlansSeeder.SeedAsync(ctx);
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE plans SET \"Status\" = 'deprecated';");

            var tenant = new Tenant
            {
                Id = OrphanTenantId,
                Name = "orphan-tenant",
                Slug = "orphan-" + OrphanTenantId.ToString("N")[..6],
                Type = "team",
                Plan = "free", // canonical (passes ck_tenants_plan); no active row now
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Tenants.Add(tenant);
            ctx.Entry(tenant).Property("Status").CurrentValue = "active";
            ctx.Entry(tenant).Property("PlanId").CurrentValue = (Guid?)null;
            ctx.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3, 4 };
            await ctx.SaveChangesAsync();
        }
        // NOTE: AddTenantPlanAssignment is intentionally NOT applied here — the
        // test applies it and asserts it throws.
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    [Test]
    public async Task Backfill_FailsLoud_When_Free_Absent_And_Tenant_Would_Be_Orphaned()
    {
        var act = async () =>
        {
            await using var ctx = NewContext();
            await ctx.Database.MigrateAsync();
        };

        var ex = (await act.Should().ThrowAsync<Exception>(
            "the back-fill must fail loud, not silently leave a tenant assignment-less")).Which;

        Flatten(ex).Should().Contain("free",
            "the RAISE message must name the missing 'free' terminal fallback");

        // And the migration aborted transactionally: the assignments table it
        // would have created never landed (PostgreSQL DDL is transactional).
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables "
            + "WHERE table_schema='public' AND table_name='tenant_plan_assignments');", conn);
        var tableExists = (bool)(await cmd.ExecuteScalarAsync())!;
        tableExists.Should().BeFalse(
            "the failed migration rolled back — the assignments table was never created");
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }
        return string.Join(" | ", messages);
    }
}

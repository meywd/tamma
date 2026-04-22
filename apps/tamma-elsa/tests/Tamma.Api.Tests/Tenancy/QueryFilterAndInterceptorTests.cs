using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Npgsql;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Integration tests covering the Phase-3 dual-DbContext + RLS plane.
/// Closes port-gap findings orgs/002 (EF filter permissive on null tenant)
/// and orgs/004 (withTenantContext SET LOCAL gone).
///
/// <para>The fixture boots a real Postgres 17 container because:</para>
/// <list type="bullet">
///   <item><description>RLS policies only evaluate on the Postgres provider
///     (not InMemory / SQLite).</description></item>
///   <item><description>The <c>tamma_app</c> role + role-based policy
///     enforcement requires genuine multi-role login.</description></item>
///   <item><description>The <c>TenantContextInterceptor</c> runs
///     <c>set_config(...)</c> which is a Postgres builtin.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class QueryFilterAndInterceptorTests
{
    [SetUp]
    public Task SetUp() => TenancySetUpFixture.ResetDatabaseAsync();

    // ──────────────────────────────────────────────────────────────────────
    // EF Query Filter behavior — finding orgs/002
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AdminContext_NullTenant_ReturnsAllRows()
    {
        // Arrange — seed two tenants' rows via a no-tenant-bound context.
        var (tenantA, tenantB) = await SeedTwoTenantsAsync();

        // Act — fetch via TammaDbContext (admin/permissive) with null context.
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var prompts = await db.PromptOverrides.ToListAsync();

        // Assert — admin context returns both tenants' rows.
        prompts.Select(p => p.TenantId).Should().Contain(new Guid?[] { tenantA, tenantB });
    }

    [Test]
    public async Task AppContext_NullTenant_ReturnsZeroRows_FailClosed()
    {
        // Arrange — seed two tenants.
        await SeedTwoTenantsAsync();

        // Act — fetch via TammaAppDbContext with no tenant bound.
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaAppDbContext>();
        // ITenantContext is null by default in this scope.

        var prompts = await db.PromptOverrides.ToListAsync();

        // Assert — fail-closed: no tenant context → zero rows.
        prompts.Should().BeEmpty();
    }

    [Test]
    public async Task AppContext_TenantSet_ReturnsOnlyThatTenantsRows()
    {
        // Arrange
        var (tenantA, tenantB) = await SeedTwoTenantsAsync();

        // Sanity check — admin context can see both (seed landed).
        using (var adminScope = TenancySetUpFixture.Factory.Services.CreateScope())
        {
            var adminDb = adminScope.ServiceProvider.GetRequiredService<TammaDbContext>();
            var all = await adminDb.PromptOverrides.ToListAsync();
            all.Should().HaveCount(2, because: "seed must land via admin context");
        }

        // Act — scope A: bind tenant A before resolving the context.
        List<PromptOverride> fromA;
        List<PromptOverride> fromB;
        using (var scope = TenancySetUpFixture.Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenantId(tenantA);
            var db = scope.ServiceProvider.GetRequiredService<TammaAppDbContext>();
            fromA = await db.PromptOverrides.ToListAsync();
        }

        // Act — scope B: bind tenant B.
        using (var scope = TenancySetUpFixture.Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenantId(tenantB);
            var db = scope.ServiceProvider.GetRequiredService<TammaAppDbContext>();
            fromB = await db.PromptOverrides.ToListAsync();
        }

        // Assert — each scope only sees its tenant's rows.
        fromA.Should().HaveCount(1, because: "tenant A should see its own row");
        fromB.Should().HaveCount(1, because: "tenant B should see its own row");
        fromA.Should().OnlyContain(p => p.TenantId == tenantA);
        fromB.Should().OnlyContain(p => p.TenantId == tenantB);
    }

    [Test]
    public async Task AppContext_IgnoreQueryFilters_StillRespectsRls()
    {
        // Arrange
        var (tenantA, _) = await SeedTwoTenantsAsync();

        // Act — scoped as tenant A. Bypass EF filter via IgnoreQueryFilters,
        // but RLS policies still run against the tamma_app role.
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenantId(tenantA);
        var db = scope.ServiceProvider.GetRequiredService<TammaAppDbContext>();

        var all = await db.PromptOverrides.IgnoreQueryFilters().ToListAsync();

        // Assert — IgnoreQueryFilters skips the EF layer but the DB-layer
        // RLS policy still gates reads to tenant A's rows only. This is
        // the defense-in-depth that Phase-2 + Phase-3 promise: even if the
        // EF filter is bypassed by an admin-flavored query, the DB denies
        // cross-tenant reads when connected as tamma_app.
        all.Should().HaveCount(1);
        all.Should().OnlyContain(p => p.TenantId == tenantA);
    }

    [Test]
    public async Task AdminContext_IgnoreQueryFilters_ReturnsAllTenants()
    {
        // Arrange
        var (tenantA, tenantB) = await SeedTwoTenantsAsync();

        // Act — scoped as tenant A (irrelevant to admin context).
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenantId(tenantA);
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var all = await db.PromptOverrides.IgnoreQueryFilters().ToListAsync();

        // Admin connection is superuser → RLS bypassed → all rows visible.
        all.Should().HaveCount(2);
        all.Select(p => p.TenantId).Should().Contain(new Guid?[] { tenantA, tenantB });
    }

    // ──────────────────────────────────────────────────────────────────────
    // TenantContextInterceptor — finding orgs/004
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Interceptor_SetsAppCurrentTenantId_OnConnectionOpen()
    {
        var tenantId = Guid.NewGuid();

        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenantId(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TammaAppDbContext>();

        // Keep the connection open for the duration of the read — Npgsql's
        // pool semantics mean EF may hand the connection back between
        // ToListAsync and our follow-up current_setting read. Explicitly
        // opening before both calls pins the same physical connection.
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            // Touch the context so ConnectionOpenedAsync has fired on THIS
            // physical connection (the interceptor ran once when we called
            // OpenConnectionAsync above).
            _ = await db.Tenants.IgnoreQueryFilters().ToListAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_setting('app.current_tenant_id', true)";
            var raw = (string?)await cmd.ExecuteScalarAsync();

            // Assert — interceptor planted the right value on this connection.
            raw.Should().Be(tenantId.ToString());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [Test]
    public async Task Interceptor_SetsEmptyString_WhenTenantContextIsUnset()
    {
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        // Do NOT set a tenant.
        var db = scope.ServiceProvider.GetRequiredService<TammaAppDbContext>();

        _ = await db.Tenants.IgnoreQueryFilters().ToListAsync();

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_setting('app.current_tenant_id', true)";
        var raw = (string?)await cmd.ExecuteScalarAsync();

        // Empty string marker — NULLIF(...) in the RLS policy turns it into
        // NULL, which no tenant row matches, so the session fails closed.
        raw.Should().Be(string.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────
    // RLS enforcement — finding orgs/004 + admin-db 020
    //
    // When the connection logs in as tamma_app (not superuser), RLS
    // policies on tenant-scoped tables actually reject cross-tenant reads.
    // The admin (tamma/superuser) connection does NOT reject them.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Rls_AppRoleConnection_SessionTenant_LimitsReadToOwnTenant()
    {
        var (tenantA, _) = await SeedTwoTenantsAsync();

        // Direct Npgsql connection as tamma_app, bypassing EF. This is the
        // "raw SQL from Elsa, psql, pg_dump, or ADO.NET" defense-in-depth
        // path called out in the audit summary.
        await using var conn = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString);
        await conn.OpenAsync();

        // Simulate what the interceptor would do.
        await using (var bindCmd = conn.CreateCommand())
        {
            bindCmd.CommandText = "SELECT set_config('app.current_tenant_id', @p, false)";
            var p = bindCmd.CreateParameter();
            p.ParameterName = "p";
            p.Value = tenantA.ToString();
            bindCmd.Parameters.Add(p);
            await bindCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prompt_overrides";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // RLS should only see tenant A's row under the tamma_app role.
        count.Should().Be(1);
    }

    [Test]
    public async Task Rls_AppRoleConnection_EmptyTenantSetting_FailsClosed()
    {
        await SeedTwoTenantsAsync();

        await using var conn = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString);
        await conn.OpenAsync();

        // Explicitly clear / never-set tenant binding.
        await using (var bindCmd = conn.CreateCommand())
        {
            bindCmd.CommandText = "SELECT set_config('app.current_tenant_id', '', false)";
            await bindCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prompt_overrides";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // Empty string → NULLIF makes it NULL; policy compares TenantId =
        // NULL which is never true, so zero rows.
        count.Should().Be(0);
    }

    [Test]
    public async Task Rls_SuperuserConnection_BypassesPolicies_SeesAllTenants()
    {
        await SeedTwoTenantsAsync();

        // Superuser (tamma) connection with no tenant bound — RLS is
        // bypassed by superusers, so admin paths still see every row.
        await using var conn = new NpgsqlConnection(TenancySetUpFixture.AdminConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prompt_overrides";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        count.Should().Be(2);
    }

    // ──────────────────────────────────────────────────────────────────────
    // NULL-tenant policy tightening — review 2026-04-20 finding 2
    //
    // Phase-2 originally wrote the tenant_isolation_policy as
    // "TenantId IS NULL OR TenantId = current_setting(...)". Strict tenant-
    // scoped tables (users, github_installations, user_invites,
    // domain_events, workflow_instances, provider_diagnostics,
    // provider_health) dropped the IS NULL branch in migration
    // 20260420120000_Phase2RlsNullPolicyTightening. A row with TenantId =
    // NULL must no longer be visible to an app-role session whose bound
    // tenant doesn't match.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Rls_NullTenantRow_NotVisibleToAppRole_OnStrictTable()
    {
        // Seed a GitHubInstallation row with TenantId = NULL via the admin
        // connection (superuser bypasses RLS + trigger for NULL→uuid path).
        // The prevent_tenant_id_change trigger only fires on UPDATE (not
        // INSERT), and INSERT with TenantId = NULL is permitted by design
        // (bootstrap-before-tenant).
        using (var scope = TenancySetUpFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.GitHubInstallations.Add(new Tamma.Data.Entities.GitHubInstallation
            {
                Id = Guid.NewGuid(),
                InstallationId = 555_555L,
                AccountLogin = "orphan-install",
                AccountType = "Organization",
                AppId = 999_999L,
                TenantId = null, // strict-table NULL-tenant row
            });
            await db.SaveChangesAsync();
        }

        // Sanity check: the admin (superuser) connection DOES see the row
        // (RLS is bypassed for superusers).
        await using (var admin = new NpgsqlConnection(TenancySetUpFixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var adminCmd = admin.CreateCommand();
            adminCmd.CommandText = "SELECT COUNT(*) FROM github_installations WHERE \"TenantId\" IS NULL";
            var adminCount = Convert.ToInt32(await adminCmd.ExecuteScalarAsync());
            adminCount.Should().Be(1, "seed landed via admin");
        }

        // App-role connection with an arbitrary bound tenant. The NULL-
        // tenant row is strictly not its, so the tightened policy must
        // return zero rows.
        await using var conn = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString);
        await conn.OpenAsync();

        await using (var bindCmd = conn.CreateCommand())
        {
            bindCmd.CommandText = "SELECT set_config('app.current_tenant_id', @p, false)";
            var p = bindCmd.CreateParameter();
            p.ParameterName = "p";
            p.Value = Guid.NewGuid().ToString();
            bindCmd.Parameters.Add(p);
            await bindCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM github_installations";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        count.Should().Be(0,
            "NULL-tenant rows must not be visible to the app-role plane on strict tables");
    }

    [Test]
    public async Task Rls_NullTenantRow_NotVisibleToAppRole_EvenWithEmptyTenantSetting()
    {
        // Same seed, but the app-role session has no tenant bound at all.
        // The policy evaluates `"TenantId" = NULLIF('', '')::uuid` which
        // compares to NULL → always false; zero rows returned.
        using (var scope = TenancySetUpFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.GitHubInstallations.Add(new Tamma.Data.Entities.GitHubInstallation
            {
                Id = Guid.NewGuid(),
                InstallationId = 666_666L,
                AccountLogin = "another-orphan",
                AccountType = "Organization",
                AppId = 888_888L,
                TenantId = null,
            });
            await db.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString);
        await conn.OpenAsync();

        await using (var bindCmd = conn.CreateCommand())
        {
            bindCmd.CommandText = "SELECT set_config('app.current_tenant_id', '', false)";
            await bindCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM github_installations";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        count.Should().Be(0,
            "empty tenant binding + NULL-tenant row must fail closed on strict tables");
    }

    [Test]
    public async Task Rls_NullTenantRow_StillVisibleToAppRole_OnPlatformGlobalTable()
    {
        // prompt_overrides retains the IS NULL branch because NULL means
        // "Tamma-shipped system default" — legitimately visible to every
        // tenant session. This is the complementary invariant to the
        // strict-table tightening.
        using (var scope = TenancySetUpFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.PromptOverrides.Add(new Tamma.Data.Entities.PromptOverride
            {
                TenantId = null, // system default
                UserId = Guid.NewGuid(),
                Scope = "role-action",
                Role = "pm",
                Action = "create-story",
                Template = "system-default template",
            });
            await db.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString);
        await conn.OpenAsync();

        await using (var bindCmd = conn.CreateCommand())
        {
            bindCmd.CommandText = "SELECT set_config('app.current_tenant_id', @p, false)";
            var p = bindCmd.CreateParameter();
            p.ParameterName = "p";
            p.Value = Guid.NewGuid().ToString();
            bindCmd.Parameters.Add(p);
            await bindCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prompt_overrides";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        count.Should().Be(1,
            "NULL-tenant rows on platform-global tables remain visible by design");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static async Task<(Guid TenantA, Guid TenantB)> SeedTwoTenantsAsync()
    {
        // Seed via the admin context so the initial INSERTs are not
        // filtered out by RLS or by the fail-closed EF filter.
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var tenantA = await CreateTenantAsync(db, "tenant-a");
        var tenantB = await CreateTenantAsync(db, "tenant-b");

        db.PromptOverrides.Add(new PromptOverride
        {
            TenantId = tenantA,
            UserId = Guid.NewGuid(),
            Scope = "role-action",
            Role = "pm",
            Action = "create-story",
            Template = "tenant-A template",
        });
        db.PromptOverrides.Add(new PromptOverride
        {
            TenantId = tenantB,
            UserId = Guid.NewGuid(),
            Scope = "role-action",
            Role = "pm",
            Action = "create-story",
            Template = "tenant-B template",
        });
        await db.SaveChangesAsync();

        return (tenantA, tenantB);
    }

    private static async Task<Guid> CreateTenantAsync(TammaDbContext db, string slug)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            Type = "team",
            Plan = "free",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }
}

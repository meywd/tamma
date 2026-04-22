using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Npgsql;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Story 19-6 fail-closed regression. Inserts NULL-tenant rows on every
/// strict tenant-scoped table as the superuser admin connection, then
/// opens a fresh app-role connection bound to a random tenant id (and
/// also one with no binding at all) and asserts that
/// <c>SELECT COUNT(*)</c> returns zero on every strict table.
///
/// <para>This is the runtime contract that the Phase-2 RLS policy
/// tightening (<c>20260420120000_Phase2RlsNullPolicyTightening</c>) +
/// the Story 19-6 repository / endpoint swaps together promise: a row
/// that lacks a tenant must not be visible to a per-request session
/// even when its bound tenant differs from the row's NULL.</para>
///
/// <para>Complements <see cref="QueryFilterAndInterceptorTests"/> which
/// exercises the same end-to-end behaviour through the EF DbContext;
/// this fixture verifies the DB-layer guarantee directly so a future
/// repository regression that bypasses EF (raw SQL, ADO.NET, dapper)
/// still fails-closed at the policy layer.</para>
/// </summary>
[TestFixture]
public class AppRoleRegressionTests
{
    /// <summary>
    /// Strict tenant-scoped tables — those whose
    /// <c>tenant_isolation_policy</c> drops the <c>TenantId IS NULL OR</c>
    /// branch (per Phase-2 + Phase-2.1 migrations). NULL-tenant rows on
    /// these tables must not be visible to the app-role plane.
    ///
    /// <para>Excludes the platform-global tables (<c>prompt_overrides</c>,
    /// <c>agent_configs</c>, <c>sanitization_rules</c>,
    /// <c>workflow_definitions</c>) which keep the IS NULL branch by
    /// design — system defaults are reachable cross-tenant.</para>
    /// </summary>
    private static readonly string[] StrictTables =
    {
        "users",
        "github_installations",
        // user_invites omitted: UserInvite.TenantId is non-nullable in the
        // entity model (a tenant must exist before an invite is minted).
        // The Phase-2 RLS policy on user_invites is still strict, but the
        // regression vector — a NULL-tenant row leaking — is unreachable
        // through EF and through the database column constraint.
        "domain_events",
        "workflow_instances",
        "provider_diagnostics",
        "provider_health",
    };

    [SetUp]
    public Task SetUp() => TenancySetUpFixture.ResetDatabaseAsync();

    [Test]
    public async Task NullTenantRows_AreNeverVisible_ToAppRole_OnAnyStrictTable()
    {
        // 1. Insert a NULL-tenant seed into every strict table via the
        //    admin connection. Superuser bypasses RLS + the
        //    prevent_tenant_id_change trigger only fires on UPDATE so
        //    INSERTing a NULL TenantId is permitted.
        await InsertNullTenantSeedsAsync();

        // 2. Sanity check via admin: every table has its NULL-tenant row.
        await using (var admin = new NpgsqlConnection(TenancySetUpFixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            foreach (var table in StrictTables)
            {
                await using var cmd = admin.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE \"TenantId\" IS NULL";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                count.Should().Be(1,
                    $"seed must land via admin for {table}");
            }
        }

        // 3. Open a fresh app-role connection, bind a random tenant id
        //    (so the policy compares NULL == <random uuid>), assert zero.
        var randomTenant = Guid.NewGuid();
        await using (var app = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString))
        {
            await app.OpenAsync();

            await using (var bind = app.CreateCommand())
            {
                bind.CommandText = "SELECT set_config('app.current_tenant_id', @p, false)";
                var p = bind.CreateParameter();
                p.ParameterName = "p";
                p.Value = randomTenant.ToString();
                bind.Parameters.Add(p);
                await bind.ExecuteNonQueryAsync();
            }

            foreach (var table in StrictTables)
            {
                await using var cmd = app.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                count.Should().Be(0,
                    $"NULL-tenant {table} row must not be visible to app-role with bound tenant");
            }
        }

        // 4. Same assertion with NO tenant bound at all — empty string
        //    marker — RLS NULLIF turns it into NULL → also zero.
        await using (var app = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString))
        {
            await app.OpenAsync();

            await using (var bind = app.CreateCommand())
            {
                bind.CommandText = "SELECT set_config('app.current_tenant_id', '', false)";
                await bind.ExecuteNonQueryAsync();
            }

            foreach (var table in StrictTables)
            {
                await using var cmd = app.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                count.Should().Be(0,
                    $"NULL-tenant {table} row must not be visible to app-role with empty binding");
            }
        }
    }

    [Test]
    public async Task FlippingTenantContext_MidSession_DoesNotResurrectNullRows()
    {
        // Reproduces the secondary AC from the impl plan: even after we
        // flip the bound tenant context to a different value mid-session,
        // a NULL-tenant row created behind the admin connection must not
        // become visible.
        await InsertNullTenantSeedsAsync();

        await using var app = new NpgsqlConnection(TenancySetUpFixture.AppConnectionString);
        await app.OpenAsync();

        // First binding — random A.
        await using (var bind = app.CreateCommand())
        {
            bind.CommandText = "SELECT set_config('app.current_tenant_id', @p, false)";
            var p = bind.CreateParameter();
            p.ParameterName = "p";
            p.Value = Guid.NewGuid().ToString();
            bind.Parameters.Add(p);
            await bind.ExecuteNonQueryAsync();
        }

        await AssertEachStrictTableEmptyAsync(app);

        // Flip to a fresh random tenant context — same connection.
        await using (var bind = app.CreateCommand())
        {
            bind.CommandText = "SELECT set_config('app.current_tenant_id', @p, false)";
            var p = bind.CreateParameter();
            p.ParameterName = "p";
            p.Value = Guid.NewGuid().ToString();
            bind.Parameters.Add(p);
            await bind.ExecuteNonQueryAsync();
        }

        await AssertEachStrictTableEmptyAsync(app);
    }

    private static async Task InsertNullTenantSeedsAsync()
    {
        // Use the admin (superuser) EF context so RLS + WITH CHECK don't
        // get in the way. Each seed is a minimal valid row — only the
        // required NOT NULL columns are set.
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        // ── users ──
        db.Users.Add(new User
        {
            Email = $"orphan-{Guid.NewGuid():N}@example.com",
            DisplayName = "Orphan User",
            TenantId = null,
        });

        // ── github_installations ──
        db.GitHubInstallations.Add(new GitHubInstallation
        {
            InstallationId = unchecked((long)(uint)Random.Shared.Next(int.MinValue, int.MaxValue)),
            AccountLogin = "orphan-org",
            AccountType = "Organization",
            AppId = 12345L,
            TenantId = null,
        });

        // ── domain_events ──
        db.DomainEvents.Add(new Tamma.Data.Entities.DomainEvent
        {
            Type = "TEST.ORPHAN.EVENT",
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
            TenantId = null,
        });

        // ── workflow_instances ──
        // workflow_instances requires a definition id — seed a definition
        // first via the same admin context (definitions are platform-global).
        var def = new WorkflowDefinition
        {
            Name = "orphan-def",
            Steps = "[]",
            Version = 1,
            TenantId = null,
        };
        db.WorkflowDefinitions.Add(def);
        await db.SaveChangesAsync();
        db.WorkflowInstances.Add(new WorkflowInstance
        {
            DefinitionId = def.Id,
            Status = "pending",
            Variables = "{}",
            TenantId = null,
        });

        // ── provider_diagnostics ──
        db.ProviderDiagnostics.Add(new ProviderDiagnostic
        {
            ProviderKey = "orphan-provider",
            Cost = 0m,
            Success = true,
            TenantId = null,
        });

        // ── provider_health ──
        db.ProviderHealths.Add(new ProviderHealth
        {
            ProviderKey = "orphan-provider",
            Status = "unknown",
            TenantId = null,
        });

        await db.SaveChangesAsync();
    }

    private static async Task AssertEachStrictTableEmptyAsync(NpgsqlConnection conn)
    {
        foreach (var table in StrictTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            count.Should().Be(0,
                $"NULL-tenant {table} row must not be visible regardless of bound tenant");
        }
    }
}

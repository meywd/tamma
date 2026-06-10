using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 Task 5 — tests for the placement-aware
/// <see cref="DropTenantRoleActivity.DropRoleAsync"/> core: tenants with
/// an assigned <c>tenant_databases</c> row drop their role via
/// <see cref="ITenantDatabasePool"/> on the TARGET cluster (roles are
/// cluster-scoped; <c>DROP OWNED BY</c> acts per-database); tenants
/// without a placement keep the legacy central
/// <see cref="ITenantAdminConnection"/> path (pre-Phase-2 dev runs made
/// the role on the central cluster).
/// </summary>
[TestFixture]
public class DropTenantRoleActivityTests
{
    private static readonly Guid Tenant = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PoolRow = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Test]
    public async Task DropRoleAsync_Placed_DropsOwnedThenRole_ViaPool()
    {
        var pool = new RecordingTenantDatabasePool { RoleExists = true };
        var admin = new RecordingAdminConnection { RoleExists = true };
        var quoted = TenantNaming.Quote(TenantNaming.RoleName(Tenant));

        var dropped = await DropTenantRoleActivity.DropRoleAsync(
            pool, admin, Tenant, PoolRow,
            logger: null, logScope: "lifecycle", CancellationToken.None);

        dropped.Should().BeTrue();
        pool.ExecutedCommands.Should().HaveCount(2);
        pool.ExecutedCommands[0].Should().Be((PoolRow, $"DROP OWNED BY {quoted};"));
        pool.ExecutedCommands[1].Should().Be((PoolRow, $"DROP ROLE IF EXISTS {quoted};"));
        admin.ExecutedCommands.Should().BeEmpty(
            "a placed tenant's role lives on the assigned pool row's cluster — "
            + "the central admin connection must not be touched");
    }

    [Test]
    public async Task DropRoleAsync_Placed_RoleMissing_SkipsIdempotently()
    {
        var pool = new RecordingTenantDatabasePool { RoleExists = false };
        var admin = new RecordingAdminConnection { RoleExists = true };

        var dropped = await DropTenantRoleActivity.DropRoleAsync(
            pool, admin, Tenant, PoolRow,
            logger: null, logScope: "lifecycle", CancellationToken.None);

        dropped.Should().BeFalse();
        pool.RoleExistsCalls.Should().Be(1, "the probe runs on the pool row's cluster");
        pool.ExecutedCommands.Should().BeEmpty();
        admin.ExecutedCommands.Should().BeEmpty(
            "the placement path must not fall through to the central cluster");
    }

    [Test]
    public async Task DropRoleAsync_NoPlacement_FallsBackToCentralAdmin()
    {
        // Legacy: roles from pre-Phase-2 dev runs live on the central
        // cluster (the old db-per-tenant create made them there).
        var pool = new RecordingTenantDatabasePool { RoleExists = true };
        var admin = new RecordingAdminConnection { RoleExists = true };
        var quoted = TenantNaming.Quote(TenantNaming.RoleName(Tenant));

        var dropped = await DropTenantRoleActivity.DropRoleAsync(
            pool, admin, Tenant, databaseId: null,
            logger: null, logScope: "lifecycle", CancellationToken.None);

        dropped.Should().BeTrue();
        admin.ExecutedCommands.Should().Equal(
            $"DROP OWNED BY {quoted};",
            $"DROP ROLE IF EXISTS {quoted};");
        pool.ExecutedCommands.Should().BeEmpty();
        pool.RoleExistsCalls.Should().Be(0,
            "an unplaced tenant has no pool row to probe");
    }

    [Test]
    public async Task DropRoleAsync_NoPlacement_RoleMissing_SkipsIdempotently()
    {
        var pool = new RecordingTenantDatabasePool { RoleExists = true };
        var admin = new RecordingAdminConnection { RoleExists = false };

        var dropped = await DropTenantRoleActivity.DropRoleAsync(
            pool, admin, Tenant, databaseId: null,
            logger: null, logScope: "lifecycle", CancellationToken.None);

        dropped.Should().BeFalse();
        admin.ExecutedCommands.Should().BeEmpty();
        pool.ExecutedCommands.Should().BeEmpty();
    }

    private sealed class RecordingAdminConnection : ITenantAdminConnection
    {
        public bool RoleExists { get; set; }
        public List<string> ExecutedCommands { get; } = new();

        public Task<bool> RoleExistsAsync(string roleName, CancellationToken ct = default)
            => Task.FromResult(RoleExists);

        public Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<int> ExecuteAsync(string commandText, CancellationToken ct = default)
        {
            ExecutedCommands.Add(commandText);
            return Task.FromResult(0);
        }

        public string BuildTenantConnectionString(
            string databaseName, string roleName, string password)
            => $"Host=localhost;Database={databaseName}";

        public TenantAdminConnectionInfo GetConnectionInfo(string databaseName)
            => new("localhost", 5432, "tamma_provisioner", "pw", databaseName);
    }
}

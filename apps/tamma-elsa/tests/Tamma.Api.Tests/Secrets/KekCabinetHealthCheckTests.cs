using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// PF-S10 — pin the legacy-NULL handling in
/// <see cref="KekCabinetHealthCheck"/>. The previous filter
/// (<c>WHERE v != null</c>) skipped legacy rows entirely; after two
/// rotations they would silently fall off the retired-keys ring and
/// become permanently undecryptable, but readiness still passed.
///
/// The fix: count rows with <c>KekVersion IS NULL</c> separately and
/// surface as <see cref="HealthStatus.Unhealthy"/> with a remediation
/// message.
/// </summary>
[TestFixture]
public class KekCabinetHealthCheckTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private IDbContextFactory<ControlPlaneDbContext> _dbContextFactory = null!;
    private NpgsqlConnection _conn = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _dbContextFactory = _scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        _conn = new NpgsqlConnection(ApiTestFixture.Postgres.GetConnectionString());
        await _conn.OpenAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_conn is not null) await _conn.DisposeAsync();
        _scope?.Dispose();
    }

    private static KekProvider MakeKekProvider(int activeVersion, int retainedHistorySize)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(new byte[32]),
                [KekProvider.ActiveVersionConfigKey] = activeVersion.ToString(),
                [KekProvider.RetainedHistorySizeConfigKey] = retainedHistorySize.ToString(),
            }).Build();
        return new KekProvider(config, NullLogger<KekProvider>.Instance);
    }

    private async Task<Guid> SeedTenantWithKekVersionAsync(int? kekVersion)
    {
        // Insert a tenant row, then update the shadow KekVersion +
        // EncryptedConnectionString columns directly via raw SQL so we
        // bypass the Tenant POCO (which doesn't expose those fields).
        var tenant = new Tenant
        {
            Name = $"laggard-{Guid.NewGuid():N}".Substring(0, 16),
            Slug = $"slug-{Guid.NewGuid():N}".Substring(0, 16),
            Type = "org",
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        // Set EncryptedConnectionString to a non-null bytea so the
        // health check picks the row up; KekVersion to whatever the
        // test wants (NULL allowed).
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tenants
            SET "EncryptedConnectionString" = '\x00'::bytea,
                "KekVersion" = @kek
            WHERE "Id" = @id
        """;
        cmd.Parameters.Add(new NpgsqlParameter("id", tenant.Id));
        cmd.Parameters.Add(new NpgsqlParameter("kek",
            kekVersion is null ? (object)DBNull.Value : (object)kekVersion.Value));
        await cmd.ExecuteNonQueryAsync();
        return tenant.Id;
    }

    [Test]
    public async Task CheckHealth_NoEncryptedRows_ReturnsHealthy()
    {
        var kek = MakeKekProvider(activeVersion: 1, retainedHistorySize: 2);
        var check = new KekCabinetHealthCheck(
            kek, NullLogger<KekCabinetHealthCheck>.Instance, _dbContextFactory);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("no encrypted tenant rows yet");
    }

    [Test]
    public async Task CheckHealth_AllRowsAtCurrentVersion_ReturnsHealthy()
    {
        await SeedTenantWithKekVersionAsync(kekVersion: 1);
        var kek = MakeKekProvider(activeVersion: 1, retainedHistorySize: 2);
        var check = new KekCabinetHealthCheck(
            kek, NullLogger<KekCabinetHealthCheck>.Instance, _dbContextFactory);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Test]
    public async Task CheckHealth_LegacyNullVersionRow_ReturnsUnhealthy()
    {
        // PF-S10 — exact regression case: KekVersion=null + active=v3 +
        // retainedHistorySize=2 (so the cabinet covers v1..v3). Without
        // the fix the legacy row is invisible to the laggard check and
        // readiness passes. With the fix we surface as Unhealthy with
        // a remediation message.
        await SeedTenantWithKekVersionAsync(kekVersion: null);
        var kek = MakeKekProvider(activeVersion: 3, retainedHistorySize: 2);
        var check = new KekCabinetHealthCheck(
            kek, NullLogger<KekCabinetHealthCheck>.Instance, _dbContextFactory);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("legacy");
        result.Description.Should().Contain("re-encrypt");
    }

    [Test]
    public async Task CheckHealth_LegacyNullCount_ReportedInMessage()
    {
        // Three legacy rows (NULL version) + one current row. The
        // health check should surface "3 legacy rows lack version
        // stamp" so operators have an actionable count.
        for (var i = 0; i < 3; i++)
        {
            await SeedTenantWithKekVersionAsync(kekVersion: null);
        }
        await SeedTenantWithKekVersionAsync(kekVersion: 3);

        var kek = MakeKekProvider(activeVersion: 3, retainedHistorySize: 2);
        var check = new KekCabinetHealthCheck(
            kek, NullLogger<KekCabinetHealthCheck>.Instance, _dbContextFactory);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("3 legacy rows");
    }

    [Test]
    public async Task CheckHealth_LaggardVersion_BeyondRing_ReturnsUnhealthy()
    {
        // Existing behaviour: a row at v1 with active=v3 + history=1
        // (cabinet covers v2..v3) is too far behind. Pre-PF-S10 path
        // still works.
        await SeedTenantWithKekVersionAsync(kekVersion: 1);
        var kek = MakeKekProvider(activeVersion: 3, retainedHistorySize: 1);
        var check = new KekCabinetHealthCheck(
            kek, NullLogger<KekCabinetHealthCheck>.Instance, _dbContextFactory);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("rotation runbook");
    }

    [Test]
    public async Task CheckHealth_NoDbContextFactory_ReturnsHealthy()
    {
        var kek = MakeKekProvider(activeVersion: 1, retainedHistorySize: 2);
        // dbContextFactory: null → dev/test path. The check is healthy
        // with a "no factory wired" note.
        var check = new KekCabinetHealthCheck(
            kek, NullLogger<KekCabinetHealthCheck>.Instance, dbContextFactory: null);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("no CP DbContext factory");
    }
}

using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Tests.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1 AC6 — the parallel never-joined mode surfaces of
/// <see cref="TrackerPreferenceRepository"/>: a user-plane row is invisible to
/// every tenant-plane method and vice versa (each predicate pins the opposite
/// principal key to NULL, the <c>AcceptanceRulesRepository</c> contract), the
/// upsert routes strictly by which key is set, and the mismatched-surface
/// calls are rejected before any SQL runs.
///
/// <para>REQUIRES DOCKER to run (CI-verified).</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class TrackerPreferenceRepositoryTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;
    private Guid _tenantId;
    private string _schema = null!;
    private TrackerPreferenceRepository _repository = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tracker_prefs_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();

        _tenantId = Guid.NewGuid();
        _schema = TenantNaming.SchemaName(_tenantId);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(_schema));

        _repository = new TrackerPreferenceRepository(
            new DocumentTestData.SchemaRoutingFactory(_baseConnectionString).Map(_tenantId, _schema),
            new DocumentTestData.FakeTenantContext(_tenantId));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    [SetUp]
    public async Task ClearTable()
    {
        await using var conn = new NpgsqlConnection(CsFor(_schema));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"TRUNCATE TABLE {_schema}.tracker_preferences;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }
            .ConnectionString;

    [Test]
    public async Task Planes_never_join()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await _repository.UpsertAsync(new TrackerPreference
        {
            UserId = userId,
            DefaultKind = "story",
        });
        await _repository.UpsertForTenantAsync(new TrackerPreference
        {
            TenantId = tenantId,
            DefaultKind = "epic",
        });

        // The user row is invisible to the tenant surface and vice versa —
        // even when handed the "wrong" id, the opposite-key-NULL predicate
        // returns nothing rather than silently crossing planes.
        (await _repository.GetAsync(userId))!.DefaultKind.Should().Be("story");
        (await _repository.GetByTenantAsync(tenantId))!.DefaultKind.Should().Be("epic");
        (await _repository.GetByTenantAsync(userId)).Should().BeNull(
            "a user-plane row must be unreachable through the tenant plane");
        (await _repository.GetAsync(tenantId)).Should().BeNull(
            "a tenant-plane row must be unreachable through the user plane");

        (await _repository.DeleteByTenantAsync(userId)).Should().BeFalse(
            "the tenant-plane delete must not reach across and remove the user row");
        (await _repository.GetAsync(userId)).Should().NotBeNull();
    }

    [Test]
    public async Task Upsert_creates_then_updates_and_bumps_version()
    {
        var userId = Guid.NewGuid();

        var (created, wasCreated) = await _repository.UpsertAsync(
            new TrackerPreference { UserId = userId, BoardGroupBy = "status" });
        wasCreated.Should().BeTrue();
        created.Version.Should().Be(1);

        var (updated, wasCreatedAgain) = await _repository.UpsertAsync(
            new TrackerPreference { UserId = userId, BoardGroupBy = "kind" });
        wasCreatedAgain.Should().BeFalse();
        updated.Version.Should().Be(2);
        updated.BoardGroupBy.Should().Be("kind");
    }

    [Test]
    public async Task Mismatched_surface_calls_are_rejected_before_any_sql_runs()
    {
        var wrongPlaneForUser = () => _repository.UpsertAsync(
            new TrackerPreference { TenantId = Guid.NewGuid() });
        await wrongPlaneForUser.Should().ThrowAsync<ArgumentException>();

        var wrongPlaneForTenant = () => _repository.UpsertForTenantAsync(
            new TrackerPreference { UserId = Guid.NewGuid() });
        await wrongPlaneForTenant.Should().ThrowAsync<ArgumentException>();
    }
}

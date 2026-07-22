using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Data;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-11 (AC2/AC4/AC8) — Postgres 17 Testcontainer proof of the write path:
/// registry validation before write, immutable revisions + supersession chain
/// linearity, status-only transitions, the latest-accepted filter matrix, and the
/// CHECK constraint. EF InMemory models neither the filtered unique index nor the
/// CHECK, so a real Postgres is the only proof. Docker-gated (CI runs it).
/// </summary>
[TestFixture]
public class DocumentInstanceRepositoryTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("document_store_api_test")
            .WithUsername("tamma").WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    private async Task<(DocumentInstanceRepository Repo, Guid Tenant, string Schema)> NewRepoAsync()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));
        var factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        var repo = new DocumentInstanceRepository(factory, new DocumentTestData.FakeTenantContext(tenant));
        return (repo, tenant, schema);
    }

    private async Task<int> CountRowsAsync(string schema)
    {
        var opts = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options;
        await using var ctx = new TenantDbContext(opts, Guid.NewGuid());
        return await ctx.Documents.IgnoreQueryFilters().CountAsync();
    }

    [Test]
    public async Task InsertAsync_InvalidBody_Throws_And_PersistsNothing()
    {
        var (repo, tenant, schema) = await NewRepoAsync();
        var envelope = DocumentTestData.DecompositionEnvelope("i", body: "{}");

        var act = async () => await repo.InsertAsync(tenant, envelope, null, CancellationToken.None);

        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("DOCUMENT.STORE.INVALID_BODY");
        (await CountRowsAsync(schema)).Should().Be(0);
    }

    [Test]
    public async Task InsertAsync_UnknownType_Throws_And_PersistsNothing()
    {
        var (repo, tenant, schema) = await NewRepoAsync();
        var envelope = DocumentTestData.DecompositionEnvelope("i") with { Type = "not-a-type" };

        var act = async () => await repo.InsertAsync(tenant, envelope, null, CancellationToken.None);

        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("DOCUMENT.TYPE.UNKNOWN");
        (await CountRowsAsync(schema)).Should().Be(0);
    }

    [Test]
    public async Task InsertAsync_RevisionChain_SupersedesPrior_BodyUnchanged()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        var r1 = await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i", DocumentState.Accepted), null, CancellationToken.None);
        r1.Revision.Should().Be(1);

        var supersedingEnvelope = DocumentTestData.DecompositionEnvelope(
            "i", DocumentState.Accepted, supersedesDocumentId: r1.Id);
        var r2 = await repo.InsertAsync(tenant, supersedingEnvelope, null, CancellationToken.None);

        r2.Revision.Should().Be(2);
        var priorReloaded = await repo.GetByIdAsync(tenant, r1.Id, CancellationToken.None);
        priorReloaded!.Status.Should().Be("superseded");
        DocumentTestData.SameJson(priorReloaded.BodyJson, r1.BodyJson)
            .Should().BeTrue("the prior body is never mutated (jsonb re-serializes, so compare semantically)");
    }

    [Test]
    public async Task InsertAsync_SecondSupersedeOfSamePrior_ViolatesUniqueIndex()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        var r1 = await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i"), null, CancellationToken.None);
        await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i", supersedesDocumentId: r1.Id), null, CancellationToken.None);

        var act = async () => await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i", supersedesDocumentId: r1.Id), null, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>(
            "the unique filtered index keeps the supersession chain linear");
    }

    [Test]
    public async Task SetStatusAsync_TransitionsStatusOnly_NeverBody()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        var row = await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i"), null, CancellationToken.None);

        var updated = await repo.SetStatusAsync(
            tenant, row.Id, DocumentInstanceStatus.Validated, Guid.NewGuid(), CancellationToken.None);

        updated.Status.Should().Be("validated");
        DocumentTestData.SameJson(updated.BodyJson, row.BodyJson)
            .Should().BeTrue("SetStatus never touches the body (jsonb re-serializes, so compare semantically)");
    }

    [Test]
    public async Task SetStatusAsync_RejectsSuperseded()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        var row = await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i"), null, CancellationToken.None);

        var act = async () => await repo.SetStatusAsync(
            tenant, row.Id, DocumentInstanceStatus.Superseded, null, CancellationToken.None);

        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("DOCUMENT.STORE.ILLEGAL_STATUS");
    }

    [Test]
    public async Task SetStatusAsync_MissingRow_ThrowsNotFound()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        var act = async () => await repo.SetStatusAsync(
            tenant, Guid.NewGuid(), DocumentInstanceStatus.Accepted, null, CancellationToken.None);

        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("DOCUMENT.STORE.NOT_FOUND");
    }

    [Test]
    public async Task GetLatestAcceptedAsync_ExcludesNonAccepted_AndReturnsOnePerType()
    {
        var (repo, tenant, _) = await NewRepoAsync();

        // Accepted r1, superseded by accepted r2 → only r2 is the latest accepted.
        var r1 = await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i", DocumentState.Accepted), null, CancellationToken.None);
        var r2 = await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i", DocumentState.Accepted, supersedesDocumentId: r1.Id),
            null, CancellationToken.None);
        // A draft row (never in latest-accepted).
        await repo.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope("i", DocumentState.Draft), null, CancellationToken.None);

        var latest = await repo.GetLatestAcceptedAsync(tenant, "i", CancellationToken.None);

        latest.Should().ContainSingle("≤1 per type; superseded/draft never appear");
        latest[0].Id.Should().Be(r2.Id);
    }

    [Test]
    public async Task CheckConstraint_RejectsJunkStatus_ViaRawSql()
    {
        var (_, _, schema) = await NewRepoAsync();
        await using var conn = new NpgsqlConnection(CsFor(schema));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO document_instances " +
            "(id, document_type, issue_id, produced_by_role, produced_by_action, schema_version, revision, status, body, created_at, updated_at) " +
            "VALUES (@id, 'decomposition', 'i', 'senior_developer', 'decompose-issue', 1, 1, 'bogus', '{}'::jsonb, now(), now())";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());

        var act = async () => await cmd.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }
}

using FluentAssertions;
using NUnit.Framework;
using Npgsql;
using Tamma.Core.Documents;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-11 (AC6/AC8) — two-schema cross-tenant isolation proof (the
/// <c>TenantAnalyticsIntegrationTests</c> pattern). Tenant A's documents are
/// invisible to tenant B through every repository read: the per-tenant schema is
/// the isolation plane, and the explicit <c>TenantId</c> predicate is
/// defence-in-depth. Docker-gated (CI runs it).
/// </summary>
[TestFixture]
public class DocumentStoreIsolationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("document_store_isolation_test")
            .WithUsername("tamma").WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    [Test]
    public async Task TenantA_Documents_AreInvisibleToTenantB()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schemaA = TenantNaming.SchemaName(tenantA);
        var schemaB = TenantNaming.SchemaName(tenantB);

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));
        await migrator.MigrateTenantAppAsync(CsFor(schemaB));

        var factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString)
            .Map(tenantA, schemaA)
            .Map(tenantB, schemaB);
        var repoA = new DocumentInstanceRepository(factory, new DocumentTestData.FakeTenantContext(tenantA));
        var repoB = new DocumentInstanceRepository(factory, new DocumentTestData.FakeTenantContext(tenantB));

        var aDoc = await repoA.InsertAsync(
            tenantA, DocumentTestData.DecompositionEnvelope("issue-X", DocumentState.Accepted), null, CancellationToken.None);
        await repoB.InsertAsync(
            tenantB, DocumentTestData.DecompositionEnvelope("issue-Y", DocumentState.Accepted), null, CancellationToken.None);

        // B's lineage read for A's issue → empty.
        (await repoB.ListByIssueAsync(tenantB, "issue-X", CancellationToken.None)).Should().BeEmpty();
        // B fetching A's document id → null.
        (await repoB.GetByIdAsync(tenantB, aDoc.Id, CancellationToken.None)).Should().BeNull();
        // A sees only A.
        var aRows = await repoA.ListByIssueAsync(tenantA, "issue-X", CancellationToken.None);
        aRows.Should().ContainSingle().Which.Id.Should().Be(aDoc.Id);
        (await repoA.ListByIssueAsync(tenantA, "issue-Y", CancellationToken.None)).Should().BeEmpty();
    }
}

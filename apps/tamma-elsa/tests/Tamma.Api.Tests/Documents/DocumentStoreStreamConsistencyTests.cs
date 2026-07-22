using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Npgsql;
using Tamma.Core.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-11 (AC7/AC8) — store↔stream consistency. A pre-minted event Guid is
/// appended to <c>domain_events</c> as a <c>DOCUMENT.ACCEPTED</c> row (via
/// <see cref="EventRepository"/>) and stamped onto the store row's
/// <c>correlating_event_id</c>. An auditor can then cross-check store ↔ stream
/// mechanically: the row's linkage resolves to an existing event of the right type
/// whose tags match the row's issue/type. Docker-gated (CI runs it).
/// </summary>
[TestFixture]
public class DocumentStoreStreamConsistencyTests
{
    // DocumentEvents.Accepted (Tamma.Activities.Documents) — consumed here as the
    // correlating_event_id linkage type only.
    private const string DocumentAccepted = "DOCUMENT.ACCEPTED";

    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("document_store_consistency_test")
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
    public async Task AcceptedRow_CorrelatingEventId_ResolvesToMatchingStreamEvent()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));

        var factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        var tc = new DocumentTestData.FakeTenantContext(tenant);
        var docs = new DocumentInstanceRepository(factory, tc);
        var events = new EventRepository(factory, tc);

        const string issueId = "issue-42";
        const string documentType = "decomposition";

        // 1) Pre-mint the transition event id, append the DOCUMENT.ACCEPTED event.
        var eventId = Guid.NewGuid();
        await events.AppendAsync(new DomainEvent
        {
            Id = eventId,
            Type = DocumentAccepted,
            TenantId = tenant,
            Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["issueId"] = issueId,
                ["documentType"] = documentType,
            }),
        });

        // 2) Insert the document + stamp the SAME event id as its acceptance linkage.
        var row = await docs.InsertAsync(
            tenant, DocumentTestData.DecompositionEnvelope(issueId), null, CancellationToken.None);
        var accepted = await docs.SetStatusAsync(
            tenant, row.Id, DocumentInstanceStatus.Accepted, eventId, CancellationToken.None);

        // 3) The row's linkage resolves to the stream event, same type + tags.
        accepted.CorrelatingEventId.Should().Be(eventId);
        var streamEvent = await events.GetByIdAsync(accepted.CorrelatingEventId!.Value);
        streamEvent.Should().NotBeNull();
        streamEvent!.Type.Should().Be(DocumentAccepted);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(streamEvent.Tags)!;
        tags["issueId"].Should().Be(accepted.IssueId);
        tags["documentType"].Should().Be(accepted.DocumentType);
    }
}

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Tests.Documents;
using Tamma.Core.Documents;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Channels;

/// <summary>
/// Story 39-18 (AC4 storage half) — Postgres 17 Testcontainer proof of the outbox
/// repository: enqueue + list-unacked ordering by UUID-v7 id, idempotent per-recipient
/// ack (second ack false, row unchanged), recipient scoping (user A never sees user
/// B's rows), and jsonb payload round-trip through the real column. Docker-gated (CI
/// runs it; the local run without Docker skips at container start).
/// </summary>
[TestFixture]
public class ChannelOutboxRepositoryTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("channel_outbox_test")
            .WithUsername("tamma").WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    private async Task<(ChannelOutboxRepository Repo, Guid Tenant)> NewRepoAsync()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));
        var factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        var cpOpts = new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_baseConnectionString).Options;
        var repo = new ChannelOutboxRepository(factory, new ControlPlaneDbContext(cpOpts));
        return (repo, tenant);
    }

    private static ChannelOutboxMessage Row(Guid tenant, Guid? recipient, string kind, string payload) => new()
    {
        Id = UuidV7.NewGuid(),
        TenantId = tenant,
        Audience = recipient is null ? "orchestrator" : "user",
        RecipientUserId = recipient,
        Kind = kind,
        PayloadJson = payload,
    };

    [Test]
    public async Task Enqueue_ListUnacked_OrderedByUuidV7Id()
    {
        var (repo, tenant) = await NewRepoAsync();
        var a = await repo.EnqueueAsync(Row(tenant, null, "escalation-raised", "{}"));
        var b = await repo.EnqueueAsync(Row(tenant, null, "escalation-raised", "{}"));
        var c = await repo.EnqueueAsync(Row(tenant, null, "escalation-raised", "{}"));

        var listed = await repo.ListUnackedAsync(tenant, "orchestrator", null, 100);

        listed.Select(r => r.Id).Should().ContainInOrder(a.Id, b.Id, c.Id);
        listed.Should().OnlyContain(r => r.Status == "pending");
    }

    [Test]
    public async Task Ack_IsIdempotent_SecondAckFalse_RowUnchanged()
    {
        var (repo, tenant) = await NewRepoAsync();
        var row = await repo.EnqueueAsync(Row(tenant, null, "guidance-query", "{}"));

        (await repo.AckAsync(tenant, row.Id, null)).Should().BeTrue("first ack transitions the row");
        (await repo.AckAsync(tenant, row.Id, null)).Should().BeFalse("acking an acked row is a no-op");

        var remaining = await repo.ListUnackedAsync(tenant, "orchestrator", null, 100);
        remaining.Should().NotContain(r => r.Id == row.Id, "an acked row is no longer unacked");
    }

    [Test]
    public async Task ListUnacked_RecipientScoping_UserANeverSeesUserBRows()
    {
        var (repo, tenant) = await NewRepoAsync();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var rowA = await repo.EnqueueAsync(Row(tenant, userA, "task-assigned", "{}"));
        await repo.EnqueueAsync(Row(tenant, userB, "task-assigned", "{}"));

        var forA = await repo.ListUnackedAsync(tenant, "user", userA, 100);

        forA.Should().ContainSingle().Which.Id.Should().Be(rowA.Id);
        // And B's ack of A's row is refused (per-recipient ack).
        (await repo.AckAsync(tenant, rowA.Id, userB)).Should().BeFalse();
    }

    [Test]
    public async Task PayloadJson_RoundTripsThroughJsonbColumn()
    {
        var (repo, tenant) = await NewRepoAsync();
        const string payload = """{"messageId":"x","nested":{"b":2,"a":1},"list":[3,2,1]}""";
        var row = await repo.EnqueueAsync(Row(tenant, null, "guidance-query", payload));

        var back = (await repo.ListUnackedAsync(tenant, "orchestrator", null, 100)).Single(r => r.Id == row.Id);

        // jsonb re-serializes (whitespace stripped, keys reordered) — compare parsed.
        JsonNode.DeepEquals(JsonNode.Parse(back.PayloadJson), JsonNode.Parse(payload)).Should().BeTrue();
    }

    [Test]
    public async Task ListStale_ReturnsPendingAndOldDelivered()
    {
        var (repo, tenant) = await NewRepoAsync();
        var pending = await repo.EnqueueAsync(Row(tenant, null, "escalation-raised", "{}"));
        var delivered = await repo.EnqueueAsync(Row(tenant, null, "escalation-raised", "{}"));
        await repo.MarkDeliveredAsync(tenant, delivered.Id);

        // Everything delivered before "now + 1min" is stale; pending is always stale.
        var stale = await repo.ListStaleAsync(tenant, DateTime.UtcNow.AddMinutes(1), 100);

        stale.Select(r => r.Id).Should().Contain(new[] { pending.Id, delivered.Id });
    }
}

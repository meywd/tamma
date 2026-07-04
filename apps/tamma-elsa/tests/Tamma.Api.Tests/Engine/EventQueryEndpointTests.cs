using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Story 4-7 (event query API for time-travel) — direct-handler coverage for
/// <see cref="EngineEndpoints.QueryEvents"/> (the keyset-paginated time-travel
/// query at <c>GET /api/engine/events/query</c>). Mirrors
/// <see cref="EngineHistoryEndpointTests"/>: bypass the auth + tenant-binding
/// middleware and drive the real <see cref="EventRepository"/> against the
/// fixture's tenant Postgres container so the JSONB (<c>Tags-&gt;&gt;'…'</c>)
/// predicates + BIGSERIAL cursor exercise the true EF/Postgres path (they are not
/// translatable on EF-InMemory).
///
/// <para>Proves: time-range filter (only in-range events), correlationId/actor
/// (userId)/type (exact + prefix) filters, stable + complete cursor pagination
/// (no dupes/gaps across pages), tenant-scope isolation (tenant A's query never
/// returns tenant B's events — even for an identical correlationId), opt-in total,
/// and fail-loud 400s on an inverted time range / non-positive cursor.</para>
/// </summary>
[TestFixture]
public class EventQueryEndpointTests
{
    private IServiceScope _scope = null!;
    private IEventRepository _events = null!;
    private ITenantDbContextFactory _factory = null!;

    // A fixed base instant so time-range assertions are TZ-independent (explicit
    // UTC kind; the handler is called directly, no HTTP string boundary).
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
        _factory = _scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── time range ──────────────────────────────────────────────────────────

    [Test]
    public async Task QueryEvents_FiltersByTimeRange_ReturnsOnlyInRangeEvents()
    {
        var tenantId = Guid.NewGuid();
        await SeedEventAsync(tenantId, "TEST.TT.EARLY", createdAt: Base);                    // 00:00
        await SeedEventAsync(tenantId, "TEST.TT.MIDDLE", createdAt: Base.AddHours(12));      // 12:00
        await SeedEventAsync(tenantId, "TEST.TT.LATE", createdAt: Base.AddHours(24));        // next day

        // Half-open window [06:00, 18:00) — only the 12:00 event qualifies.
        var doc = await CallOkAsync(
            tenantId,
            from: new DateTimeOffset(Base.AddHours(6)),
            to: new DateTimeOffset(Base.AddHours(18)),
            includeTotal: true);

        doc.GetProperty("total").GetInt32().Should().Be(1);
        var types = Types(doc);
        types.Should().ContainSingle().Which.Should().Be("TEST.TT.MIDDLE");
    }

    // ── correlationId ───────────────────────────────────────────────────────

    [Test]
    public async Task QueryEvents_FiltersByCorrelationId()
    {
        var tenantId = Guid.NewGuid();
        await SeedEventAsync(tenantId, "TEST.TT.RUN", correlationId: "corr-A");
        await SeedEventAsync(tenantId, "TEST.TT.RUN", correlationId: "corr-A");
        await SeedEventAsync(tenantId, "TEST.TT.RUN", correlationId: "corr-B");

        var doc = await CallOkAsync(tenantId, correlationId: "corr-A", includeTotal: true);

        doc.GetProperty("total").GetInt32().Should().Be(2);
        CorrelationIds(doc).Should().OnlyContain(c => c == "corr-A");
    }

    // ── actor (Tags.userId) ─────────────────────────────────────────────────

    [Test]
    public async Task QueryEvents_FiltersByActor()
    {
        var tenantId = Guid.NewGuid();
        var alice = Guid.NewGuid().ToString();
        var bob = Guid.NewGuid().ToString();
        await SeedEventAsync(tenantId, "TEST.TT.ACT", actor: alice);
        await SeedEventAsync(tenantId, "TEST.TT.ACT", actor: bob);
        await SeedEventAsync(tenantId, "TEST.TT.ACT", actor: bob);

        var doc = await CallOkAsync(tenantId, actor: bob, includeTotal: true);

        doc.GetProperty("total").GetInt32().Should().Be(2);
        Actors(doc).Should().OnlyContain(a => a == bob);
    }

    // ── type: exact + prefix ────────────────────────────────────────────────

    [Test]
    public async Task QueryEvents_FiltersByType_ExactByDefault()
    {
        var tenantId = Guid.NewGuid();
        await SeedEventAsync(tenantId, "AGENT.TASK.SUCCESS");
        await SeedEventAsync(tenantId, "AGENT.TASK.FAILED");
        await SeedEventAsync(tenantId, "AGENT.TOOL_CALL.SUCCESS");

        var doc = await CallOkAsync(tenantId, type: "AGENT.TASK.SUCCESS", includeTotal: true);

        doc.GetProperty("total").GetInt32().Should().Be(1);
        Types(doc).Should().ContainSingle().Which.Should().Be("AGENT.TASK.SUCCESS");
    }

    [Test]
    public async Task QueryEvents_FiltersByType_Prefix()
    {
        var tenantId = Guid.NewGuid();
        await SeedEventAsync(tenantId, "AGENT.TASK.SUCCESS");
        await SeedEventAsync(tenantId, "AGENT.TASK.FAILED");
        await SeedEventAsync(tenantId, "AGENT.TOOL_CALL.SUCCESS");

        var doc = await CallOkAsync(tenantId, type: "AGENT.TASK", prefix: true, includeTotal: true);

        doc.GetProperty("total").GetInt32().Should().Be(2);
        Types(doc).Should().OnlyContain(t => t!.StartsWith("AGENT.TASK", StringComparison.Ordinal));
    }

    // ── cursor pagination: stable + complete ────────────────────────────────

    [Test]
    public async Task QueryEvents_CursorPagination_IsStableAndComplete_NoDupesOrGaps()
    {
        var tenantId = Guid.NewGuid();
        // 5 events sharing the SAME CreatedAt millisecond — only SequenceNumber
        // disambiguates the order, so this proves the keyset cursor is immune to
        // same-millisecond collisions.
        var ts = Base.AddHours(3);
        for (var i = 0; i < 5; i++)
            await SeedEventAsync(tenantId, "TEST.TT.PAGE", createdAt: ts);

        var page1 = await CallOkAsync(tenantId, limit: 2, includeTotal: true);
        page1.GetProperty("total").GetInt32().Should().Be(5);
        page1.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        page1.GetProperty("events").GetArrayLength().Should().Be(2);
        var cursor1 = page1.GetProperty("nextCursor").GetInt64();

        var page2 = await CallOkAsync(tenantId, limit: 2, cursor: cursor1);
        page2.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        page2.GetProperty("events").GetArrayLength().Should().Be(2);
        var cursor2 = page2.GetProperty("nextCursor").GetInt64();

        var page3 = await CallOkAsync(tenantId, limit: 2, cursor: cursor2);
        page3.GetProperty("events").GetArrayLength().Should().Be(1);
        page3.GetProperty("hasMore").GetBoolean().Should().BeFalse(
            "the final partial page signals no further pages");
        page3.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        // No dupes / no gaps across the three pages: 5 distinct, descending seqs.
        var seqs = Seqs(page1).Concat(Seqs(page2)).Concat(Seqs(page3)).ToList();
        seqs.Should().OnlyHaveUniqueItems();
        seqs.Should().BeInDescendingOrder();
        seqs.Should().HaveCount(5);
    }

    // ── tenant-scope isolation ──────────────────────────────────────────────

    [Test]
    public async Task QueryEvents_DoesNotLeakOtherTenantEvents_EvenForSameCorrelationId()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        // BOTH tenants stamp the IDENTICAL correlationId — a leak would surface here.
        await SeedEventAsync(tenantA, "TEST.TT.SHARED", correlationId: "shared-corr");
        await SeedEventAsync(tenantA, "TEST.TT.SHARED", correlationId: "shared-corr");
        await SeedEventAsync(tenantB, "TEST.TT.SHARED", correlationId: "shared-corr");
        await SeedEventAsync(tenantB, "TEST.TT.SHARED", correlationId: "shared-corr");
        await SeedEventAsync(tenantB, "TEST.TT.SHARED", correlationId: "shared-corr");

        var docA = await CallOkAsync(tenantA, correlationId: "shared-corr", includeTotal: true);
        docA.GetProperty("total").GetInt32().Should().Be(2,
            "tenant A's query must see only tenant A's rows, even with a shared correlationId");
        docA.GetProperty("events").GetArrayLength().Should().Be(2);

        var docB = await CallOkAsync(tenantB, correlationId: "shared-corr", includeTotal: true);
        docB.GetProperty("total").GetInt32().Should().Be(3);
    }

    [Test]
    public async Task QueryEvents_NoTenant_ReturnsEmptyPage_NeverLeaks()
    {
        var otherTenant = Guid.NewGuid();
        await SeedEventAsync(otherTenant, "TEST.TT.PRIVATE");

        var doc = await CallOkAsync(tenantId: null, includeTotal: true);

        doc.GetProperty("events").GetArrayLength().Should().Be(0);
        doc.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        doc.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── opt-in total ────────────────────────────────────────────────────────

    [Test]
    public async Task QueryEvents_Total_IsNullUnlessIncludeTotalRequested()
    {
        var tenantId = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            await SeedEventAsync(tenantId, "TEST.TT.COUNT");

        var noTotal = await CallOkAsync(tenantId, limit: 2);
        noTotal.GetProperty("total").ValueKind.Should().Be(JsonValueKind.Null,
            "the unbounded COUNT(*) is opt-in; null means 'not computed', not zero");
        noTotal.GetProperty("events").GetArrayLength().Should().Be(2,
            "the page still fills; pagination uses the cursor, not the total");

        var withTotal = await CallOkAsync(tenantId, limit: 2, includeTotal: true);
        withTotal.GetProperty("total").GetInt32().Should().Be(3);
    }

    // ── fail-loud 400s ──────────────────────────────────────────────────────

    [Test]
    public async Task QueryEvents_InvertedTimeRange_Returns400()
    {
        var (status, _) = await CallAsync(
            Guid.NewGuid(),
            from: new DateTimeOffset(Base.AddHours(18)),
            to: new DateTimeOffset(Base.AddHours(6)));

        status.Should().Be(StatusCodes.Status400BadRequest,
            "an inverted window must 400, not silently full-scan");
    }

    [Test]
    public async Task QueryEvents_NonPositiveCursor_Returns400()
    {
        var (zero, _) = await CallAsync(Guid.NewGuid(), cursor: 0);
        zero.Should().Be(StatusCodes.Status400BadRequest);

        var (neg, _) = await CallAsync(Guid.NewGuid(), cursor: -5);
        neg.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── repository guard: no cross-tenant read path ─────────────────────────

    [Test]
    public void QueryEvents_Repository_EmptyTenant_Throws_NoCrossTenantReadPath()
    {
        Func<Task> act = () => _events.QueryEventsAsync(
            Guid.Empty, null, false, null, null, null, null, null, 50);
        act.Should().ThrowAsync<NotSupportedException>();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task EnsureTenantProvisionedAsync(Guid tenantId)
    {
        var cp = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        if (!await cp.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            cp.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = $"Test {tenantId:N}",
                Slug = $"t-{tenantId:N}",
                Plan = "free"
            });
            await cp.SaveChangesAsync();
        }
        await ApiTestFixture.ProvisionTenantAsync(tenantId);
    }

    /// <summary>
    /// Seed one event directly through the tenant DbContext (NOT
    /// <see cref="IEventRepository.AppendAsync"/>, which stamps
    /// <c>CreatedAt = UtcNow</c>) so the test controls the timestamp for the
    /// time-range assertions and the Tags for the correlationId/actor filters.
    /// </summary>
    private async Task SeedEventAsync(
        Guid tenantId, string type,
        string? correlationId = null, string? actor = null,
        DateTime? createdAt = null, int? issueNumber = null)
    {
        await EnsureTenantProvisionedAsync(tenantId);

        var tags = new Dictionary<string, string?>();
        if (correlationId is not null) tags["correlationId"] = correlationId;
        if (actor is not null) tags["userId"] = actor;

        await using var db = await _factory.CreateAsync(tenantId);
        db.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            IssueNumber = issueNumber,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = "{\"workflowVersion\":\"1.0.0\",\"eventSource\":\"system\"}",
            Data = "{}",
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(int Status, JsonElement Body)> CallAsync(
        Guid? tenantId,
        string? type = null,
        bool? prefix = null,
        string? correlationId = null,
        string? actor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        long? cursor = null,
        int? limit = null,
        bool? includeTotal = null)
    {
        var tc = new TenantContext();
        if (tenantId is Guid tid) tc.SetTenantId(tid);

        var result = await EngineEndpoints.QueryEvents(
            _events, tc, type, prefix, correlationId, actor, from, to, cursor, limit, includeTotal);

        var ctx = new DefaultHttpContext { RequestServices = ApiTestFixture.Factory.Services };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = ctx.Response.Body.Length == 0
            ? default
            : JsonDocument.Parse(ctx.Response.Body).RootElement.Clone();
        return (ctx.Response.StatusCode, body);
    }

    private async Task<JsonElement> CallOkAsync(
        Guid? tenantId,
        string? type = null,
        bool? prefix = null,
        string? correlationId = null,
        string? actor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        long? cursor = null,
        int? limit = null,
        bool? includeTotal = null)
    {
        var (status, body) = await CallAsync(
            tenantId, type, prefix, correlationId, actor, from, to, cursor, limit, includeTotal);
        status.Should().Be(StatusCodes.Status200OK);
        return body;
    }

    private static List<string?> Types(JsonElement doc)
        => doc.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("type").GetString()).ToList();

    private static List<string?> CorrelationIds(JsonElement doc)
        => doc.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("tags").GetProperty("correlationId").GetString()).ToList();

    private static List<string?> Actors(JsonElement doc)
        => doc.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("tags").GetProperty("userId").GetString()).ToList();

    private static List<long> Seqs(JsonElement doc)
        => doc.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("sequenceNumber").GetInt64()).ToList();
}

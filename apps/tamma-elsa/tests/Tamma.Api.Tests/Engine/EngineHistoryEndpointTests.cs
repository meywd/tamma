using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Story 4-7 (event-query API for time-travel) — direct-handler coverage
/// for <see cref="EngineEndpoints.GetHistory"/>. Mirrors the
/// handler-direct pattern used by <c>TenantAuditEndpointTests</c>: bypass
/// the auth + tenant-binding middleware so we can isolate the
/// in-handler invariants (pagination, filter-by-type, filter-by-issue,
/// tenant scoping).
///
/// <para>The tenant id is supplied through a stub
/// <see cref="ITenantContext"/> rather than the JWT pipeline. The repo
/// is the real <see cref="EventRepository"/> wired against the test
/// fixture's tenant Postgres container, so we exercise the real EF
/// query path rather than an in-memory mock.</para>
/// </summary>
[TestFixture]
public class EngineHistoryEndpointTests
{
    private IServiceScope _scope = null!;
    private IEventRepository _events = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    [Test]
    public async Task GetHistory_PaginatesForward_WithHasMoreAndNextOffset()
    {
        var tenantId = Guid.NewGuid();
        await SeedEventsAsync(tenantId, count: 5, type: "TEST.HISTORY.EVENT");

        var page1 = await CallAndReadAsync(tenantId, limit: 2, offset: 0);
        page1.GetProperty("total").GetInt32().Should().Be(5);
        page1.GetProperty("limit").GetInt32().Should().Be(2);
        page1.GetProperty("offset").GetInt32().Should().Be(0);
        page1.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        page1.GetProperty("nextOffset").GetInt32().Should().Be(2);
        page1.GetProperty("events").GetArrayLength().Should().Be(2);

        var page3 = await CallAndReadAsync(tenantId, limit: 2, offset: 4);
        page3.GetProperty("total").GetInt32().Should().Be(5);
        page3.GetProperty("offset").GetInt32().Should().Be(4);
        page3.GetProperty("hasMore").GetBoolean().Should().BeFalse(
            "the last partial page must signal no further pages");
        page3.GetProperty("nextOffset").ValueKind.Should().Be(JsonValueKind.Null);
        page3.GetProperty("events").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task GetHistory_FiltersByEventType()
    {
        var tenantId = Guid.NewGuid();
        await SeedEventsAsync(tenantId, count: 3, type: "TEST.HISTORY.WANTED");
        await SeedEventsAsync(tenantId, count: 4, type: "TEST.HISTORY.OTHER");

        var doc = await CallAndReadAsync(
            tenantId, limit: 50, offset: 0, eventType: "TEST.HISTORY.WANTED");

        doc.GetProperty("total").GetInt32().Should().Be(3,
            "exact-match eventType filter must exclude TEST.HISTORY.OTHER rows");
        var types = doc.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("type").GetString()).ToList();
        types.Should().OnlyContain(t => t == "TEST.HISTORY.WANTED");
    }

    [Test]
    public async Task GetHistory_FiltersByIssueNumber()
    {
        var tenantId = Guid.NewGuid();
        await SeedEventsAsync(tenantId, count: 2, type: "TEST.HISTORY.EVENT", issueNumber: 42);
        await SeedEventsAsync(tenantId, count: 3, type: "TEST.HISTORY.EVENT", issueNumber: 99);

        var doc = await CallAndReadAsync(
            tenantId, limit: 50, offset: 0, issueNumber: 42);

        doc.GetProperty("total").GetInt32().Should().Be(2);
        var issues = doc.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("issueNumber").GetInt32()).ToList();
        issues.Should().OnlyContain(n => n == 42);
    }

    [Test]
    public async Task GetHistory_DoesNotLeakOtherTenantEvents()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedEventsAsync(tenantA, count: 2, type: "TEST.HISTORY.MINE");
        await SeedEventsAsync(tenantB, count: 5, type: "TEST.HISTORY.MINE");

        var doc = await CallAndReadAsync(
            tenantA, limit: 50, offset: 0, eventType: "TEST.HISTORY.MINE");

        doc.GetProperty("total").GetInt32().Should().Be(2,
            "tenant-scoped read must exclude rows belonging to a different tenant");
        doc.GetProperty("events").GetArrayLength().Should().Be(2);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task SeedEventsAsync(
        Guid tenantId, int count, string type, int? issueNumber = null)
    {
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = type,
                TenantId = tenantId,
                IssueNumber = issueNumber,
                Tags = "{}",
                Metadata = "{\"workflowVersion\":\"1.0.0\",\"eventSource\":\"system\"}",
                Data = "{}",
                // Stagger so OrderByDescending produces a stable order.
                CreatedAt = baseTime.AddMilliseconds(i * 10),
            });
        }
    }

    private async Task<JsonElement> CallAndReadAsync(
        Guid tenantId,
        int? limit,
        int? offset,
        string? eventType = null,
        int? issueNumber = null)
    {
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);

        var result = await EngineEndpoints.GetHistory(
            _events, tc, limit, offset, eventType, issueNumber);

        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        return JsonDocument.Parse(ctx.Response.Body).RootElement.Clone();
    }
}

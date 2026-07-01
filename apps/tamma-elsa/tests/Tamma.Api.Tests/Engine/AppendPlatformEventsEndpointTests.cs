using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Dtos.Engine;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Durable engine→platform_events callback — direct-handler coverage for
/// <see cref="EngineEndpoints.AppendPlatformEvents"/>. Mirrors the handler-direct
/// pattern from <see cref="EngineAppendEventsEndpointTests"/>: bypass the auth +
/// tenant-binding middleware, supply <see cref="IPlatformEventPublisher"/> from the
/// real DI container, and assert against <see cref="ControlPlaneDbContext.PlatformEvents"/>
/// on the fixture's control-plane Postgres container (real EF write path, not a mock).
/// </summary>
[TestFixture]
public class AppendPlatformEventsEndpointTests
{
    private IServiceScope _scope = null!;
    private IPlatformEventPublisher _publisher = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _publisher = _scope.ServiceProvider.GetRequiredService<IPlatformEventPublisher>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    [Test]
    public async Task AppendPlatformEvents_PersistsRowToPlatformEvents_AndIsIdempotent()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var req = BuildRequest(new[]
        {
            new PlatformEventRecord(
                id, "TENANT.DELETED.SUCCESS", tenantId, null,
                new Dictionary<string, string?> { ["source"] = "cleanup-workflow" },
                null, null, null),
        });

        var result = await EngineEndpoints.AppendPlatformEvents(req, _publisher);
        var (status, body) = await ReadAsync(result);

        status.Should().Be(StatusCodes.Status201Created);
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("persisted").GetInt32().Should().Be(1);

        // Idempotent re-POST of the same id → dedup no-op, still 201, still one row.
        var result2 = await EngineEndpoints.AppendPlatformEvents(req, _publisher);
        (await ReadAsync(result2)).Status.Should().Be(StatusCodes.Status201Created,
            "a dedup no-op counts as success — engine drain advances cursor");

        await using var assertScope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var cp = assertScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await cp.PlatformEvents.Where(e => e.Id == id).ToListAsync();
        rows.Should().HaveCount(1, "idempotent append must not duplicate rows");
        rows[0].Type.Should().Be("TENANT.DELETED.SUCCESS");
        rows[0].TenantId.Should().Be(tenantId);

        // Tags must be persisted as serialized JSON.
        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(rows[0].Tags)!;
        tags["source"].Should().Be("cleanup-workflow");
    }

    [Test]
    public async Task AppendPlatformEvents_NullableTenantId_PersistedWithNullTenant()
    {
        // platform_events is cross-tenant; TenantId=null is valid (e.g. orchestrator ticks).
        var id = Guid.NewGuid();
        var req = BuildRequest(new[]
        {
            new PlatformEventRecord(id, "ORCHESTRATOR.TICK.COMPLETED", null, null, null, null, null, null),
        });

        var result = await EngineEndpoints.AppendPlatformEvents(req, _publisher);
        (await ReadAsync(result)).Status.Should().Be(StatusCodes.Status201Created);

        await using var assertScope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var cp = assertScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await cp.PlatformEvents.SingleAsync(e => e.Id == id);
        row.TenantId.Should().BeNull();
        row.Type.Should().Be("ORCHESTRATOR.TICK.COMPLETED");
    }

    [Test]
    public async Task AppendPlatformEvents_EmptyBatch_ReturnsBadRequest()
    {
        var result = await EngineEndpoints.AppendPlatformEvents(
            new AppendPlatformEventsRequest(new List<PlatformEventRecord>()), _publisher);
        (await ReadAsync(result)).Status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task AppendPlatformEvents_PartialFailure_PersistsValid_ReportsInvalid()
    {
        var tenantId = Guid.NewGuid();
        var req = BuildRequest(new[]
        {
            new PlatformEventRecord(Guid.NewGuid(), "TENANT.PROVISIONED.SUCCESS", tenantId, null, null, null, null, null),
            new PlatformEventRecord(Guid.NewGuid(), "", tenantId, null, null, null, null, null), // empty type → per-event reject
            new PlatformEventRecord(Guid.NewGuid(), "TENANT.DELETED.SUCCESS", tenantId, null, null, null, null, null),
        });

        var result = await EngineEndpoints.AppendPlatformEvents(req, _publisher);
        var (status, body) = await ReadAsync(result);

        status.Should().Be(StatusCodes.Status502BadGateway,
            "a partial batch failure must NOT 2xx so the engine drain retries (cursor stays put)");
        body.GetProperty("error").GetString().Should().Be("partial_append_failure");
        body.GetProperty("persisted").GetInt32().Should().Be(2);
        body.GetProperty("failed").GetInt32().Should().Be(1);
    }

    [Test]
    public async Task AppendPlatformEvents_GuidEmptyId_ServerMintsNewId()
    {
        var req = BuildRequest(new[]
        {
            new PlatformEventRecord(Guid.Empty, "TENANT.CREATED.SUCCESS", Guid.NewGuid(), null, null, null, null, null),
        });

        var result = await EngineEndpoints.AppendPlatformEvents(req, _publisher);
        var (status, body) = await ReadAsync(result);

        status.Should().Be(StatusCodes.Status201Created);
        body.GetProperty("persisted").GetInt32().Should().Be(1);

        // Row should exist with a server-minted id (non-Empty).
        await using var assertScope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var cp = assertScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await cp.PlatformEvents.Where(e => e.Type == "TENANT.CREATED.SUCCESS").ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].Id.Should().NotBe(Guid.Empty);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AppendPlatformEventsRequest BuildRequest(IEnumerable<PlatformEventRecord> records) =>
        new(records.ToList());

    private static async Task<(int Status, JsonElement Body)> ReadAsync(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = ApiTestFixture.Factory.Services };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = ctx.Response.Body.Length == 0
            ? JsonDocument.Parse("null").RootElement
            : JsonDocument.Parse(ctx.Response.Body).RootElement.Clone();
        return (ctx.Response.StatusCode, body);
    }
}

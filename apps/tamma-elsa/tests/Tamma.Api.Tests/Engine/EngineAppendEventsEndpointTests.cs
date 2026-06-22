using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Dtos.Engine;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Durable engine→domain_events DCB-event persistence — direct-handler
/// coverage for <see cref="EngineEndpoints.AppendEvents"/>. Mirrors the
/// handler-direct pattern from <see cref="EngineHistoryEndpointTests"/>:
/// bypass the auth + tenant-binding middleware, supply the tenant id through
/// a stub <see cref="ITenantContext"/>, and assert against the real
/// <see cref="EventRepository"/> wired to the fixture's tenant Postgres
/// container (real EF write path, not a mock).
/// </summary>
[TestFixture]
public class EngineAppendEventsEndpointTests
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
    public async Task AppendEvents_PersistsBatch_ToTenantDomainEvents()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantProvisionedAsync(tenantId);

        var req = new AppendEventsRequest(new List<EngineEventRecord>
        {
            BuildRecord("ADL.CONFIG.INIT.STARTED", status: "started", workflowInstanceId: "wf-1", activityId: "act-1"),
            BuildRecord("ADL.CONFIG.INIT.COMPLETED", status: "success", durationMs: 12.5, workflowInstanceId: "wf-1", activityId: "act-1"),
            BuildRecord("CODE.GENERATED.SUCCESS", status: "success", issueNumber: 42, workflowInstanceId: "wf-1"),
        });

        var result = await EngineEndpoints.AppendEvents(req, _events, TenantCtx(tenantId));
        var (status, body) = await ReadAsync(result);

        status.Should().Be(StatusCodes.Status201Created);
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("persisted").GetInt32().Should().Be(3);

        var stored = await _events.QueryAsync(tenantId, null, null, 50);
        stored.Should().HaveCount(3);
        stored.Select(e => e.Type).Should().BeEquivalentTo(new[]
        {
            "ADL.CONFIG.INIT.STARTED",
            "ADL.CONFIG.INIT.COMPLETED",
            "CODE.GENERATED.SUCCESS",
        });
        stored.Should().OnlyContain(e => e.TenantId == tenantId);

        var issueEvent = stored.Single(e => e.Type == "CODE.GENERATED.SUCCESS");
        issueEvent.IssueNumber.Should().Be(42);

        // Workflow/activity identifiers + tenant land in Tags for time-travel.
        var initTags = JsonSerializer.Deserialize<Dictionary<string, string?>>(
            stored.Single(e => e.Type == "ADL.CONFIG.INIT.STARTED").Tags)!;
        initTags["workflowInstanceId"].Should().Be("wf-1");
        initTags["activityId"].Should().Be("act-1");
        initTags["tenantId"].Should().Be(tenantId.ToString());
    }

    [Test]
    public async Task AppendEvents_PreservesEventData()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantProvisionedAsync(tenantId);

        var dataJson = JsonDocument.Parse("""{"filesChanged":["src/foo.ts"],"count":3}""").RootElement;
        var req = new AppendEventsRequest(new List<EngineEventRecord>
        {
            BuildRecord("CODE.GENERATED.SUCCESS", data: dataJson),
        });

        var result = await EngineEndpoints.AppendEvents(req, _events, TenantCtx(tenantId));
        (await ReadAsync(result)).Status.Should().Be(StatusCodes.Status201Created);

        var stored = (await _events.QueryAsync(tenantId, null, null, 1)).Single();
        var data = JsonDocument.Parse(stored.Data).RootElement;
        data.GetProperty("count").GetInt32().Should().Be(3);
        data.GetProperty("filesChanged").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task AppendEvents_PartialFailure_PersistsValid_ReportsInvalid()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantProvisionedAsync(tenantId);

        var req = new AppendEventsRequest(new List<EngineEventRecord>
        {
            BuildRecord("VALID.ONE"),
            BuildRecord(""),                 // empty eventType — rejected per-event
            BuildRecord("VALID.TWO"),
        });

        var result = await EngineEndpoints.AppendEvents(req, _events, TenantCtx(tenantId));
        var (status, body) = await ReadAsync(result);

        status.Should().Be(StatusCodes.Status502BadGateway,
            "a partial-batch failure must NOT 2xx so the engine drain retries (cursor stays put)");
        body.GetProperty("error").GetString().Should().Be("partial_append_failure");
        body.GetProperty("persisted").GetInt32().Should().Be(2);
        body.GetProperty("failed").GetInt32().Should().Be(1);

        var stored = await _events.QueryAsync(tenantId, null, null, 50);
        stored.Select(e => e.Type).Should().BeEquivalentTo(new[] { "VALID.ONE", "VALID.TWO" });
    }

    [Test]
    public async Task AppendEvents_EmptyBatch_ReturnsBadRequest()
    {
        var result = await EngineEndpoints.AppendEvents(
            new AppendEventsRequest(new List<EngineEventRecord>()),
            _events, TenantCtx(Guid.NewGuid()));
        (await ReadAsync(result)).Status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task AppendEvents_RetryAfterMidBatchFailure_DoesNotDuplicate()
    {
        // C2: a mid-batch failure makes the engine drain re-POST the FULL batch
        // (cursor stays put). The events that DID persist on the first attempt
        // carry a stable per-event id, so the idempotent append must treat the
        // re-send as a no-op — NO duplicate audit rows.
        var tenantId = Guid.NewGuid();
        await EnsureTenantProvisionedAsync(tenantId);

        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        // First attempt: event index 2 has an empty eventType — rejected
        // per-event, so the handler returns 502 (partial_append_failure). The 4
        // VALID events (indices 0,1,3,4) ARE persisted with their stable ids.
        var first = new AppendEventsRequest(new List<EngineEventRecord>
        {
            BuildRecord("EVT.ZERO", id: ids[0]),
            BuildRecord("EVT.ONE", id: ids[1]),
            BuildRecord("", id: ids[2]),            // bad — forces a mid-batch failure
            BuildRecord("EVT.THREE", id: ids[3]),
            BuildRecord("EVT.FOUR", id: ids[4]),
        });

        var firstResult = await EngineEndpoints.AppendEvents(first, _events, TenantCtx(tenantId));
        (await ReadAsync(firstResult)).Status.Should().Be(StatusCodes.Status502BadGateway);
        (await _events.QueryAsync(tenantId, null, null, 50)).Should().HaveCount(4,
            "the 4 valid events persist; only the empty-type one is rejected");

        // Retry: the engine re-sends the SAME batch (same stable ids). The bad
        // record is fixed (now a valid type) so all 5 are valid this time.
        var retry = new AppendEventsRequest(new List<EngineEventRecord>
        {
            BuildRecord("EVT.ZERO", id: ids[0]),
            BuildRecord("EVT.ONE", id: ids[1]),
            BuildRecord("EVT.TWO", id: ids[2]),     // the previously-bad one, now fixed
            BuildRecord("EVT.THREE", id: ids[3]),
            BuildRecord("EVT.FOUR", id: ids[4]),
        });

        var retryResult = await EngineEndpoints.AppendEvents(retry, _events, TenantCtx(tenantId));
        (await ReadAsync(retryResult)).Status.Should().Be(StatusCodes.Status201Created);

        var stored = await _events.QueryAsync(tenantId, null, null, 50);
        stored.Should().HaveCount(5, "the 4 re-sent events must NOT duplicate; only EVT.TWO is newly added");
        stored.Select(e => e.Id).Should().BeEquivalentTo(ids, "every row keeps its stable engine-minted id");
        stored.Select(e => e.Type).Should().BeEquivalentTo(new[]
        {
            "EVT.ZERO", "EVT.ONE", "EVT.TWO", "EVT.THREE", "EVT.FOUR",
        });
    }

    [Test]
    public async Task AppendEvents_DoesNotLeakAcrossTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await EnsureTenantProvisionedAsync(tenantA);
        await EnsureTenantProvisionedAsync(tenantB);

        await EngineEndpoints.AppendEvents(
            new AppendEventsRequest(new List<EngineEventRecord> { BuildRecord("TENANT.A.EVENT") }),
            _events, TenantCtx(tenantA));
        await EngineEndpoints.AppendEvents(
            new AppendEventsRequest(new List<EngineEventRecord>
            {
                BuildRecord("TENANT.B.EVENT"),
                BuildRecord("TENANT.B.EVENT2"),
            }),
            _events, TenantCtx(tenantB));

        (await _events.QueryAsync(tenantA, null, null, 50)).Should().HaveCount(1);
        (await _events.QueryAsync(tenantB, null, null, 50)).Should().HaveCount(2);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static EngineEventRecord BuildRecord(
        string eventType,
        string? status = "success",
        string? error = null,
        double? durationMs = null,
        string? activityId = null,
        string? activityName = null,
        string? workflowInstanceId = null,
        int? issueNumber = null,
        JsonElement? data = null,
        Dictionary<string, string?>? tags = null,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), eventType, status, error, DateTime.UtcNow, durationMs,
            activityId, activityName, workflowInstanceId, issueNumber, data, tags);

    private static ITenantContext TenantCtx(Guid tenantId)
    {
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        return tc;
    }

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

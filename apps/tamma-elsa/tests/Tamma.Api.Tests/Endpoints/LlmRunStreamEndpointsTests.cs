using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Streaming;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Endpoints;

/// <summary>
/// Story 32-23 (AC1/AC2/AC8) — the streaming run tap endpoint. Pumps the static
/// handler with a hand-rolled <see cref="HttpContext"/> (the same idiom as
/// <c>AdminTenantEventsSseLoopTests</c>) to lock the SaaS ownership guard
/// (cross-tenant ⇒ 404), the SSE protocol (headers, clean <c>event: end</c> close
/// on <c>final</c>), single-user "any local run", and <c>?replay=true</c> catch-up.
///
/// <para>401 for missing/invalid auth is enforced by the route policy
/// (<c>.RequireAuthorization("AuthenticatedAny")</c>) BEFORE the handler runs —
/// the same JWT+ApiKey pipeline covered by the auth suite — so it is not
/// re-asserted at the handler level here.</para>
/// </summary>
[TestFixture]
public class LlmRunStreamEndpointsTests
{
    [Test]
    public async Task EmptyCorrelationId_Returns400()
    {
        var http = BuildContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await LlmRunStreamEndpoints.StreamRun(
            "  ", new LlmRunStreamBus(), Tenant(null), Mode(TammaMode.SingleUser),
            Repo(), NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Saas_ForeignCorrelationId_Returns404_NoStream()
    {
        var http = BuildContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // The caller's tenant store has NO event carrying this correlationId — the
        // run belongs to another tenant. No cross-tenant existence oracle ⇒ 404.
        await LlmRunStreamEndpoints.StreamRun(
            "corr-foreign", new LlmRunStreamBus(), Tenant(Guid.NewGuid()),
            Mode(TammaMode.SaaS), Repo(/* empty */), NullLoggerFactory.Instance,
            TimeProvider.System, http, cts.Token);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        http.Response.ContentType.Should().NotBe("text/event-stream", "the SSE stream never opened");
    }

    [Test]
    public async Task Saas_OwnedRun_Streams_LiveFrames_And_ClosesOnFinal()
    {
        var tenantId = Guid.NewGuid();
        var http = BuildContext();
        var bus = new LlmRunStreamBus();

        // The caller's own tenant store carries the run's AGENT.RUN.STARTED —
        // ownership passes.
        var repo = Repo(RunEvent(tenantId, "corr-owned", "AGENT.RUN.STARTED", seq: 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LlmRunStreamEndpoints.StreamRun(
            "corr-owned", bus, Tenant(tenantId), Mode(TammaMode.SaaS), repo,
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        await WaitUntilAsync(() => bus.SubscriberCount("corr-owned") > 0, cts.Token);
        await bus.PublishAsync("corr-owned",
            new RunStreamFrame(RunStreamFrameType.ToolCall, "corr-owned", 0, new { toolName = "file_read", toolCallId = "c1", turn = 0L }));
        await bus.PublishAsync("corr-owned",
            new RunStreamFrame(RunStreamFrameType.Final, "corr-owned", 0, new { success = true }));

        await task;

        var body = ReadBody(http);
        http.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");
        http.Response.ContentType.Should().Be("text/event-stream");
        body.Should().Contain("event: tool_call");
        body.Should().Contain("\"toolName\":\"file_read\"");
        body.Should().Contain("event: final");
        body.Should().Contain("event: end");
        body.Should().Contain("\"reason\":\"run_complete\"", "the tap closes cleanly on the final frame");
    }

    [Test]
    public async Task SingleUser_AnyLocalRun_Streams_WithoutOwnershipCheck()
    {
        var http = BuildContext();
        var bus = new LlmRunStreamBus();

        // Single-user: tenantId is null and the ownership guard is skipped — the
        // sole user taps any local run. The repo is empty and never consulted.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LlmRunStreamEndpoints.StreamRun(
            "corr-local", bus, Tenant(null), Mode(TammaMode.SingleUser), Repo(),
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        await WaitUntilAsync(() => bus.SubscriberCount("corr-local") > 0, cts.Token);
        await bus.PublishAsync("corr-local",
            new RunStreamFrame(RunStreamFrameType.Final, "corr-local", 0, new { success = true }));

        await task;

        var body = ReadBody(http);
        body.Should().Contain("event: final");
        body.Should().Contain("event: end");
    }

    [Test]
    public async Task Replay_CatchesUpFromDcbStore_ThenLiveTail()
    {
        var tenantId = Guid.NewGuid();
        var http = BuildContext(query: "?replay=true");
        var bus = new LlmRunStreamBus();

        var repo = Repo(
            RunEvent(tenantId, "corr-replay", "AGENT.RUN.STARTED", seq: 5),
            RunEvent(tenantId, "corr-replay", "AGENT.RUN.SUCCESS", seq: 9),
            // an event for a DIFFERENT run must NOT be replayed (isolation within the tenant)
            RunEvent(tenantId, "corr-other", "AGENT.RUN.STARTED", seq: 7));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LlmRunStreamEndpoints.StreamRun(
            "corr-replay", bus, Tenant(tenantId), Mode(TammaMode.SaaS), repo,
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        await WaitUntilAsync(() => bus.SubscriberCount("corr-replay") > 0, cts.Token);
        await bus.PublishAsync("corr-replay",
            new RunStreamFrame(RunStreamFrameType.Final, "corr-replay", 0, new { success = true }));

        await task;

        var body = ReadBody(http);
        body.Should().Contain("event: replay", "?replay=true first replays the run's DCB events");
        body.Should().Contain("AGENT.RUN.STARTED");
        body.Should().Contain("AGENT.RUN.SUCCESS");
        body.Should().NotContain("corr-other", "another run's events are never replayed");
        body.Should().Contain("event: final", "then it switches to the live tail");
    }

    // -------------------------------------------------------------------
    // helpers / fakes
    // -------------------------------------------------------------------

    private static DefaultHttpContext BuildContext(string? query = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        if (!string.IsNullOrEmpty(query))
        {
            ctx.Request.QueryString = new QueryString(query);
        }
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    private static FakeTenantContext Tenant(Guid? id) => new(id);
    private static FakeModeProvider Mode(TammaMode mode) => new(mode);
    private static StubEventRepository Repo(params DomainEvent[] events) => new(events);

    private static DomainEvent RunEvent(Guid tenantId, string correlationId, string type, long seq) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        TenantId = tenantId,
        Tags = $"{{\"correlationId\":\"{correlationId}\",\"provider\":\"anthropic\"}}",
        Metadata = "{}",
        Data = "{}",
        CreatedAt = DateTime.UtcNow,
        SequenceNumber = seq,
    };

    private sealed class FakeTenantContext : ITenantContext
    {
        public FakeTenantContext(Guid? id) => TenantId = id;
        public Guid? TenantId { get; private set; }
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FakeModeProvider : ITammaModeProvider
    {
        public FakeModeProvider(TammaMode mode) => Mode = mode;
        public TammaMode Mode { get; }
    }

    private sealed class StubEventRepository : IEventRepository
    {
        private readonly List<DomainEvent> _events;
        public StubEventRepository(IEnumerable<DomainEvent> events) => _events = events.ToList();

        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
        {
            var rows = _events
                .Where(e => e.TenantId == tenantId
                    && (typePrefix is null || e.Type.StartsWith(typePrefix, StringComparison.Ordinal)))
                .OrderByDescending(e => e.SequenceNumber) // repo contract: most-recent first
                .Take(limit)
                .ToList();
            return Task.FromResult(((IReadOnlyList<DomainEvent>)rows, rows.Count));
        }

        public Task<DomainEvent> AppendAsync(DomainEvent evt) => Task.FromResult(evt);
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}

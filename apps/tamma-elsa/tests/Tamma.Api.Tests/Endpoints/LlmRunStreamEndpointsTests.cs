using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
/// <para>Also locks the review fixes: the owner of a busy tenant's run whose
/// <c>AGENT.RUN.STARTED</c> has aged past any recent-N window is STILL recognised as
/// owner (targeted correlationId lookup, not a recent-200 scan) — no false 404, no
/// replay truncation; and a tap on an already-finished run closes PROMPTLY (terminal
/// <c>event: end</c>) instead of parking 30 minutes for a live <c>final</c> that will
/// never come, and never (re)creates a bus topic.</para>
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
    public async Task NonGuidCorrelationId_Returns400_NoStream()
    {
        var http = BuildContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // The run correlationId is the workflow-instance Guid. A non-Guid route value
        // can never match a real run and is rejected before any stream/DB work — this
        // also closes the SSE-line-injection nit (raw value echoed into `: stream-open`).
        await LlmRunStreamEndpoints.StreamRun(
            "not-a-guid\ninjected", new LlmRunStreamBus(), Tenant(null),
            Mode(TammaMode.SingleUser), Repo(), NullLoggerFactory.Instance,
            TimeProvider.System, http, cts.Token);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        http.Response.ContentType.Should().NotBe("text/event-stream", "the SSE stream never opened");
    }

    [Test]
    public async Task Saas_ForeignCorrelationId_Returns404_NoStream()
    {
        var http = BuildContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // The caller's tenant store has NO event carrying this correlationId — the
        // run belongs to another tenant. No cross-tenant existence oracle ⇒ 404.
        await LlmRunStreamEndpoints.StreamRun(
            NewCorr(), new LlmRunStreamBus(), Tenant(Guid.NewGuid()),
            Mode(TammaMode.SaaS), Repo(/* empty */), NullLoggerFactory.Instance,
            TimeProvider.System, http, cts.Token);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        http.Response.ContentType.Should().NotBe("text/event-stream", "the SSE stream never opened");
    }

    [Test]
    public async Task Saas_OwnedRun_Streams_LiveFrames_And_ClosesOnFinal()
    {
        var tenantId = Guid.NewGuid();
        var corr = NewCorr();
        var http = BuildContext();
        var bus = new LlmRunStreamBus();

        // The caller's own tenant store carries the run's AGENT.RUN.STARTED —
        // ownership passes. The run is NOT terminal, so the tap subscribes + live-tails.
        var repo = Repo(RunEvent(tenantId, corr, "AGENT.RUN.STARTED", seq: 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LlmRunStreamEndpoints.StreamRun(
            corr, bus, Tenant(tenantId), Mode(TammaMode.SaaS), repo,
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        await WaitUntilAsync(() => bus.SubscriberCount(corr) > 0, cts.Token);
        await bus.PublishAsync(corr,
            new RunStreamFrame(RunStreamFrameType.ToolCall, corr, 0, new { toolName = "file_read", toolCallId = "c1", turn = 0L }));
        await bus.PublishAsync(corr,
            new RunStreamFrame(RunStreamFrameType.Final, corr, 0, new { success = true }));

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
        var corr = NewCorr();

        // Single-user: tenantId is null and the ownership guard is skipped — the
        // sole user taps any local run. The repo is empty and never consulted.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LlmRunStreamEndpoints.StreamRun(
            corr, bus, Tenant(null), Mode(TammaMode.SingleUser), Repo(),
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        await WaitUntilAsync(() => bus.SubscriberCount(corr) > 0, cts.Token);
        await bus.PublishAsync(corr,
            new RunStreamFrame(RunStreamFrameType.Final, corr, 0, new { success = true }));

        await task;

        var body = ReadBody(http);
        body.Should().Contain("event: final");
        body.Should().Contain("event: end");
    }

    [Test]
    public async Task Replay_CatchesUpFromDcbStore_ThenLiveTail()
    {
        var tenantId = Guid.NewGuid();
        var corr = NewCorr();
        var other = NewCorr();
        var http = BuildContext(query: "?replay=true");
        var bus = new LlmRunStreamBus();

        // A LIVE run (STARTED but no terminal event) with ?replay=true: replay the
        // stored STARTED, then switch to the live tail for the published final.
        var repo = Repo(
            RunEvent(tenantId, corr, "AGENT.RUN.STARTED", seq: 5),
            // an event for a DIFFERENT run must NOT be replayed (isolation within the tenant)
            RunEvent(tenantId, other, "AGENT.RUN.STARTED", seq: 7));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LlmRunStreamEndpoints.StreamRun(
            corr, bus, Tenant(tenantId), Mode(TammaMode.SaaS), repo,
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        await WaitUntilAsync(() => bus.SubscriberCount(corr) > 0, cts.Token);
        await bus.PublishAsync(corr,
            new RunStreamFrame(RunStreamFrameType.Final, corr, 0, new { success = true }));

        await task;

        var body = ReadBody(http);
        body.Should().Contain("event: replay", "?replay=true first replays the run's DCB events");
        body.Should().Contain("AGENT.RUN.STARTED");
        body.Should().NotContain(other, "another run's events are never replayed");
        body.Should().Contain("event: final", "then it switches to the live tail");
    }

    // -------------------------------------------------------------------
    // Review fix 1 — owner false-404 + replay truncation on a busy tenant
    // -------------------------------------------------------------------

    [Test]
    public async Task Saas_OwnerRunOutsideRecent200Window_StillPasses_And_ReplaysOlderFrames()
    {
        var tenantId = Guid.NewGuid();
        var corr = NewCorr();
        var http = BuildContext(query: "?replay=true");
        var bus = new LlmRunStreamBus();

        // The target run's STARTED (seq 1) is buried under 250 NEWER AGENT.* events
        // for OTHER runs of the same tenant. The retired recent-200 window scan would
        // never see seq 1 → the OWNER would get a spurious 404 on their own in-flight
        // run and replay would silently truncate. The targeted correlationId lookup is
        // volume-independent, so ownership passes (200, not 404) and replay returns the
        // older frame regardless of the noise volume.
        var events = new List<DomainEvent> { RunEvent(tenantId, corr, "AGENT.RUN.STARTED", seq: 1) };
        for (var i = 0; i < 250; i++)
        {
            events.Add(RunEvent(tenantId, NewCorr(), "AGENT.TOOL.CALLED", seq: 100 + i));
        }
        var repo = Repo(events.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LlmRunStreamEndpoints.StreamRun(
            corr, bus, Tenant(tenantId), Mode(TammaMode.SaaS), repo,
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);

        await WaitUntilAsync(() => bus.SubscriberCount(corr) > 0, cts.Token);
        await bus.PublishAsync(corr,
            new RunStreamFrame(RunStreamFrameType.Final, corr, 0, new { success = true }));

        await task;

        http.Response.StatusCode.Should().NotBe(StatusCodes.Status404NotFound,
            "the owner must not get a spurious 404 on their own run just because it is old");
        http.Response.ContentType.Should().Be("text/event-stream", "the stream opened for the owner");
        var body = ReadBody(http);
        body.Should().Contain("event: replay", "replay is not truncated by a recent-N window");
        body.Should().Contain("AGENT.RUN.STARTED", "the older stored frame is replayed");
        body.Should().Contain("event: final");
    }

    // -------------------------------------------------------------------
    // Review fix 2 — finished runs close promptly, never park 30 minutes
    // -------------------------------------------------------------------

    [Test]
    public async Task FinishedRun_NonReplay_ClosesPromptly_And_NeverSubscribes()
    {
        var tenantId = Guid.NewGuid();
        var corr = NewCorr();
        var http = BuildContext();
        var bus = new LlmRunStreamBus();

        // A run that already finished: a terminal AGENT.RUN.SUCCESS is persisted and
        // there is NO live topic. Tapping it (no replay) must close PROMPTLY with a
        // terminal event: end — not park until MaxStreamDuration for a live `final`
        // that will never come — and must never (re)create a bus topic.
        var repo = Repo(
            RunEvent(tenantId, corr, "AGENT.RUN.STARTED", seq: 1),
            RunEvent(tenantId, corr, "AGENT.RUN.SUCCESS", seq: 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        await LlmRunStreamEndpoints.StreamRun(
            corr, bus, Tenant(tenantId), Mode(TammaMode.SaaS), repo,
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "a finished run closes at once, nowhere near the 30-minute ceiling");
        var body = ReadBody(http);
        body.Should().Contain("event: end");
        body.Should().Contain("\"reason\":\"already_complete\"");
        bus.TopicCount.Should().Be(0, "a finished run must not (re)create a bus topic");
        bus.HasTopic(corr).Should().BeFalse();
    }

    [Test]
    public async Task FinishedRun_Replay_StreamsStoredFrames_ThenClosesPromptly()
    {
        var tenantId = Guid.NewGuid();
        var corr = NewCorr();
        var http = BuildContext(query: "?replay=true");
        var bus = new LlmRunStreamBus();

        // A finished run with ?replay=true: stream the stored frames, then close —
        // do NOT wait for a live `final`. No live topic is created.
        var repo = Repo(
            RunEvent(tenantId, corr, "AGENT.RUN.STARTED", seq: 1),
            RunEvent(tenantId, corr, "AGENT.RUN.FAILED", seq: 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        await LlmRunStreamEndpoints.StreamRun(
            corr, bus, Tenant(tenantId), Mode(TammaMode.SaaS), repo,
            NullLoggerFactory.Instance, TimeProvider.System, http, cts.Token);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        var body = ReadBody(http);
        body.Should().Contain("event: replay");
        body.Should().Contain("AGENT.RUN.STARTED");
        body.Should().Contain("AGENT.RUN.FAILED");
        body.Should().Contain("event: end");
        body.Should().Contain("\"reason\":\"already_complete\"");
        bus.TopicCount.Should().Be(0, "a finished run must not (re)create a bus topic");
    }

    // -------------------------------------------------------------------
    // helpers / fakes
    // -------------------------------------------------------------------

    private static string NewCorr() => Guid.NewGuid().ToString();

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
            // Real SQL honours limit/offset — model that so a test that reverts the
            // ownership/replay path to a recent-N window scan would (correctly) fail.
            var rows = _events
                .Where(e => e.TenantId == tenantId
                    && (typePrefix is null || e.Type.StartsWith(typePrefix, StringComparison.Ordinal)))
                .OrderByDescending(e => e.SequenceNumber) // repo contract: most-recent first
                .Skip(offset)
                .Take(limit)
                .ToList();
            return Task.FromResult(((IReadOnlyList<DomainEvent>)rows, rows.Count));
        }

        // Review-fix methods — behave like the real tenant-scoped SQL: match by
        // Tags.correlationId across ALL rows (never limited), oldest-first for the list.
        public Task<bool> ExistsByCorrelationIdAsync(Guid tenantId, string correlationId)
        {
            var exists = _events.Any(e => e.TenantId == tenantId
                && CorrelationIdOf(e) == correlationId);
            return Task.FromResult(exists);
        }

        public Task<IReadOnlyList<DomainEvent>> ListByCorrelationIdAsync(Guid tenantId, string correlationId)
        {
            var rows = _events
                .Where(e => e.TenantId == tenantId && CorrelationIdOf(e) == correlationId)
                .OrderBy(e => e.SequenceNumber)
                .ToList();
            return Task.FromResult((IReadOnlyList<DomainEvent>)rows);
        }

        private static string? CorrelationIdOf(DomainEvent e)
        {
            using var doc = JsonDocument.Parse(e.Tags);
            return doc.RootElement.TryGetProperty("correlationId", out var c)
                && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
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

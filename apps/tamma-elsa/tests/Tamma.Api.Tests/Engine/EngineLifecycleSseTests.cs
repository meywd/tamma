using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Engine.Lifecycle;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// HTTP-level integration tests for the engine lifecycle SSE endpoints
/// (finding 012). Verifies:
/// - Content-Type negotiation (<c>text/event-stream</c>).
/// - Initial snapshot frame is written immediately.
/// - Live bus publishes surface on the wire in SSE format.
/// - Client cancellation cleans up the subscription.
/// - The "logs" variant filters out non-workflow / non-task frames.
/// </summary>
[TestFixture]
public class EngineLifecycleSseTests
{
    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    [Test]
    public async Task EventsState_ReturnsSseContentType_AndInitialFrame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ApiTestFixture.CreateClient();

        using var resp = await client.GetAsync(
            "/api/engine/events/state",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        resp.Headers.CacheControl!.NoCache.Should().BeTrue();

        // Read the first SSE frame — it's the initial snapshot from the
        // event repository, written before the pump loop enters. The frame
        // terminator is the "\n\n" sequence.
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        var firstFrame = await ReadNextFrameAsync(stream, cts.Token);

        firstFrame.Should().NotBeNull();
        firstFrame.Should().Contain("event: state");
        firstFrame.Should().Contain("data: ");
    }

    [Test]
    public async Task EventsState_LivePublishedEvent_ArrivesOnWire()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ApiTestFixture.CreateClient();

        using var resp = await client.GetAsync(
            "/api/engine/events/state",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);

        // Drain the initial snapshot frame first so it doesn't race the
        // live publish we're about to do.
        _ = await ReadNextFrameAsync(stream, cts.Token);

        // Give the request pipeline a moment to enter the pump loop (the
        // async iterator needs to call SubscribeAsync and reach the first
        // MoveNextAsync before its channel is registered on the bus).
        var bus = (InMemoryEngineLifecycleBus)ApiTestFixture.Factory.Services
            .GetRequiredService<IEngineLifecycleBus>();
        for (var i = 0; i < 100 && bus.SubscriberCount < 1; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        bus.SubscriberCount.Should().BeGreaterThanOrEqualTo(1,
            "the pump loop should have registered a subscription by now");

        // Publish a synthetic workflow.started with the "empty-GUID" tenant
        // (dev-mode permissive auth leaves TenantContext null; the endpoint
        // falls back to Guid.Empty in that branch).
        await bus.PublishAsync(new EngineLifecycleEvent(
            "workflow.started",
            TenantId: Guid.Empty,
            DateTimeOffset.UtcNow,
            new { instanceId = "deadbeef", status = "started" }));

        var liveFrame = await ReadNextFrameAsync(stream, cts.Token);
        liveFrame.Should().NotBeNull();
        liveFrame.Should().Contain("event: workflow.started");
        liveFrame.Should().Contain("deadbeef");
    }

    [Test]
    public async Task EventsLogs_FiltersOutEngineHeartbeats()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = ApiTestFixture.CreateClient();

        using var resp = await client.GetAsync(
            "/api/engine/events/logs?limit=0",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);

        var bus = (InMemoryEngineLifecycleBus)ApiTestFixture.Factory.Services
            .GetRequiredService<IEngineLifecycleBus>();
        for (var i = 0; i < 100 && bus.SubscriberCount < 1; i++)
        {
            await Task.Delay(20, cts.Token);
        }
        bus.SubscriberCount.Should().BeGreaterThanOrEqualTo(1);

        // Publish a heartbeat first — logs stream must suppress it. Then a
        // task.completed which must arrive.
        await bus.PublishAsync(new EngineLifecycleEvent(
            "engine.heartbeat", TenantId: null, DateTimeOffset.UtcNow,
            new { engineCount = 1 }));

        await bus.PublishAsync(new EngineLifecycleEvent(
            "task.completed", TenantId: Guid.Empty, DateTimeOffset.UtcNow,
            new { id = Guid.NewGuid(), type = "github.webhook" }));

        // Read with a short deadline — the heartbeat must not appear on
        // the wire. First frame we see must be task.completed.
        var frame = await ReadNextFrameAsync(stream, cts.Token);
        frame.Should().NotBeNull();
        frame.Should().Contain("event: task.completed",
            "logs stream filters out engine.heartbeat noise (finding 012)");
        frame.Should().NotContain("engine.heartbeat");
    }

    [Test]
    public async Task EventsState_ClientDisconnect_CleansUpSubscription()
    {
        using var client = ApiTestFixture.CreateClient();
        var bus = (InMemoryEngineLifecycleBus)ApiTestFixture.Factory.Services
            .GetRequiredService<IEngineLifecycleBus>();

        var initialCount = bus.SubscriberCount;

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            using var resp = await client.GetAsync(
                "/api/engine/events/state",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            _ = await ReadNextFrameAsync(stream, cts.Token);

            for (var i = 0; i < 100 && bus.SubscriberCount <= initialCount; i++)
            {
                await Task.Delay(20, cts.Token);
            }
            bus.SubscriberCount.Should().BeGreaterThan(initialCount,
                "opening a connection should register at least one subscription");
        }

        // HttpClient disposal closes the underlying socket; the server-side
        // pump should observe RequestAborted and drain its subscription.
        for (var i = 0; i < 100 && bus.SubscriberCount > initialCount; i++)
        {
            await Task.Delay(50);
        }
        bus.SubscriberCount.Should().Be(initialCount,
            "client disconnect must drain the subscription from the bus");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Read bytes from the SSE stream until the "\n\n" frame terminator is
    /// seen, then return the frame as a UTF-8 string (without the trailing
    /// terminator). Skips any pure comment frames (":heartbeat\n\n").
    /// </summary>
    private static async Task<string?> ReadNextFrameAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var builder = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read == 0) return null; // EOF

            builder.Append((char)buffer[0]);

            // Frame terminator check.
            if (builder.Length >= 2 &&
                builder[^1] == '\n' && builder[^2] == '\n')
            {
                var frame = builder.ToString();
                builder.Clear();
                // Skip pure comment heartbeats (start with ':' line).
                if (frame.StartsWith(':')) continue;
                return frame;
            }
        }
        return null;
    }
}

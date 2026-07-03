using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ToolExecution;
using Tamma.Api.Services.Streaming;

namespace Tamma.Api.Tests.Streaming;

/// <summary>
/// Story 32-23 (AC3/AC9) — the live <see cref="BusToolLoopEventSink"/> that
/// replaces the null sink. Locks the TOOL_LOOP.* → frame-vocabulary mapping, the
/// correlationId routing (threaded from the emitter's <c>workflowInstanceId</c>),
/// the "unmapped events publish nothing" rule, and the credential-safety
/// guarantee that only fixed-vocabulary fields ever reach a frame.
/// </summary>
[TestFixture]
public class BusToolLoopEventSinkTests
{
    [Test]
    public void BusToolLoopEventSink_IsAn_IToolLoopEventSink()
    {
        // Locks AC3: the live sink is a drop-in for the null sink the DI swap
        // replaces behind the streaming flag.
        typeof(BusToolLoopEventSink).Should().BeAssignableTo<IToolLoopEventSink>();
    }

    [Test]
    public async Task WriteEventAsync_ToolExecuting_MapsTo_ToolCall_Frame()
    {
        var bus = new LlmRunStreamBus();
        var sink = new BusToolLoopEventSink(bus);
        using var sub = bus.Subscribe("run-1");

        await sink.WriteEventAsync("TOOL_LOOP.TOOL_EXECUTING", new
        {
            turnNumber = 3,
            toolName = "file_read",
            toolCallId = "call_abc",
            workflowInstanceId = "run-1",
        });

        var frame = DrainAvailable(sub.Reader).Should().ContainSingle().Subject;
        frame.Type.Should().Be(RunStreamFrameType.ToolCall);
        frame.CorrelationId.Should().Be("run-1", "the correlationId is threaded from workflowInstanceId");

        var json = Scrubbed(frame);
        json.Should().Contain("\"toolName\":\"file_read\"");
        json.Should().Contain("\"toolCallId\":\"call_abc\"");
        json.Should().Contain("\"turn\":3");
    }

    [Test]
    public async Task WriteEventAsync_ToolCompleted_MapsTo_ToolResult_Frame()
    {
        var bus = new LlmRunStreamBus();
        var sink = new BusToolLoopEventSink(bus);
        using var sub = bus.Subscribe("run-2");

        await sink.WriteEventAsync("TOOL_LOOP.TOOL_COMPLETED", new
        {
            turnNumber = 1,
            toolName = "shell_execute",
            toolCallId = "call_xyz",
            success = false,
            durationMs = 1234L,
            workflowInstanceId = "run-2",
        });

        var frame = DrainAvailable(sub.Reader).Should().ContainSingle().Subject;
        frame.Type.Should().Be(RunStreamFrameType.ToolResult);

        var json = Scrubbed(frame);
        json.Should().Contain("\"toolName\":\"shell_execute\"");
        json.Should().Contain("\"success\":false");
        json.Should().Contain("\"durationMs\":1234");
    }

    [Test]
    public async Task WriteEventAsync_LoopCompleted_MapsTo_Final_Frame()
    {
        var bus = new LlmRunStreamBus();
        var sink = new BusToolLoopEventSink(bus);
        using var sub = bus.Subscribe("run-3");

        await sink.WriteEventAsync("TOOL_LOOP.COMPLETED", new
        {
            totalTurns = 5,
            totalToolCalls = 12,
            totalDurationMs = 3000L,
            totalTokens = 150000,
            exhausted = true,
            workflowInstanceId = "run-3",
        });

        var frame = DrainAvailable(sub.Reader).Should().ContainSingle().Subject;
        frame.Type.Should().Be(RunStreamFrameType.Final);

        var json = Scrubbed(frame);
        json.Should().Contain("\"exhausted\":true");
        json.Should().Contain("\"totalTurns\":5");
        json.Should().Contain("\"totalTokens\":150000");
    }

    [Test]
    public async Task WriteEventAsync_TurnEvents_AreIgnored_NoFrame()
    {
        var bus = new LlmRunStreamBus();
        var sink = new BusToolLoopEventSink(bus);
        using var sub = bus.Subscribe("run-4");

        await sink.WriteEventAsync("TOOL_LOOP.TURN_STARTED",
            new { turnNumber = 0, messageCount = 2, estimatedTokens = 1000, workflowInstanceId = "run-4" });
        await sink.WriteEventAsync("TOOL_LOOP.TURN_COMPLETED",
            new { turnNumber = 0, totalTools = 1, totalDurationMs = 10L, cumulativeTokens = 1000, workflowInstanceId = "run-4" });

        DrainAvailable(sub.Reader).Should().BeEmpty("turn-progress events have no run-tap frame");
    }

    [Test]
    public async Task WriteEventAsync_MissingCorrelationId_PublishesNothing()
    {
        var bus = new LlmRunStreamBus();
        var sink = new BusToolLoopEventSink(bus);
        using var sub = bus.Subscribe("run-5");

        // No workflowInstanceId => the sink can't route => no publish (the run is
        // unaffected — pure observability).
        await sink.WriteEventAsync("TOOL_LOOP.TOOL_EXECUTING",
            new { turnNumber = 1, toolName = "x", toolCallId = "c" });

        DrainAvailable(sub.Reader).Should().BeEmpty();
    }

    [Test]
    public async Task WriteEventAsync_NeverCopiesOffVocabularyFields_Even_IfPresent()
    {
        // AC9 — even if a (hypothetical) upstream payload smuggled a secret, the
        // sink only ever copies the fixed vocabulary; the secret never reaches a
        // frame.
        var bus = new LlmRunStreamBus();
        var sink = new BusToolLoopEventSink(bus);
        using var sub = bus.Subscribe("run-6");

        await sink.WriteEventAsync("TOOL_LOOP.TOOL_EXECUTING", new
        {
            turnNumber = 1,
            toolName = "file_read",
            toolCallId = "c1",
            apiKey = "sk-should-never-appear",
            rawArgs = "{\"path\":\"/etc/shadow\"}",
            workflowInstanceId = "run-6",
        });

        var frame = DrainAvailable(sub.Reader).Should().ContainSingle().Subject;
        var json = Scrubbed(frame);
        json.Should().NotContain("sk-should-never-appear");
        json.Should().NotContain("rawArgs");
        json.Should().NotContain("/etc/shadow");
    }

    private static string Scrubbed(RunStreamFrame frame)
        => JsonSerializer.Serialize(RunStreamFrameScrubber.Scrub(frame));

    private static List<RunStreamFrame> DrainAvailable(ChannelReader<RunStreamFrame> reader)
    {
        var list = new List<RunStreamFrame>();
        while (reader.TryRead(out var f))
        {
            list.Add(f);
        }
        return list;
    }
}

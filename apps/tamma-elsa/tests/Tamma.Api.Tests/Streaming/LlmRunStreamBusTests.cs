using System.Threading.Channels;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Streaming;

namespace Tamma.Api.Tests.Streaming;

/// <summary>
/// Story 32-23 (AC5) — the in-process run-stream bus. Locks the decoupling
/// invariants that let the tap never break the buffered run: publish with zero
/// subscribers is a no-op that never throws; N subscribers each receive every
/// frame in order; a slow subscriber drops oldest under back-pressure (never a
/// producer stall); <c>seq</c> is per-run monotonic and independent per
/// correlationId; a <c>final</c> frame completes + tears down the topic.
/// </summary>
[TestFixture]
public class LlmRunStreamBusTests
{
    [Test]
    public async Task PublishAsync_ZeroSubscribers_IsNoOp_NeverThrows()
    {
        var bus = new LlmRunStreamBus();

        var act = async () => await bus.PublishAsync("run-nobody", Frame(RunStreamFrameType.ToolCall));

        await act.Should().NotThrowAsync();
        bus.SubscriberCount("run-nobody").Should().Be(0);
    }

    [Test]
    public async Task PublishAsync_NSubscribers_EachReceivesEveryFrame_InOrder()
    {
        var bus = new LlmRunStreamBus();
        using var s1 = bus.Subscribe("run-1");
        using var s2 = bus.Subscribe("run-1");

        bus.SubscriberCount("run-1").Should().Be(2);

        await bus.PublishAsync("run-1", Frame(RunStreamFrameType.ToolCall));
        await bus.PublishAsync("run-1", Frame(RunStreamFrameType.ToolResult));

        var got1 = DrainAvailable(s1.Reader);
        var got2 = DrainAvailable(s2.Reader);

        got1.Should().HaveCount(2);
        got2.Should().HaveCount(2);

        got1[0].Type.Should().Be(RunStreamFrameType.ToolCall);
        got1[0].Seq.Should().Be(1, "the bus stamps a per-run monotonic seq starting at 1");
        got1[1].Type.Should().Be(RunStreamFrameType.ToolResult);
        got1[1].Seq.Should().Be(2);

        // Both subscribers see the identical stamped seq sequence.
        got2.Select(f => f.Seq).Should().Equal(got1.Select(f => f.Seq));
    }

    [Test]
    public async Task PublishAsync_SlowSubscriber_DropsOldest_NoStall()
    {
        var bus = new LlmRunStreamBus();
        using var sub = bus.Subscribe("run-slow");

        const int published = LlmRunStreamBus.Capacity + 50; // overflow the bounded channel
        for (var i = 0; i < published; i++)
        {
            // Never blocks — DropOldest bounded channel. If it stalled, this
            // loop (2s guard on the whole test via the fixture) would hang.
            await bus.PublishAsync("run-slow", Frame(RunStreamFrameType.Token));
        }

        var got = DrainAvailable(sub.Reader);

        got.Should().HaveCount(LlmRunStreamBus.Capacity,
            "a slow subscriber's channel retains at most Capacity frames — oldest dropped");
        got[0].Seq.Should().Be(published - LlmRunStreamBus.Capacity + 1,
            "the retained window is the newest Capacity frames (oldest dropped)");
        got[^1].Seq.Should().Be(published, "the newest frame is retained");
    }

    [Test]
    public async Task Seq_IsPerRunMonotonic_AndIndependentPerCorrelationId()
    {
        var bus = new LlmRunStreamBus();
        using var a = bus.Subscribe("run-a");
        using var b = bus.Subscribe("run-b");

        await bus.PublishAsync("run-a", Frame(RunStreamFrameType.ToolCall));
        await bus.PublishAsync("run-b", Frame(RunStreamFrameType.ToolCall));
        await bus.PublishAsync("run-a", Frame(RunStreamFrameType.ToolResult));

        var ga = DrainAvailable(a.Reader);
        var gb = DrainAvailable(b.Reader);

        ga.Select(f => f.Seq).Should().Equal(new long[] { 1, 2 }, "run-a's seq is independent of run-b");
        gb.Select(f => f.Seq).Should().Equal(new long[] { 1 }, "run-b's seq starts fresh at 1");
    }

    [Test]
    public async Task PublishAsync_FinalFrame_CompletesAndTearsDownTopic()
    {
        var bus = new LlmRunStreamBus();
        var sub = bus.Subscribe("run-final");

        await bus.PublishAsync("run-final", Frame(RunStreamFrameType.ToolCall));
        await bus.PublishAsync("run-final", Frame(RunStreamFrameType.Final));

        // The channel is completed on `final`, so ReadAllAsync drains both frames
        // then ends — no hang.
        var all = new List<RunStreamFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var f in sub.Reader.ReadAllAsync(cts.Token))
        {
            all.Add(f);
        }

        all.Should().HaveCount(2);
        all[^1].Type.Should().Be(RunStreamFrameType.Final);
        bus.SubscriberCount("run-final").Should().Be(0, "the topic is torn down on `final`");

        sub.Dispose(); // idempotent after teardown
    }

    [Test]
    public void Dispose_DetachesSubscriber()
    {
        var bus = new LlmRunStreamBus();
        var sub = bus.Subscribe("run-detach");
        bus.SubscriberCount("run-detach").Should().Be(1);

        sub.Dispose();

        bus.SubscriberCount("run-detach").Should().Be(0, "dispose detaches the channel so nothing leaks");
    }

    [Test]
    public void Detach_RemovesTopic_WhenLastSubscriberLeaves_NoZombieLeak()
    {
        // Review fix — a run tapped-then-disconnected (or a finished/aborted run that
        // never publishes `final`) must NOT leak a topic into the process-global
        // singleton. Removing the LAST subscriber reclaims the topic.
        var bus = new LlmRunStreamBus();
        var sub = bus.Subscribe("run-reclaim");
        bus.TopicCount.Should().Be(1);
        bus.HasTopic("run-reclaim").Should().BeTrue();

        sub.Dispose();

        bus.SubscriberCount("run-reclaim").Should().Be(0);
        bus.HasTopic("run-reclaim").Should().BeFalse("the empty topic is reclaimed on the last detach");
        bus.TopicCount.Should().Be(0, "no zombie topic lingers after the last subscriber leaves");
    }

    [Test]
    public void Detach_KeepsTopic_WhileOtherSubscribersRemain_RemovesOnLast()
    {
        var bus = new LlmRunStreamBus();
        var s1 = bus.Subscribe("run-multi");
        var s2 = bus.Subscribe("run-multi");
        bus.TopicCount.Should().Be(1);

        s1.Dispose();
        bus.HasTopic("run-multi").Should().BeTrue("one subscriber still remains");
        bus.SubscriberCount("run-multi").Should().Be(1);

        s2.Dispose();
        bus.HasTopic("run-multi").Should().BeFalse("last subscriber left ⇒ topic reclaimed");
        bus.TopicCount.Should().Be(0);
    }

    private static RunStreamFrame Frame(string type)
        => new(type, "run", 0, new { x = 1 });

    private static List<RunStreamFrame> DrainAvailable(ChannelReader<RunStreamFrame> reader)
    {
        // Publishing is synchronous (TryWrite), so everything published is already
        // queued — a TryRead loop drains it deterministically.
        var list = new List<RunStreamFrame>();
        while (reader.TryRead(out var f))
        {
            list.Add(f);
        }
        return list;
    }
}

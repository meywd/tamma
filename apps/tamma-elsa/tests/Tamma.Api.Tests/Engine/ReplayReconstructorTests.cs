using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Engine.Replay;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Story 4-8 (black-box replay) — unit coverage for the PURE fold
/// (<see cref="ReplayReconstructor"/>). No DB, no clock, no I/O: these prove the
/// reconstruction is a deterministic left-fold over an in-memory ordered slice —
/// a known slice folds to the expected state, an <c>upTo</c> point returns the
/// as-of-then state (not the final), the same slice always yields the same result,
/// and the diff between two folds is a pure comparison.
/// </summary>
[TestFixture]
public class ReplayReconstructorTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DomainEvent Ev(
        long seq, string type,
        int? issue = null,
        DateTime? at = null,
        string? tags = null,
        string? data = null,
        string? metadata = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        SequenceNumber = seq,
        IssueNumber = issue,
        CreatedAt = at ?? Base.AddSeconds(seq),
        Tags = tags ?? "{}",
        Data = data ?? "{}",
        Metadata = metadata ?? "{}",
    };

    /// <summary>A representative run: issue context, an LLM decision, code + git
    /// artifacts, a gate/approval, a failed call, and a terminal completion.</summary>
    private static List<DomainEvent> SampleRun() => new()
    {
        Ev(10, "WORKFLOW.STEP_STARTED", issue: 42),
        Ev(11, "LLM.CALL.SUCCESS", issue: 42,
            tags: "{\"correlationId\":\"run-1\",\"provider\":\"anthropic\",\"model\":\"claude\",\"role\":\"developer\"}"),
        Ev(12, "CODE.GENERATED.SUCCESS", issue: 42,
            data: "{\"repository\":\"acme/app\"}"),
        Ev(13, "GIT.PR_OPENED.SUCCESS", issue: 42,
            data: "{\"pullRequestUrl\":\"https://github.com/acme/app/pull/7\"}"),
        Ev(14, "GATE.EVALUATED.SUCCESS", issue: 42),
        Ev(15, "APPROVAL.GATE", issue: 42, data: "{\"decision\":\"approved\"}"),
        Ev(16, "LLM.CALL.FAILED", issue: 42,
            metadata: "{\"error\":\"rate limited\"}"),
        Ev(17, "WORKFLOW.COMPLETED", issue: 42),
    };

    [Test]
    public void Reconstruct_FoldsKnownSlice_IntoExpectedState()
    {
        var run = SampleRun();

        var state = ReplayReconstructor.Reconstruct("run-1", run, run.Count);

        state.CorrelationId.Should().Be("run-1");
        state.EventsReplayed.Should().Be(8);
        state.TotalEvents.Should().Be(8);
        state.ReplayedToEnd.Should().BeTrue();
        state.AtSequenceNumber.Should().Be(17);
        state.StepReached.Should().Be("WORKFLOW.COMPLETED");
        state.Status.Should().Be("completed");
        state.IssueNumber.Should().Be(42);
        state.Repository.Should().Be("acme/app");

        // AI decisions: the two LLM calls (success + failed).
        state.AiDecisions.Select(d => d.Type).Should()
            .BeEquivalentTo(new[] { "LLM.CALL.SUCCESS", "LLM.CALL.FAILED" });
        var ok = state.AiDecisions.Single(d => d.Type == "LLM.CALL.SUCCESS");
        ok.Provider.Should().Be("anthropic");
        ok.Model.Should().Be("claude");
        ok.Role.Should().Be("developer");
        ok.Outcome.Should().Be("success");

        // Code changes: CODE + GIT + PR events (PR here is none; CODE + GIT = 2).
        state.CodeChanges.Select(c => c.Type).Should()
            .BeEquivalentTo(new[] { "CODE.GENERATED.SUCCESS", "GIT.PR_OPENED.SUCCESS" });
        state.CodeChanges.Single(c => c.Type == "GIT.PR_OPENED.SUCCESS").Detail
            .Should().Be("https://github.com/acme/app/pull/7");

        // Approvals: the gate + the approval point.
        state.Approvals.Select(a => a.Type).Should()
            .BeEquivalentTo(new[] { "GATE.EVALUATED.SUCCESS", "APPROVAL.GATE" });
        state.Approvals.Single(a => a.Type == "APPROVAL.GATE").Decision.Should().Be("approved");

        // Errors (cross-cut): the failed LLM call, with its metadata.error message.
        state.Errors.Should().ContainSingle().Which.Type.Should().Be("LLM.CALL.FAILED");
        state.Errors.Single().Message.Should().Be("rate limited");

        // Timeline is the full ordered slice.
        state.Timeline.Select(t => t.SequenceNumber).Should()
            .Equal(10, 11, 12, 13, 14, 15, 16, 17);
        state.Timeline.Single(t => t.SequenceNumber == 11).Category.Should().Be("ai");
        state.Timeline.Single(t => t.SequenceNumber == 12).Category.Should().Be("code");
        state.Timeline.Single(t => t.SequenceNumber == 15).Category.Should().Be("approval");
    }

    [Test]
    public void SliceUpTo_BySequence_ReturnsAsOfThen_NotFinal()
    {
        var run = SampleRun();

        // Replay up to seq 13 (the PR-opened event) — mid-run.
        var slice = ReplayReconstructor.SliceUpTo(run, upToSequence: 13, upToTimestamp: null);
        var state = ReplayReconstructor.Reconstruct("run-1", slice, run.Count);

        state.EventsReplayed.Should().Be(4, "events 10..13 are at or before seq 13");
        state.TotalEvents.Should().Be(8);
        state.ReplayedToEnd.Should().BeFalse();
        state.AtSequenceNumber.Should().Be(13);
        state.StepReached.Should().Be("GIT.PR_OPENED.SUCCESS");
        state.Status.Should().Be("running", "the terminal WORKFLOW.COMPLETED (seq 17) is not in the slice");
        state.Errors.Should().BeEmpty("the failed call at seq 16 is after the replay point");
        state.Approvals.Should().BeEmpty("the gate/approval at seq 14/15 is after the replay point");
    }

    [Test]
    public void SliceUpTo_ByTimestamp_ReturnsAsOfThen()
    {
        var run = SampleRun();
        // Each event's CreatedAt is Base + seq seconds. Cut at seq 12's instant.
        var cut = new DateTimeOffset(Base.AddSeconds(12));

        var slice = ReplayReconstructor.SliceUpTo(run, upToSequence: null, upToTimestamp: cut);
        var state = ReplayReconstructor.Reconstruct("run-1", slice, run.Count);

        state.EventsReplayed.Should().Be(3, "events at <= Base+12s are seq 10,11,12");
        state.StepReached.Should().Be("CODE.GENERATED.SUCCESS");
        state.ReplayedToEnd.Should().BeFalse();
    }

    [Test]
    public void SliceUpTo_BeyondLastEvent_ReturnsFullState()
    {
        var run = SampleRun();

        var slice = ReplayReconstructor.SliceUpTo(run, upToSequence: 9999, upToTimestamp: null);
        var state = ReplayReconstructor.Reconstruct("run-1", slice, run.Count);

        state.EventsReplayed.Should().Be(8);
        state.ReplayedToEnd.Should().BeTrue();
        state.Status.Should().Be("completed");
    }

    [Test]
    public void SliceUpTo_BeforeRun_ReturnsEmptyButKnownState()
    {
        var run = SampleRun();

        // A point before the first event (seq 10) — the run is known, the as-of view
        // is empty (NOT a 404 — that distinction is the service/endpoint's).
        var slice = ReplayReconstructor.SliceUpTo(run, upToSequence: 5, upToTimestamp: null);
        var state = ReplayReconstructor.Reconstruct("run-1", slice, run.Count);

        state.EventsReplayed.Should().Be(0);
        state.TotalEvents.Should().Be(8);
        state.ReplayedToEnd.Should().BeFalse();
        state.AtSequenceNumber.Should().BeNull();
        state.StepReached.Should().BeNull();
        state.Status.Should().Be("running");
        state.Timeline.Should().BeEmpty();
    }

    [Test]
    public void Reconstruct_IsDeterministic_SameSliceSameState()
    {
        var run = SampleRun();

        var a = ReplayReconstructor.Reconstruct("run-1", run, run.Count);
        var b = ReplayReconstructor.Reconstruct("run-1", run, run.Count);

        // A pure fold: identical input → byte-identical serialized output.
        JsonSerializer.Serialize(a).Should().Be(JsonSerializer.Serialize(b));
    }

    [Test]
    public void Diff_BetweenTwoFolds_ShowsOnlyEventsAfterFrom()
    {
        var run = SampleRun();

        var fromSlice = ReplayReconstructor.SliceUpTo(run, 13, null);   // seq 10..13
        var toSlice = ReplayReconstructor.SliceUpTo(run, 17, null);     // seq 10..17
        var from = ReplayReconstructor.Reconstruct("run-1", fromSlice, run.Count);
        var to = ReplayReconstructor.Reconstruct("run-1", toSlice, run.Count);

        var delta = ReplayReconstructor.Diff(from, to);

        delta.FromSequenceNumber.Should().Be(13);
        delta.AddedEventCount.Should().Be(4, "seq 14,15,16,17 are new");
        delta.AddedEvents.Select(e => e.SequenceNumber).Should().Equal(14, 15, 16, 17);
        delta.AddedApprovals.Should().Be(2, "the gate + approval (seq 14,15)");
        delta.AddedErrors.Should().Be(1, "the failed call (seq 16)");
        delta.AddedDecisions.Should().Be(1, "the failed LLM call is an AI decision too (seq 16)");
    }

    [Test]
    public void Reconstruct_TolerantOfMalformedJson_NeverThrows()
    {
        var run = new List<DomainEvent>
        {
            Ev(1, "LLM.CALL.SUCCESS", tags: "not json", data: "{bad", metadata: "]"),
        };

        var act = () => ReplayReconstructor.Reconstruct("run-x", run, run.Count);

        act.Should().NotThrow();
        var state = act();
        state.AiDecisions.Should().ContainSingle();
        state.AiDecisions.Single().Provider.Should().BeNull();
    }
}

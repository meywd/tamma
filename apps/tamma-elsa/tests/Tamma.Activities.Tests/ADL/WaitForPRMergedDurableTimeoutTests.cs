using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// SingleIssueCycle.md §Missing #6 (landed 2026-07-02) — the durable merge-wait-timeout
/// invariant for <c>WaitForPRMergedActivity</c>.
///
/// <para>The cycle's last unbounded human/webhook wait (the <c>pr-merged-{pr}</c> bookmark)
/// now arms a durable SLA alongside it. The SLA MUST use <c>context.DelayFor(...)</c> — the
/// EF-persisted Delay bookmark that <c>Elsa.Scheduling</c>'s startup task RE-ARMS after a host
/// restart — NOT the in-memory <c>IWorkflowScheduler.ScheduleAtAsync</c> timer (lost on any
/// restart within the SLA window, which is the exact hang this closes). These source-scan
/// invariants guard against regression to the in-memory scheduler and mirror the
/// <c>BlockerDurableTimeoutTests</c> / <c>ReviewDurableTimeoutTests</c> precedents. A live
/// resume/rehydration test would need the full Elsa runtime + EF store, which this activity has
/// no unit harness for; the escalation-on-timeout branch is proved structurally in
/// <c>SingleIssueCycleMergeSlaTests</c>.</para>
/// </summary>
[TestFixture]
public class WaitForPRMergedDurableTimeoutTests
{
    [Test]
    public void WaitForPRMerged_ArmsDurableDelayBookmark_WithATimeoutHandler()
    {
        var src = ReadAdlActivity("WaitForPRMergedActivity.cs");

        // The durable primitive — DelayFor (the Delay bookmark) — must be armed, and a
        // dedicated timeout callback must exist to take the TimedOut edge.
        src.Should().Contain("context.DelayFor(",
            "WaitForPRMerged must arm the durable DelayFor (Delay) bookmark so the SLA survives a host restart");
        src.Should().Contain("OnTimeoutAsync",
            "WaitForPRMerged must resume into a dedicated durable-timeout handler");
        src.Should().Contain("CompleteActivityWithOutcomesAsync(\"TimedOut\")",
            "the durable timeout must take the deterministic TimedOut outcome");
    }

    [Test]
    public void WaitForPRMerged_StillArmsTheMergeWebhookBookmark()
    {
        var src = ReadAdlActivity("WaitForPRMergedActivity.cs");

        // Dual-arm: the merge bookmark must remain so a real merge webhook can resume (only
        // the timeout primitive was added, not a change to the resume path).
        src.Should().Contain("pr-merged-",
            "the merge-webhook bookmark must remain so a real merge resumes the wait");
        src.Should().Contain("CompleteActivityWithOutcomesAsync(\"Merged\")",
            "a delivered merge webhook must take the Merged outcome (the unchanged happy path)");
    }

    [Test]
    public void WaitForPRMerged_DoesNotUseTheInMemoryScheduler()
    {
        var src = ReadAdlActivity("WaitForPRMergedActivity.cs");

        src.Should().NotContain("ScheduleAtAsync(",
            "WaitForPRMerged must NOT schedule via IWorkflowScheduler.ScheduleAtAsync (in-memory timer lost on restart)");
        src.Should().NotContain("GetService<IWorkflowScheduler>",
            "WaitForPRMerged must not resolve the in-memory IWorkflowScheduler");
    }

    // ---------------------------------------------------------------------
    // Source-tree helper (mirrors BlockerDurableTimeoutTests' worktree locator).
    // ---------------------------------------------------------------------

    private static string ReadAdlActivity(string fileName)
    {
        var path = Path.Combine(TammaElsaSrcRoot(), "Tamma.Activities", "ADL", fileName);
        File.Exists(path).Should().BeTrue($"expected ADL activity source at {path}");
        return File.ReadAllText(path);
    }

    private static string TammaElsaSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tamma.Activities");
            if (Directory.Exists(candidate))
                return Path.Combine(dir.FullName, "src");

            var nested = Path.Combine(dir.FullName, "apps", "tamma-elsa", "src", "Tamma.Activities");
            if (Directory.Exists(nested))
                return Path.Combine(dir.FullName, "apps", "tamma-elsa", "src");

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate apps/tamma-elsa/src by walking up from "
            + AppContext.BaseDirectory + " — the durable-timeout invariant test needs the source tree.");
    }
}

using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Activities.Tests.Review;

/// <summary>
/// Review fix 2026-06-25 (code-review P0) — the durable wait-timeout invariant for the three
/// code-review bookmark waits.
///
/// <para>The build-out left <see cref="Tamma.Activities.Review.MonitorReviewActivity"/>,
/// <see cref="Tamma.Activities.Review.WaitForFixesActivity"/> and
/// <see cref="Tamma.Activities.Review.EscalateReviewActivity"/> with ONLY a
/// <c>CreateBookmark</c> plus an in-memory deadline that was checked exclusively inside the
/// resume callback. If the reviewer / junior / senior never responds, the callback never
/// fires, so the <c>TimedOut</c> outcome was runtime-unreachable and the workflow instance
/// suspended forever — the exact hang this guards against.</para>
///
/// <para>The fix arms a DURABLE <c>context.DelayFor(...)</c> (the EF-persisted Delay bookmark
/// that <c>Elsa.Scheduling</c>'s startup background task RE-ARMS on rehydration) alongside the
/// work bookmark, resuming into a dedicated <c>OnTimeoutAsync</c> that takes the <c>TimedOut</c>
/// outcome. These source-scan invariants guard against regression to a callback-only timeout
/// or the in-memory <c>IWorkflowScheduler</c>. (A live resume/rehydration test needs the full
/// Elsa runtime + EF store, which these activities have no unit harness for; the behavioural
/// contract — never-resumed bookmark → <c>TimedOut</c> terminal — is covered structurally by
/// <see cref="Tamma.Activities.Tests.Workflows.CodeReviewWorkflowStructureTests"/>.) Mirrors
/// <see cref="Tamma.Activities.Tests.Blocker.BlockerDurableTimeoutTests"/>.</para>
/// </summary>
[TestFixture]
public class ReviewDurableTimeoutTests
{
    private static readonly string[] TimeoutBearingActivities =
    {
        "MonitorReviewActivity.cs",
        "WaitForFixesActivity.cs",
        "EscalateReviewActivity.cs",
    };

    [Test]
    [TestCaseSource(nameof(TimeoutBearingActivities))]
    public void TimeoutActivity_ArmsDurableDelayBookmark_WithATimeoutHandler(string fileName)
    {
        var src = ReadReviewActivity(fileName);

        // The durable primitive — DelayFor (the Delay bookmark) — must be armed, and a
        // dedicated timeout callback must exist to take the TimedOut edge.
        src.Should().Contain("context.DelayFor(",
            $"{fileName} must arm the durable DelayFor (Delay) bookmark so the timeout survives a host restart and is reachable when no one responds");
        src.Should().Contain("OnTimeoutAsync",
            $"{fileName} must resume into a dedicated durable-timeout handler");
        src.Should().Contain("CompleteActivityWithOutcomesAsync(\"TimedOut\")",
            $"{fileName}'s timeout handler must complete with the TimedOut outcome (not suspend forever)");
    }

    [Test]
    [TestCaseSource(nameof(TimeoutBearingActivities))]
    public void TimeoutActivity_StillArmsTheExternalWorkBookmark(string fileName)
    {
        var src = ReadReviewActivity(fileName);

        // Dual-arm: the external work bookmark must remain so a real reviewer/junior/senior
        // signal can resume (only the timeout primitive was added, not the resume path).
        src.Should().Contain("CreateBookmark(",
            $"{fileName} must keep its external work bookmark so a real response can resume");
    }

    [Test]
    [TestCaseSource(nameof(TimeoutBearingActivities))]
    public void TimeoutActivity_DoesNotUseTheInMemoryScheduler(string fileName)
    {
        var src = ReadReviewActivity(fileName);

        // The in-memory LocalScheduler seam (ScheduleAtAsync / IWorkflowScheduler) is the lost-
        // on-restart hazard. It must be absent.
        src.Should().NotContain("ScheduleAtAsync(",
            $"{fileName} must NOT schedule via IWorkflowScheduler.ScheduleAtAsync (in-memory timer lost on restart)");
        src.Should().NotContain("GetService<IWorkflowScheduler>",
            $"{fileName} must not resolve the in-memory IWorkflowScheduler");
    }

    // ---------------------------------------------------------------------
    // Source-tree helper (mirrors BlockerDurableTimeoutTests' worktree locator).
    // ---------------------------------------------------------------------

    private static string ReadReviewActivity(string fileName)
    {
        var path = Path.Combine(ReviewActivityDir(), fileName);
        File.Exists(path).Should().BeTrue($"expected review activity source at {path}");
        return File.ReadAllText(path);
    }

    private static string ReviewActivityDir() =>
        Path.Combine(TammaElsaSrcRoot(), "Tamma.Activities", "Review");

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

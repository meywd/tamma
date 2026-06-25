using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Activities.Tests.Blocker;

/// <summary>
/// Review fix 2026-06-25 (BlockerDiagnosis P0) — the durable wait-timeout invariant.
///
/// <para>The original build-out armed each per-level / escalation timeout via
/// <c>IWorkflowScheduler.ScheduleAtAsync</c>. With the host's default <c>Elsa.Scheduling</c>
/// setup (no persistent scheduling store) that is the in-memory <c>LocalScheduler</c>
/// (<c>System.Timers.Timer</c>): on ANY host restart during the wait the timer is lost and
/// nothing re-arms it on bookmark rehydration → the bookmark hangs forever — the exact P0 the
/// feature claims to close. The escalation SLA defaults to 24h and a VPS restart inside that
/// window is routine, so this is not theoretical.</para>
///
/// <para>The fix switches both <c>DetectProgressActivity</c> and
/// <c>EscalateToSeniorActivity</c> to <c>context.DelayFor(...)</c> — the EF-persisted Delay
/// bookmark that <c>Elsa.Scheduling</c>'s startup background task RE-ARMS after a restart.
/// These source-scan invariants guard against regression to the in-memory scheduler. (A live
/// resume/rehydration test would need the full Elsa runtime + EF store, which these activities
/// have no unit harness for; the behavioural contract — never-resumed bookmark → Timeout
/// terminal — is covered by the <c>ResolveStatus</c>/<c>TerminalEventType</c> precedence tests
/// in <see cref="BlockerEventTests"/>.)</para>
/// </summary>
[TestFixture]
public class BlockerDurableTimeoutTests
{
    private static readonly string[] TimeoutBearingActivities =
    {
        "DetectProgressActivity.cs",
        "EscalateToSeniorActivity.cs",
    };

    [Test]
    [TestCaseSource(nameof(TimeoutBearingActivities))]
    public void TimeoutActivity_ArmsDurableDelayBookmark_WithATimeoutHandler(string fileName)
    {
        var src = ReadBlockerActivity(fileName);

        // The durable primitive — DelayFor (the Delay bookmark) — must be armed, and a
        // dedicated timeout callback must exist to take the Timeout edge.
        src.Should().Contain("context.DelayFor(",
            $"{fileName} must arm the durable DelayFor (Delay) bookmark so the timeout survives a host restart");
        src.Should().Contain("OnTimeoutAsync",
            $"{fileName} must resume into a dedicated durable-timeout handler");
    }

    [Test]
    [TestCaseSource(nameof(TimeoutBearingActivities))]
    public void TimeoutActivity_DoesNotUseTheInMemoryScheduler(string fileName)
    {
        var src = ReadBlockerActivity(fileName);

        // The in-memory LocalScheduler seam (ScheduleAtAsync / IWorkflowScheduler) is the lost-
        // on-restart P0. It must be GONE — a string-literal/identifier scan, doc-comment safe
        // because the doc-comments reference it as <c>IWorkflowScheduler</c> / <c>ScheduleAtAsync</c>
        // (angle-bracketed, never as a bare call/using).
        src.Should().NotContain("ScheduleAtAsync(",
            $"{fileName} must NOT schedule via IWorkflowScheduler.ScheduleAtAsync (in-memory timer lost on restart)");
        src.Should().NotContain("using Elsa.Scheduling;",
            $"{fileName} must not import Elsa.Scheduling's scheduler types — DelayFor lives in Elsa.Extensions");
        src.Should().NotContain("GetService<IWorkflowScheduler>",
            $"{fileName} must not resolve the in-memory IWorkflowScheduler");
    }

    [Test]
    public void DetectProgress_StillArmsTheExternalProgressBookmark()
    {
        var src = ReadBlockerActivity("DetectProgressActivity.cs");
        // Dual-arm: the external progress bookmark must remain so a real progress signal can
        // resume (only the timeout primitive changed, not the resume path).
        src.Should().Contain("CreateBookmark(",
            "the external progress bookmark must remain so a junior's progress signal can resume");
        src.Should().Contain("OnResumeAsync",
            "the external progress resume handler must remain");
    }

    [Test]
    public void Escalation_StillArmsTheExternalSeniorBookmark()
    {
        var src = ReadBlockerActivity("EscalateToSeniorActivity.cs");
        src.Should().Contain("CreateBookmark(",
            "the external escalation bookmark must remain so the senior's response can resume");
        src.Should().Contain("OnResumeAsync",
            "the external senior-response resume handler must remain");
    }

    // ---------------------------------------------------------------------
    // Source-tree helper (mirrors NoDirectLlmCallTests' worktree locator).
    // ---------------------------------------------------------------------

    private static string ReadBlockerActivity(string fileName)
    {
        var path = Path.Combine(BlockerActivityDir(), fileName);
        File.Exists(path).Should().BeTrue($"expected blocker activity source at {path}");
        return File.ReadAllText(path);
    }

    private static string BlockerActivityDir() =>
        Path.Combine(TammaElsaSrcRoot(), "Tamma.Activities", "Blocker");

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

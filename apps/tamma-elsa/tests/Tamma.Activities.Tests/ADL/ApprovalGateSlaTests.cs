using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Both human approval gates used to wait FOREVER. An unanswered gate pinned its instance
/// in <c>Running</c> — and, for the merge gate, held one of the ADL's <c>MaxConcurrent</c>
/// slots — so a single PR nobody looked at could stop the autonomous loop dispatching
/// anything else. These pin the durable SLA that closes it.
///
/// <para>The SLA MUST use <c>context.DelayFor(...)</c> — the EF-persisted Delay bookmark
/// that <c>Elsa.Scheduling</c>'s startup task RE-ARMS after a host restart — and NOT the
/// in-memory <c>IWorkflowScheduler.ScheduleAtAsync</c> timer, which is lost on any restart
/// inside a 24h window (i.e. almost always). Same invariants and same source-scan shape as
/// <see cref="WaitForPRMergedDurableTimeoutTests"/>, whose merge SLA set the precedent; a
/// live rehydration test would need the full Elsa runtime + EF store, which these
/// activities have no unit harness for.</para>
/// </summary>
[TestFixture]
public class ApprovalGateSlaTests
{
    // ── Merge-approval gate ─────────────────────────────────────────────────

    [Test]
    public void MergeApprovalGate_ExposesATimedOutOutcome()
    {
        Outcomes<WaitForMergeApprovalActivity>().Should().Contain("TimedOut",
            "an expired gate needs a deterministic edge of its own — reusing 'Reject' would "
            + "record a human decision that never happened");
    }

    [Test]
    public void MergeApprovalGate_ArmsADurableDelayBookmark()
    {
        var src = ReadAdlActivity("WaitForMergeApprovalActivity.cs");

        src.Should().Contain("context.DelayFor(",
            "the SLA must survive a host restart inside the window");
        src.Should().Contain("OnTimeoutAsync",
            "expiry must resume into a dedicated handler, not fall through the decision path");
        src.Should().Contain("CompleteActivityWithOutcomesAsync(\"TimedOut\")");
    }

    [Test]
    public void MergeApprovalGate_DoesNotUseTheInMemoryScheduler()
    {
        var src = ReadAdlActivity("WaitForMergeApprovalActivity.cs");

        src.Should().NotContain("ScheduleAtAsync(");
        src.Should().NotContain("GetService<IWorkflowScheduler>");
    }

    [Test]
    public void MergeApprovalGate_StillArmsTheDecisionBookmark()
    {
        var src = ReadAdlActivity("WaitForMergeApprovalActivity.cs");

        src.Should().Contain("adl-merge-approval-",
            "only the timeout arm was added — a real human decision must still resume the gate");
    }

    [Test]
    public void MergeApprovalWorkflow_RoutesTimedOut_ToTheLoudEscalateTerminal()
    {
        var src = ReadWorkflow("MergeApprovalWorkflow.cs");

        src.Should().Contain("ConnectOutcome(waitMerge, \"TimedOut\", emitEscalated)",
            "an expired gate must reach the SAME audited terminal an invalid decision does "
            + "(outcome=\"escalated\", which the cycle routes to reportError) — never an "
            + "implicit approval, and never a dangling edge that silently ends the branch");
    }

    // ── Plan-approval gate ──────────────────────────────────────────────────

    [Test]
    public void PlanApprovalGate_ExposesATimedOutOutcome()
    {
        Outcomes<WaitForPlanApprovalActivity>().Should().Contain("TimedOut");
    }

    [Test]
    public void PlanApprovalGate_ArmsADurableDelayBookmark()
    {
        var src = ReadAdlActivity("WaitForPlanApprovalActivity.cs");

        src.Should().Contain("context.DelayFor(");
        src.Should().Contain("CompleteActivityWithOutcomesAsync(\"TimedOut\")");
        src.Should().NotContain("ScheduleAtAsync(");
    }

    [Test]
    public void PlanApprovalTimeout_IsAuditedAsItsOwnEvent_notAsARejection()
    {
        // "nobody looked" and "a human said no" must not be the same audit row.
        PlanApprovalEvents.DecisionTimedOut.Should().NotBe(PlanApprovalEvents.DecisionRejected);
        PlanApprovalEvents.StatusForEvent(PlanApprovalEvents.DecisionTimedOut)
            .Should().Be("error", "an expired approval is a LOUD row, not a quiet success");
    }

    [Test]
    public void PlanApprovalTimeout_RecordsARejectDecision_soNoParentCanReadItAsApproval()
    {
        var src = ReadAdlActivity("WaitForPlanApprovalActivity.cs");

        src.Should().Contain("Decision = ApprovalDecision.Reject",
            "the serialized ApprovalResult is what a parent branches on — fail closed");
    }

    // ── Defaults (flagged for owner confirmation in the lane report) ─────────

    [Test]
    public void BothGatesDefaultToTheSameDocumentedSla()
    {
        WaitForMergeApprovalActivity.DefaultTimeoutMinutes.Should().Be(1440);
        WaitForPlanApprovalActivity.DefaultTimeoutMinutes.Should().Be(1440);
    }

    [Test]
    public void BothGatesAreConfigurable()
    {
        WaitForMergeApprovalActivity.TimeoutConfigKey.Should().Be("Adl:MergeApprovalTimeoutMinutes");
        WaitForPlanApprovalActivity.TimeoutConfigKey.Should().Be("Adl:PlanApprovalTimeoutMinutes");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IEnumerable<string> Outcomes<T>() where T : IActivity
        => typeof(T).GetCustomAttributes(typeof(FlowNodeAttribute), inherit: false)
            .Cast<FlowNodeAttribute>()
            .SelectMany(a => a.Outcomes);

    private static string ReadAdlActivity(string fileName)
        => ReadSource(Path.Combine("Tamma.Activities", "ADL", fileName));

    private static string ReadWorkflow(string fileName)
        => ReadSource(Path.Combine("Tamma.ElsaServer", "Workflows", fileName));

    private static string ReadSource(string relativePath)
    {
        var path = Path.Combine(TammaElsaSrcRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"expected source at {path}");
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
            "Could not locate apps/tamma-elsa/src by walking up from " + AppContext.BaseDirectory);
    }
}

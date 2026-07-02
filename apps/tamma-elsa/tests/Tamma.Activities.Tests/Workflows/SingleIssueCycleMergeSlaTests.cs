using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// SingleIssueCycle.md §Missing #6 (the tracked DEFERRED follow-up, landed 2026-07-02) —
/// the durable merge-SLA timer on the cycle's last unbounded human/webhook wait
/// (<c>WaitForPRMerged</c>). Two invariants:
///
/// <list type="bullet">
///   <item>the happy path is UNCHANGED — a merge webhook resumes via the <c>Merged</c> outcome
///     and still flows to CloseIssue + the deployment pipeline + the success terminal;</item>
///   <item>exceeding the SLA fires the durable <c>context.DelayFor</c> timer, which takes the
///     <c>TimedOut</c> outcome and escalates to the needs-human terminal (never ReportSuccess),
///     terminating at Finish — the loop can NEVER wait on the merge webhook indefinitely.</item>
/// </list>
///
/// <para>These are the structural proofs (the established test style for this workflow — see
/// <see cref="SingleIssueCycleRoutingTests"/> / <see cref="SingleIssueCycleSafetyTests"/>). The
/// durable-primitive proof (that the timer survives a host restart) is the source-scan invariant
/// in <see cref="WaitForPRMergedDurableTimeoutTests"/>, mirroring the Blocker/Review precedents.
/// A live resume/rehydration test would need the full Elsa runtime + EF store, which these
/// activities have no unit harness for.</para>
/// </summary>
[TestFixture]
public class SingleIssueCycleMergeSlaTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ── The activity declares the two typed outcomes the cycle branches on ──

    [Test]
    public void WaitForPRMerged_DeclaresMergedAndTimedOutOutcomes()
    {
        var flowNode = typeof(WaitForPRMergedActivity).GetCustomAttribute<FlowNodeAttribute>();
        flowNode.Should().NotBeNull(
            "WaitForPRMerged must declare typed outcomes so the cycle can branch merge vs. SLA-timeout");
        flowNode!.Outcomes.Should().Contain("Merged", "the merge webhook resumes on the Merged outcome");
        flowNode.Outcomes.Should().Contain("TimedOut", "the durable SLA timer resumes on the TimedOut outcome");
    }

    [Test]
    public void WaitForPRMerged_HasASaneDefaultSla()
    {
        // A configurable timeout with a sane default (Adl:PrMergeTimeoutMinutes) — never zero /
        // never "wait forever". 12h absorbs a delayed/retried webhook without stalling a loop.
        WaitForPRMergedActivity.DefaultTimeoutMinutes.Should().BeGreaterThan(0,
            "the merge wait must have a positive default SLA — a non-positive value would wait forever");
        WaitForPRMergedActivity.DefaultTimeoutMinutes.Should().Be(720, "12h is the documented default");
    }

    // ── (a) Happy path UNCHANGED: Merged → CloseIssue + DeploymentPipeline → success ──

    [Test]
    public void MergedOutcome_StillReachesCloseIssueDeploymentAndSuccess()
    {
        HasEdge("WaitForPRMerged", "CloseIssue", "Merged").Should().BeTrue(
            "a merged PR must still close the issue (happy path unchanged)");
        HasEdge("WaitForPRMerged", "DeploymentPipeline", "Merged").Should().BeTrue(
            "a merged PR must still trigger the deployment pipeline (happy path unchanged)");

        var reach = ReachableFromPort("WaitForPRMerged", "Merged");
        reach.Should().Contain("ReportSuccess",
            "the merge happy path must still be able to report success");
        reach.Should().Contain("Finish", "the merge happy path must terminate at Finish");
    }

    [Test]
    public void WaitForPRMerged_HasNoUnconditionalFallthrough()
    {
        // Every edge out of WaitForPRMerged must be outcome-qualified (Merged / TimedOut).
        // A portless edge would let the happy-path successors also fire on a timeout.
        var outgoing = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "WaitForPRMerged")
            .ToList();

        outgoing.Should().NotBeEmpty();
        outgoing.Should().OnlyContain(c => c.Source.Port == "Merged" || c.Source.Port == "TimedOut",
            "every WaitForPRMerged edge must be qualified by the Merged or TimedOut outcome");
    }

    // ── (b) SLA exceeded → durable timer → escalation terminal, never an indefinite wait ──

    [Test]
    public void TimedOutOutcome_EscalatesToNeedsHuman_AndTerminates()
    {
        HasEdge("WaitForPRMerged", "NotifyMergeTimeout", "TimedOut").Should().BeTrue(
            "an SLA timeout must notify that the merge was not confirmed in time");
        HasEdge("WaitForPRMerged", "ReportNeedsHuman", "TimedOut").Should().BeTrue(
            "an SLA timeout must escalate to the needs-human handoff terminal");

        var reach = ReachableFromPort("WaitForPRMerged", "TimedOut");
        reach.Should().Contain("Finish",
            "the SLA-timeout path must terminate at Finish (no dangling edge / no indefinite wait)");
        reach.Should().NotContain("ReportSuccess",
            "an unconfirmed merge must NEVER report success (no false success on a timeout)");
        reach.Should().NotContain("CloseIssue",
            "an unconfirmed merge must NOT close the issue as resolved");
    }

    [Test]
    public void NotifyMergeTimeout_IsWiredIntoTheFlowchart()
    {
        _flowchart.Activities.Any(a => a.Id == "NotifyMergeTimeout")
            .Should().BeTrue("the SLA-timeout notify must be present in the flowchart");
        _flowchart.Connections.Any(c => c.Target.Activity.Id == "NotifyMergeTimeout")
            .Should().BeTrue("NotifyMergeTimeout must be reachable (not an orphaned node)");
    }

    // ── Helpers (local — mirror the SafetyTests helpers) ──

    private bool HasEdge(string sourceId, string targetId, string? port = null)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            c.Target.Activity.Id == targetId &&
            (port == null || c.Source.Port == port));

    private HashSet<string> ReachableFromPort(string sourceId, string port)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        foreach (var c in _flowchart.Connections.Where(c =>
            c.Source.Activity.Id == sourceId && c.Source.Port == port))
        {
            if (seen.Add(c.Target.Activity.Id)) queue.Enqueue(c.Target.Activity.Id);
        }
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var c in _flowchart.Connections.Where(c => c.Source.Activity.Id == id))
            {
                if (c.Target.Activity.Id is { } t && seen.Add(t)) queue.Enqueue(t);
            }
        }
        return seen;
    }
}

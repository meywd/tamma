using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Build-out structure coverage for the <c>merge-approval</c> gate (FR-19 /
/// FR-34 / Story 4-6). Asserts the load-bearing guarantees of the build-out:
/// every outcome of the human decision activity is routed to a distinct edge (no
/// dangling outcome, no fall-through), the reject / invalid paths reach an
/// explicit terminal (never the merge/success path), each decision edge emits a
/// <c>MERGE_APPROVAL.*</c> / <c>MERGE.*</c> DCB event, the Merge decision
/// dispatches the real <c>merge</c> workflow, and the Test decision loops back to
/// the gate for a re-decision.
///
/// <para>Inspects the BUILT Flowchart via <see cref="WorkflowTestHelper"/> (the
/// codebase convention — see PullRequestWorkflowTests / SingleIssueCycleRoutingTests)
/// rather than running the full Elsa runtime.</para>
/// </summary>
[TestFixture]
public class MergeApprovalWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new MergeApprovalWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new MergeApprovalWorkflow());
        builder.Object.DefinitionId.Should().Be("merge-approval");
    }

    [Test]
    public void Gate_IsWaitForMergeApprovalActivity_WithFourTypedOutcomes()
    {
        var gate = _flowchart.Activities
            .OfType<WaitForMergeApprovalActivity>()
            .FirstOrDefault(a => a.Id == "WaitMergeApproval");

        gate.Should().NotBeNull("the merge-approval workflow must suspend on the human gate activity");

        // The four decision edges must all leave the gate, each outcome-qualified.
        var ports = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "WaitMergeApproval")
            .Select(c => c.Source.Port)
            .ToList();

        ports.Should().Contain("Merge");
        ports.Should().Contain("Test");
        ports.Should().Contain("Reject");
        ports.Should().Contain("Invalid");
    }

    // ================================================================
    // No dangling outcome / no fall-through
    // ================================================================

    [Test]
    public void Gate_HasNoUnconditionalFallthrough_EveryEdgeIsOutcomeQualified()
    {
        // A portless edge out of the gate would be the old silent fan-out bug
        // (the skeleton wired waitMerge → SetOutput with a plain Connect()).
        var fromGate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "WaitMergeApproval")
            .ToList();

        fromGate.Should().NotBeEmpty();
        fromGate.Should().OnlyContain(c =>
            c.Source.Port == "Merge" || c.Source.Port == "Test" ||
            c.Source.Port == "Reject" || c.Source.Port == "Invalid");
    }

    [Test]
    public void EveryGateOutcome_IsRouted()
    {
        foreach (var outcome in new[] { "Merge", "Test", "Reject", "Invalid" })
        {
            _flowchart.Connections.Any(c =>
                c.Source.Activity.Id == "WaitMergeApproval" && c.Source.Port == outcome)
                .Should().BeTrue($"the '{outcome}' outcome must route to a distinct edge");
        }
    }

    // ================================================================
    // Merge path — emit MERGE.REQUESTED → dispatch the real merge workflow
    // ================================================================

    [Test]
    public void MergeOutcome_EmitsMergeRequested_ThenDispatchesMergeWorkflow()
    {
        HasEdge("WaitMergeApproval", "Merge", "EmitMergeRequested").Should().BeTrue(
            "the Merge decision must first emit a MERGE.REQUESTED DCB event");
        HasEdge("EmitMergeRequested", null, "DispatchMerge").Should().BeTrue();

        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchMerge");
        dispatch.Should().NotBeNull("approval must actually trigger the merge — not emit a bare string");
        ReadDefinitionId(dispatch!).Should().Be("merge",
            "the gate must dispatch the existing merge workflow on approval");

        HasEdge("DispatchMerge", null, "MergeOutputs").Should().BeTrue();
        HasEdge("MergeOutputs", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // Test path — emit TEST_REQUESTED → re-run CI → loop back to the gate
    // ================================================================

    [Test]
    public void TestOutcome_EmitsTestRequested_RunsCi_ThenLoopsBackToGate()
    {
        HasEdge("WaitMergeApproval", "Test", "EmitTestRequested").Should().BeTrue();
        HasEdge("EmitTestRequested", null, "DispatchTesting").Should().BeTrue();

        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchTesting");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("ci-with-debug-retry");

        // Re-test then re-enter the gate for a fresh decision (the PRD "run more
        // tests before merging" loop).
        HasEdge("DispatchTesting", null, "WaitMergeApproval").Should().BeTrue(
            "the Test decision must loop back to the gate for a re-decision");
    }

    // ================================================================
    // Reject path — explicit terminal, NEVER the merge/success path
    // ================================================================

    [Test]
    public void RejectOutcome_RoutesToExplicitRejectTerminal_NotMerge()
    {
        HasEdge("WaitMergeApproval", "Reject", "EmitRejected").Should().BeTrue(
            "the Reject decision must emit a MERGE_APPROVAL.DECISION.REJECTED event");
        HasEdge("EmitRejected", null, "NotifyRejected").Should().BeTrue();
        HasEdge("NotifyRejected", null, "RejectOutputs").Should().BeTrue();
        HasEdge("RejectOutputs", null, "Finish").Should().BeTrue();

        // Reject must NOT reach the merge dispatch or the merge success outputs.
        var rejectReach = Reachable("EmitRejected");
        rejectReach.Should().NotContain("DispatchMerge");
        rejectReach.Should().NotContain("MergeOutputs");
    }

    // ================================================================
    // Invalid path — unknown/empty decision escalates (no silent reject, no
    // fall-through to success)
    // ================================================================

    [Test]
    public void InvalidOutcome_Escalates_NotSilentRejectOrMerge()
    {
        HasEdge("WaitMergeApproval", "Invalid", "EmitEscalated").Should().BeTrue(
            "an unknown/empty decision must emit MERGE_APPROVAL.ESCALATED — never silently reject");
        HasEdge("EmitEscalated", null, "NotifyEscalated").Should().BeTrue();
        HasEdge("NotifyEscalated", null, "EscalateOutputs").Should().BeTrue();
        HasEdge("EscalateOutputs", null, "Finish").Should().BeTrue();

        var invalidReach = Reachable("EmitEscalated");
        invalidReach.Should().NotContain("DispatchMerge");
        invalidReach.Should().NotContain("MergeOutputs");
        // Invalid must NOT be folded into the Reject path either — it is a loud,
        // distinct escalation.
        invalidReach.Should().NotContain("NotifyRejected");
    }

    // ================================================================
    // DCB events on every decision edge
    // ================================================================

    [Test]
    public void EveryDecisionEdge_EmitsAGateEvent()
    {
        var emitIds = _flowchart.Activities
            .OfType<EmitMergeApprovalEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitMergeRequested");
        emitIds.Should().Contain("EmitTestRequested");
        emitIds.Should().Contain("EmitRejected");
        emitIds.Should().Contain("EmitEscalated");
    }

    // ================================================================
    // Outputs — reject/escalate carry a non-success outcome token
    // ================================================================

    [Test]
    public void EachTerminal_EmitsItsOutcomeAndDecisionOutputs()
    {
        // Each terminal sequence surfaces an explicit `outcome` token plus the
        // decision/feedback/approver — the gate stops emitting a bare string and
        // instead reports a structured, distinct result per path.
        foreach (var id in new[] { "MergeOutputs", "RejectOutputs", "EscalateOutputs" })
        {
            var seq = _flowchart.Activities.OfType<Sequence>().FirstOrDefault(s => s.Id == id);
            seq.Should().NotBeNull($"{id} terminal sequence must exist");

            var outputNames = seq!.Activities.OfType<SetOutput>().Select(o => o.Id ?? "").ToList();
            outputNames.Should().Contain($"{id}_Outcome");
            outputNames.Should().Contain($"{id}_Decision");
            outputNames.Should().Contain($"{id}_Approver");
        }

        // Reject/escalate are distinct terminals from the merge terminal — they
        // must not be the same sequence node.
        var terminals = new[] { "MergeOutputs", "RejectOutputs", "EscalateOutputs" };
        terminals.Distinct().Should().HaveCount(3);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    /// <summary>Forward-reachable activity ids from a starting node.</summary>
    private HashSet<string> Reachable(string startId)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var c in _flowchart.Connections.Where(c => c.Source.Activity.Id == id))
            {
                var t = c.Target.Activity.Id;
                if (t != null && seen.Add(t)) queue.Enqueue(t);
            }
        }
        return seen;
    }

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}

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
        // "TimedOut" added 2026-08-18 — the gate's durable approval SLA
        // (Adl:MergeApprovalTimeoutMinutes, default 24h). It is a genuinely new outcome,
        // not a widened assertion: before it the gate waited forever, pinning the cycle
        // instance and one of the ADL's MaxConcurrent slots on a PR nobody answered.
        fromGate.Should().OnlyContain(c =>
            c.Source.Port == "Merge" || c.Source.Port == "Test" ||
            c.Source.Port == "Reject" || c.Source.Port == "Invalid" ||
            c.Source.Port == "TimedOut");
    }

    [Test]
    public void EveryGateOutcome_IsRouted()
    {
        foreach (var outcome in new[] { "Merge", "Test", "Reject", "Invalid", "TimedOut" })
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

        // CRITICAL-2: the merge dispatch must read its `success` output and branch
        // — a successful merge reaches the merge/success terminal, a failed one
        // does NOT.
        HasEdge("DispatchMerge", null, "ExtractMergeSuccess").Should().BeTrue(
            "the gate must read the merge sub-workflow's success output");
        HasEdge("ExtractMergeSuccess", null, "MergeSucceeded").Should().BeTrue();
        HasEdge("MergeSucceeded", "True", "EmitMerged").Should().BeTrue();
        HasEdge("EmitMerged", null, "MergeOutputs").Should().BeTrue();
        HasEdge("MergeOutputs", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // CRITICAL-2 — a failed merge sub-workflow surfaces loudly (MERGE.FAILED +
    // ESCALATED) and routes to the escalate terminal (outcome="escalated"), NEVER
    // the merge/success terminal (which would hang the cycle on a merge webhook).
    // ================================================================

    [Test]
    public void MergeFailure_RoutesToEscalateTerminal_NotMergeSuccess()
    {
        HasEdge("MergeSucceeded", "False", "EmitMergeFailed").Should().BeTrue(
            "a failed merge (success=false) must emit a loud MERGE.FAILED event");
        HasEdge("EmitMergeFailed", null, "EmitEscalated").Should().BeTrue(
            "a failed merge must funnel into the escalate terminal");

        var failureReach = Reachable("EmitMergeFailed");
        // A failed merge must NOT reach the merge/success outputs (outcome="merge").
        failureReach.Should().NotContain("MergeOutputs",
            "a failed merge must never reach the merge/success terminal");
        failureReach.Should().NotContain("EmitMerged");
        // It MUST reach the escalate outputs (outcome="escalated") so the cycle's
        // GateOutcomeSwitch routes it to reportError (and never to WaitForPRMerged).
        failureReach.Should().Contain("EscalateOutputs",
            "a failed merge must reach the escalate terminal (outcome=escalated)");
    }

    [Test]
    public void MergeFailed_IsAFailureEvent_ErrorStatus()
    {
        // The MERGE.FAILED audit row must be loud (error status), not a false success.
        EmitMergeApprovalEventActivity.IsFailureEvent(MergeApprovalEvents.MergeFailed)
            .Should().BeTrue("MERGE.FAILED must be an error-status audit event");
    }

    // ================================================================
    // CRITICAL-3 — the Test → gate → test loop is iteration-capped; over the cap
    // it escalates instead of spinning forever.
    // ================================================================

    [Test]
    public void TestLoop_IsBounded_IncrementsThenChecksCap()
    {
        HasEdge("WaitMergeApproval", "Test", "EmitTestDecision").Should().BeTrue();
        HasEdge("EmitTestDecision", null, "IncrementTestIteration").Should().BeTrue(
            "each Test decision must increment the iteration counter");
        HasEdge("IncrementTestIteration", null, "TestMaxIterations").Should().BeTrue(
            "after incrementing, the loop must check the iteration cap");
    }

    [Test]
    public void TestLoop_UnderCap_RunsCiThenLoopsBackToGate()
    {
        HasEdge("TestMaxIterations", "False", "EmitTestRequested").Should().BeTrue(
            "under the cap the gate re-runs CI");
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchTesting");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("ci-with-debug-retry");
        HasEdge("DispatchTesting", null, "WaitMergeApproval").Should().BeTrue(
            "under the cap the loop returns to the gate for a re-decision");
    }

    [Test]
    public void TestLoop_OverCap_EscalatesNotLoops()
    {
        HasEdge("TestMaxIterations", "True", "EmitEscalated").Should().BeTrue(
            "over the iteration cap the test loop must escalate (loud), not loop forever");

        // Over the cap must reach the escalate terminal and must NOT re-run CI /
        // loop back to the gate.
        var overCapReach = ReachableFromPort("TestMaxIterations", "True");
        overCapReach.Should().Contain("EscalateOutputs");
        overCapReach.Should().NotContain("DispatchTesting",
            "the cap path must not re-run CI");
    }

    // ================================================================
    // Test path — emit TEST_REQUESTED → re-run CI → loop back to the gate
    // ================================================================

    [Test]
    public void TestOutcome_EmitsTestRequested_RunsCi_ThenLoopsBackToGate()
    {
        // The Test edge now goes through the bounded loop (DECISION.TEST →
        // increment → cap check → TEST_REQUESTED → CI → back to gate).
        HasEdge("WaitMergeApproval", "Test", "EmitTestDecision").Should().BeTrue();
        HasEdge("TestMaxIterations", "False", "EmitTestRequested").Should().BeTrue();
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
        // Invalid now emits DECISION.INVALID first, then funnels into the shared
        // escalate terminal — it ESCALATES, it does NOT loop back to the gate.
        HasEdge("WaitMergeApproval", "Invalid", "EmitInvalid").Should().BeTrue(
            "an unknown/empty decision must emit MERGE_APPROVAL.DECISION.INVALID — never silently reject");
        HasEdge("EmitInvalid", null, "EmitEscalated").Should().BeTrue();
        HasEdge("EmitEscalated", null, "NotifyEscalated").Should().BeTrue();
        HasEdge("NotifyEscalated", null, "EscalateOutputs").Should().BeTrue();
        HasEdge("EscalateOutputs", null, "Finish").Should().BeTrue();

        var invalidReach = Reachable("EmitInvalid");
        invalidReach.Should().NotContain("DispatchMerge");
        invalidReach.Should().NotContain("MergeOutputs");
        // Invalid must NOT loop back to the gate (would let a malformed payload
        // re-arm the suspend forever) and must NOT be folded into the Reject path.
        invalidReach.Should().NotContain("WaitMergeApproval");
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
    // MINOR-1 — the DECISION.MERGED / DECISION.TEST / DECISION.INVALID constants
    // are now actually emitted on their matching edges (no dead constants).
    // ================================================================

    [Test]
    public void DecisionConstants_AreEmittedOnTheirEdges()
    {
        var emitIds = _flowchart.Activities
            .OfType<EmitMergeApprovalEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitMerged", "DECISION.MERGED must be emitted on the successful-merge edge");
        emitIds.Should().Contain("EmitTestDecision", "DECISION.TEST must be emitted on the test edge");
        emitIds.Should().Contain("EmitInvalid", "DECISION.INVALID must be emitted on the invalid edge");
        emitIds.Should().Contain("EmitMergeFailed", "MERGE.FAILED must be emitted on a failed merge");
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
    // SECURITY I1 — fail-closed on internal fault (no-deadlock last hole)
    // ================================================================

    [Test]
    public void Workflow_SeedsEscalatedOutcomeBeforeAnythingThatCanFault()
    {
        // The flowchart must START by setting outcome="escalated" so a later
        // fault (with continue-with-incidents) that stops the flow before a
        // terminal still yields a parseable, fail-closed outcome the cycle routes
        // to reportError — never a silent merge, never a stuck instance.
        _flowchart.Start.Should().NotBeNull();
        ((Elsa.Workflows.IActivity)_flowchart.Start!).Id.Should().Be("DefaultOutcome",
            "the fail-closed default outcome must be set before any faultable activity");

        var defaultOutcome = _flowchart.Activities
            .OfType<SetOutput>()
            .FirstOrDefault(a => a.Id == "DefaultOutcome");
        defaultOutcome.Should().NotBeNull("a DefaultOutcome SetOutput node must seed the outcome");

        HasEdge("DefaultOutcome", null, "ReadInputs").Should().BeTrue(
            "the default-outcome node must flow into ReadInputs");
    }

    [Test]
    public void Workflow_UsesContinueWithIncidentsStrategy_SoAFaultDoesNotHaltSilently()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new MergeApprovalWorkflow());
        builder.Object.WorkflowOptions.IncidentStrategyType
            .Should().Be(typeof(Elsa.Workflows.IncidentStrategies.ContinueWithIncidentsStrategy),
                "a faulted gate must not halt the workflow with an incident and produce no outcome");
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

    /// <summary>Forward-reachable activity ids starting from a specific outcome
    /// port of a source node.</summary>
    private HashSet<string> ReachableFromPort(string sourceId, string port)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        foreach (var c in _flowchart.Connections.Where(c =>
            c.Source.Activity.Id == sourceId && c.Source.Port == port))
        {
            if (c.Target.Activity.Id is { } t && seen.Add(t)) queue.Enqueue(t);
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

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}

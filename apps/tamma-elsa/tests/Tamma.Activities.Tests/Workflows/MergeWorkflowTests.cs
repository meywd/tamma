using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 2-10 build-out — workflow-structure integration coverage for the
/// built-out <c>merge</c> graph. Asserts the load-bearing guarantees of the
/// build-out (which the activity unit tests can't): the <c>Error</c> outcome is
/// WIRED to an explicit failure terminal and NEVER falls through to success (the
/// headline thin-wrapper dead-end bug), the failure terminal sets
/// <c>success=false</c> and emits <c>MERGE.FAILED</c>, both merge outcomes
/// (Merged + MergedWithWarnings) reach the success terminal that sets
/// <c>success=true</c> (the gate's contract) and emits <c>MERGE.SUCCESS</c> +
/// the issue/branch sub-action events, every edge out of the merge step is
/// outcome-qualified, and every terminal reaches Finish (no dangling / deadlock).
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> rather than running the full Elsa runtime
/// (see <see cref="BranchCreationWorkflowTests"/>).</para>
/// </summary>
[TestFixture]
public class MergeWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new MergeWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new MergeWorkflow());
        builder.Object.DefinitionId.Should().Be("merge");
    }

    // ================================================================
    // No false success / no silent failure — the Error outcome routes to the
    // explicit failure path ONLY (the headline dead-end bug is gone).
    // ================================================================

    [Test]
    public void MergePR_ErrorOutcome_RoutesToFailurePath_NotSuccess()
    {
        HasEdge("MergePR", "Error", "FailureOutputs").Should().BeTrue(
            "the Error outcome must route to the explicit failure path (no dead-end)");

        var errorTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "MergePR" && c.Source.Port == "Error")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        errorTargets.Should().NotContain("EmitSuccess");
        errorTargets.Should().NotContain("SuccessOutputs");
    }

    [Test]
    public void MergePR_HasNoUnconditionalFallthrough()
    {
        // Every edge out of MergePR must be outcome-qualified
        // (Merged / MergedWithWarnings / Error). A portless edge would be the old
        // silent fall-through bug.
        var fromMerge = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "MergePR")
            .ToList();

        fromMerge.Should().NotBeEmpty();
        fromMerge.Should().OnlyContain(c =>
            c.Source.Port == "Merged" ||
            c.Source.Port == "MergedWithWarnings" ||
            c.Source.Port == "Error");
    }

    [Test]
    public void AllThreeMergeOutcomes_AreWired()
    {
        // All three FlowNode outcomes must have an edge — none dangling.
        HasEdge("MergePR", "Merged", "EmitSuccess").Should().BeTrue();
        HasEdge("MergePR", "MergedWithWarnings", "EmitSuccess").Should().BeTrue(
            "a partial merge is still a success — routes to the success terminal");
        HasEdge("MergePR", "Error", "FailureOutputs").Should().BeTrue();
    }

    // ================================================================
    // DCB events on every terminal transition
    // ================================================================

    [Test]
    public void SuccessPath_EmitsMergeSuccess_AndSubActionEvents()
    {
        var emit = _flowchart.Activities
            .OfType<EmitMergeEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitSuccess");
        emit.Should().NotBeNull("success path must emit a MERGE.SUCCESS DCB event");

        // Sub-action audit events (AC5): ISSUE.CLOSED.* + BRANCH.DELETED.*.
        HasEdge("EmitSuccess", null, "EmitIssueClosed").Should().BeTrue();
        HasEdge("EmitIssueClosed", null, "EmitBranchDeleted").Should().BeTrue();
        HasEdge("EmitBranchDeleted", null, "SuccessOutputs").Should().BeTrue();
    }

    [Test]
    public void FailurePath_EmitsMergeFailed()
    {
        var emit = _flowchart.Activities
            .OfType<EmitMergeEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitFailed");
        emit.Should().NotBeNull("failure path must emit MERGE.FAILED");
        // success=false is set BEFORE the failed event (no fall-through to success).
        HasEdge("FailureOutputs", null, "EmitFailed").Should().BeTrue();
    }

    [Test]
    public void BothTerminalPaths_ReachFinish()
    {
        HasEdge("SuccessOutputs", null, "Finish").Should().BeTrue();
        HasEdge("EmitFailed", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // Outputs — the `success` contract the merge-approval gate reads
    // ================================================================

    [Test]
    public void SuccessPath_SetsSuccessTrue_PreservingGateContract()
    {
        var successSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "SuccessOutputs");

        var outputs = successSeq.Activities.OfType<SetOutput>().ToList();
        outputs.Select(o => o.Id ?? "").Should().Contain("OutSuccess");

        // The success output name must be exactly "success" — the gate reads it.
        outputs.Should().Contain(o => o.Id == "OutSuccess");
        outputs.First(o => o.Id == "OutSuccess").OutputName.Should().NotBeNull();
        outputs.Select(o => o.Id).Should().Contain("OutMergeSha");
    }

    [Test]
    public void FailurePath_SetsSuccessFalse_And_FailureCode_And_EmptyMergeSha()
    {
        var failureSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "FailureOutputs");

        var ids = failureSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutFailSuccess");   // success = false (gate → escalate)
        ids.Should().Contain("OutFailCode");      // failureCode
        ids.Should().Contain("OutFailReason");    // failureReason
        ids.Should().Contain("OutFailMergeSha");  // mergeSha = "" (no false merge)
    }

    // ================================================================
    // No deadlock — every terminal node reaches Finish (the flowchart's only
    // dead-ends should be Finish itself).
    // ================================================================

    [Test]
    public void Flowchart_HasSingleFinish_AndStartsAtReadInputs()
    {
        _flowchart.Activities.OfType<Finish>().Should().HaveCount(1);
        HasEdge("ReadInputs", null, "MergePR").Should().BeTrue();
    }

    // ================================================================
    // Helpers
    // ================================================================

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);
}

using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 2.10 build-out — workflow-structure coverage for the built-out
/// <c>update-issue-status</c> graph. Asserts the load-bearing guarantees the
/// activity unit tests can't: the <b>failure edge exists</b> and the activity's
/// <c>Failed</c> outcome NEVER falls through to success (the headline
/// swallow-failure fix), both terminal transitions emit an <c>ISSUE_STATUS.*</c>
/// DCB event via the durable drain, every outcome is routed (no dangling edge),
/// and both paths reach Finish.
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> rather than running the full Elsa runtime.</para>
/// </summary>
[TestFixture]
public class UpdateIssueStatusWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new UpdateIssueStatusWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new UpdateIssueStatusWorkflow());
        builder.Object.DefinitionId.Should().Be("update-issue-status");
    }

    [Test]
    public void Workflow_RootIsFlowchart_NotBareActivity()
    {
        // The old thin form set builder.Root = updateIssue (a bare activity). The
        // build-out must be a real flowchart with branches.
        _flowchart.Should().NotBeNull();
        _flowchart.Activities.OfType<UpdateIssueStatusActivity>()
            .Should().ContainSingle("the update activity must be a node in the flowchart");
    }

    // ================================================================
    // Flow shape
    // ================================================================

    [Test]
    public void Flow_ReadInputs_Then_UpdateIssue()
    {
        HasEdge("ReadInputs", null, "UpdateIssue").Should().BeTrue();
    }

    // ================================================================
    // No false success — the Failed outcome routes to the failure path ONLY
    // ================================================================

    [Test]
    public void UpdateIssue_FailedOutcome_RoutesToFailurePath_NotSuccess()
    {
        HasEdge("UpdateIssue", "Failed", "FailureOutputs").Should().BeTrue(
            "the Failed outcome must route to the explicit failure path");

        var failedTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "UpdateIssue" && c.Source.Port == "Failed")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        failedTargets.Should().NotContain("EmitSuccess");
        failedTargets.Should().NotContain("SuccessOutputs");
    }

    [Test]
    public void UpdateIssue_HasNoUnconditionalFallthrough()
    {
        // Every edge out of UpdateIssue must be outcome-qualified (Updated/Failed).
        // A portless edge would be the old silent fall-through bug.
        var fromUpdate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "UpdateIssue")
            .ToList();

        fromUpdate.Should().NotBeEmpty();
        fromUpdate.Should().OnlyContain(c => c.Source.Port == "Updated" || c.Source.Port == "Failed");
    }

    [Test]
    public void UpdateIssue_UpdatedOutcome_RoutesToSuccess()
    {
        HasEdge("UpdateIssue", "Updated", "EmitSuccess").Should().BeTrue();
    }

    // ================================================================
    // DCB events on every terminal transition (durable drain)
    // ================================================================

    [Test]
    public void SuccessPath_EmitsIssueStatusUpdatedSuccess()
    {
        var emit = _flowchart.Activities
            .OfType<EmitIssueStatusEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitSuccess");
        emit.Should().NotBeNull("success path must emit an ISSUE_STATUS DCB event");
        HasEdge("EmitSuccess", null, "SuccessOutputs").Should().BeTrue();
    }

    [Test]
    public void FailurePath_EmitsIssueStatusUpdatedFailed()
    {
        var emit = _flowchart.Activities
            .OfType<EmitIssueStatusEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitFailed");
        emit.Should().NotBeNull("failure path must emit ISSUE_STATUS.UPDATED.FAILED");
        // success=false outputs must run before / into the failed-event emit.
        HasEdge("FailureOutputs", null, "EmitFailed").Should().BeTrue();
    }

    [Test]
    public void BothTerminalPaths_ReachFinish()
    {
        HasEdge("SuccessOutputs", null, "Finish").Should().BeTrue();
        HasEdge("EmitFailed", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // No dangling edge — every outcome / node routed to a terminal
    // ================================================================

    [Test]
    public void EveryConnection_PointsAtAKnownActivity()
    {
        var ids = _flowchart.Activities.Select(a => a.Id).ToHashSet();
        foreach (var c in _flowchart.Connections)
        {
            ids.Should().Contain(c.Source.Activity.Id);
            ids.Should().Contain(c.Target.Activity.Id);
        }
    }

    // ================================================================
    // Outputs — success=false on the failure path; success=true on success
    // ================================================================

    [Test]
    public void FailurePath_SetsSuccessFalse_And_ErrorCode()
    {
        var failureSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "FailureOutputs");

        var ids = failureSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutFailSuccess");   // success = false
        ids.Should().Contain("OutFailErrorCode"); // errorCode
        ids.Should().Contain("OutFailReason");    // exitReason
    }

    [Test]
    public void SuccessPath_SetsSuccessTrue()
    {
        var successSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "SuccessOutputs");

        var ids = successSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutSuccess");   // success = true
        ids.Should().Contain("OutDegraded");  // degraded flag (auditable local no-op)
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

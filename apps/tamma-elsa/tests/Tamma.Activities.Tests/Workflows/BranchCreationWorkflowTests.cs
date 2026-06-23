using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 2.4 build-out — workflow-structure integration coverage for the built-out
/// <c>branch-creation</c> graph. Asserts the load-bearing guarantees of the
/// build-out (which the activity unit tests can't): the <c>Error</c> outcome NEVER
/// falls through to success, both terminal transitions emit a <c>BRANCH.*</c> DCB
/// event, the failure path sets <c>success=false</c> and surfaces an
/// <c>errorCode</c>, and every edge out of the create step is outcome-qualified
/// (no portless silent fall-through — the headline "thin wrapper" bug).
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> rather than running the full Elsa runtime
/// (see <see cref="PullRequestWorkflowTests"/>).</para>
/// </summary>
[TestFixture]
public class BranchCreationWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new BranchCreationWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new BranchCreationWorkflow());
        builder.Object.DefinitionId.Should().Be("branch-creation");
    }

    // ================================================================
    // No false success — the Error outcome routes to the failure path ONLY
    // ================================================================

    [Test]
    public void CreateBranch_ErrorOutcome_RoutesToFailurePath_NotSuccess()
    {
        HasEdge("CreateBranch", "Error", "FailureOutputs").Should().BeTrue(
            "the Error outcome must route to the explicit failure path");

        var errorTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "CreateBranch" && c.Source.Port == "Error")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        errorTargets.Should().NotContain("EmitSuccess");
        errorTargets.Should().NotContain("SuccessOutputs");
    }

    [Test]
    public void CreateBranch_HasNoUnconditionalFallthrough()
    {
        // Every edge out of CreateBranch must be outcome-qualified (Created/Error).
        // A portless edge would be the old silent fall-through bug.
        var fromCreate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "CreateBranch")
            .ToList();

        fromCreate.Should().NotBeEmpty();
        fromCreate.Should().OnlyContain(c =>
            c.Source.Port == "Created" || c.Source.Port == "Error");
    }

    [Test]
    public void CreatedOutcome_RoutesToSuccess()
    {
        HasEdge("CreateBranch", "Created", "EmitSuccess").Should().BeTrue();
    }

    // ================================================================
    // DCB events on every terminal transition
    // ================================================================

    [Test]
    public void SuccessPath_EmitsBranchCreatedSuccess()
    {
        var emit = _flowchart.Activities
            .OfType<EmitBranchEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitSuccess");
        emit.Should().NotBeNull("success path must emit a BRANCH DCB event");
        HasEdge("EmitSuccess", null, "SuccessOutputs").Should().BeTrue();
    }

    [Test]
    public void FailurePath_EmitsBranchCreatedFailed()
    {
        var emit = _flowchart.Activities
            .OfType<EmitBranchEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitFailed");
        emit.Should().NotBeNull("failure path must emit BRANCH.CREATED.FAILED");
        HasEdge("FailureOutputs", null, "EmitFailed").Should().BeTrue();
    }

    [Test]
    public void BothTerminalPaths_ReachFinish()
    {
        HasEdge("SuccessOutputs", null, "Finish").Should().BeTrue();
        HasEdge("EmitFailed", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // Outputs — success=false on the failure path; branchName/baseSha on success
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

        ids.Should().Contain("OutFailSuccess");    // success = false
        ids.Should().Contain("OutFailErrorCode");  // errorCode
        ids.Should().Contain("OutFailBranchName"); // branchName = "" (no false branch)
    }

    [Test]
    public void SuccessPath_ExposesOutputs_BranchNameBaseShaSuccess()
    {
        var successSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "SuccessOutputs");

        var ids = successSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutSuccess");     // success = true
        ids.Should().Contain("OutBranchName");  // branchName
        ids.Should().Contain("OutBaseSha");     // baseSha (AC4)
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

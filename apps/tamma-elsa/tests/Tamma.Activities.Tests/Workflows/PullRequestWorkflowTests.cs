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
/// Story 2.8 AC7 — workflow-structure integration coverage for the built-out
/// <c>pull-request</c> graph. Asserts the load-bearing guarantees of the
/// build-out (which the activity unit tests can't): the failure edge exists and
/// the <c>Error</c> outcome NEVER falls through to success, both terminal
/// transitions emit a <c>PR.*</c> DCB event, the description is LLM-mediated
/// (dispatch → <c>llm-call</c>, never a raw provider call), and the new
/// draft / base / head / linkedIssue outputs are present.
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> rather than running the full Elsa runtime
/// (see SingleIssueCycleRoutingTests).</para>
/// </summary>
[TestFixture]
public class PullRequestWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new PullRequestWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new PullRequestWorkflow());
        builder.Object.DefinitionId.Should().Be("pull-request");
    }

    // ================================================================
    // LLM mediation — description must go through the llm-call sub-workflow
    // ================================================================

    [Test]
    public void DescriptionGeneration_IsMediatedThroughLlmCall()
    {
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "GenerateDescription");

        dispatch.Should().NotBeNull("description generation must be a DispatchWorkflow step");
        // The dispatch must target the llm-call sub-workflow (the mediation seam),
        // never a raw provider call inside an activity.
        var defId = ReadDefinitionId(dispatch!);
        defId.Should().Be("llm-call");
    }

    [Test]
    public void Flow_ReadInputs_Then_GenerateDescription_Then_CreatePR()
    {
        HasEdge("ReadInputs", null, "GenerateDescription").Should().BeTrue();
        HasEdge("GenerateDescription", null, "CaptureDescription").Should().BeTrue();
        // Epic 31 P5 M2 (DG-3): the reviewers gate sits between the
        // description capture and the PR step — no reviewers requested (the
        // cycle's default) goes straight to CreatePR; a reviewer request
        // passes the §4 check step first (see DegradationPairsTests).
        HasEdge("CaptureDescription", null, "HasReviewers").Should().BeTrue();
        HasEdge("HasReviewers", "False", "CreatePR").Should().BeTrue();
    }

    // ================================================================
    // No false success — the Error outcome routes to the failure path ONLY
    // ================================================================

    [Test]
    public void CreatePR_ErrorOutcome_RoutesToFailurePath_NotSuccess()
    {
        // Error must go to FailureOutputs and NOWHERE near the success outputs.
        HasEdge("CreatePR", "Error", "FailureOutputs").Should().BeTrue(
            "the Error outcome must route to the explicit failure path");

        var errorTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "CreatePR" && c.Source.Port == "Error")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        errorTargets.Should().NotContain("EmitSuccess");
        errorTargets.Should().NotContain("SuccessOutputs");
    }

    [Test]
    public void CreatePR_HasNoUnconditionalFallthrough_ToSuccess()
    {
        // Every edge out of CreatePR must be outcome-qualified (Created/Updated/Error).
        // A portless edge would be the old silent fall-through bug.
        var fromCreate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "CreatePR")
            .ToList();

        fromCreate.Should().NotBeEmpty();
        fromCreate.Should().OnlyContain(c =>
            c.Source.Port == "Created" || c.Source.Port == "Updated" || c.Source.Port == "Error");
    }

    [Test]
    public void CreatedAndUpdated_BothRouteToSuccess()
    {
        HasEdge("CreatePR", "Created", "EmitSuccess").Should().BeTrue();
        HasEdge("CreatePR", "Updated", "EmitSuccess").Should().BeTrue(
            "the idempotency reuse/update path must also report success");
    }

    // ================================================================
    // DCB events on every terminal transition
    // ================================================================

    [Test]
    public void SuccessPath_EmitsPrCreatedSuccess()
    {
        var emit = _flowchart.Activities
            .OfType<EmitPrEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitSuccess");
        emit.Should().NotBeNull("success path must emit a PR DCB event");
        HasEdge("EmitSuccess", null, "SuccessOutputs").Should().BeTrue();
    }

    [Test]
    public void FailurePath_EmitsPrCreatedFailed()
    {
        var emit = _flowchart.Activities
            .OfType<EmitPrEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitFailed");
        emit.Should().NotBeNull("failure path must emit PR.CREATED.FAILED");
        // failure outputs (success=false) must run before / into the failed-event emit
        HasEdge("FailureOutputs", null, "EmitFailed").Should().BeTrue();
    }

    [Test]
    public void BothTerminalPaths_ReachFinish()
    {
        HasEdge("SuccessOutputs", null, "Finish").Should().BeTrue();
        HasEdge("EmitFailed", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // Outputs — success=false on the failure path; new draft/base/head/linkedIssue
    // ================================================================

    [Test]
    public void FailurePath_SetsSuccessFalse_And_ExitReason()
    {
        var failureSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "FailureOutputs");

        var ids = failureSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutFailSuccess");   // success = false
        ids.Should().Contain("OutFailReason");    // exitReason = pr-creation-failed
        ids.Should().Contain("OutFailErrorCode"); // errorCode
    }

    [Test]
    public void SuccessPath_ExposesNewOutputs_DraftBaseHeadLinkedIssue()
    {
        var successSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "SuccessOutputs");

        var ids = successSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutSuccess");      // success = true
        ids.Should().Contain("OutPrNumber");
        ids.Should().Contain("OutPrUrl");
        ids.Should().Contain("OutIsDraft");      // §6.10 new output
        ids.Should().Contain("OutBaseBranch");   // §6.10 new output
        ids.Should().Contain("OutHeadBranch");   // §6.10 new output
        ids.Should().Contain("OutLinkedIssue");  // §6.10 new output
    }

    // ================================================================
    // Helpers
    // ================================================================

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}

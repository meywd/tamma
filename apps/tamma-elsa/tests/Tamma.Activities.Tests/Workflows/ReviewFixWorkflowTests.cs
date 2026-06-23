using Elsa.Extensions;
using Elsa.Workflows;
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
/// Build-out structure coverage for the <c>review-fix</c> workflow (Story 2-18
/// Phases 4 &amp; 5 / Story 2-9). Asserts the load-bearing guarantees of the
/// build-out:
/// <list type="bullet">
///   <item><description>a <b>graph-enforced</b> iteration bound on the fix loop —
///     over the cap routes to a loud escalate terminal (not an activity-internal
///     bound, not an infinite loop, not a silent success);</description></item>
///   <item><description>an explicit failure / exhaustion path — <c>AnalyzeReview</c>'s
///     <c>Error</c> outcome and a failed <c>llm-call</c> route to a loud
///     <c>OutputFailure</c> terminal (never fall through to success);</description></item>
///   <item><description>fail-closed apply — a failed fix-apply
///     (<c>fixesApplied=false</c> or the activity's <c>Error</c> outcome) routes
///     to <c>OutputFailure</c>, never reports <c>success=true</c>;</description></item>
///   <item><description><c>REVIEW_FIX.*</c> DCB events emitted on every meaningful
///     edge;</description></item>
///   <item><description>every outcome is routed (no dangling edge / no
///     deadlock).</description></item>
/// </list>
///
/// <para>Inspects the BUILT Flowchart via <see cref="WorkflowTestHelper"/> (the
/// codebase convention — see MergeApprovalWorkflowTests / PullRequestWorkflowTests)
/// rather than running the full Elsa runtime.</para>
/// </summary>
[TestFixture]
public class ReviewFixWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ReviewFixWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ReviewFixWorkflow());
        builder.Object.DefinitionId.Should().Be("review-fix");
    }

    [Test]
    public void Workflow_HasTenantIdVariable()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ReviewFixWorkflow());
        builder.Object.Variables.Any(v => v.Name == "TenantId").Should().BeTrue(
            "review-fix must carry a TenantId so REVIEW_FIX.* events are metered to the right tenant");
    }

    [Test]
    public void AllActivities_HaveDisplayText()
    {
        foreach (var activity in WorkflowTestHelper.GetAllActivities(_flowchart))
        {
            activity.GetDisplayText().Should().NotBeNullOrEmpty(
                $"Activity '{activity.GetType().Name}' (Id: {activity.Id}) should have DisplayText set");
        }
    }

    // ================================================================
    // The dispatched llm-call fix-generation is preserved (taxonomy drift guard
    // depends on DispatchFixGeneration emitting developer/address-review-comments)
    // ================================================================

    [Test]
    public void FixGeneration_DispatchesLlmCall_NotADirectProviderCall()
    {
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchFixGeneration");
        dispatch.Should().NotBeNull("fix generation must route through the mediated llm-call workflow");
        ReadDefinitionId(dispatch!).Should().Be("llm-call",
            "the LLM must stay mediated via llm-call — no direct provider call in a step");
    }

    // ================================================================
    // Graph-enforced iteration bound — over the cap escalates (loud), not infinite
    // ================================================================

    [Test]
    public void FixLoop_IsGraphEnforced_IncrementsThenChecksCap()
    {
        // The cap must be a FlowDecision IN THE GRAPH (not an activity-internal
        // bound) reached BEFORE the fix is generated.
        HasEdge("HasActionable", "True", "IncrementIteration").Should().BeTrue(
            "an actionable analysis must increment the iteration counter before generating fixes");
        HasEdge("IncrementIteration", null, "MaxIterations").Should().BeTrue(
            "after incrementing, the loop must check the iteration cap in the graph");

        _flowchart.Activities.OfType<FlowDecision>()
            .Any(fd => fd.Id == "MaxIterations")
            .Should().BeTrue("the iteration bound must be a graph FlowDecision, not buried in an activity");
    }

    [Test]
    public void FixLoop_UnderCap_GeneratesFixes()
    {
        HasEdge("MaxIterations", "False", "DispatchFixGeneration").Should().BeTrue(
            "under the cap the loop generates fixes via llm-call");
    }

    [Test]
    public void FixLoop_OverCap_EscalatesToLoudTerminal_NotSilentSuccess()
    {
        HasEdge("MaxIterations", "True", "EmitEscalated").Should().BeTrue(
            "over the iteration cap the fix loop must escalate (loud), not loop forever or succeed silently");

        var overCapReach = ReachableFromPort("MaxIterations", "True");
        // The escalation must reach the FAILURE terminal, never the success terminal.
        overCapReach.Should().Contain("OutputFailure",
            "an exhausted fix loop must reach the loud failure terminal");
        overCapReach.Should().NotContain("OutputSuccess",
            "an exhausted fix loop must NEVER reach the success terminal");
        overCapReach.Should().NotContain("DispatchFixGeneration",
            "the over-cap path must not re-generate fixes");
    }

    [Test]
    public void Escalated_IsAFailureEvent_ErrorStatus()
    {
        ReviewFixEvents.IsFailureEvent(ReviewFixEvents.Escalated)
            .Should().BeTrue("REVIEW_FIX.ESCALATED must be an error-status audit event");
    }

    // ================================================================
    // Explicit error path — AnalyzeReview.Error and a failed llm-call route loud
    // ================================================================

    [Test]
    public void AnalyzeError_RoutesToLoudFailureTerminal_NotDeadEnd()
    {
        HasEdge("AnalyzeReview", "Error", "EmitAnalyzeFailed").Should().BeTrue(
            "AnalyzeReview's Error outcome must emit REVIEW_FIX.ANALYZED.FAILED — not be a dead end");

        var errReach = Reachable("EmitAnalyzeFailed");
        errReach.Should().Contain("OutputFailure",
            "an analysis error must reach the loud failure terminal");
        errReach.Should().NotContain("OutputSuccess",
            "an analysis error must never reach the success terminal");
    }

    [Test]
    public void FixGenerationFailure_RoutesToLoudFailureTerminal_NotApply()
    {
        // The llm-call `success` output is read and branched — a failed generation
        // must NOT flow into "apply" as a false success.
        HasEdge("DispatchFixGeneration", null, "ExtractGenerateSuccess").Should().BeTrue(
            "the workflow must read the llm-call success output");
        HasEdge("ExtractGenerateSuccess", null, "GenerateSucceeded").Should().BeTrue();
        HasEdge("GenerateSucceeded", "False", "EmitGenerateFailed").Should().BeTrue(
            "a failed llm-call must emit REVIEW_FIX.GENERATED.FAILED");

        var genFailReach = Reachable("EmitGenerateFailed");
        genFailReach.Should().Contain("OutputFailure");
        genFailReach.Should().NotContain("ApplyFixes",
            "a failed llm-call must never flow into apply as a false success");
        genFailReach.Should().NotContain("OutputSuccess");
    }

    [Test]
    public void FixGenerationSuccess_EmitsEvent_ThenApplies()
    {
        HasEdge("GenerateSucceeded", "True", "EmitGenerateSuccess").Should().BeTrue();
        HasEdge("EmitGenerateSuccess", null, "ApplyFixes").Should().BeTrue(
            "a successful generation flows into the apply step");
    }

    // ================================================================
    // Fail-closed apply — a failed apply is NOT a silent success
    // ================================================================

    [Test]
    public void ApplyFixesFailure_RoutesToLoudFailureTerminal_NotSuccess()
    {
        // The apply activity's own Error outcome is loud.
        HasEdge("ApplyFixes", "Error", "EmitApplyFailed").Should().BeTrue(
            "ApplyFixes.Error must emit REVIEW_FIX.APPLIED.FAILED");

        // And a "Fixed" outcome that nonetheless did not apply files
        // (fixesApplied=false) is branched and treated as a failure — not a silent
        // success.
        HasEdge("ApplyFixes", "Fixed", "FixesApplied").Should().BeTrue(
            "a Fixed outcome must be gated on whether files were actually applied");
        HasEdge("FixesApplied", "False", "EmitApplyFailed").Should().BeTrue(
            "fixesApplied=false must route to the loud failure terminal — never a silent success");

        var applyFailReach = Reachable("EmitApplyFailed");
        applyFailReach.Should().Contain("OutputFailure");
        applyFailReach.Should().NotContain("OutputSuccess",
            "a failed apply must never reach the success terminal");
    }

    [Test]
    public void ApplyFixesSuccess_EmitsEvent_IndexesThenSucceeds()
    {
        HasEdge("FixesApplied", "True", "EmitApplySuccess").Should().BeTrue();
        HasEdge("EmitApplySuccess", null, "UpdateCodeIndex").Should().BeTrue();

        var applyOkReach = Reachable("EmitApplySuccess");
        applyOkReach.Should().Contain("OutputSuccess",
            "a successful apply must reach the success terminal");
    }

    // ================================================================
    // No-actionable-comments — a genuine success terminal (nothing to fix)
    // ================================================================

    [Test]
    public void NoActionableComments_RoutesToSuccessTerminal()
    {
        // No actionable comments is a genuine success (nothing to fix) — and it
        // must NOT enter the fix loop.
        var noActionableReach = ReachableFromPort("HasActionable", "False");
        noActionableReach.Should().Contain("OutputSuccess");
        noActionableReach.Should().NotContain("DispatchFixGeneration",
            "no-actionable-comments must not generate fixes");
    }

    // ================================================================
    // DCB events on every meaningful edge
    // ================================================================

    [Test]
    public void EveryMeaningfulEdge_EmitsAReviewFixEvent()
    {
        var emitIds = _flowchart.Activities
            .OfType<EmitReviewFixEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitAnalyzeSuccess");
        emitIds.Should().Contain("EmitAnalyzeFailed");
        emitIds.Should().Contain("EmitGenerateSuccess");
        emitIds.Should().Contain("EmitGenerateFailed");
        emitIds.Should().Contain("EmitApplySuccess");
        emitIds.Should().Contain("EmitApplyFailed");
        emitIds.Should().Contain("EmitEscalated");
    }

    // ================================================================
    // Terminal hygiene — success is not a constant true; failure terminal exists
    // ================================================================

    [Test]
    public void FailureTerminal_SetsSuccessFalse_AndAnErrorReason()
    {
        var outputFailure = _flowchart.Activities.OfType<Sequence>()
            .FirstOrDefault(s => s.Id == "OutputFailure");
        outputFailure.Should().NotBeNull("a loud failure terminal must exist (no silent success)");

        var outputIds = outputFailure!.Activities.OfType<SetOutput>().Select(o => o.Id ?? "").ToList();
        outputIds.Should().Contain("OutputFailure_Success",
            "the failure terminal must set success=false");
        outputIds.Should().Contain("OutputFailure_ErrorReason",
            "the failure terminal must surface an errorReason");
    }

    [Test]
    public void SuccessTerminal_Exists_AndIsDistinctFromFailure()
    {
        var success = _flowchart.Activities.OfType<Sequence>()
            .FirstOrDefault(s => s.Id == "OutputSuccess");
        success.Should().NotBeNull("a success terminal sequence must exist");

        var failure = _flowchart.Activities.OfType<Sequence>()
            .FirstOrDefault(s => s.Id == "OutputFailure");
        failure.Should().NotBeNull();

        success.Should().NotBeSameAs(failure, "success and failure must be distinct terminals");
    }

    [Test]
    public void EveryActivity_IsReachableFromStart_NoDanglingNode()
    {
        // No node may be orphaned — every activity (besides the start) must be
        // forward-reachable from the start, and every non-terminal must have an
        // outgoing edge (no deadlock).
        var start = (IActivity)_flowchart.Start!;
        var reachable = Reachable(start.Id!);
        reachable.Add(start.Id!);

        var terminalTypes = new[] { typeof(Finish) };
        foreach (var activity in _flowchart.Activities)
        {
            var id = activity.Id!;
            reachable.Should().Contain(id, $"activity '{id}' must be reachable from the start (no orphan node)");

            // Sequences are terminal output blocks (they end the flow); Finish is
            // terminal. Everything else must have an outgoing edge.
            var isTerminal = activity is Sequence || terminalTypes.Contains(activity.GetType());
            if (!isTerminal)
            {
                _flowchart.Connections.Any(c => c.Source.Activity.Id == id)
                    .Should().BeTrue($"non-terminal activity '{id}' must have an outgoing edge (no deadlock)");
            }
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

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

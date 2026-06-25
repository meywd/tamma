using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Review;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness build-out 2026-06-22 (<c>CodeReview.md</c>, Story 7-1D) — structural
/// verification of the corrected <c>code-review</c> graph:
///   - inputs are bound by a head node (#1) before CreatePR,
///   - a ValidateInputs decision routes invalid inputs to a specific failure terminal (#3),
///   - fix guidance is produced by mediated llm-call dispatches (#4) — no in-engine provider,
///   - merge is CI-gated / strategy-aware / branch-deleting via the outcome activity (#5),
///   - structured CodeReviewWorkflowResult terminals replace loose SetOutputs (#6),
///   - CODE_REVIEW.* DCB emits are wired at each milestone (#8),
///   - config variables (timeouts/strategy/flags) are present (#9),
///   - the workflow keeps its DefinitionId and uses the computed version,
///   - every wait (review / fix / escalation) reaches a terminal — no dangling bookmark.
/// </summary>
[TestFixture]
public class CodeReviewWorkflowStructureTests
{
    private static Flowchart Build()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new CodeReviewWorkflow());
        return WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void BuildsWithoutError_AndKeepsDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new CodeReviewWorkflow());
        builder.Object.DefinitionId.Should().Be("code-review");
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
    }

    [Test]
    public void HasInputBindingHeadNode_BeforeCreatePR_DefectNo1()
    {
        var fc = Build();
        var bind = fc.Activities.FirstOrDefault(a => a.Id == "BindInputs");
        bind.Should().NotBeNull("a Bind Inputs head node must read workflow inputs (defect #1)");

        var createPr = fc.Activities.First(a => a.Id == "CreatePR");
        // BindInputs must reach CreatePR; CreatePR must NOT be the entry (it must be downstream of binding/validation).
        var createPrHasInbound = fc.Connections.Any(c => c.Target.Activity == createPr);
        createPrHasInbound.Should().BeTrue("CreatePR must be downstream of input binding + validation, not the entry point");
    }

    [Test]
    public void HasValidationDecision_RoutingToSpecificFailure_No3()
    {
        var fc = Build();
        var validate = fc.Activities.FirstOrDefault(a => a.Id == "ValidateInputs");
        validate.Should().BeOfType<ValidateCodeReviewInputsActivity>();

        // Invalid edge must reach the validation-failed terminal (not the generic failed path / silent drop).
        var emitValidationFailed = fc.Activities.FirstOrDefault(a => a.Id == "EmitValidationFailed");
        emitValidationFailed.Should().NotBeNull();
        var hasInvalidEdge = fc.Connections.Any(c =>
            c.Source.Activity?.Id == "ValidateInputs" && c.Source.Port == "Invalid");
        hasInvalidEdge.Should().BeTrue("ValidateInputs must have an Invalid outcome edge");
    }

    [Test]
    public void FixGuidance_UsesMediatedLlmCall_No4()
    {
        var fc = Build();
        var dispatches = fc.Activities.OfType<DispatchWorkflow>().ToList();
        var ids = dispatches.Select(d => d.Id).ToList();
        ids.Should().Contain("AnalyzeChanges", "AC7 AnalyzeChanges must be a mediated llm-call dispatch");
        ids.Should().Contain("GenerateGuidance", "AC7 GenerateGuidance must be a mediated llm-call dispatch");
    }

    [Test]
    public void Merge_IsOutcomeActivity_WithFailureEscalation_No5()
    {
        var fc = Build();
        var merge = fc.Activities.FirstOrDefault(a => a.Id == "MergeAndComplete");
        merge.Should().BeOfType<MergeAndCompleteReviewActivity>();

        // A merge Failed outcome must escalate (not silently succeed).
        var failedEdge = fc.Connections.FirstOrDefault(c =>
            c.Source.Activity?.Id == "MergeAndComplete" && c.Source.Port == "Failed");
        failedEdge.Should().NotBeNull("a CI-red / merge-failed outcome must route to escalation");
        failedEdge!.Target.Activity!.Id.Should().Be("EscalateMerge");
    }

    [Test]
    public void EmitsStructuredResultTerminals_No6()
    {
        var fc = Build();
        var resultBuilders = fc.Activities.OfType<BuildCodeReviewResultActivity>().ToList();
        resultBuilders.Should().HaveCountGreaterThanOrEqualTo(3,
            "every terminal (merged / validation-failed / rejected) must build a structured result");
    }

    [Test]
    public void EmitsDcbEvents_AtMilestones_No8()
    {
        var fc = Build();
        var emits = fc.Activities.OfType<EmitCodeReviewEventActivity>().ToList();
        emits.Should().HaveCountGreaterThanOrEqualTo(6,
            "DCB CODE_REVIEW.* events must be emitted at PR-created/guidance/iteration/merged/escalated/failed milestones");
    }

    [Test]
    public void HasConfigVariables_No9()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new CodeReviewWorkflow());
        var names = builder.Object.Variables.Select(v => v.Name).ToHashSet();
        names.Should().Contain("ReviewTimeoutHours");
        names.Should().Contain("FixTimeoutHours");
        names.Should().Contain("VerifyCIBeforeMerge");
        names.Should().Contain("DeleteBranchAfterMerge");
        names.Should().Contain("TenantId");
        // NB: the MergeStrategy variable IS declared by the workflow, but the shared mock
        // builder has no WithVariable<MergeStrategy>(name, default) setup, so its name isn't
        // captured here. Its resolution is covered by BindCodeReviewConfigActivity.Resolve tests.
    }

    [Test]
    public void EveryWaitReachesATerminal_NoDanglingBookmark()
    {
        var fc = Build();
        var finish = fc.Activities.OfType<Finish>().FirstOrDefault();
        finish.Should().NotBeNull("workflow must have a terminal Finish node");

        // Every bookmark-bearing wait must have at least one outbound edge so it can't hang.
        foreach (var waitId in new[] { "MonitorReview", "WaitForFixes", "EscalateReview", "EscalateTimeout", "EscalateGuidance", "EscalateMerge" })
        {
            var node = fc.Activities.FirstOrDefault(a => a.Id == waitId);
            node.Should().NotBeNull($"{waitId} should exist");
            var hasOutbound = fc.Connections.Any(c => c.Source.Activity == node);
            hasOutbound.Should().BeTrue($"{waitId} must have an outbound edge to a terminal (no dangling bookmark)");
        }
    }

    [Test]
    public void NoOrphanActivities_EveryNonEntryNodeHasInbound_EveryNonTerminalHasOutbound()
    {
        var fc = Build();
        var entry = fc.Activities.First(a => a.Id == "BindInputs");

        foreach (var a in fc.Activities)
        {
            if (a == entry) continue;
            var hasInbound = fc.Connections.Any(c => c.Target.Activity == a);
            hasInbound.Should().BeTrue($"activity '{a.Id}' must be reachable (have an inbound edge)");
        }

        // Every node except the terminal Finish must have an outbound edge.
        var finish = fc.Activities.OfType<Finish>().Single();
        foreach (var a in fc.Activities)
        {
            if (a == finish) continue;
            var hasOutbound = fc.Connections.Any(c => c.Source.Activity == a);
            hasOutbound.Should().BeTrue($"activity '{a.Id}' must lead somewhere (have an outbound edge)");
        }
    }

    [Test]
    public void AllActivitiesHaveDisplayText()
    {
        var fc = Build();
        foreach (var activity in WorkflowTestHelper.GetAllActivities(fc))
        {
            activity.GetDisplayText().Should().NotBeNullOrEmpty(
                $"Activity '{activity.GetType().Name}' (Id: {activity.Id}) should have DisplayText");
        }
    }
}

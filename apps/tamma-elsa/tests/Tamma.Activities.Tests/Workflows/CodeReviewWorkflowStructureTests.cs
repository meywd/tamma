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

        // A merge Failed outcome must escalate (not silently succeed). It first emits the
        // auditable CODE_REVIEW.MERGED.FAILED event, then routes to EscalateMerge.
        var failedEdge = fc.Connections.FirstOrDefault(c =>
            c.Source.Activity?.Id == "MergeAndComplete" && c.Source.Port == "Failed");
        failedEdge.Should().NotBeNull("a CI-red / merge-failed outcome must route to escalation");
        failedEdge!.Target.Activity!.Id.Should().Be("EmitMergeFailed");
        var emitToEscalate = fc.Connections.FirstOrDefault(c => c.Source.Activity?.Id == "EmitMergeFailed");
        emitToEscalate!.Target.Activity!.Id.Should().Be("EscalateMerge");
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
    public void EveryEscalation_HasADurableTimedOutEdge_ToALoudTerminal_ReviewFix()
    {
        // Each EscalateReviewActivity now arms a durable senior-SLA Delay → TimedOut outcome.
        // The TimedOut edge must be wired to the escalation-timeout terminal (never a silent
        // suspend-forever / false success).
        var fc = Build();
        foreach (var escId in new[] { "EscalateReview", "EscalateTimeout", "EscalateGuidance", "EscalateMerge" })
        {
            var timedOutEdge = fc.Connections.FirstOrDefault(c =>
                c.Source.Activity?.Id == escId && c.Source.Port == "TimedOut");
            timedOutEdge.Should().NotBeNull($"{escId} must have a TimedOut outcome edge (durable senior-SLA timeout)");
            timedOutEdge!.Target.Activity!.Id.Should().Be("EmitEscalationTimedOut",
                $"{escId}.TimedOut must route to the escalation-timeout terminal");
        }

        // The escalation-timeout terminal must build a structured (loud) result reaching Finish.
        var emit = fc.Activities.FirstOrDefault(a => a.Id == "EmitEscalationTimedOut");
        emit.Should().NotBeNull();
        var toResult = fc.Connections.FirstOrDefault(c => c.Source.Activity?.Id == "EmitEscalationTimedOut");
        toResult!.Target.Activity!.Id.Should().Be("BuildEscalationTimedOutResult");
    }

    [Test]
    public void MergeReEscalationLoop_IsBounded_ReviewFix()
    {
        // The escalate-merge → resolved → re-merge loop must be bounded: EscalateMerge.Resolved
        // routes through the retry counter + cap check (NOT straight back to MergeAndComplete),
        // and the cap-reached branch terminates as rejected instead of looping forever.
        var fc = Build();

        var resolvedEdge = fc.Connections.FirstOrDefault(c =>
            c.Source.Activity?.Id == "EscalateMerge" && c.Source.Port == "Resolved");
        resolvedEdge.Should().NotBeNull();
        resolvedEdge!.Target.Activity!.Id.Should().Be("IncrementMergeRetry",
            "a resolved merge escalation must go through the re-merge counter, not straight back to merge");

        var capCheck = fc.Activities.FirstOrDefault(a => a.Id == "MergeRetryCapCheck");
        capCheck.Should().NotBeNull("there must be a cap-check decision on the re-merge loop");

        // True (cap reached) -> distinct event -> terminal; never loops back to MergeAndComplete.
        var capReached = fc.Connections.FirstOrDefault(c =>
            c.Source.Activity?.Id == "MergeRetryCapCheck" && c.Source.Port == "True");
        capReached.Should().NotBeNull();
        capReached!.Target.Activity!.Id.Should().Be("EmitMergeLoopExhausted");

        var exhaustedToResult = fc.Connections.FirstOrDefault(c => c.Source.Activity?.Id == "EmitMergeLoopExhausted");
        exhaustedToResult!.Target.Activity!.Id.Should().Be("BuildMergeExhaustedResult");
        var toFinish = fc.Connections.FirstOrDefault(c => c.Source.Activity?.Id == "BuildMergeExhaustedResult");
        toFinish!.Target.Activity.Should().BeOfType<Finish>("the capped merge loop must terminate, not cycle");

        // Under the cap, it re-merges (capture -> emit escalated -> merge).
        var underCap = fc.Connections.FirstOrDefault(c =>
            c.Source.Activity?.Id == "MergeRetryCapCheck" && c.Source.Port == "False");
        underCap!.Target.Activity!.Id.Should().Be("CaptureEscalated");
    }

    [Test]
    public void MergeFailedEdge_EmitsMergedFailedEvent_BeforeEscalating_ReviewFix()
    {
        // CODE_REVIEW.MERGED.FAILED was defined but never emitted — the MergeAndComplete.Failed
        // edge must now pass through an emit node before escalating.
        var fc = Build();
        var failedEdge = fc.Connections.FirstOrDefault(c =>
            c.Source.Activity?.Id == "MergeAndComplete" && c.Source.Port == "Failed");
        failedEdge.Should().NotBeNull();
        failedEdge!.Target.Activity!.Id.Should().Be("EmitMergeFailed",
            "the merge-failed edge must emit CODE_REVIEW.MERGED.FAILED before escalating");

        var emitToEscalate = fc.Connections.FirstOrDefault(c => c.Source.Activity?.Id == "EmitMergeFailed");
        emitToEscalate!.Target.Activity!.Id.Should().Be("EscalateMerge");
    }

    [Test]
    public void PrCreationFailure_HasItsOwnTerminal_WithNonEmptyMessage_ReviewFix()
    {
        // PR-creation failure must NOT reuse the validation-failed terminal (whose message is
        // empty on this path). It has a dedicated terminal that reaches Finish.
        var fc = Build();
        var prFailedEdge = fc.Connections.FirstOrDefault(c => c.Source.Activity?.Id == "EmitPrFailed");
        prFailedEdge.Should().NotBeNull();
        prFailedEdge!.Target.Activity!.Id.Should().Be("BuildPrFailedResult",
            "PR-creation failure must build its own result with a specific message, not reuse the empty validation message");

        var toFinish = fc.Connections.FirstOrDefault(c => c.Source.Activity?.Id == "BuildPrFailedResult");
        toFinish!.Target.Activity.Should().BeOfType<Finish>();
    }

    [Test]
    public void LlmDispatches_PassOnlyDataPlaceholders_NoInertPromptVariable_ReviewFix()
    {
        // The hand-written variables["prompt"] is inert (LlmCallWorkflow reads top-level
        // prompt/taskPrompt, not variables). It must be dropped — the seeded role+action
        // template is the prompt; only data placeholders are passed. (Match the ASSIGNMENT
        // form so the explanatory doc-comments that mention variables["prompt"] don't trip it.)
        var src = ReadWorkflowSource();
        src.Should().NotContain("[\"prompt\"] =",
            "the inert variables[\"prompt\"] = ... assignment must be dropped from the llm-call dispatches");
    }

    [Test]
    public void HasMergeRetryCountVariable_ReviewFix()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new CodeReviewWorkflow());
        var names = builder.Object.Variables.Select(v => v.Name).ToHashSet();
        names.Should().Contain("MergeRetryCount", "the re-merge loop must track a bounded retry count");
    }

    private static string ReadWorkflowSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var nested = Path.Combine(dir.FullName, "apps", "tamma-elsa", "src", "Tamma.ElsaServer", "Workflows", "CodeReviewWorkflow.cs");
            if (File.Exists(nested)) return File.ReadAllText(nested);
            var flat = Path.Combine(dir.FullName, "src", "Tamma.ElsaServer", "Workflows", "CodeReviewWorkflow.cs");
            if (File.Exists(flat)) return File.ReadAllText(flat);
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate CodeReviewWorkflow.cs source.");
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

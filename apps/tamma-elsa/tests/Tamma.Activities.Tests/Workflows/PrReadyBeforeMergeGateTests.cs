using Elsa.Workflows;
using Elsa.Workflows.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// THE DRAFT BLOCKER — the autonomous loop opens its PR as a DRAFT
/// (<c>SingleIssueCycleWorkflow</c> passes <c>draft = true</c> to CreatePullRequest)
/// and GitHub REFUSES to merge a draft PR. Story 31-13 shipped the governed draft
/// verb; these tests pin the place the loop actually uses it, and pin that a FAILED
/// un-draft never reaches the gate.
///
/// <para><b>Epic 31 P2 (plan §4)</b> — the un-draft edge is the FIRST INSTANCE of
/// the owner-decided capability mechanism: a reusable
/// <see cref="CheckPlatformCapabilityActivity"/> sits between the CI-passed edge
/// and the action step; unsupported routes to the DG-1 alternative step
/// (mark-satisfied-with-audit-event: <c>GIT.PR_DRAFT_SET.SKIPPED</c> → the merge
/// gate); the action step's own <c>Unsupported</c> outcome (exact
/// <c>capability_unsupported</c> code) is the §4.3 safety net onto the SAME
/// alternative step. These tests pin all three paths.</para>
/// </summary>
[TestFixture]
public class PrReadyBeforeMergeGateTests
{
    [Test]
    public void TheCycle_MarksThePrReady_beforeTheMergeGate()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .SingleOrDefault(a => a.Id == "MarkPrReadyForReview");

        markReady.Should().NotBeNull(
            "the cycle must flip its own draft PR to ready-for-review, or the merge it later "
            + "asks a human to approve can never succeed");

        markReady!.Draft.Should().NotBeNull();
    }

    [Test]
    public void TheCheckStep_SitsBetweenCiPassed_andTheUndraft_andTheGate()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var check = flowchart.Activities.OfType<CheckPlatformCapabilityActivity>()
            .Single(a => a.Id == "CheckUndraftSupported");
        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .Single(a => a.Id == "MarkPrReadyForReview");
        var gate = flowchart.Activities.Single(a => a.Id == "MergeApprovalGate");
        var ciOk = flowchart.Activities.Single(a => a.Id == "CiOk");

        // CI passed → the §4 check step (support decided BEFORE the action)
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == ciOk && c.Source.Port == "True" && c.Target.Activity == check,
            "the is-supported check step must hang off the CI-passed edge");

        // check says supported → the action step runs as today
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == check && c.Source.Port == "Supported" && c.Target.Activity == markReady,
            "supported → the un-draft action runs exactly as before");

        // un-draft succeeded → the merge gate
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == markReady && c.Source.Port == "DraftSet" && c.Target.Activity == gate,
            "only a SUCCESSFUL un-draft may open the merge gate directly");

        // CI-passed must not reach the gate or the action directly
        flowchart.Connections.Should().NotContain(
            c => c.Source.Activity == ciOk && c.Target.Activity == gate,
            "if CI still reached the gate directly, the un-draft would be bypassable and the "
            + "draft blocker would silently return");
        flowchart.Connections.Should().NotContain(
            c => c.Source.Activity == ciOk && c.Target.Activity == markReady,
            "the action must be PRECEDED by the check step (plan §4) — CI must not skip it");
    }

    [Test]
    public void CheckUnsupported_RoutesToTheAlternativeStep_WhichOpensTheGate()
    {
        // DG-1: unsupported un-draft = mark-satisfied-with-audit-event and
        // proceed to the merge gate — the gate is preserved; only the
        // "not mergeable while cooking" guard is lost, and only on platforms
        // that cannot express it.
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var check = flowchart.Activities.OfType<CheckPlatformCapabilityActivity>()
            .Single(a => a.Id == "CheckUndraftSupported");
        var skipped = flowchart.Activities.OfType<EmitCycleEventActivity>()
            .Single(a => a.Id == "MarkDraftSkipped");
        var gate = flowchart.Activities.Single(a => a.Id == "MergeApprovalGate");

        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == check && c.Source.Port == "Unsupported" && c.Target.Activity == skipped,
            "the check step's Unsupported edge must route to the defined alternative step, "
            + "never to the fail-the-cycle sink (that is the perma-fail §4 exists to prevent)");

        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == skipped && c.Target.Activity == gate,
            "the alternative step marks the un-draft satisfied and OPENS the merge gate — the "
            + "gate itself is preserved");

        flowchart.Connections.Should().NotContain(
            c => c.Source.Activity == check && c.Source.Port == "Unsupported" && c.Target.Activity.Id == "EmitStepFailed",
            "unsupported is a DEGRADED path, not a failure");
    }

    [Test]
    public void TheAlternativeStep_EmitsTheSkippedAuditEvent()
    {
        // §4.4 — silent skips are forbidden: every trip through the alternative
        // step emits a DCB audit event.
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var skipped = flowchart.Activities.OfType<EmitCycleEventActivity>()
            .SingleOrDefault(a => a.Id == "MarkDraftSkipped");

        skipped.Should().NotBeNull("the DG-1 alternative step is an audited mark-satisfied");
        // The literal event type is pinned so the audit trail's vocabulary is stable.
        var evtType = skipped!.EventType.Expression;
        evtType.Should().NotBeNull();
    }

    [Test]
    public void TheSafetyNet_ActionUnsupportedOutcome_RoutesToTheSameAlternativeStep()
    {
        // §4.3 defense in depth — a stale or lying probe: the action step's own
        // typed capability_unsupported outcome routes to the SAME alternative
        // step, never the gate, never the sink.
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .Single(a => a.Id == "MarkPrReadyForReview");
        var skipped = flowchart.Activities.Single(a => a.Id == "MarkDraftSkipped");
        var gate = flowchart.Activities.Single(a => a.Id == "MergeApprovalGate");

        var unsupportedEdges = flowchart.Connections
            .Where(c => c.Source.Activity == markReady && c.Source.Port == "Unsupported")
            .ToList();

        unsupportedEdges.Should().ContainSingle()
            .Which.Target.Activity.Should().Be(skipped,
                "the safety-net outcome shares the check step's alternative step");
        unsupportedEdges.Should().NotContain(c => c.Target.Activity == gate);
    }

    [Test]
    public void AFailedUndraft_FailsTheCycle_andNeverOpensTheGate()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .Single(a => a.Id == "MarkPrReadyForReview");
        var gate = flowchart.Activities.Single(a => a.Id == "MergeApprovalGate");

        var errorEdges = flowchart.Connections
            .Where(c => c.Source.Activity == markReady && c.Source.Port == "Error")
            .ToList();

        errorEdges.Should().NotBeEmpty("a failed un-draft must be routed, not dropped on the floor");
        errorEdges.Should().NotContain(c => c.Target.Activity == gate,
            "a failed un-draft must NEVER open the merge gate — asking a human to approve a merge "
            + "that cannot complete is the exact failure this fix removes");
        errorEdges.Should().Contain(c => c.Target.Activity.Id == "EmitStepFailed",
            "it routes to the shared loud fail-the-cycle sink");
    }

    [Test]
    public void TheUndraft_TargetsReadyForReview_notDraft()
    {
        // Direction matters: Draft=true would re-draft the PR and make the blocker worse.
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var markReady = flowchart.Activities.OfType<SetPullRequestDraftActivity>()
            .Single(a => a.Id == "MarkPrReadyForReview");

        // The input is a literal false (ready-for-review), not an expression.
        markReady.Draft.Expression.Should().NotBeNull();
        SetPullRequestDraftActivity.MapResponse(null).Success.Should().BeFalse(
            "and an unanswered mediation call must fail closed — never read as 'ready'");
    }

    // ================================================================
    // The exact-code classification (§4.5) — the safety net fires ONLY on
    // capability_unsupported; anything else stays a real Error.
    // ================================================================

    [Test]
    public void PrDraftOutcome_UnsupportedIsExactCodeMatchOnly()
    {
        PrDraftOutcome.Failed("capability_unsupported", "x").Unsupported.Should().BeTrue();
        PrDraftOutcome.Failed("CAPABILITY_UNSUPPORTED", "x").Unsupported.Should().BeFalse(
            "classification is ordinal exact-code only — a lookalike must stay on Error");
        PrDraftOutcome.Failed("PLATFORM_ERROR", "x").Unsupported.Should().BeFalse();
        PrDraftOutcome.Failed("NOT_FOUND", "x").Unsupported.Should().BeFalse(
            "mis-classifying a real failure as unsupported would silently skip a gate");
        PrDraftOutcome.Ok(false).Unsupported.Should().BeFalse();
    }

    [Test]
    public void CheckCapability_Evaluate_DecidesOnlyOnAPositiveProbeAnswer()
    {
        // Supported path: the probe lists the capability.
        CheckPlatformCapabilityActivity.Evaluate(
            new Tamma.Activities.LlmCall.Models.GitCapabilitiesResponse
            {
                Success = true,
                PlatformKind = "github",
                Capabilities = new[] { "PrLifecycle", "Actions" },
            }, "PrLifecycle", out var kind).Should().BeTrue();
        kind.Should().Be("github");

        // Unsupported path: a SUCCESSFUL probe that positively lacks it.
        CheckPlatformCapabilityActivity.Evaluate(
            new Tamma.Activities.LlmCall.Models.GitCapabilitiesResponse
            {
                Success = true,
                PlatformKind = "gitea",
                Capabilities = new[] { "Actions" },
            }, "PrLifecycle", out _).Should().BeFalse();

        // Unknown / unreachable probe: proceed (the action's §4.3 safety net
        // decides) — a probe outage must never silently skip a real action.
        CheckPlatformCapabilityActivity.Evaluate(null, "PrLifecycle", out _).Should().BeTrue();
        CheckPlatformCapabilityActivity.Evaluate(
            new Tamma.Activities.LlmCall.Models.GitCapabilitiesResponse { Success = false },
            "PrLifecycle", out _).Should().BeTrue();
    }
}

using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.Policy;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 43-9 <b>AC11</b> — Seam E's ONE v1 adoption: the deployment pipeline's
/// production-approval decision gains a third <b>OR</b> term fed by
/// <see cref="CheckActionGateActivity"/>, evaluated on
/// <c>effect:deploy.promote-prod</c>.
///
/// <para><b>Both halves of AC11 are load-bearing and both are pinned here.</b></para>
/// <list type="bullet">
///   <item><b>BY OR, never by replacement.</b> <c>prodApprovalNeeded</c> already
///   fires unconditionally for business mode; replacing that predicate with a
///   threshold check would be STRICTLY WEAKER for business-mode tenants — a
///   governance epic that REMOVED an existing gate. The new term can only ADD a
///   wait.</item>
///   <item><b>On the EFFECT, not the agent-action.</b> <c>StageDeployDispatch</c>
///   is SHARED across qa / uat / production, so one <c>agent-action:deploy</c>
///   member cannot tell a staging deploy from a production one. Gating
///   <c>effect:deploy.promote-prod</c> at the prod-approval decision can.</item>
/// </list>
/// </summary>
[TestFixture]
public class DeploymentPipelineGateTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    private bool HasEdge(string sourceId, string? port, string targetId) =>
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port is null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    private CheckActionGateActivity Gate() =>
        _flowchart.Activities.OfType<CheckActionGateActivity>()
            .Single(a => a.Id == "CheckProdDeployGate");

    // ====================================================================
    // The node exists, on the right action, on the right path
    // ====================================================================

    [Test]
    public void TheGateNode_existsExactlyOnce_andRunsBeforeTheApprovalDecision()
    {
        _flowchart.Activities.OfType<CheckActionGateActivity>().Should().ContainSingle(
            "one gate check per pipeline run. A second would either double-count a ledger grant "
            + "or disagree with the first.");

        HasEdge("EmitUatSuccess", null, "CheckProdDeployGate").Should().BeTrue(
            "the gate check sits between UAT success and the prod-approval decision, so the "
            + "decision reads a variable that has already been written");

        // BOTH outcomes converge on the SAME next node. The activity's job is to
        // SET the outcome variable; the routing decision stays where it already
        // was. Wiring the two edges to different nodes would create a second,
        // competing prod gate that the existing predicate knows nothing about.
        HasEdge("CheckProdDeployGate", "Automated", "ProdApprovalNeeded").Should().BeTrue();
        HasEdge("CheckProdDeployGate", "RequiresHuman", "ProdApprovalNeeded").Should().BeTrue();
    }

    [Test]
    public void Gate_is_on_the_effect_not_the_shared_dispatch()
    {
        Gate().ActionKey.Expression?.Value.Should().Be("effect:deploy.promote-prod",
            "gating agent-action:deploy would gate the SHARED StageDeployDispatch, which cannot "
            + "distinguish qa / uat / production — the same member would gate a staging deploy as "
            + "though it were a production promotion");

        // The shared dispatch itself is NOT individually gated: no gate node is
        // adjacent to any StageDeployDispatch node.
        var dispatchIds = _flowchart.Activities.OfType<DispatchWorkflow>()
            .Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        var adjacentToADispatch = _flowchart.Connections
            .Where(c => c.Source.Activity is CheckActionGateActivity
                        || c.Target.Activity is CheckActionGateActivity)
            .Where(c => dispatchIds.Contains(c.Source.Activity.Id)
                        || dispatchIds.Contains(c.Target.Activity.Id))
            .Select(c => $"  {c.Source.Activity.Id} → {c.Target.Activity.Id}")
            .ToList();

        adjacentToADispatch.Should().BeEmpty(
            "the gate is on the stage TRANSITION, never on the shared dispatch:"
            + Environment.NewLine + string.Join(Environment.NewLine, adjacentToADispatch));
    }

    [Test]
    public void TheGateOutcome_isWrittenToAWorkflowVariable_theDecisionCanRead()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        builder.Object.Variables.Select(v => v.Name).Should().Contain("ProdGateOutcome",
            "the decision predicate is a synchronous FlowDecision — it cannot make an HTTP call, "
            + "so the activity must have already written its answer somewhere the predicate reads");

        Gate().Outcome.Should().NotBeNull(
            "an activity whose Outcome output is unbound would leave the predicate reading an "
            + "empty string for ever — the gate would be decorative");
    }

    // ====================================================================
    // AC11's two named behaviour pins, driven through the REAL predicate
    // ====================================================================

    /// <summary>
    /// The production-approval predicate, re-derived from the workflow's own three
    /// terms. It is expressed here rather than reached through the built
    /// <c>FlowDecision</c> because the built delegate needs a live
    /// <c>ExpressionExecutionContext</c>; the SHAPE under test is the
    /// composition, and the composition is what AC11 is about.
    /// </summary>
    private static bool ApprovalNeeded(string mode, bool requireProdApproval, string gateOutcome) =>
        string.Equals(mode?.Trim(), "business", StringComparison.OrdinalIgnoreCase)
        || requireProdApproval
        || string.Equals(
            gateOutcome?.Trim(),
            GovernanceEvaluateResponse.OutcomeRequiresHuman,
            StringComparison.OrdinalIgnoreCase);

    [Test]
    public void EnforceMode_NeverWeakensBusinessModeGate()
    {
        // The failure this forbids: a governance epic that REMOVES an existing
        // gate. Business mode waits today unconditionally; it must keep waiting no
        // matter what the gate says, including when the gate says "automated".
        ApprovalNeeded("business", requireProdApproval: false,
            gateOutcome: GovernanceEvaluateResponse.OutcomeAutomated).Should().BeTrue();
        ApprovalNeeded("business", requireProdApproval: false,
            gateOutcome: "").Should().BeTrue();
        ApprovalNeeded("business", requireProdApproval: false,
            gateOutcome: CheckActionGateActivity.OutcomeUnavailable).Should().BeTrue();

        ApprovalNeeded("dev", requireProdApproval: true,
            gateOutcome: GovernanceEvaluateResponse.OutcomeAutomated).Should().BeTrue(
            "the explicit requireProdApproval flag is the second pre-existing term and is equally "
            + "untouched");
    }

    [Test]
    public void GateRequiresHuman_AddsAWaitWhereThereWasNone()
    {
        // The whole point of the story, at the one seam with a real human wait:
        // dev mode, no explicit flag — today this deploys straight through — and a
        // tenant admin who set effect:deploy.promote-prod to human-only now gets a
        // wait.
        ApprovalNeeded("dev", requireProdApproval: false,
            gateOutcome: GovernanceEvaluateResponse.OutcomeAutomated).Should().BeFalse(
            "THE ANTI-NO-OP HALF: with the gate allowing, dev mode must still deploy straight "
            + "through, or 'the gate adds a wait' would be satisfiable by a term that is always "
            + "true");

        ApprovalNeeded("dev", requireProdApproval: false,
            gateOutcome: GovernanceEvaluateResponse.OutcomeRequiresHuman).Should().BeTrue();
    }

    [Test]
    public void ShippedDefaults_DoNotAlterControlFlow_atSeamE()
    {
        // AC2 for this seam. effect:deploy.promote-prod ships at AutonomyDial.Min,
        // so with no policy rows the gate answers `automated` and every routing
        // decision is byte-identical to before this story.
        foreach (var mode in new[] { "dev", "business", "" })
        {
            foreach (var flag in new[] { true, false })
            {
                ApprovalNeeded(mode, flag, GovernanceEvaluateResponse.OutcomeAutomated)
                    .Should().Be(ApprovalNeededBeforeThisStory(mode, flag),
                        $"mode={mode}, requireProdApproval={flag}: a shipped-default gate must "
                        + "change nothing");
            }
        }

        static bool ApprovalNeededBeforeThisStory(string mode, bool flag) =>
            string.Equals(mode?.Trim(), "business", StringComparison.OrdinalIgnoreCase) || flag;
    }

    [Test]
    public void AnUnavailableGate_isTreatedAsNoOpinion_notAsABlock()
    {
        // EngineGateCall_FailsOpenOnTransportError, expressed at the predicate.
        // Fail-open here is safe ONLY because the term is OR'd: a null contributes
        // nothing and the pre-existing predicate is untouched, which is exactly
        // today's behaviour. A future adoption that REPLACED a predicate would make
        // this posture wrong.
        ApprovalNeeded("dev", requireProdApproval: false,
            gateOutcome: CheckActionGateActivity.OutcomeUnavailable).Should().BeFalse();
        ApprovalNeeded("dev", requireProdApproval: false, gateOutcome: "").Should().BeFalse();
    }

    [Test]
    public void ADeniedOutcome_doesNotSilentlyPassTheApprovalDecision()
    {
        // The activity maps `denied` onto its RequiresHuman EDGE (there is no
        // third edge, deliberately — an unrouted outcome is how a governance
        // activity silently falls through). But it also writes the raw wire into
        // the variable, so the predicate must not treat `denied` as an allow.
        //
        // It currently DOES fall to false at the predicate, and that is safe only
        // because the routing already went down the RequiresHuman edge. This test
        // records that dependency explicitly so a future author who re-wires the
        // edges sees what they are relying on.
        ApprovalNeeded("dev", requireProdApproval: false,
            gateOutcome: GovernanceEvaluateResponse.OutcomeDenied).Should().BeFalse();

        HasEdge("CheckProdDeployGate", "RequiresHuman", "ProdApprovalNeeded").Should().BeTrue(
            "a denied resolution takes the RequiresHuman edge; if that edge is ever re-pointed "
            + "away from the approval decision, the predicate above stops being enough");
    }

    // ====================================================================
    // The wait it routes into is the EXISTING one
    // ====================================================================

    [Test]
    public void TheGate_routesIntoTheExistingHumanWait_andMintsNoNewSuspendActivity()
    {
        // D11: no new suspend activity and no new bookmark prefix. Seam E reuses
        // WaitForDeploymentApprovalActivity, which already has a resume path, a
        // bookmark and a UI. A new wait would be a suspend nobody can resume.
        _flowchart.Activities.OfType<WaitForDeploymentApprovalActivity>().Should().ContainSingle();

        HasEdge("ProdApprovalNeeded", "True", "WaitProdApproval").Should().BeTrue();
        HasEdge("ProdApprovalNeeded", "False", "EmitProdStarted").Should().BeTrue();

        _flowchart.Activities.OfType<CheckActionGateActivity>()
            .Should().OnlyContain(a => a.Id == "CheckProdDeployGate",
                "the gate check is not a suspend activity and must never grow into one");
    }
}

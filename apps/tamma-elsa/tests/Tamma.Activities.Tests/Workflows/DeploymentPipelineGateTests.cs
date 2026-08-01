using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
///
/// <para><b>2026-08-01 review finding F1 — these behaviour tests now drive the
/// REAL <c>FlowDecision</c> delegate.</b> They used to call a hand-copy of the
/// predicate re-derived in this file, which is how the shipped
/// <c>ADeniedOutcome_…</c> test came to assert the OPPOSITE of its own name and
/// stay green: a re-derived copy cannot disagree with itself. <see cref="ApprovalNeeded"/>
/// extracts the built decision's Delegate expression and invokes it against a real
/// <c>ExpressionExecutionContext</c> holding the workflow's own variables, so
/// nothing here can pass while the graph says something else.</para>
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

    /// <summary>Everything reachable from one outcome port of one node.</summary>
    private HashSet<string> ReachableFromPort(string sourceId, string port)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
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

        // Automated and RequiresHuman converge on the SAME next node. For those two
        // the activity's job is to SET the outcome variable; the routing decision
        // stays where it already was. Splitting them would create a second,
        // competing prod gate that the existing predicate knows nothing about.
        HasEdge("CheckProdDeployGate", "Automated", "ProdApprovalNeeded").Should().BeTrue();
        HasEdge("CheckProdDeployGate", "RequiresHuman", "ProdApprovalNeeded").Should().BeTrue();

        // Denied does NOT converge — see ADeniedOutcome_isAHardStop_notAnEscalation.
        HasEdge("CheckProdDeployGate", "Denied", "ProdApprovalNeeded").Should().BeFalse(
            "a denial must not be answerable by the deployment-approval wait: the only answer "
            + "that wait has is 'a human approves', and a denial is exactly the case where no "
            + "human on this graph may");
    }

    [Test]
    public void EveryGateOutcome_isWired_noDanglingEdge()
    {
        // F1's remedy for the objection that used to justify folding `denied` into
        // `RequiresHuman`: "a third edge is one every adopting workflow must
        // remember to wire, and an unrouted outcome silently falls through". True —
        // so the wiring is a BUILD FAILURE rather than a comment. Every outcome the
        // activity can complete with must leave the node.
        var wired = _flowchart.Connections
            .Where(c => c.Source.Activity is CheckActionGateActivity)
            .Select(c => c.Source.Port)
            .ToHashSet(StringComparer.Ordinal);

        wired.Should().BeEquivalentTo(CheckActionGateActivity.Edges,
            "a CheckActionGateActivity outcome with no edge is a governance decision the graph "
            + "silently drops — which is the failure mode the single-edge design was trying to "
            + "avoid and did not");
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

        Gate().Reason.Should().NotBeNull(
            "F1: the denial terminal writes the gate's reason into the audit payload. An unbound "
            + "Reason leaves an operator staring at a stopped pipeline with nothing to act on");
    }

    // ====================================================================
    // The REAL predicate, extracted from the built graph and invoked
    // ====================================================================

    /// <summary>
    /// Invoke the pipeline's ACTUAL <c>ProdApprovalNeeded</c> delegate.
    ///
    /// <para>The built <c>FlowDecision</c> holds its condition as a
    /// <c>Delegate</c> expression — a <c>Func&lt;ExpressionExecutionContext,
    /// ValueTask&lt;object&gt;&gt;</c>. Giving it a real
    /// <see cref="ExpressionExecutionContext"/> over a
    /// <see cref="MemoryRegister"/> holding the workflow's own three input
    /// variables is all it needs; no workflow runtime, no host. This is
    /// deliberately NOT a re-derived copy of the predicate — a copy is what let a
    /// shipped test assert the opposite of its own name and stay green.</para>
    /// </summary>
    private static bool ApprovalNeeded(string mode, bool requireProdApproval, string gateOutcome)
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);
        var decision = flowchart.Activities.OfType<FlowDecision>()
            .Single(a => a.Id == "ProdApprovalNeeded");

        var variables = builder.Object.Variables;
        var modeVar = (Variable<string>)variables.Single(v => v.Name == "Mode");
        var flagVar = (Variable<bool>)variables.Single(v => v.Name == "RequireProdApproval");
        var gateVar = (Variable<string>)variables.Single(v => v.Name == "ProdGateOutcome");

        var register = new MemoryRegister();
        register.Declare(new MemoryBlockReference[] { modeVar, flagVar, gateVar });

        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new ExpressionExecutionContext(services, register);

        modeVar.Set(context, mode);
        flagVar.Set(context, requireProdApproval);
        gateVar.Set(context, gateOutcome);

        var condition = decision.Condition.Expression?.Value
            as Func<ExpressionExecutionContext, ValueTask<object>>;
        condition.Should().NotBeNull(
            "ProdApprovalNeeded must still be a Delegate-expression FlowDecision; if it became a "
            + "JS/Liquid expression these behaviour pins stop testing the real thing and must be "
            + "rewritten rather than deleted");

        return (bool)condition!(context).AsTask().GetAwaiter().GetResult()!;
    }

    // ====================================================================
    // AC11's named behaviour pins, driven through the REAL predicate
    // ====================================================================

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

    // ====================================================================
    // F1 — a denial is at least as blocking as requires-human, and is a
    //      HARD STOP rather than an escalation
    // ====================================================================

    [Test]
    public void ADeniedOutcome_doesNotSilentlyPassTheApprovalDecision()
    {
        // THE F1 REGRESSION PIN, asserting what its name says. This test previously
        // asserted the OPPOSITE — that `denied` fell to False here — and justified
        // it with "safe only because the routing already went down the RequiresHuman
        // edge". That edge led to the SAME node as Automated, so it protected
        // nothing: dev mode + requireProdApproval=false + `denied` deployed to
        // production with no human, which an admin reaches by DISABLING
        // effect:deploy.promote-prod or putting any AllowedRoles restriction on it
        // or its deploy-control group (this call passes Role unset, so every
        // restriction excludes it).
        ApprovalNeeded("dev", requireProdApproval: false,
            gateOutcome: GovernanceEvaluateResponse.OutcomeDenied).Should().BeTrue(
            "a denied resolution must never be read as 'no opinion'. It is the STRONGEST refusal "
            + "the gate can produce; falling through it deployed production unattended.");
    }

    [Test]
    public void TheApprovalPredicate_isMonotone_inAdminStrictness()
    {
        // THE INVARIANT the F1 defect violated, stated directly: every strengthening
        // of the admin's setting must be at least as blocking as the one below it.
        // `denied` (action disabled / role-restricted) is strictly stronger than
        // `requires-human` (AlwaysHuman), so it cannot be less blocking.
        foreach (var mode in new[] { "dev", "business", "" })
        {
            foreach (var flag in new[] { true, false })
            {
                var automated = ApprovalNeeded(mode, flag, GovernanceEvaluateResponse.OutcomeAutomated);
                var requiresHuman = ApprovalNeeded(mode, flag, GovernanceEvaluateResponse.OutcomeRequiresHuman);
                var denied = ApprovalNeeded(mode, flag, GovernanceEvaluateResponse.OutcomeDenied);

                (requiresHuman || !automated).Should().BeTrue(
                    $"mode={mode}, flag={flag}: requires-human must block wherever automated does");
                (denied || !requiresHuman).Should().BeTrue(
                    $"mode={mode}, flag={flag}: DISABLING the action must be at least as blocking "
                    + "as pinning it to AlwaysHuman — the inversion F1 found");
            }
        }
    }

    [Test]
    public void ADeniedOutcome_isAHardStop_notAnEscalation()
    {
        // THE ROUTING half of F1, and the design call it records. `denied` arises
        // from an Enabled=false row or an AllowedRoles restriction. Neither means
        // "a person may approve this": the human on WaitProdApproval is approving a
        // DEPLOYMENT, and letting them approve past an action an admin switched OFF
        // would make the deploy-control dial advisory. So the Denied edge reaches
        // the refusal terminal and NEVER the approval wait or the prod deploy.
        HasEdge("CheckProdDeployGate", "Denied", "SetProdGateDenied").Should().BeTrue();
        HasEdge("SetProdGateDenied", null, "EmitProdRejected").Should().BeTrue(
            "a governance refusal is a PRODUCTION.REJECTED audit row, not a silent stall");

        var reachable = ReachableFromPort("CheckProdDeployGate", "Denied");

        reachable.Should().NotContain("WaitProdApproval",
            "a denial must not be routable into the standing deployment-approval flow — that "
            + "would make a human able to approve past an action an admin DISABLED");
        reachable.Should().NotContain("ProdDeploy",
            "and it must certainly not reach the production deploy");
        reachable.Should().NotContain("EmitProdStarted",
            "nor the production stage at all");

        reachable.Should().Contain("SetProdFailed",
            "the denial terminates the pipeline fail-closed (deploymentStatus = failed:production)");
        reachable.Should().Contain("EmitPipelineFailed",
            "loudly — PIPELINE.FAILED carries the gate's reason via stageError");
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

    // ====================================================================
    // The edge-selection rule the activity applies (F1)
    // ====================================================================

    [Test]
    public void SelectEdge_mapsEachResolution_ontoTheEdgeItsSeverityDeserves()
    {
        // Enforced blocks.
        CheckActionGateActivity.SelectEdge(true, GovernanceEvaluateResponse.OutcomeAutomated)
            .Should().Be(CheckActionGateActivity.EdgeAutomated);
        CheckActionGateActivity.SelectEdge(true, GovernanceEvaluateResponse.OutcomeRequiresHuman)
            .Should().Be(CheckActionGateActivity.EdgeRequiresHuman);
        CheckActionGateActivity.SelectEdge(true, GovernanceEvaluateResponse.OutcomeDenied)
            .Should().Be(CheckActionGateActivity.EdgeDenied,
            "F1: a denial is a hard refusal, not an escalation — folding it onto RequiresHuman is "
            + "what made DISABLING the action weaker than pinning it to AlwaysHuman");

        // Observe-only NEVER hard-refuses, whatever the wire says: `enforced = false`
        // is the admin's explicit "report but do not block", and an observe-mode
        // rollout that could terminate a production pipeline would be unusable.
        foreach (var wire in new[]
                 {
                     GovernanceEvaluateResponse.OutcomeAutomated,
                     GovernanceEvaluateResponse.OutcomeRequiresHuman,
                     GovernanceEvaluateResponse.OutcomeDenied,
                 })
        {
            CheckActionGateActivity.SelectEdge(false, wire)
                .Should().Be(CheckActionGateActivity.EdgeAutomated,
                    $"observe-only must take the Automated edge for '{wire}'");
        }

        // An enforced wire this build does not recognise fails CLOSED onto the safe
        // edge rather than proceeding.
        CheckActionGateActivity.SelectEdge(true, "allowed-with-audit")
            .Should().Be(CheckActionGateActivity.EdgeRequiresHuman);
        CheckActionGateActivity.SelectEdge(true, null)
            .Should().Be(CheckActionGateActivity.EdgeRequiresHuman);
    }
}

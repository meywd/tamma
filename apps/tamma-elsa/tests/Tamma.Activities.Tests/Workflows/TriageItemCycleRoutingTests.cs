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
/// Completeness audit 2026-06-22 — the consumer of the (now fail-closed)
/// triage panel must HONOUR its failure signal. A "failed" panel (below quorum)
/// must NOT march on to PO decision + label application: it routes to a loud
/// non-applying terminal (no silent false success downstream). An "ok"/"partial"
/// panel proceeds as before. Both gate outcomes reach Finish (no deadlock/hang).
/// </summary>
[TestFixture]
public class TriageItemCycleRoutingTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageItemCycleWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageItemCycleWorkflow());
        builder.Object.DefinitionId.Should().Be("triage-item-cycle");
    }

    [Test]
    public void PanelUsableGate_Exists_AfterExtractingPanelResult()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "PanelUsable")
            .Should().BeTrue("the cycle must gate on the panel's fail-closed signal");

        HasEdge("ExtractPanelResult", null, "PanelUsable").Should().BeTrue();
    }

    // ================================================================
    // Context-gathering fail-closed signal (TriageContextGathering build-out)
    // ================================================================

    [Test]
    public void ContextGatheredGate_Exists_AfterExtractingContext()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "ContextGathered")
            .Should().BeTrue("the cycle must gate on the context stage's fail-closed signal");

        HasEdge("ExtractContext", null, "ContextGathered").Should().BeTrue();
    }

    [Test]
    public void GatheredContext_ProceedsToPanelReview()
    {
        HasEdge("ContextGathered", "True", "PanelReview").Should().BeTrue();
    }

    [Test]
    public void FailedContext_SkipsPanelAndLabels_RoutesToLoudTerminal()
    {
        // False (no context gathered) → set reason → mark skipped → finish. It must
        // NOT reach the panel review, PO decision, or label-application activity
        // (no panel over phantom context, no silent labelling).
        HasEdge("ContextGathered", "False", "SetContextFailedReason").Should().BeTrue();
        HasEdge("SetContextFailedReason", null, "MarkSkipped").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "ContextGathered" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("PanelReview");
        falseTargets.Should().NotContain("PODecision");
        falseTargets.Should().NotContain("ApplyLabels");
    }

    [Test]
    public void ContextGatheredGate_HasNoUnconditionalFallthrough()
    {
        var fromGate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "ContextGathered")
            .ToList();

        fromGate.Should().NotBeEmpty();
        fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False");
    }

    [Test]
    public void ContextDispatch_ThreadsTenantId()
    {
        // The cycle forwards tenantId to the context sub-workflow so a tenant-scoped
        // caller's TRIAGE.CONTEXT.* events carry the tenant tag (durable drain reads
        // the TenantId variable, which the sub-workflow stamps from this input).
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "GatherTriageContext");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("triage-context-gathering");
    }

    [Test]
    public void UsablePanel_ProceedsToPoDecisionAndDecisionGate()
    {
        // Build-out: ExtractDecision now feeds the Decision-OK gate (not apply directly),
        // and apply is reached only via the gate's True edge → BuildApplyInputs.
        HasEdge("PanelUsable", "True", "PODecision").Should().BeTrue();
        HasEdge("PODecision", null, "ExtractDecision").Should().BeTrue();
        HasEdge("ExtractDecision", null, "DecisionOK").Should().BeTrue();
        HasEdge("DecisionOK", "True", "BuildApplyInputs").Should().BeTrue();
        // Review fix: BuildApplyInputs → record dropped labels → seed fail-closed result
        // → apply; the apply Success outcome → COMPLETED (a failed apply takes Failure).
        HasEdge("BuildApplyInputs", null, "EmitLabelsInvalid").Should().BeTrue();
        HasEdge("SeedFailedResult", null, "ApplyLabels").Should().BeTrue();
        HasEdge("ApplyLabels", "Success", "EmitCycleCompleted").Should().BeTrue();
    }

    [Test]
    public void FailedPanel_SkipsLabelApplication_RoutesToLoudTerminal()
    {
        // False (failed panel) → set reason → mark skipped → finish. It must NOT
        // reach the PO decision or the label-application activity (no silent
        // labelling off a wholly-failed panel).
        HasEdge("PanelUsable", "False", "SetPanelFailedReason").Should().BeTrue();
        HasEdge("SetPanelFailedReason", null, "MarkSkipped").Should().BeTrue();
        HasEdge("MarkSkipped", null, "OutSkipReason").Should().BeTrue();
        // Build-out: the SKIPPED terminal now emits TRIAGE.ISSUE.SKIPPED + a per-item
        // result before Finish (no longer OutSkipReason → Finish directly).
        HasEdge("OutSkipReason", null, "EmitCycleSkipped").Should().BeTrue();
        HasEdge("EmitCycleSkipped", null, "OutSkippedResult").Should().BeTrue();
        HasEdge("OutSkippedResult", null, "Finish").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "PanelUsable" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("PODecision");
        falseTargets.Should().NotContain("ApplyLabels");
    }

    [Test]
    public void PanelUsableGate_HasNoUnconditionalFallthrough()
    {
        var fromGate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "PanelUsable")
            .ToList();

        fromGate.Should().NotBeEmpty();
        fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False");
    }

    [Test]
    public void BothGateOutcomes_ReachFinish_NoDeadlock()
    {
        // Build-out: every terminal routes to Finish (no deadlock/hang).
        //   triaged:  ApplyLabels → EmitCycleCompleted → OutCompletedResult → Finish
        //   skipped:  OutSkipReason → EmitCycleSkipped → OutSkippedResult → Finish
        //   failed:   SetDecisionFailedReason → EmitCycleFailed → OutFailedResult → Finish
        HasEdge("OutCompletedResult", null, "Finish").Should().BeTrue();
        HasEdge("OutSkippedResult", null, "Finish").Should().BeTrue();
        HasEdge("OutFailedResult", null, "Finish").Should().BeTrue();
    }

    [Test]
    public void PanelDispatch_ThreadsTenantId()
    {
        // The cycle threads tenantId through to the panel sub-workflow input so a
        // tenant-scoped caller's TRIAGE.PANEL.* events carry the tenant tag.
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "PanelReview");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("triage-panel-review");
    }

    // ================================================================
    // #1/#2 — decision-OK gate before apply (no labelling off a bad decision)
    // ================================================================

    [Test]
    public void DecisionOkGate_Exists_AfterExtractingDecision()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "DecisionOK")
            .Should().BeTrue("the cycle must gate apply on a usable PO decision");

        HasEdge("ExtractDecision", null, "DecisionOK").Should().BeTrue();
    }

    [Test]
    public void GoodDecision_RoutesToBuildApplyInputsThenApply()
    {
        // Review fix: BuildApplyInputs → record dropped labels (LABELS.INVALID) → seed a
        // fail-closed itemResult → apply. The seed guarantees a parseable failed output
        // even if the flow halts at a faulting apply.
        HasEdge("DecisionOK", "True", "BuildApplyInputs").Should().BeTrue();
        HasEdge("BuildApplyInputs", null, "EmitLabelsInvalid").Should().BeTrue();
        HasEdge("EmitLabelsInvalid", null, "SeedFailedReason").Should().BeTrue();
        HasEdge("SeedFailedReason", null, "SeedFailedResult").Should().BeTrue();
        HasEdge("SeedFailedResult", null, "ApplyLabels").Should().BeTrue();
    }

    [Test]
    public void BadDecision_RoutesToFailedTerminal_NeverToApply()
    {
        // False (faulted PO / llm-failed / unparsed / empty) → set reason → FAILED →
        // result → finish. It must NEVER reach BuildApplyInputs / ApplyLabels.
        HasEdge("DecisionOK", "False", "SetDecisionFailedReason").Should().BeTrue();
        HasEdge("SetDecisionFailedReason", null, "EmitCycleFailed").Should().BeTrue();
        HasEdge("EmitCycleFailed", null, "OutFailedResult").Should().BeTrue();
        HasEdge("OutFailedResult", null, "Finish").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "DecisionOK" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("BuildApplyInputs");
        falseTargets.Should().NotContain("ApplyLabels");
    }

    [Test]
    public void AllGates_HaveNoUnconditionalFallthrough()
    {
        foreach (var gateId in new[] { "ContextGathered", "PanelUsable", "DecisionOK" })
        {
            var fromGate = _flowchart.Connections
                .Where(c => c.Source.Activity.Id == gateId)
                .ToList();
            fromGate.Should().NotBeEmpty();
            fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False",
                $"gate '{gateId}' must branch only on True/False, never fall through");
        }
    }

    // ================================================================
    // #3 — cycle-scoped TRIAGE.ISSUE.* events on each path
    // ================================================================

    [Test]
    public void StartedEvent_EmittedAtInit_BeforeContextGathering()
    {
        CycleEventNode("EmitCycleStarted").Should().NotBeNull();
        HasEdge("Init", null, "EmitCycleStarted").Should().BeTrue();
        HasEdge("EmitCycleStarted", null, "GatherTriageContext").Should().BeTrue();
    }

    [Test]
    public void EachTerminalPath_EmitsExactlyOneCycleEvent()
    {
        // COMPLETED on the apply-SUCCESS outcome only.
        CycleEventNode("EmitCycleCompleted").Should().NotBeNull();
        HasEdge("ApplyLabels", "Success", "EmitCycleCompleted").Should().BeTrue();

        // SKIPPED on the shared context/panel non-applying terminal.
        CycleEventNode("EmitCycleSkipped").Should().NotBeNull();
        HasEdge("OutSkipReason", null, "EmitCycleSkipped").Should().BeTrue();

        // FAILED on the bad-decision terminal.
        CycleEventNode("EmitCycleFailed").Should().NotBeNull();
        HasEdge("SetDecisionFailedReason", null, "EmitCycleFailed").Should().BeTrue();

        // FAILED on the apply-FAILURE outcome (review fix — the "stuck, no terminal" hole).
        CycleEventNode("EmitCycleApplyFailed").Should().NotBeNull();
        HasEdge("SetApplyFailedReason", null, "EmitCycleApplyFailed").Should().BeTrue();
    }

    // ================================================================
    // Review fix (CRITICAL) — the apply step exposes Success / Failure outcomes; an
    // apply failure routes to a LOUD TRIAGE.ISSUE.FAILED terminal (not a silent halt),
    // and the workflow seeds a fail-closed itemResult before apply + uses
    // continue-with-incidents so a fault can never leave the cycle with no terminal.
    // ================================================================

    [Test]
    public void ApplySuccessOutcome_RoutesToCompletedTerminal()
    {
        HasEdge("ApplyLabels", "Success", "EmitCycleCompleted").Should().BeTrue(
            "a successful apply routes to the COMPLETED terminal");
        HasEdge("EmitCycleCompleted", null, "OutCompletedResult").Should().BeTrue();
        HasEdge("OutCompletedResult", null, "Finish").Should().BeTrue();
    }

    [Test]
    public void ApplyFailureOutcome_RoutesToLoudFailedTerminal_NotCompleted()
    {
        // The apply Failure outcome must reach the loud TRIAGE.ISSUE.FAILED terminal —
        // exactly one cycle terminal on the apply-fault path — and must NOT reach the
        // COMPLETED terminal (no false success / no stuck instance with no terminal).
        HasEdge("ApplyLabels", "Failure", "SetApplyFailedReason").Should().BeTrue();
        HasEdge("SetApplyFailedReason", null, "EmitCycleApplyFailed").Should().BeTrue();
        HasEdge("EmitCycleApplyFailed", null, "OutApplyFailedResult").Should().BeTrue();
        HasEdge("OutApplyFailedResult", null, "Finish").Should().BeTrue();

        var failureReach = Reachable("SetApplyFailedReason");
        failureReach.Should().NotContain("EmitCycleCompleted",
            "an apply failure must never reach the COMPLETED terminal");
        failureReach.Should().NotContain("OutCompletedResult");
    }

    [Test]
    public void ApplyNode_HasBothOutcomePorts_NoUnconditionalFallthrough()
    {
        var fromApply = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "ApplyLabels")
            .ToList();

        fromApply.Should().NotBeEmpty();
        fromApply.Should().OnlyContain(c => c.Source.Port == "Success" || c.Source.Port == "Failure",
            "apply must branch only on its Success/Failure outcomes, never fall through");
        fromApply.Select(c => c.Source.Port).Should().Contain(new[] { "Success", "Failure" });
    }

    [Test]
    public void Workflow_SeedsFailClosedItemResultBeforeApply()
    {
        // A SeedFailedResult SetOutput runs BEFORE ApplyLabels so even a halt (a fault
        // that stops the flow before a terminal) yields a parseable failed itemResult.
        var seed = _flowchart.Activities.OfType<SetOutput>().FirstOrDefault(o => o.Id == "SeedFailedResult");
        seed.Should().NotBeNull("a fail-closed itemResult must be seeded before apply");
        ReadOutputName(seed!).Should().Be("itemResult");
        HasEdge("SeedFailedResult", null, "ApplyLabels").Should().BeTrue(
            "the seeded fail-closed result must immediately precede apply");
    }

    [Test]
    public void Workflow_UsesContinueWithIncidentsStrategy_SoAFaultDoesNotHaltSilently()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageItemCycleWorkflow());
        builder.Object.WorkflowOptions.IncidentStrategyType
            .Should().Be(typeof(Elsa.Workflows.IncidentStrategies.ContinueWithIncidentsStrategy),
                "a faulted node must not halt the cycle with no TRIAGE.ISSUE.* terminal");
    }

    [Test]
    public void DroppedLabels_AreRecordedAsALabelsInvalidEvent_BeforeApply()
    {
        // #7 (MINOR) — dropped out-of-vocab labels are recorded as a loud (warning)
        // TRIAGE.LABELS.INVALID audit row rather than silently discarded. Non-terminal:
        // the cycle still applies the validated subset and proceeds toward apply.
        var node = CycleEventNode("EmitLabelsInvalid");
        node.Should().NotBeNull("dropped labels must be recorded, not silently discarded");
        HasEdge("BuildApplyInputs", null, "EmitLabelsInvalid").Should().BeTrue();
        // It is on the path to apply (non-terminal), not a separate exit.
        Reachable("EmitLabelsInvalid").Should().Contain("ApplyLabels");
    }

    // ================================================================
    // #5 — per-item outcome output on every terminal
    // ================================================================

    [Test]
    public void EveryTerminal_EmitsAnItemResultOutput()
    {
        var itemResultNodes = _flowchart.Activities.OfType<SetOutput>()
            .Where(o => ReadOutputName(o) == "itemResult")
            .Select(o => o.Id)
            .ToList();

        itemResultNodes.Should().Contain(new[] { "OutCompletedResult", "OutSkippedResult", "OutFailedResult" },
            "the fire-and-forget parent needs a per-item outcome on every exit");
    }

    [Test]
    public void PoDispatch_ThreadsTenantId_AndIsMediated()
    {
        var dispatch = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "PODecision");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("triage-po-decision");
    }

    // ================================================================
    // No dangling edge — every non-terminal activity routes somewhere.
    // ================================================================

    [Test]
    public void EveryActivity_RoutesToATerminal_NoDanglingEdge()
    {
        var allIds = _flowchart.Activities.Select(a => a.Id!).ToHashSet();
        var sources = _flowchart.Connections.Select(c => c.Source.Activity.Id!).ToHashSet();

        foreach (var id in allIds.Where(i => i != "Finish"))
        {
            sources.Should().Contain(id, $"activity '{id}' must route somewhere (no dangling edge / hang)");
        }
    }

    private EmitTriageCycleEventActivity? CycleEventNode(string id)
        => _flowchart.Activities.OfType<EmitTriageCycleEventActivity>()
            .FirstOrDefault(a => a.Id == id);

    private static string? ReadOutputName(SetOutput output)
    {
        var value = output.OutputName;
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    /// <summary>Forward-reachable activity ids from a starting node.</summary>
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

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}

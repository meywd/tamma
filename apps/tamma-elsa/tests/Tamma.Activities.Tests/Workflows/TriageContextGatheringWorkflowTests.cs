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
/// Completeness audit 2026-06-22 (<c>TriageContextGathering.md</c>) —
/// workflow-structure coverage for the built-out <c>triage-context-gathering</c>
/// graph. Asserts the load-bearing guarantees: the scan is LLM-mediated (dispatch →
/// <c>llm-call</c>, never a raw provider call); the fail-closed gate routes a failed
/// scan to a LOUD <c>TRIAGE.CONTEXT.FAILED</c> terminal (the FAILED edge never falls
/// through to the COMPLETED/EMPTY path — no false success); STARTED is emitted after
/// init and exactly one terminal event per path; both terminals output
/// <c>contextStatus</c>; every outcome reaches Finish (no dangling edge).
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> (see <see cref="TriagePanelReviewWorkflowTests"/>).</para>
/// </summary>
[TestFixture]
public class TriageContextGatheringWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageContextGatheringWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageContextGatheringWorkflow());
        builder.Object.DefinitionId.Should().Be("triage-context-gathering");
    }

    // ================================================================
    // LLM mediation — the scan goes through llm-call, never a raw provider call
    // ================================================================

    [Test]
    public void GatherContext_IsMediatedThroughLlmCall()
    {
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "GatherContext");

        dispatch.Should().NotBeNull("context gathering must be a DispatchWorkflow step");
        ReadDefinitionId(dispatch!).Should().Be("llm-call",
            "the scan must be mediated through llm-call, never a raw provider call");
    }

    // ================================================================
    // DCB events — STARTED after init; exactly one terminal event per path
    // ================================================================

    [Test]
    public void StartedEvent_EmittedRightAfterInit()
    {
        var started = _flowchart.Activities
            .OfType<EmitTriageContextEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitStarted");
        started.Should().NotBeNull("the stage must emit TRIAGE.CONTEXT.STARTED");

        HasEdge("Init", null, "EmitStarted").Should().BeTrue();
        HasEdge("EmitStarted", null, "GatherContext").Should().BeTrue();
    }

    [Test]
    public void BothTerminalPaths_EmitAContextEvent_AndReachFinish()
    {
        _flowchart.Activities.OfType<EmitTriageContextEventActivity>()
            .Any(a => a.Id == "EmitUsable").Should().BeTrue();
        _flowchart.Activities.OfType<EmitTriageContextEventActivity>()
            .Any(a => a.Id == "EmitFailed").Should().BeTrue();

        HasEdge("EmitUsable", null, "Finish").Should().BeTrue();
        HasEdge("EmitFailed", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // Fail-closed gate — failed scan reaches a LOUD terminal, no false success
    // ================================================================

    [Test]
    public void ContextGatheredGate_ExistsAfterExtraction()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "ContextGathered")
            .Should().BeTrue("the gate must exist to fail a non-gathered scan");

        HasEdge("ExtractResult", null, "ContextGathered").Should().BeTrue();
    }

    [Test]
    public void FailedScan_RoutesToFailedTerminal_NotToCompleted()
    {
        // False (no context) → mark failed → failed outputs → TRIAGE.CONTEXT.FAILED.
        // The False edge must NEVER reach the usable/COMPLETED emit (no false success).
        HasEdge("ContextGathered", "False", "FailedSetStatus").Should().BeTrue();
        HasEdge("FailedSetStatus", null, "FailedOutputs").Should().BeTrue();
        HasEdge("FailedOutputs", null, "EmitFailed").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "ContextGathered" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("EmitUsable");
        falseTargets.Should().NotContain("UsableOutputs");
    }

    [Test]
    public void UsableScan_RoutesToUsableTerminal()
    {
        HasEdge("ContextGathered", "True", "UsableOutputs").Should().BeTrue();
        HasEdge("UsableOutputs", null, "EmitUsable").Should().BeTrue();
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

    // ================================================================
    // Outputs — both terminals expose the contextStatus contract
    // ================================================================

    [Test]
    public void BothTerminals_OutputContextJsonAndStatus()
    {
        foreach (var seqId in new[] { "UsableOutputs", "FailedOutputs" })
        {
            var seq = _flowchart.Activities.OfType<Sequence>().First(s => s.Id == seqId);
            var ids = seq.Activities.OfType<SetOutput>()
                .Select(o => o.Id ?? "")
                .ToList();

            ids.Should().Contain($"{seqId}_Context");
            ids.Should().Contain($"{seqId}_Status");
        }
    }

    // ================================================================
    // No dangling edges — every non-terminal activity has an outgoing edge.
    // ================================================================

    [Test]
    public void EveryActivity_RoutesToATerminal_NoDanglingEdge()
    {
        var allIds = _flowchart.Activities.Select(a => a.Id!).ToHashSet();
        var sources = _flowchart.Connections.Select(c => c.Source.Activity.Id!).ToHashSet();

        foreach (var id in allIds.Where(i => i != "Finish"))
        {
            sources.Should().Contain(id, $"activity '{id}' must route somewhere (no dangling edge)");
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

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}

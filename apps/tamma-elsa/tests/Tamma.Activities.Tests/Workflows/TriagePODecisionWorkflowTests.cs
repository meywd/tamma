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
/// Completeness audit 2026-06-22 (<c>TriagePODecision.md</c>) — workflow-structure
/// coverage for the built-out <c>triage-po-decision</c> graph. Asserts the
/// load-bearing fail-closed / no-false-success guarantees:
/// <list type="bullet">
///   <item><description>#1 — a "PO Call Succeeded?" gate exists; its False edge
///     routes to a BuildFailure → TRIAGE.PO_DECISION.FAILED terminal and NEVER falls
///     through to the success/COMPLETED path.</description></item>
///   <item><description>#3 — STARTED is emitted before the dispatch; each terminal
///     emits exactly its event; the LLM call is mediated through <c>llm-call</c>.</description></item>
///   <item><description>#7 — an "Inputs Present?" gate short-circuits empty input to
///     a BuildSkipped → SKIPPED terminal before the dispatch (no LLM spend).</description></item>
///   <item><description>No dangling edge — every node routes to a terminal.</description></item>
/// </list>
/// Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> (see <see cref="TriageContextGatheringWorkflowTests"/>).
/// </summary>
[TestFixture]
public class TriagePODecisionWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriagePODecisionWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriagePODecisionWorkflow());
        builder.Object.DefinitionId.Should().Be("triage-po-decision");
    }

    // ================================================================
    // LLM mediation — the decision goes through llm-call (32-5 boundary)
    // ================================================================

    [Test]
    public void PODecision_IsMediatedThroughLlmCall()
    {
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "PODecisionCall");

        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("llm-call",
            "the PO decision must be mediated through llm-call, never a raw provider call");
    }

    // ================================================================
    // #7 — empty-input guard short-circuits before the dispatch
    // ================================================================

    [Test]
    public void InputsPresentGate_ExistsAfterInit_BeforeDispatch()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "InputsPresent").Should().BeTrue();

        HasEdge("Init", null, "InputsPresent").Should().BeTrue();
    }

    [Test]
    public void EmptyInput_RoutesToSkippedTerminal_NotToDispatch()
    {
        // False (empty input) → BuildSkipped → SKIPPED. The False edge must NEVER
        // reach the dispatch / STARTED path (no LLM spend on garbage).
        HasEdge("InputsPresent", "False", "BuildSkipped").Should().BeTrue();
        HasEdge("BuildSkipped", null, "EmitSkipped").Should().BeTrue();
        HasEdge("EmitSkipped", null, "SetOutputs").Should().BeTrue();

        var falseTargets = FromPort("InputsPresent", "False");
        falseTargets.Should().NotContain("EmitStarted");
        falseTargets.Should().NotContain("PODecisionCall");

        EventActivityId("EmitSkipped").Should().NotBeNull();
    }

    [Test]
    public void PresentInput_RoutesToStartedThenDispatch()
    {
        HasEdge("InputsPresent", "True", "EmitStarted").Should().BeTrue();
        HasEdge("EmitStarted", null, "PODecisionCall").Should().BeTrue();
        HasEdge("PODecisionCall", null, "CaptureResult").Should().BeTrue();
        HasEdge("CaptureResult", null, "CallSucceeded").Should().BeTrue();

        EventActivityId("EmitStarted").Should().NotBeNull();
    }

    // ================================================================
    // #1 — fail-closed success gate, no false success
    // ================================================================

    [Test]
    public void CallSucceededGate_ExistsAfterCaptureResult()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "CallSucceeded").Should().BeTrue();
    }

    [Test]
    public void FailedCall_RoutesToFailedTerminal_NotToExtractOrCompleted()
    {
        // False (LLM failed) → BuildFailure → TRIAGE.PO_DECISION.FAILED.
        // The False edge must NEVER reach ExtractDecision / the COMPLETED emit
        // (no fabricated clean decision on a failed call).
        HasEdge("CallSucceeded", "False", "BuildFailure").Should().BeTrue();
        HasEdge("BuildFailure", null, "EmitFailed").Should().BeTrue();
        HasEdge("EmitFailed", null, "SetOutputs").Should().BeTrue();

        var falseTargets = FromPort("CallSucceeded", "False");
        falseTargets.Should().NotContain("ExtractDecision");
        falseTargets.Should().NotContain("EmitCompleted");

        EventActivityId("EmitFailed").Should().NotBeNull();
    }

    [Test]
    public void SucceededCall_RoutesToExtractThenCompleted()
    {
        HasEdge("CallSucceeded", "True", "ExtractDecision").Should().BeTrue();
        HasEdge("ExtractDecision", null, "EmitCompleted").Should().BeTrue();
        HasEdge("EmitCompleted", null, "SetOutputs").Should().BeTrue();

        EventActivityId("EmitCompleted").Should().NotBeNull();
    }

    [Test]
    public void Gates_HaveNoUnconditionalFallthrough()
    {
        foreach (var gateId in new[] { "InputsPresent", "CallSucceeded" })
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
    // Output contract — decisionJson preserved + audit outputs (#6)
    // ================================================================

    [Test]
    public void SetOutputs_PreservesDecisionJson_AndAddsAuditOutputs()
    {
        var seq = _flowchart.Activities.OfType<Sequence>().First(s => s.Id == "SetOutputs");
        var names = seq.Activities.OfType<SetOutput>()
            .Select(o => ReadOutputName(o))
            .ToList();

        names.Should().Contain("decisionJson", "the existing output contract must be preserved");
        names.Should().Contain("callSucceeded");
        names.Should().Contain("providerUsed");
        names.Should().Contain("costUsd");
        names.Should().Contain("rawResponse");
    }

    [Test]
    public void SetOutputs_ReachesFinish()
    {
        HasEdge("SetOutputs", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // No dangling edges — every non-terminal activity routes somewhere.
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

    private List<string> FromPort(string sourceId, string port)
        => _flowchart.Connections
            .Where(c => c.Source.Activity.Id == sourceId && c.Source.Port == port)
            .Select(c => c.Target.Activity.Id!)
            .ToList();

    private string? EventActivityId(string id)
        => _flowchart.Activities.OfType<EmitTriagePoDecisionEventActivity>()
            .FirstOrDefault(a => a.Id == id)?.Id;

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }

    private static string? ReadOutputName(SetOutput output)
    {
        var value = output.OutputName;
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}

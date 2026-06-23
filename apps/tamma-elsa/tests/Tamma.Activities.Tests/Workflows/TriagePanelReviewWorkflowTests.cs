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
/// Completeness audit 2026-06-22 — workflow-structure coverage for the built-out
/// <c>triage-panel-review</c> graph. Asserts the load-bearing guarantees the
/// pure-function tests can't: every panel role is LLM-mediated (dispatch →
/// <c>llm-call</c>, never a raw provider call); every role's finding is persisted;
/// the quorum gate routes a failed panel to a LOUD <c>TRIAGE.PANEL.FAILED</c>
/// terminal (no false success — the FAILED edge never falls through to the
/// COMPLETED/PARTIAL path); every outcome reaches Finish (no dangling edge).
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> rather than running the full Elsa runtime
/// (see <see cref="PullRequestWorkflowTests"/>).</para>
/// </summary>
[TestFixture]
public class TriagePanelReviewWorkflowTests
{
    private Flowchart _flowchart = null!;

    private static readonly string[] RoleIdBases = { "Security", "Developer", "Devops", "Tester" };

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriagePanelReviewWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriagePanelReviewWorkflow());
        builder.Object.DefinitionId.Should().Be("triage-panel-review");
    }

    // ================================================================
    // LLM mediation — every role review goes through llm-call
    // ================================================================

    [Test]
    public void EveryRoleReview_IsMediatedThroughLlmCall()
    {
        foreach (var role in RoleIdBases)
        {
            var dispatch = _flowchart.Activities
                .OfType<DispatchWorkflow>()
                .FirstOrDefault(d => d.Id == $"{role}Review");

            dispatch.Should().NotBeNull($"{role} review must be a DispatchWorkflow step");
            ReadDefinitionId(dispatch!).Should().Be("llm-call",
                $"{role} review must be mediated through llm-call, never a raw provider call");
        }
    }

    // ================================================================
    // Per-role persistence (partial-result durability)
    // ================================================================

    [Test]
    public void EveryRole_PersistsItsFinding_BeforeAggregation()
    {
        foreach (var role in RoleIdBases)
        {
            // call → extract → store chain per role.
            HasEdge($"{role}Review", null, $"Extract{role}Review").Should().BeTrue();
            HasEdge($"Extract{role}Review", null, $"Store{role}Review").Should().BeTrue();

            _flowchart.Activities.Any(a => a.Id == $"Store{role}Review")
                .Should().BeTrue($"{role}'s finding must be persisted (StoreRoleFinding)");
        }

        // The last role's store chains into aggregation.
        HasEdge("StoreTesterReview", null, "Aggregate").Should().BeTrue();
    }

    // ================================================================
    // Fail-closed quorum gate — failed panel reaches a LOUD terminal
    // ================================================================

    [Test]
    public void PanelUsableGate_ExistsAfterAggregation()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "PanelUsable")
            .Should().BeTrue("the quorum gate must exist to fail a below-quorum panel");

        HasEdge("Aggregate", null, "PanelUsable").Should().BeTrue();
    }

    [Test]
    public void FailedPanel_RoutesToFailedTerminal_NotToCompleted()
    {
        // False (below quorum) → failed outputs → TRIAGE.PANEL.FAILED. The False
        // edge must NEVER reach the usable/COMPLETED emit (no false success).
        HasEdge("PanelUsable", "False", "FailedOutputs").Should().BeTrue();
        HasEdge("FailedOutputs", null, "EmitFailed").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "PanelUsable" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("EmitUsable");
        falseTargets.Should().NotContain("UsableOutputs");
    }

    [Test]
    public void UsablePanel_RoutesToUsableTerminal()
    {
        HasEdge("PanelUsable", "True", "UsableOutputs").Should().BeTrue();
        HasEdge("UsableOutputs", null, "EmitUsable").Should().BeTrue();
    }

    [Test]
    public void PanelUsableGate_HasNoUnconditionalFallthrough()
    {
        // Every edge out of the gate must be outcome-qualified (True/False); a
        // portless edge would be a silent fall-through.
        var fromGate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "PanelUsable")
            .ToList();

        fromGate.Should().NotBeEmpty();
        fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False");
    }

    // ================================================================
    // DCB events — STARTED after init; exactly one terminal event per path
    // ================================================================

    [Test]
    public void StartedEvent_EmittedRightAfterInit()
    {
        var started = _flowchart.Activities
            .OfType<EmitTriageEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitStarted");
        started.Should().NotBeNull("the panel must emit TRIAGE.PANEL.STARTED");

        HasEdge("Init", null, "EmitStarted").Should().BeTrue();
        HasEdge("EmitStarted", null, "SecurityReview").Should().BeTrue();
    }

    [Test]
    public void BothTerminalPaths_EmitAPanelEvent_AndReachFinish()
    {
        _flowchart.Activities.OfType<EmitTriageEventActivity>()
            .Any(a => a.Id == "EmitUsable").Should().BeTrue();
        _flowchart.Activities.OfType<EmitTriageEventActivity>()
            .Any(a => a.Id == "EmitFailed").Should().BeTrue();

        HasEdge("EmitUsable", null, "Finish").Should().BeTrue();
        HasEdge("EmitFailed", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // Outputs — both terminals expose the panel-health contract
    // ================================================================

    [Test]
    public void BothTerminals_OutputPanelStatusAndHealthSignals()
    {
        foreach (var seqId in new[] { "UsableOutputs", "FailedOutputs" })
        {
            var seq = _flowchart.Activities.OfType<Sequence>().First(s => s.Id == seqId);
            var ids = seq.Activities.OfType<SetOutput>()
                .Select(o => o.Id ?? "")
                .ToList();

            ids.Should().Contain($"{seqId}_PanelResult");
            ids.Should().Contain($"{seqId}_PanelStatus");
            ids.Should().Contain($"{seqId}_Succeeded");
            ids.Should().Contain($"{seqId}_FailedRoles");
        }
    }

    // ================================================================
    // No dangling edges — every non-terminal activity has an outgoing edge,
    // and Finish is reachable from each terminal.
    // ================================================================

    [Test]
    public void EveryActivity_RoutesToATerminal_NoDanglingEdge()
    {
        var allIds = _flowchart.Activities.Select(a => a.Id!).ToHashSet();
        var sources = _flowchart.Connections.Select(c => c.Source.Activity.Id!).ToHashSet();

        // Every activity except Finish must have at least one outgoing connection.
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

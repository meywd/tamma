using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Structure coverage for the <c>tdd-cycle</c> workflow's commit terminal. The
/// workflow had NO test file at all before 2026-08-18, and the hole that absence
/// hid was load-bearing rather than cosmetic:
///
/// <para><c>SetCompletedOutputs</c> sets the workflow's <c>success</c> output to a
/// literal <c>true</c> and never reads <c>CommitResult.Success</c>, while the graph
/// wired <c>CommitChanges</c> straight into <c>UpdateCodeIndex</c> → that terminal.
/// So a commit step that failed WITHOUT throwing still reported a completed TDD
/// cycle — and <c>tdd-with-debug-retry</c> gates its retry loop on exactly that
/// output (<c>TddWithDebugRetryWorkflow</c>'s <c>TddSuccess</c> FlowDecision reads
/// <c>result["success"]</c>), so its gate was permanently satisfied and a commit
/// failure could never trigger a retry.</para>
///
/// <para><c>CommitChangesActivity</c> now throws a typed <c>TammaError</c> on every
/// non-commit path, which faults the cycle before it can reach the success terminal.
/// These tests pin the GRAPH-level guarantee anyway: reporting success must not
/// depend on an activity remembering to throw.</para>
/// </summary>
[TestFixture]
public class TddWorkflowCommitHonestyTests
{
    private Flowchart _flowchart = null!;

    /// <summary>The three <c>Finish</c> terminals, which legitimately have no outgoing edge.</summary>
    private static readonly string[] Terminals =
        { "FinishSuccess", "FinishFailed", "FinishSyntaxInvalid" };

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TddWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TddWorkflow());
        builder.Object.DefinitionId.Should().Be("tdd-cycle");
    }

    [Test]
    public void CommitStep_IsGatedByADecision_NotWiredStraightToTheSuccessTerminal()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "CommitSucceededCheck")
            .Should().BeTrue("whether the commit happened must be a graph decision, not an assumption");

        HasEdge("CommitChanges", null, "UpdateCodeIndex")
            .Should().BeFalse("the commit must not reach the success terminal without passing the gate");

        HasEdge("CommitChanges", null, "CommitSucceededCheck").Should().BeTrue();
    }

    [Test]
    public void CommitGate_RoutesOnlyViaTrueAndFalse_NoUnconditionalFallThrough()
    {
        var fromGate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "CommitSucceededCheck")
            .ToList();

        fromGate.Should().NotBeEmpty();
        fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False");
    }

    [Test]
    public void CommitGate_TrueContinuesToIndexing_FalseReachesAFailureTerminal()
    {
        HasEdge("CommitSucceededCheck", "True", "UpdateCodeIndex").Should().BeTrue();
        HasEdge("CommitSucceededCheck", "False", "SetCommitFailedOutputs").Should().BeTrue();
        HasEdge("SetCommitFailedOutputs", null, "FinishFailed").Should().BeTrue();

        // The whole point: a failed commit must never reach the success terminal.
        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "CommitSucceededCheck" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("SetCompletedOutputs");
        falseTargets.Should().NotContain("UpdateCodeIndex");
    }

    [Test]
    public void SuccessTerminal_IsReachableOnlyThroughTheCommitGate()
    {
        // SetCompletedOutputs is fed by UpdateCodeIndex alone, and UpdateCodeIndex is
        // fed by the gate's True port alone — so there is exactly one route to success.
        _flowchart.Connections
            .Where(c => c.Target.Activity.Id == "SetCompletedOutputs")
            .Select(c => c.Source.Activity.Id)
            .Should().BeEquivalentTo(new[] { "UpdateCodeIndex" });

        _flowchart.Connections
            .Where(c => c.Target.Activity.Id == "UpdateCodeIndex")
            .Select(c => c.Source.Activity.Id)
            .Should().BeEquivalentTo(new[] { "CommitSucceededCheck" });
    }

    [Test]
    public void CommitFailureSink_IsDistinctFromTheGreenPhaseFailureSink()
    {
        // SetFailedOutputs hardcodes a "GREEN phase failed after N debug iterations"
        // message; reusing it for a commit failure would mislabel the cause.
        _flowchart.Activities.Any(a => a.Id == "SetCommitFailedOutputs").Should().BeTrue();
        _flowchart.Activities.Any(a => a.Id == "SetFailedOutputs").Should().BeTrue();

        HasEdge("CommitSucceededCheck", "False", "SetFailedOutputs").Should().BeFalse();
    }

    [Test]
    public void EveryActivity_RoutesSomewhere_NoDanglingEdge()
    {
        var sources = _flowchart.Connections.Select(c => c.Source.Activity.Id!).ToHashSet();

        foreach (var id in _flowchart.Activities.Select(a => a.Id!).Where(i => !Terminals.Contains(i)))
        {
            sources.Should().Contain(id, $"activity '{id}' must route somewhere (no dangling edge)");
        }
    }

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);
}

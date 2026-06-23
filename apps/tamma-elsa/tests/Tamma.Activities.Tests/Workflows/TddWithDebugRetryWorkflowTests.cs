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
/// Completeness audit 2026-06-22 (<c>TddWithDebugRetry.md</c>) — workflow-structure
/// coverage for the built-out <c>tdd-with-debug-retry</c> orchestrator. Asserts the
/// load-bearing guarantees the audit flagged as missing:
/// <list type="bullet">
///   <item><description>a <b>graph-enforced</b> retry bound (the debug guard FlowDecision
///     with True/False ports — no unconditional fall-through back to the cycle);</description></item>
///   <item><description>an explicit <b>exhaustion terminal</b> — the guard's False edge
///     reaches a LOUD <c>TDD_DEBUG.RETRY.EXHAUSTED</c> emit + a <c>success=false</c>
///     failure terminal, NEVER the success path (no silent success);</description></item>
///   <item><description><b>every dispatched sub-workflow's outcome is routed</b> — the
///     <c>tdd-cycle</c> result feeds a success gate, and the <c>debugging</c> result
///     feeds an escalation gate that short-circuits to failure instead of dangling;</description></item>
///   <item><description>run-level <c>TDD_DEBUG.*</c> DCB events at graph boundaries;</description></item>
///   <item><description>every activity routes to a terminal (no dangling edge / no hang).</description></item>
/// </list>
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> (see <see cref="TriageContextGatheringWorkflowTests"/>).</para>
/// </summary>
[TestFixture]
public class TddWithDebugRetryWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TddWithDebugRetryWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TddWithDebugRetryWorkflow());
        builder.Object.DefinitionId.Should().Be("tdd-with-debug-retry");
    }

    // ================================================================
    // Sub-workflow mediation — TDD + debugging dispatched (no raw provider call)
    // ================================================================

    [Test]
    public void TddCycle_IsDispatchedToTddCycleWorkflow()
    {
        var dispatch = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchTddCycle");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("tdd-cycle");
    }

    [Test]
    public void DebugFailure_IsDispatchedToDebuggingWorkflow()
    {
        var dispatch = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchTddDebugging");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("debugging");
    }

    // ================================================================
    // Graph-enforced loop bound — the retry guard exists with True/False ports
    // and has NO unconditional fall-through.
    // ================================================================

    [Test]
    public void RetryGuard_IsAGraphEnforcedFlowDecision_WithOnlyTrueFalsePorts()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "TddDebugGuard")
            .Should().BeTrue("the bounded loop must be enforced by a FlowDecision guard, not code-only");

        var fromGuard = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "TddDebugGuard")
            .ToList();

        fromGuard.Should().NotBeEmpty();
        fromGuard.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False",
            "the guard must route only via True/False — no unconditional fall-through past the bound");
    }

    [Test]
    public void RetryGuard_TrueRoutesIntoTheDebugLeg_FalseRoutesToExhaustionTerminal()
    {
        // True (budget remaining) → increment + debug; False (exhausted) → loud terminal.
        HasEdge("TddDebugGuard", "True", "IncrTddDebug").Should().BeTrue();

        // False MUST reach the exhaustion emit, and MUST NOT reach the success path.
        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "TddDebugGuard" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("TddRetryFinishSuccess");
        falseTargets.Should().NotContain("EmitCompletedSuccess");
    }

    // ================================================================
    // Explicit exhaustion terminal — NOT a silent success.
    // ================================================================

    [Test]
    public void RetryExhaustion_RoutesToLoudFailureTerminal_NotSuccess()
    {
        // The exhaustion path: guard False → emit RETRY.EXHAUSTED → failure outputs.
        _flowchart.Activities.OfType<EmitTddDebugEventActivity>()
            .Any(a => a.Id == "EmitRetryExhausted")
            .Should().BeTrue("the loop must emit a loud TDD_DEBUG.RETRY.EXHAUSTED on exhaustion");

        // Guard False -> set reason -> emit EXHAUSTED -> failure terminal -> finish.
        HasEdge("TddDebugGuard", "False", "SetReasonNotConverged").Should().BeTrue();
        HasEdge("SetReasonNotConverged", null, "EmitRetryExhausted").Should().BeTrue();
        HasEdge("EmitRetryExhausted", null, "TddRetryFinishFailure").Should().BeTrue();
        HasEdge("TddRetryFinishFailure", null, "TddRetryFinish").Should().BeTrue();
    }

    [Test]
    public void FailureTerminal_SetsSuccessFalse_AndSurfacesRealFailureDetail()
    {
        var seq = _flowchart.Activities.OfType<Sequence>()
            .First(s => s.Id == "TddRetryFinishFailure");
        var outputs = seq.Activities.OfType<SetOutput>().ToList();

        // success=false is set.
        outputs.Any(o => o.OutputName?.Expression?.Value?.ToString() == "success")
            .Should().BeTrue();

        // The failure carries an errorMessage AND a finishReason (no generic-only string).
        var names = outputs
            .Select(o => o.OutputName?.Expression?.Value?.ToString())
            .ToList();
        names.Should().Contain("errorMessage");
        names.Should().Contain("finishReason");
        // Sibling parity: callers see how many retries were burned.
        names.Should().Contain("tddDebugAttempt");
    }

    // ================================================================
    // Sub-workflow outcome routing — TDD success gate + debugger escalation gate.
    // ================================================================

    [Test]
    public void TddCycleResult_FeedsASuccessGate_RoutedBothWays()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "TddSuccess").Should().BeTrue();

        // Passed → completed-success emit + success terminal.
        HasEdge("TddSuccess", "True", "EmitCyclePassed").Should().BeTrue();
        // Failed → capture the real cause → cycle-failed emit → retry guard (the bound).
        HasEdge("TddSuccess", "False", "CaptureTddError").Should().BeTrue();
        HasEdge("CaptureTddError", null, "EmitCycleFailed").Should().BeTrue();
        HasEdge("EmitCycleFailed", null, "TddDebugGuard").Should().BeTrue();
    }

    [Test]
    public void DebuggerResult_FeedsAnEscalationGate_ThatShortCircuitsToFailure()
    {
        // cap.9 — the debugging result is INSPECTED, not blindly looped back.
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "DebuggerEscalated")
            .Should().BeTrue("the dispatched debugging outcome must be routed, not dangling");

        HasEdge("DispatchTddDebugging", null, "DebuggerEscalated").Should().BeTrue();

        // Escalated (debugger success=false) → set reason → loud escalation terminal
        // (NOT loop back).
        HasEdge("DebuggerEscalated", "True", "SetReasonEscalated").Should().BeTrue();
        HasEdge("SetReasonEscalated", null, "EmitDebuggerEscalated").Should().BeTrue();
        HasEdge("EmitDebuggerEscalated", null, "TddRetryFinishFailure").Should().BeTrue();

        // Not escalated (debugger fixed / soft-fail) → loop back to re-test (via the
        // STARTED emit, so each cycle dispatch is audited).
        HasEdge("DebuggerEscalated", "False", "EmitCycleStarted").Should().BeTrue();

        // The escalation gate has only True/False — no dangling.
        var fromGate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "DebuggerEscalated")
            .ToList();
        fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False");
    }

    // ================================================================
    // Run-level DCB events at graph boundaries.
    // ================================================================

    [Test]
    public void CycleStarted_IsEmittedBeforeEachDispatch()
    {
        _flowchart.Activities.OfType<EmitTddDebugEventActivity>()
            .Any(a => a.Id == "EmitCycleStarted").Should().BeTrue();

        // STARTED runs right before the cycle dispatch (and the loop re-enters via it).
        HasEdge("EmitCycleStarted", null, "DispatchTddCycle").Should().BeTrue();
    }

    [Test]
    public void SuccessPath_EmitsCompletedSuccessEvent_BeforeSuccessTerminal()
    {
        _flowchart.Activities.OfType<EmitTddDebugEventActivity>()
            .Any(a => a.Id == "EmitCyclePassed").Should().BeTrue();
        _flowchart.Activities.OfType<EmitTddDebugEventActivity>()
            .Any(a => a.Id == "EmitCompletedSuccess").Should().BeTrue();

        HasEdge("EmitCyclePassed", null, "EmitCompletedSuccess").Should().BeTrue();
        HasEdge("EmitCompletedSuccess", null, "TddRetryFinishSuccess").Should().BeTrue();
        HasEdge("TddRetryFinishSuccess", null, "TddRetryFinish").Should().BeTrue();
    }

    [Test]
    public void DebugAttempt_EmitsAttemptedEvent()
    {
        _flowchart.Activities.OfType<EmitTddDebugEventActivity>()
            .Any(a => a.Id == "EmitDebugAttempted").Should().BeTrue();

        // increment → emit attempted → dispatch debugging.
        HasEdge("IncrTddDebug", null, "EmitDebugAttempted").Should().BeTrue();
        HasEdge("EmitDebugAttempted", null, "DispatchTddDebugging").Should().BeTrue();
    }

    // ================================================================
    // Outputs — success terminal exposes the attempts counter too (sibling parity).
    // ================================================================

    [Test]
    public void SuccessTerminal_ExposesAttemptsCounter()
    {
        var seq = _flowchart.Activities.OfType<Sequence>()
            .First(s => s.Id == "TddRetryFinishSuccess");
        var names = seq.Activities.OfType<SetOutput>()
            .Select(o => o.OutputName?.Expression?.Value?.ToString())
            .ToList();

        names.Should().Contain("success");
        names.Should().Contain("tddDebugAttempt");
    }

    // ================================================================
    // Real failure propagation — the failure message carries the actual cause
    // and distinguishes non-convergence from a debugger escalation (no generic
    // "retry limit reached" string; no silent failure).
    // ================================================================

    [Test]
    public void BuildFailureMessage_NotConverged_SurfacesRealCause_AndAttempts()
    {
        var msg = TddWithDebugRetryWorkflow.BuildFailureMessage(
            TddDebugEvents.ReasonNotConverged, attempts: 3, maxRetries: 3,
            lastError: "GREEN phase failed: 2 tests still red in auth.spec.ts");

        msg.Should().Contain("did not converge");
        msg.Should().Contain("3/3");
        msg.Should().Contain("auth.spec.ts", "the REAL underlying cause must be surfaced");
        msg.Should().NotBe("TDD debug retry limit reached (3 attempts)",
            "the generic string that dropped the cause must be gone");
    }

    [Test]
    public void BuildFailureMessage_DebuggerEscalated_IsDistinctFromNonConvergence()
    {
        var escalated = TddWithDebugRetryWorkflow.BuildFailureMessage(
            TddDebugEvents.ReasonDebuggerEscalated, 1, 3, "could not fix");
        var notConverged = TddWithDebugRetryWorkflow.BuildFailureMessage(
            TddDebugEvents.ReasonNotConverged, 3, 3, "still red");

        escalated.Should().Contain("escalated");
        notConverged.Should().NotContain("escalated");
        escalated.Should().NotBe(notConverged);
    }

    [Test]
    public void BuildFailureMessage_EmptyError_StillNonEmpty_NoSilentFailure()
    {
        var msg = TddWithDebugRetryWorkflow.BuildFailureMessage(
            TddDebugEvents.ReasonNotConverged, 3, 3, "");
        msg.Should().NotBeNullOrWhiteSpace();
        msg.Should().Contain("TDD cycle failed");
    }

    // ================================================================
    // No dangling edges — every non-terminal activity has an outgoing edge.
    // ================================================================

    [Test]
    public void EveryActivity_RoutesToATerminal_NoDanglingEdge()
    {
        var allIds = _flowchart.Activities.Select(a => a.Id!).ToHashSet();
        var sources = _flowchart.Connections.Select(c => c.Source.Activity.Id!).ToHashSet();

        foreach (var id in allIds.Where(i => i != "TddRetryFinish"))
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

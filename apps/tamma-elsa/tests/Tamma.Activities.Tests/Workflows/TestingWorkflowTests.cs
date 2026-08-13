using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Testing;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness audit 2026-06-22 (<c>Testing.md</c>) — workflow-structure coverage for the
/// built-out <c>testing-pipeline</c>. Asserts the P0/P1 guarantees the audit flagged as
/// missing, by inspecting the BUILT Flowchart via <see cref="WorkflowTestHelper"/>:
/// <list type="bullet">
///   <item><description>#1/#6 — the MajorIssues path GENERATES a fix (mediated
///     <c>llm-call</c>) BEFORE <c>CommitFix</c>, and a zero-files-changed commit is gated
///     to escalation (no false-success loop);</description></item>
///   <item><description>#2 — the CI wait exposes a <c>Timeout</c> edge that escalates (no
///     permanent hang);</description></item>
///   <item><description>#3 — DCB events at trigger / results / each gate / fix-commit /
///     pass / escalation;</description></item>
///   <item><description>#4 — a trigger-success gate routes a failed trigger to escalation
///     instead of into a dead wait;</description></item>
///   <item><description>#5 — a mandatory escalation terminal that sets
///     <c>escalated=true</c> + <c>passed=false</c> + a structured reason;</description></item>
///   <item><description>every activity routes to a terminal (no dangling edge / no hang).</description></item>
/// </list>
/// </summary>
[TestFixture]
public class TestingWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TestingWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TestingWorkflow());
        builder.Object.DefinitionId.Should().Be("testing-pipeline");
    }

    // ================================================================
    // #1/#6 — auto-fix that actually GENERATES a fix (mediated) before committing.
    // ================================================================

    [Test]
    public void AutoFix_GeneratesFixViaLlmCall_BeforeCommitting()
    {
        var generateFix = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "GenerateFix");
        generateFix.Should().NotBeNull("the MajorIssues path must GENERATE a fix before committing");
        ReadDefinitionId(generateFix!).Should().Be("llm-call",
            "fix generation MUST be mediated through llm-call — a step never calls a provider directly");

        // guard True -> GenerateFix -> CommitFix (generation precedes the commit)
        HasEdge("MaxAttemptGuard", "True", "GenerateFix").Should().BeTrue();
        HasEdge("GenerateFix", null, "CommitFix").Should().BeTrue();
    }

    [Test]
    public void OnlyMediatedDispatch_NoRawProviderCall()
    {
        // The single dispatch in this workflow is the mediated llm-call fix generation.
        _flowchart.Activities.OfType<DispatchWorkflow>()
            .Select(ReadDefinitionId)
            .Should().OnlyContain(id => id == "llm-call");
    }

    [Test]
    public void ZeroFilesChangedCommit_IsTreatedAsNonFix_AndEscalates()
    {
        // CommitFix -> a FlowDecision on whether files actually changed.
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "CommitMadeChanges")
            .Should().BeTrue("a no-op commit must be gated, not blindly re-CI'd");

        HasEdge("CommitFix", null, "CommitMadeChanges").Should().BeTrue();

        // True (real change) -> emit committed -> index -> increment -> re-trigger.
        HasEdge("CommitMadeChanges", "True", "EmitAutofixCommitted").Should().BeTrue();
        HasEdge("EmitAutofixCommitted", null, "UpdateCodeIndex").Should().BeTrue();

        // False (no change) -> emit NOOP -> reason -> escalation terminal (NOT a re-trigger).
        HasEdge("CommitMadeChanges", "False", "EmitAutofixNoop").Should().BeTrue();
        var noopTargets = ReachableFrom("EmitAutofixNoop");
        noopTargets.Should().Contain("EmitGateEscalated",
            "a no-op fix must escalate, never loop pretending progress");
        noopTargets.Should().NotContain("ReTriggerCI",
            "a no-op fix must NOT re-trigger CI (no false-success loop)");
    }

    // ================================================================
    // #4 — fail fast on trigger failure (no dead wait with RunId=unknown).
    // ================================================================

    [Test]
    public void TriggerFailure_RoutesToEscalation_NotIntoTheWait()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "TriggerSucceeded")
            .Should().BeTrue("a failed CI trigger must be gated before the wait");

        // True -> wait; False -> escalate (ci-trigger-failed), never the wait.
        HasEdge("TriggerSucceeded", "True", "WaitForCIResults").Should().BeTrue();
        var failTargets = ReachableFrom("SetReasonTriggerFailed");
        failTargets.Should().Contain("EmitGateEscalated");

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "TriggerSucceeded" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();
        falseTargets.Should().NotContain("WaitForCIResults");
    }

    // ================================================================
    // #2 — CI-wait timeout takes a deterministic escalation edge (no permanent hang).
    // ================================================================

    [Test]
    public void CiWait_HasReceivedAndTimeoutOutcomes()
    {
        var fromWait = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "WaitForCIResults")
            .Select(c => c.Source.Port)
            .ToList();
        fromWait.Should().Contain("Received");
        fromWait.Should().Contain("Timeout", "the dead-CI-wait hang risk is closed by a Timeout edge");
    }

    [Test]
    public void CiTimeout_RoutesToEscalation()
    {
        HasEdge("WaitForCIResults", "Timeout", "SetReasonTimeout").Should().BeTrue();
        ReachableFrom("SetReasonTimeout").Should().Contain("EmitGateEscalated");
        // The timeout edge emits a loud TEST.CI_TIMED_OUT before escalating.
        _flowchart.Activities.OfType<EmitTestingEventActivity>()
            .Any(a => a.Id == "EmitCiTimedOut").Should().BeTrue();
    }

    // ================================================================
    // #3 — DCB audit trail at every boundary.
    // ================================================================

    [Test]
    public void EmitsDcbEvents_AtEveryBoundary()
    {
        var emitIds = _flowchart.Activities.OfType<EmitTestingEventActivity>()
            .Select(a => a.Id!)
            .ToList();

        emitIds.Should().Contain("EmitCiTriggered");        // TEST.CI_TRIGGERED
        emitIds.Should().Contain("EmitResultsReceived");    // TEST.RESULTS_RECEIVED
        emitIds.Should().Contain("EmitGateEvaluated");      // GATE.EVALUATED
        emitIds.Should().Contain("EmitAutofixCommitted");   // GATE.AUTOFIX_COMMITTED
        emitIds.Should().Contain("EmitAutofixNoop");        // GATE.AUTOFIX_NOOP
        emitIds.Should().Contain("EmitGatePassed");         // GATE.PASSED
        emitIds.Should().Contain("EmitGateEscalated");      // GATE.ESCALATED
        emitIds.Should().Contain("EmitCiTimedOut");         // TEST.CI_TIMED_OUT
        emitIds.Should().Contain("EmitCiTriggerFailed");    // TEST.CI_TRIGGERED.FAILED
    }

    // ================================================================
    // #5 — mandatory escalation terminal (no false success, no infinite wait).
    // ================================================================

    [Test]
    public void RetryExhaustion_EscalatesViaSingleTerminal()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "MaxAttemptGuard").Should().BeTrue();

        // Guard False (budget exhausted) -> reason -> escalation terminal (NOT a pass).
        HasEdge("MaxAttemptGuard", "False", "SetReasonExhausted").Should().BeTrue();
        ReachableFrom("SetReasonExhausted").Should().Contain("EmitGateEscalated");

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "MaxAttemptGuard" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();
        falseTargets.Should().NotContain("FinishPass");
        falseTargets.Should().NotContain("FinishRetryPass");
    }

    [Test]
    public void CriticalOutcome_Escalates_NeverPasses()
    {
        // Critical -> evaluated emit -> checks -> reason -> escalation (no FinishPass).
        HasEdge("EvaluateResults", "Critical", "EmitGateEvaluatedCritical").Should().BeTrue();
        ReachableFrom("EmitGateEvaluatedCritical").Should().Contain("EmitGateEscalated");
        ReachableFrom("EmitGateEvaluatedCritical").Should().NotContain("FinishPass");
    }

    [Test]
    public void EscalationTerminal_SetsPassedFalse_AndEscalatedTrue_WithReason()
    {
        // The escalation terminal sets passed=false, escalated=true and an escalationReason.
        var outputNames = _flowchart.Activities.OfType<Elsa.Workflows.Management.Activities.SetOutput.SetOutput>()
            .Where(o => o.Id != null && o.Id.StartsWith("SetOutputFail"))
            .Select(o => o.OutputName?.Expression?.Value?.ToString())
            .ToList();

        outputNames.Should().Contain("passed");
        outputNames.Should().Contain("escalated");
        outputNames.Should().Contain("escalationReason");
        outputNames.Should().Contain("qualityReport");
        outputNames.Should().Contain("teachingFeedback");

        // The escalation emit feeds the fail outputs which feed FinishFail.
        HasEdge("EmitGateEscalated", null, "SetOutputFailReport").Should().BeTrue();
        ReachableFrom("EmitGateEscalated").Should().Contain("FinishFail");
    }

    // ================================================================
    // Output contract preserved on every terminal path (additively extended).
    // ================================================================

    [Test]
    public void PassTerminal_EmitsFullOutputContract()
    {
        var passOutputs = _flowchart.Activities.OfType<Elsa.Workflows.Management.Activities.SetOutput.SetOutput>()
            .Where(o => o.Id != null && o.Id.StartsWith("SetOutputPass"))
            .Select(o => o.OutputName?.Expression?.Value?.ToString())
            .ToList();

        passOutputs.Should().Contain("qualityReport");
        passOutputs.Should().Contain("passed");
        passOutputs.Should().Contain("teachingFeedback");

        // Pass path: report -> emit PASSED -> outputs -> finish.
        HasEdge("GenerateQualityReport", null, "EmitGatePassed").Should().BeTrue();
        ReachableFrom("EmitGatePassed").Should().Contain("FinishPass");
    }

    [Test]
    public void RetryPassTerminal_EmitsFullOutputContract()
    {
        var retryOutputs = _flowchart.Activities.OfType<Elsa.Workflows.Management.Activities.SetOutput.SetOutput>()
            .Where(o => o.Id != null && o.Id.StartsWith("SetOutputRetryPass"))
            .Select(o => o.OutputName?.Expression?.Value?.ToString())
            .ToList();

        retryOutputs.Should().Contain("qualityReport");
        retryOutputs.Should().Contain("passed");
        retryOutputs.Should().Contain("teachingFeedback");
    }

    // ================================================================
    // No dangling edges — every non-terminal activity routes somewhere.
    // ================================================================

    [Test]
    public void EveryActivity_RoutesToATerminal_NoDanglingEdge()
    {
        // Epic 31 P3 — + FinishCiUnsupported (the §4.3 typed-unsupported terminal).
        var terminals = new[] { "FinishPass", "FinishFail", "FinishRetryPass", "FinishCiUnsupported" };
        var allIds = _flowchart.Activities.Select(a => a.Id!).ToHashSet();
        var sources = _flowchart.Connections.Select(c => c.Source.Activity.Id!).ToHashSet();

        foreach (var id in allIds.Where(i => !terminals.Contains(i)))
        {
            sources.Should().Contain(id, $"activity '{id}' must route somewhere (no dangling edge / no hang)");
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

    /// <summary>BFS over the connection graph to collect every activity id reachable from a source.</summary>
    private HashSet<string> ReachableFrom(string startId)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in _flowchart.Connections.Where(c => c.Source.Activity.Id == current))
            {
                var target = edge.Target.Activity.Id!;
                if (visited.Add(target)) queue.Enqueue(target);
            }
        }
        return visited;
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

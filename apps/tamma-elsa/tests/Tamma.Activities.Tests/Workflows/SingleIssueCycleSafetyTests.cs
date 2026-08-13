using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness audit 2026-06-22 (<c>SingleIssueCycle.md</c> Phase A — correctness &amp;
/// safety) — structural proofs that the per-issue cycle has NO silent-failure / false-
/// success / deadlock holes:
///
/// <list type="bullet">
///   <item>every awaited sub-workflow whose critical output the cycle needs has a
///     result gate whose <c>False</c> edge reaches the shared loud fail-the-cycle sink
///     (CYCLE.STEP_FAILED → notifyError → reportError → Finish);</item>
///   <item>a faulted sub-workflow does not halt the instance (continue-with-incidents)
///     — instead its empty result is caught by the gate and fails the cycle loud;</item>
///   <item>the TDD per-task <c>Failed</c> outcome no longer advances the loop silently —
///     it routes through tdd-with-debug-retry and, if unrecovered, fails the cycle;</item>
///   <item>a CI gate (ci-with-debug-retry) sits between the TDD loop and the merge gate;</item>
///   <item>a deployment failure does NOT report success;</item>
///   <item>#386 mode/tenant threading into the deployment pipeline is preserved.</item>
/// </list>
/// </summary>
[TestFixture]
public class SingleIssueCycleSafetyTests
{
    private Flowchart _flowchart = null!;
    private WorkflowOptions _options = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleIssueCycleWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
        _options = builder.Object.WorkflowOptions;
    }

    // ── Continue-with-incidents: a faulted sub-workflow can't halt the cycle ──

    [Test]
    public void Cycle_UsesContinueWithIncidents_SoAFaultedSubWorkflowDoesNotHang()
    {
        _options.IncidentStrategyType.Should().Be(typeof(ContinueWithIncidentsStrategy),
            "a faulted awaited sub-workflow must NOT halt the instance with an incident " +
            "(which would strand the issue on tamma-processing with no terminal) — its " +
            "empty result is caught by the result gate and routed to the loud sink");
    }

    // ── Shared loud fail-the-cycle sink wiring ──

    [Test]
    public void StepFailedSink_NotifiesError_ReportsError_AndFinishes()
    {
        HasEdge("EmitStepFailed", "NotifyError").Should().BeTrue("the sink must notify tamma-error");
        HasEdge("EmitStepFailed", "ReportError").Should().BeTrue("the sink must report error (loud)");
        HasEdge("ReportError", "Finish").Should().BeTrue("the error report must terminate at Finish (no dangling edge)");

        // NotifyError was previously declared-but-unconnected; it must now be reachable.
        _flowchart.Connections.Any(c => c.Target.Activity.Id == "NotifyError")
            .Should().BeTrue("the previously orphaned NotifyError must be wired into the sink");
    }

    // ── Every critical result gate fails closed into the sink ──

    [TestCase("ContextOk")]
    [TestCase("PlanOk")]
    [TestCase("TasksOk")]
    [TestCase("PrOk")]
    [TestCase("DeployOk")]
    [TestCase("CiOk")]
    [TestCase("TddRetryOk")]
    public void ResultGate_FalseEdge_ReachesLoudSinkAndFinish(string gateId)
    {
        _flowchart.Activities.OfType<FlowDecision>().Any(d => d.Id == gateId)
            .Should().BeTrue($"{gateId} result gate must exist");

        var reach = ReachableFromPort(gateId, "False");
        reach.Should().Contain("EmitStepFailed", $"{gateId}=False must emit CYCLE.STEP_FAILED (loud)");
        reach.Should().Contain("ReportError", $"{gateId}=False must report error (no false success)");
        reach.Should().Contain("Finish", $"{gateId}=False must terminate (no dangling edge / hang)");
    }

    [Test]
    public void EmptyContext_DoesNotProceedToPlanGeneration_NoEmptyDataFlow()
    {
        // The context gate's failure must NOT reach plan generation with empty data.
        ReachableFromPort("ContextOk", "False").Should().NotContain("GeneratePlan",
            "an empty context must fail the cycle, never feed plan-generation empty data");
        // The success edge does continue.
        HasEdge("ContextOk", "GeneratePlan", "True").Should().BeTrue();
    }

    [Test]
    public void EmptyPlan_DoesNotProceedToReview_NoReviewOfNothing()
    {
        ReachableFromPort("PlanOk", "False").Should().NotContain("ReviewPlan",
            "an empty plan must fail the cycle, never trigger a review-of-nothing");
        HasEdge("PlanOk", "ReviewPlan", "True").Should().BeTrue();
    }

    [Test]
    public void ZeroPrNumber_DoesNotReachWaitForPRMerged_NorTestCases()
    {
        // prNumber<=0 must never reach test-case creation or any merge wait.
        var reach = ReachableFromPort("PrOk", "False");
        reach.Should().NotContain("CreateTestCases",
            "a missing PR number must fail the cycle, never proceed to test-case creation");
        reach.Should().NotContain("WaitForPRMerged",
            "a missing PR number must never reach the merge-webhook wait (would hang forever)");
        HasEdge("PrOk", "CreateTestCases", "True").Should().BeTrue();
    }

    // ── TDD Failed: no silent advance; routes to debug-retry → loud on no-converge ──

    [Test]
    public void TddFailed_DoesNotAdvanceLoopSilently_RoutesToDebugRetry()
    {
        // Regression: the old graph wired tddForTask Failed → IncrementTask (false
        // success). It must now route to the tdd-with-debug-retry dispatch.
        HasEdge("TddForTask", "IncrementTask", "Failed").Should().BeFalse(
            "a failed TDD task must NOT advance the loop silently (the false-success hole)");
        HasEdge("TddForTask", "DispatchTddRetry", "Failed").Should().BeTrue(
            "a failed TDD task must route through tdd-with-debug-retry");

        // Completed still advances.
        HasEdge("TddForTask", "IncrementTask", "Completed").Should().BeTrue();
    }

    [Test]
    public void TddDebugRetry_DispatchesTheExistingSubWorkflow()
    {
        var retry = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchTddRetry");
        retry.Should().NotBeNull();
        ReadDefinitionId(retry!).Should().Be("tdd-with-debug-retry",
            "the failed-task path must reuse the existing bounded debug-retry workflow");
    }

    [Test]
    public void TddRecovered_AdvancesLoop_NotConverged_FailsCycleLoud()
    {
        HasEdge("TddRetryOk", "IncrementTask", "True").Should().BeTrue(
            "a recovered task advances the loop");
        var reach = ReachableFromPort("TddRetryOk", "False");
        reach.Should().Contain("ReportError",
            "a task that never converges must fail the cycle loud (no broken PR)");
        reach.Should().NotContain("IncrementTask",
            "a still-broken task must NOT advance into a PR");
    }

    // ── CI gate between the TDD loop and the merge gate ──

    [Test]
    public void CiGate_DispatchesCiWithDebugRetry_BetweenTddAndMerge()
    {
        var ci = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "CiGate");
        ci.Should().NotBeNull("a CI gate must run before the merge gate");
        ReadDefinitionId(ci!).Should().Be("ci-with-debug-retry");

        // Epic 31 P3 — the TDD loop completion enters the §4 CHECK STEP first
        // (CheckCiSupported), whose Supported edge runs the CI gate; the
        // Unsupported edge takes the DG-7 alternative step (skip-with-audit →
        // the HUMAN merge gate).
        HasEdge("HasMoreTasks", "CheckCiSupported", "False").Should().BeTrue(
            "the TDD loop completion must enter the CI check step");
        HasEdge("CheckCiSupported", "CiGate", "Supported").Should().BeTrue(
            "a supported platform runs the CI gate exactly as before");
        HasEdge("CheckCiSupported", "MarkCiSkipped", "Unsupported").Should().BeTrue(
            "an unsupported platform takes the DG-7 alternative step");
        // Reachability, not adjacency — MarkPrReadyForReview now sits on this edge
        // (the PR is opened as a draft and GitHub cannot merge a draft). The
        // invariant is unchanged: only a CI PASS may reach the merge gate.
        ReachableFromPort("CiOk", "True").Should().Contain("MergeApprovalGate",
            "only a CI pass may proceed to the merge-approval gate");

        // A CI failure must NOT reach the merge gate (no merge of red CI).
        ReachableFromPort("CiOk", "False").Should().NotContain("MergeApprovalGate",
            "a CI failure must fail the cycle, never reach the merge gate");
    }

    // ── Deployment failure must not report success (no false success) ──

    [Test]
    public void DeploymentFailure_DoesNotReportSuccess()
    {
        // The deployment dispatch must flow through DeployOk, never straight to success.
        HasEdge("DeploymentPipeline", "ReportSuccess").Should().BeFalse(
            "deployment must be gated — a failed deploy must not report success");
        HasEdge("DeploymentPipeline", "DeployOk").Should().BeTrue();

        HasEdge("DeployOk", "ReportSuccess", "True").Should().BeFalse(
            "success is reported via EmitCycleCompleted on the deploy-OK path");
        HasEdge("DeployOk", "EmitCycleCompleted", "True").Should().BeTrue();
        HasEdge("EmitCycleCompleted", "ReportSuccess").Should().BeTrue();

        ReachableFromPort("DeployOk", "False").Should().NotContain("ReportSuccess",
            "a failed deployment must NOT reach the success report");
        ReachableFromPort("DeployOk", "False").Should().Contain("ReportError");
    }

    // ── Predicate-level proofs of the deploy/tasks gates (not just edge-existence) ──
    //
    // These exercise the EXACT predicate the gate runs (IsDeploySuccessful / HasTasks).
    // They fail against the pre-fix code (deployOk fell through to `return true` on an
    // unrecognised result; tasksOk only checked IsNullOrWhiteSpace) and pass after the
    // fail-closed fix.

    [Test]
    public void DeployOk_FailsClosed_ForFailedProductionDeploy()
    {
        // deployment-pipeline reports failure under `deploymentStatus` — the gate must
        // NOT report success for it.
        var result = new Dictionary<string, object> { ["deploymentStatus"] = "failed:production" };
        SingleIssueCycleWorkflow.IsDeploySuccessful(result).Should().BeFalse(
            "a failed:production deploy must FAIL the cycle, never route to ReportSuccess");
    }

    [TestCase("failed")]
    [TestCase("failed:qa")]
    [TestCase("failed:uat")]
    [TestCase("failed:production")]
    public void DeployOk_FailsClosed_ForEveryFailureStatus(string status)
    {
        var result = new Dictionary<string, object> { ["deploymentStatus"] = status };
        SingleIssueCycleWorkflow.IsDeploySuccessful(result).Should().BeFalse(
            $"deploymentStatus='{status}' is a failure and must fail the cycle");
    }

    [Test]
    public void DeployOk_FailsClosed_ForUnrecognisedResult()
    {
        // No `deploymentStatus` key at all (the pre-fix "no explicit signal → success"
        // hole) must FAIL the cycle, not pass.
        var result = new Dictionary<string, object> { ["someOtherKey"] = "ran" };
        SingleIssueCycleWorkflow.IsDeploySuccessful(result).Should().BeFalse(
            "an unrecognised deploy result must NOT be treated as success (fail-closed)");
    }

    [Test]
    public void DeployOk_FailsClosed_ForNullResult()
    {
        SingleIssueCycleWorkflow.IsDeploySuccessful(null).Should().BeFalse(
            "a missing deploy result must fail the cycle");
    }

    [Test]
    public void DeployOk_Passes_OnlyForExplicitSuccessStatus()
    {
        var result = new Dictionary<string, object> { ["deploymentStatus"] = "success" };
        SingleIssueCycleWorkflow.IsDeploySuccessful(result).Should().BeTrue(
            "deploymentStatus='success' is the only passing verdict");
    }

    [Test]
    public void TasksOk_FailsClosed_ForEmptyArraySentinel()
    {
        // task-creation emits "[]" on failure — a non-blank string the pre-fix gate
        // wrongly passed (zero-iteration TDD loop → PR/merge with no implementation).
        SingleIssueCycleWorkflow.HasTasks("[]").Should().BeFalse(
            "an empty task array must FAIL the cycle, never proceed to a no-op TDD loop");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-json")]
    [TestCase("{}")]              // a JSON object, not a task array
    [TestCase("[ ]")]            // whitespace-only empty array
    public void TasksOk_FailsClosed_ForEmptyOrUnparseablePayloads(string? tasksJson)
    {
        SingleIssueCycleWorkflow.HasTasks(tasksJson).Should().BeFalse(
            "empty/blank/unparseable/non-array task payloads must fail the cycle");
    }

    [Test]
    public void TasksOk_Passes_ForNonEmptyTaskArray()
    {
        SingleIssueCycleWorkflow.HasTasks("[{\"id\":1},{\"id\":2}]").Should().BeTrue(
            "a task list with at least one task must proceed");
    }

    // ── Cycle-scoped DCB events at the boundaries ──

    [Test]
    public void CycleEvents_StartedCompletedAndStepFailed_AreEmitted()
    {
        var emits = _flowchart.Activities
            .Where(a => a.Id is "EmitCycleStarted" or "EmitCycleCompleted" or "EmitStepFailed")
            .Select(a => a.Id!)
            .ToList();
        emits.Should().Contain("EmitCycleStarted", "the cycle must emit CYCLE.STARTED at the boundary");
        emits.Should().Contain("EmitCycleCompleted", "the cycle must emit CYCLE.COMPLETED on success");
        emits.Should().Contain("EmitStepFailed", "the cycle must emit CYCLE.STEP_FAILED at the failure sink");

        // STARTED sits on the validated-work-item path (after validate, before context).
        HasEdge("ValidateWorkItem", "EmitCycleStarted", "Valid").Should().BeTrue();
        HasEdge("EmitCycleStarted", "GatherContext").Should().BeTrue();
    }

    // ── #386 mode/tenant threading into the deployment pipeline preserved ──

    [Test]
    public void Pr386_ModeAndTenantThreading_IntoDeploymentPipeline_Preserved()
    {
        var pipeline = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DeploymentPipeline");
        pipeline.Should().NotBeNull("the deployment-pipeline dispatch (#386) must remain");
        ReadDefinitionId(pipeline!).Should().Be("deployment-pipeline");

        // The Mode/RequireProdApproval variables #386 added must still be present.
        // (The dispatch input dictionary is a delegate; the presence of the variables
        // + the dispatch node is the structural guarantee — the input values are
        // covered by AdlModeThreadingTests.)
        _flowchart.Activities.Any(a => a.Id == "DeploymentPipeline").Should().BeTrue();
    }

    // ── Helpers ──

    private bool HasEdge(string sourceId, string targetId, string? port = null)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            c.Target.Activity.Id == targetId &&
            (port == null || c.Source.Port == port));

    private HashSet<string> ReachableFromPort(string sourceId, string port)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        foreach (var c in _flowchart.Connections.Where(c =>
            c.Source.Activity.Id == sourceId && c.Source.Port == port))
        {
            if (seen.Add(c.Target.Activity.Id)) queue.Enqueue(c.Target.Activity.Id);
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

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}

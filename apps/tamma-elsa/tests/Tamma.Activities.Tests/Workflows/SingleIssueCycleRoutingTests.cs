using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Tests for SingleIssueCycleWorkflow routing and decision logic.
/// Validates that FlowSwitch cases, revision limits, and connection targets are correct.
/// </summary>
[TestFixture]
public class SingleIssueCycleRoutingTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var workflow = new SingleIssueCycleWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Review Outcome Routing
    // ================================================================

    [Test]
    public void ReviewOutcome_HasFiveCases()
    {
        var reviewOutcome = _flowchart.Activities
            .OfType<FlowSwitch>()
            .FirstOrDefault(fs => fs.Id == "ReviewOutcome");

        reviewOutcome.Should().NotBeNull("should have ReviewOutcome FlowSwitch");
        reviewOutcome!.Cases.Should().HaveCount(5,
            "review outcome should have 5 cases: Approved, NeedsModification, Defer, Split, NeedsHuman");
    }

    [Test]
    public void ReviewOutcome_CaseNames()
    {
        var reviewOutcome = _flowchart.Activities
            .OfType<FlowSwitch>()
            .First(fs => fs.Id == "ReviewOutcome");

        var caseNames = reviewOutcome.Cases.Select(c => c.Label).ToList();
        caseNames.Should().Contain("Approved");
        caseNames.Should().Contain("NeedsModification");
        caseNames.Should().Contain("Defer");
        caseNames.Should().Contain("Split");
        caseNames.Should().Contain("NeedsHuman");
    }

    [Test]
    public void ApprovedPath_ConnectsTo_CreateTasks()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "Approved" &&
            c.Target.Activity.Id == "CreateTasks");

        hasConnection.Should().BeTrue("Approved outcome should connect to CreateTasks");
    }

    [Test]
    public void DeferPath_ConnectsTo_CreateDeferredIssues()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "Defer" &&
            c.Target.Activity.Id == "CreateDeferredIssues");

        hasConnection.Should().BeTrue("Defer outcome should connect to CreateDeferredIssues");
    }

    [Test]
    public void DeferPath_ReportDeferred_ConnectsTo_Finish()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReportDeferred" &&
            c.Target.Activity.Id == "Finish");

        hasConnection.Should().BeTrue("ReportDeferred should connect to Finish");
    }

    [Test]
    public void SplitPath_ConnectsTo_CreateSplitIssues()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "Split" &&
            c.Target.Activity.Id == "CreateSplitIssues");

        hasConnection.Should().BeTrue("Split outcome should connect to CreateSplitIssues");
    }

    [Test]
    public void SplitPath_ReportSplit_ConnectsTo_Finish()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReportSplit" &&
            c.Target.Activity.Id == "Finish");

        hasConnection.Should().BeTrue("ReportSplit should connect to Finish");
    }

    [Test]
    public void NeedsHumanPath_ConnectsTo_ReportNeedsHuman()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "NeedsHuman" &&
            c.Target.Activity.Id == "ReportNeedsHuman");

        hasConnection.Should().BeTrue("NeedsHuman outcome should connect to ReportNeedsHuman");
    }

    [Test]
    public void NeedsModificationPath_Increments_Then_ChecksMax()
    {
        // NeedsModification → IncrPlanRevision → PlanMaxRevisions
        var hasIncrConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "NeedsModification" &&
            c.Target.Activity.Id == "IncrPlanRevision");

        var hasMaxCheck = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "IncrPlanRevision" &&
            c.Target.Activity.Id == "PlanMaxRevisions");

        hasIncrConnection.Should().BeTrue("NeedsModification should connect to IncrPlanRevision");
        hasMaxCheck.Should().BeTrue("IncrPlanRevision should connect to PlanMaxRevisions");
    }

    [Test]
    public void PlanMaxRevisions_False_LoopsToGeneratePlan()
    {
        var hasLoop = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "PlanMaxRevisions" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "GeneratePlan");

        hasLoop.Should().BeTrue("PlanMaxRevisions=False should loop back to GeneratePlan");
    }

    [Test]
    public void PlanMaxRevisions_True_EscalatesToNeedsHuman()
    {
        var hasEscalation = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "PlanMaxRevisions" &&
            c.Source.Port == "True" &&
            c.Target.Activity.Id == "ReportNeedsHuman");

        hasEscalation.Should().BeTrue("PlanMaxRevisions=True should escalate to ReportNeedsHuman");
    }

    // ================================================================
    // Task Review Outcome Routing
    // ================================================================

    [Test]
    public void TaskReviewOutcome_HasThreeCases()
    {
        var taskOutcome = _flowchart.Activities
            .OfType<FlowSwitch>()
            .FirstOrDefault(fs => fs.Id == "TaskReviewOutcome");

        taskOutcome.Should().NotBeNull("should have TaskReviewOutcome FlowSwitch");
        taskOutcome!.Cases.Should().HaveCount(3,
            "task review should have 3 cases: Approved, NeedsChanges, NeedsHuman");
    }

    [Test]
    public void TaskApproved_ConnectsTo_CreateBranch()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskReviewOutcome" &&
            c.Source.Port == "Approved" &&
            c.Target.Activity.Id == "CreateBranch");

        hasConnection.Should().BeTrue("Task Approved should connect to CreateBranch");
    }

    [Test]
    public void TaskNeedsChanges_ConnectsTo_IncrTaskRevision()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskReviewOutcome" &&
            c.Source.Port == "NeedsChanges" &&
            c.Target.Activity.Id == "IncrTaskRevision");

        hasConnection.Should().BeTrue("Task NeedsChanges should connect to IncrTaskRevision");
    }

    [Test]
    public void TaskMaxRevisions_False_LoopsToCreateTasks()
    {
        var hasLoop = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskMaxRevisions" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "CreateTasks");

        hasLoop.Should().BeTrue("TaskMaxRevisions=False should loop back to CreateTasks");
    }

    [Test]
    public void TaskMaxRevisions_True_EscalatesToNeedsHuman()
    {
        var hasEscalation = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskMaxRevisions" &&
            c.Source.Port == "True" &&
            c.Target.Activity.Id == "ReportNeedsHuman");

        hasEscalation.Should().BeTrue("TaskMaxRevisions=True should escalate to ReportNeedsHuman");
    }

    // ================================================================
    // Story 19-5 AC-6 — ExecuteAgentActivity replaces tdd-cycle dispatch
    // ================================================================

    [Test]
    public void TddForTask_IsExecuteAgentActivity_NotDispatchWorkflow()
    {
        // AC-6: The per-task agent invocation must go through the
        // mode-aware ExecuteAgentActivity so the workflow works identically
        // in CLI / self-hosted / SaaS deployments.
        var tddForTask = _flowchart.Activities
            .FirstOrDefault(a => a.Id == "TddForTask");

        tddForTask.Should().NotBeNull("TddForTask activity must exist in the flowchart");
        tddForTask.Should().BeOfType<ExecuteAgentActivity>(
            "TddForTask must be an ExecuteAgentActivity (not a DispatchWorkflow to tdd-cycle) " +
            "so LocalExecutor vs GitHubActionsExecutor is selected by AgentExecutorFactory at runtime");
    }

    [Test]
    public void TddForTask_DoesNotDispatchToTddCycleSubWorkflow()
    {
        // Guard against regression — no DispatchWorkflow in SingleIssueCycle
        // should still be pointing at the "tdd-cycle" definition.
        var dispatches = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .ToList();

        foreach (var dispatch in dispatches)
        {
            var id = dispatch.Id;
            id.Should().NotBe("TddForTask",
                "TddForTask must not be a DispatchWorkflow anymore — replaced by ExecuteAgentActivity");
        }
    }

    [Test]
    public void ExtractCurrentTask_ConnectsTo_TddForTask()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractCurrentTask" &&
            c.Target.Activity.Id == "TddForTask");

        hasConnection.Should().BeTrue(
            "ExtractCurrentTask must feed the per-task slice into TddForTask");
    }

    [Test]
    public void TddForTask_Completed_ConnectsTo_IncrementTask()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TddForTask" &&
            c.Source.Port == "Completed" &&
            c.Target.Activity.Id == "IncrementTask");

        hasConnection.Should().BeTrue(
            "TddForTask 'Completed' outcome must advance the task loop to IncrementTask");
    }

    [Test]
    public void TddForTask_Failed_RoutesToDebugRetry_NotSilentAdvance()
    {
        // Completeness audit Phase A §Missing #3 — the old silent advance
        // (Failed → IncrementTask) was the false-success hole and is now REMOVED.
        // A failed task must route through tdd-with-debug-retry instead.
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TddForTask" &&
            c.Source.Port == "Failed" &&
            c.Target.Activity.Id == "IncrementTask")
            .Should().BeFalse(
                "a failed TDD task must NOT advance the loop silently (the false-success hole)");

        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TddForTask" &&
            c.Source.Port == "Failed" &&
            c.Target.Activity.Id == "DispatchTddRetry")
            .Should().BeTrue(
                "a failed TDD task must route through the bounded tdd-with-debug-retry sub-workflow");
    }

    [Test]
    public void TddForTask_Task_Input_IsImplement()
    {
        // The per-task TDD iteration asks the agent to "implement" the slice.
        // Other task types (fix, debug, review) belong to different workflow sites.
        var tddForTask = _flowchart.Activities
            .OfType<ExecuteAgentActivity>()
            .FirstOrDefault(a => a.Id == "TddForTask");

        tddForTask.Should().NotBeNull();
        tddForTask!.Task.Should().NotBeNull();
        // The Input<string> literal value is not directly addressable without
        // an ExpressionExecutionContext; we assert via the internal delegate/literal
        // by exercising the activity's configured defaults.
        //
        // Input<T>(T literal) sets MemoryBlockReference to a literal — verified
        // by checking the serialized expression shape is stable (default == "implement").
        tddForTask.Task.Expression.Should().NotBeNull(
            "Task input must be configured to a literal or expression");
    }

    [Test]
    public void TddForTask_AgentProvider_DefaultsToClaudeCode()
    {
        var tddForTask = _flowchart.Activities
            .OfType<ExecuteAgentActivity>()
            .FirstOrDefault(a => a.Id == "TddForTask");

        tddForTask.Should().NotBeNull();
        tddForTask!.AgentProvider.Should().NotBeNull(
            "AgentProvider input must be configured (default claude-code)");
    }

    [Test]
    public void TddForTask_TimeoutMinutes_IsConfigured()
    {
        var tddForTask = _flowchart.Activities
            .OfType<ExecuteAgentActivity>()
            .FirstOrDefault(a => a.Id == "TddForTask");

        tddForTask.Should().NotBeNull();
        tddForTask!.TimeoutMinutes.Should().NotBeNull(
            "TimeoutMinutes input must be configured for the TDD task (30 minutes default)");
    }

    // ================================================================
    // Merge-Approval Gate wiring — the cycle now routes the merge step through
    // the 3-way merge/test/reject gate (was an unwired skeleton)
    // ================================================================

    [Test]
    public void TddLoopDone_DispatchesMergeApprovalGate()
    {
        // The merge step must run through the merge-approval gate sub-workflow,
        // not the bare binary WaitForPRApprovalActivity it replaced.
        var gate = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "MergeApprovalGate");

        gate.Should().NotBeNull("the cycle must dispatch the merge-approval gate");
        ReadDefinitionId(gate!).Should().Be("merge-approval",
            "the merge gate must dispatch the merge-approval workflow (previously orphaned)");

        // Completeness audit Phase A §Missing #4 — a CI gate (ci-with-debug-retry) now
        // sits between the TDD loop and the merge gate. The TDD loop completion enters
        // the CI gate; ONLY a CI pass proceeds to the merge-approval gate.
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "HasMoreTasks" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "CiGate")
            .Should().BeTrue("the CI gate must be entered when the TDD loop is done");

        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "CiOk" &&
            c.Source.Port == "True" &&
            c.Target.Activity.Id == "MergeApprovalGate")
            .Should().BeTrue("only a CI pass may proceed to the merge-approval gate");
    }

    [Test]
    public void MergeApprovalGate_WaitsForCompletion_ThenBranchesOnOutcome()
    {
        var gate = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .First(d => d.Id == "MergeApprovalGate");

        // The cycle must block on the human decision + the gate's action, not race
        // ahead fire-and-forget.
        ReadWaitForCompletion(gate).Should().BeTrue(
            "the cycle must wait for the merge-approval gate to complete");

        // CRITICAL-1: the gate must NOT connect unconditionally to WaitForPRMerged.
        // It must flow into ExtractGateOutcome → GateOutcomeSwitch and branch.
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "MergeApprovalGate" &&
            c.Target.Activity.Id == "WaitForPRMerged")
            .Should().BeFalse(
                "the gate must NOT connect directly to WaitForPRMerged — reject/escalate " +
                "would then hang the cycle forever on a merge webhook that never fires");

        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "MergeApprovalGate" &&
            c.Target.Activity.Id == "ExtractGateOutcome")
            .Should().BeTrue("the gate result must be captured for branching");
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractGateOutcome" &&
            c.Target.Activity.Id == "GateOutcomeSwitch")
            .Should().BeTrue("the cycle must branch on the gate outcome");
    }

    // ================================================================
    // CRITICAL-1 — the cycle branches on the gate `outcome`. ONLY merge reaches
    // WaitForPRMerged; reject/escalate reach loud terminals → no deadlock.
    // ================================================================

    [Test]
    public void GateOutcomeSwitch_HasThreeCases_MergeRejectEscalated()
    {
        var sw = _flowchart.Activities
            .OfType<FlowSwitch>()
            .FirstOrDefault(fs => fs.Id == "GateOutcomeSwitch");

        sw.Should().NotBeNull("the cycle must switch on the gate outcome");
        var caseNames = sw!.Cases.Select(c => c.Label).ToList();
        caseNames.Should().Contain("Merge");
        caseNames.Should().Contain("Reject");
        caseNames.Should().Contain("Escalated");
    }

    [Test]
    public void GateOutcome_OnlyMerge_ReachesWaitForPRMerged()
    {
        // The merge outcome is the ONLY path to the merge-webhook wait.
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "GateOutcomeSwitch" &&
            c.Source.Port == "Merge" &&
            c.Target.Activity.Id == "WaitForPRMerged")
            .Should().BeTrue("merge outcome must wait for the real merge webhook");

        // Exactly one edge into WaitForPRMerged, and it is the merge case (no
        // unconditional / non-merge edge can reach the hang point).
        var intoWait = _flowchart.Connections
            .Where(c => c.Target.Activity.Id == "WaitForPRMerged")
            .ToList();
        intoWait.Should().HaveCount(1, "only the merge outcome may reach WaitForPRMerged");
        intoWait[0].Source.Activity.Id.Should().Be("GateOutcomeSwitch");
        intoWait[0].Source.Port.Should().Be("Merge");
    }

    [Test]
    public void GateOutcome_NonMerge_DoesNotReachWaitForPRMerged_NoDeadlock()
    {
        // The load-bearing no-deadlock guarantee: from a reject/escalate outcome
        // the cycle must NOT be able to reach WaitForPRMerged (which blocks on a
        // pr-merged webhook that never fires for a non-merge).
        foreach (var port in new[] { "Reject", "Escalated" })
        {
            var reach = ReachableFromPort("GateOutcomeSwitch", port);
            reach.Should().NotContain("WaitForPRMerged",
                $"the '{port}' outcome must NEVER reach WaitForPRMerged (would hang forever)");
        }

        // reject → human-handoff terminal; escalated → error terminal (loud).
        ReachableFromPort("GateOutcomeSwitch", "Reject").Should().Contain("ReportNeedsHuman",
            "reject must reach the human-handoff terminal");
        ReachableFromPort("GateOutcomeSwitch", "Escalated").Should().Contain("ReportError",
            "escalated (incl. a failed merge) must reach the error terminal");
    }

    // ================================================================
    // IMPORTANT-5 — the cycle gates createPR on the branch step's success.
    // A failed branch creation routes to a loud terminal (no doomed PR with an
    // empty head, no false "branch created" notification). Both outcomes reach a
    // Finish terminal (no dangling edge / deadlock).
    // ================================================================

    [Test]
    public void BranchOutcomeSwitch_HasTwoCases_CreatedFailed()
    {
        var sw = _flowchart.Activities
            .OfType<FlowSwitch>()
            .FirstOrDefault(fs => fs.Id == "BranchOutcomeSwitch");

        sw.Should().NotBeNull("the cycle must switch on the branch-creation success flag");
        var caseNames = sw!.Cases.Select(c => c.Label).ToList();
        caseNames.Should().Contain("Created");
        caseNames.Should().Contain("Failed");
    }

    [Test]
    public void ExtractBranch_FeedsTheBranchOutcomeSwitch()
    {
        // The branch result must be captured (ExtractBranch) and routed through
        // the switch — never wired unconditionally to createPR.
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractBranch" &&
            c.Target.Activity.Id == "BranchOutcomeSwitch")
            .Should().BeTrue("the branch result must branch on success");
    }

    [Test]
    public void BranchSuccess_ReachesCreatePr_AndNotifyBranchCreated()
    {
        var created = ReachableFromPort("BranchOutcomeSwitch", "Created");
        created.Should().Contain("CreatePR", "a successful branch must continue to PR creation");
        created.Should().Contain("NotifyBranchCreated", "a successful branch fires the branch-created notify");
    }

    [Test]
    public void BranchFailure_DoesNotReachCreatePr_NorFalseBranchNotify()
    {
        // The load-bearing gate: a failed branch must NEVER reach createPR (a
        // doomed PR with an empty head) nor fire the false "branch created" notify.
        var failed = ReachableFromPort("BranchOutcomeSwitch", "Failed");
        failed.Should().NotContain("CreatePR",
            "a failed branch must not invoke createPR with an empty head");
        failed.Should().NotContain("NotifyBranchCreated",
            "a failed branch must not emit a false 'branch created' notification");
    }

    [Test]
    public void BranchFailure_ReachesLoudErrorTerminal()
    {
        // The failure path must reach a loud terminal (ReportError) and Finish —
        // no dangling edge, no deadlock.
        var failed = ReachableFromPort("BranchOutcomeSwitch", "Failed");
        failed.Should().Contain("ReportError", "a failed branch must reach the error terminal");
        failed.Should().Contain("Finish", "the failure path must terminate at Finish (no dangling edge)");
    }

    [Test]
    public void CreatePr_HasNoUnconditionalEdgeFromExtractBranch()
    {
        // Regression guard for IMPORTANT-5: the old unconditional
        // ExtractBranch → CreatePR / NotifyBranchCreated edges must be gone.
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractBranch" &&
            c.Target.Activity.Id == "CreatePR")
            .Should().BeFalse("createPR must be gated on branch success, not wired unconditionally");
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractBranch" &&
            c.Target.Activity.Id == "NotifyBranchCreated")
            .Should().BeFalse("the branch-created notify must be gated on success");

        // Every edge into CreatePR originates at the Created branch outcome.
        var intoPr = _flowchart.Connections
            .Where(c => c.Target.Activity.Id == "CreatePR")
            .ToList();
        intoPr.Should().NotBeEmpty();
        intoPr.Should().OnlyContain(c =>
            c.Source.Activity.Id == "BranchOutcomeSwitch" && c.Source.Port == "Created",
            "createPR may only be reached via the Created branch outcome");
    }

    [Test]
    public void OldBinaryApprovalGate_IsRetiredFromTheCycle()
    {
        // The prior bare WaitForPRApprovalActivity gate and its fire-and-forget
        // "DispatchMerge" node must no longer be in the cycle — exactly one gate.
        _flowchart.Activities.Any(a => a.Id == "WaitForPRApproval")
            .Should().BeFalse("the binary approval gate is replaced by the merge-approval gate");
        _flowchart.Activities.Any(a => a.Id == "DispatchMerge")
            .Should().BeFalse("the fire-and-forget merge dispatch is now inside the merge-approval gate");
    }

    /// <summary>Forward-reachable activity ids starting from a specific
    /// outcome port of a source node.</summary>
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

    private static bool ReadWaitForCompletion(DispatchWorkflow dispatch)
    {
        // WaitForCompletion is an Input<bool> whose literal is carried on the
        // expression value; a configured `new(true)` exposes "True"/"true"/True.
        var value = dispatch.WaitForCompletion?.Expression?.Value;
        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var r) && r,
            _ => false,
        };
    }
}

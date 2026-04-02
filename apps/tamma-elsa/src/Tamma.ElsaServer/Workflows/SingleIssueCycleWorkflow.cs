using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Single Issue Cycle — processes one work item from validation to merge.
/// Receives a pre-selected work item from the ADL Orchestrator.
///
/// Flow:
///   Validate Work Item → Gather Context → Generate Plan → Review Plan
///     ├─ approved → Create Tasks → Review Tasks → Create Branch
///     │    → TDD Cycle (per task) → Create PR → CI Check
///     │    → Code Review → Merge → Report Complete
///     ├─ defer → Create Deferred Issues → Report (deferred)
///     ├─ split → Create Sub-Issues → Report (split)
///     └─ needsHuman → Report (needsHuman)
///
/// Every step inherits TammaActivity for automatic event emission.
/// On any failure → Report to orchestrator → Finish.
/// </summary>
public class SingleIssueCycleWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Single Issue Cycle";
        builder.DefinitionId = "single-issue-cycle";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Processes one work item from validation through merge";

        // ================================================================
        // Variables
        // ================================================================
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var botAssignee = builder.WithVariable<string>("BotAssignee", "tamma-bot");
        var baseBranch = builder.WithVariable<string>("BaseBranch", "main");

        // Step outputs
        var contextIds = builder.WithVariable<string>("ContextIds", "");
        var poSummary = builder.WithVariable<string>("POSummary", "");
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var reviewDecision = builder.WithVariable<string>("ReviewDecision", "");
        var tasksJson = builder.WithVariable<string>("TasksJson", "");
        var taskReviewDecision = builder.WithVariable<string>("TaskReviewDecision", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var prNumber = builder.WithVariable<int>("PRNumber", 0);
        var prUrl = builder.WithVariable<string>("PRUrl", "");
        var exitReason = builder.WithVariable<string>("ExitReason", "");

        // Sub-workflow results
        var subResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Validate Work Item
        // ================================================================
        var validateItem = new ValidateWorkItemActivity
        {
            Id = "ValidateWorkItem",
            Name = "Validate Work Item",
            WorkItemJson = new Input<string>(ctx => ctx.GetInput<string>("workItemJson") ?? ""),
            Repository = new Input<string>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                repository.Set(ctx, repo);
                botAssignee.Set(ctx, ctx.GetInput<string>("botAssignee") ?? "tamma-bot");
                baseBranch.Set(ctx, ctx.GetInput<string>("baseBranch") ?? "main");
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                return repo;
            }),
        };
        validateItem.SetDisplayText("Validate Work Item");

        // ================================================================
        // 2. Gather Context (sub-workflow)
        // ================================================================
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherContext",
            Name = "Gather Context",
            WorkflowDefinitionId = new("context-gathering"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["workItemJson"] = workItemJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        gatherContext.SetDisplayText("Gather Context");

        var extractContext = Assign(poSummary, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null)
            {
                if (result.TryGetValue("summary", out var s)) poSummary.Set(ctx, s?.ToString() ?? "");
                if (result.TryGetValue("contextIds", out var ids)) contextIds.Set(ctx, ids?.ToString() ?? "");
            }
            return (object)(poSummary.Get(ctx));
        }, "ExtractContext", "Extract Context");

        // ================================================================
        // 3. Generate Plan (sub-workflow)
        // ================================================================
        var generatePlan = new DispatchWorkflow
        {
            Id = "GeneratePlan",
            Name = "Generate Plan",
            WorkflowDefinitionId = new("plan-generation"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["poSummary"] = poSummary.Get(ctx),
                ["contextIds"] = contextIds.Get(ctx),
                ["workItemJson"] = workItemJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        generatePlan.SetDisplayText("Generate Plan");

        var extractPlan = Assign(planJson, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null && result.TryGetValue("planJson", out var p))
                return (object)(p?.ToString() ?? "");
            return (object)"";
        }, "ExtractPlan", "Extract Plan");

        // ================================================================
        // 4. Review Plan (sub-workflow — 7-role panel)
        // ================================================================
        var reviewPlan = new DispatchWorkflow
        {
            Id = "ReviewPlan",
            Name = "Review Plan",
            WorkflowDefinitionId = new("plan-review"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["planJson"] = planJson.Get(ctx),
                ["contextIds"] = contextIds.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        reviewPlan.SetDisplayText("Review Plan");

        var extractReviewDecision = Assign(reviewDecision, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null)
            {
                if (result.TryGetValue("decision", out var d)) return (object)(d?.ToString() ?? "needsHuman");
                if (result.TryGetValue("planJson", out var p)) planJson.Set(ctx, p?.ToString() ?? "");
            }
            return (object)"needsHuman";
        }, "ExtractReviewDecision", "Extract Review Decision");

        // Review outcome routing
        var reviewOutcome = new FlowSwitch<string>
        {
            Id = "ReviewOutcome",
            Name = "Review Outcome",
            Expression = new(ctx => reviewDecision.Get(ctx)),
            Cases = { { "approved", "Approved" }, { "defer", "Defer" }, { "split", "Split" } },
            Default = "NeedsHuman",
        };
        reviewOutcome.SetDisplayText("Review Outcome");

        // ================================================================
        // 4a. Defer — create issues and finish
        // ================================================================
        var createDeferredIssues = new DispatchWorkflow
        {
            Id = "CreateDeferredIssues",
            Name = "Create Deferred Issues",
            WorkflowDefinitionId = new("create-issues"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issuesJson"] = subResult.Get(ctx)?.GetValueOrDefault("deferred")?.ToString() ?? "[]",
            }),
            WaitForCompletion = new(true),
        };
        createDeferredIssues.SetDisplayText("Create Deferred Issues");

        // ================================================================
        // 4b. Split — create sub-issues and finish
        // ================================================================
        var createSplitIssues = new DispatchWorkflow
        {
            Id = "CreateSplitIssues",
            Name = "Create Sub-Issues",
            WorkflowDefinitionId = new("create-issues"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issuesJson"] = subResult.Get(ctx)?.GetValueOrDefault("split")?.ToString() ?? "[]",
            }),
            WaitForCompletion = new(true),
        };
        createSplitIssues.SetDisplayText("Create Sub-Issues");

        // ================================================================
        // 5. Create Tasks (senior dev LLM — deep implementation plans)
        // ================================================================
        var createTasks = new DispatchWorkflow
        {
            Id = "CreateTasks",
            Name = "Create Tasks",
            WorkflowDefinitionId = new("task-creation"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["planJson"] = planJson.Get(ctx),
                ["contextIds"] = contextIds.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        createTasks.SetDisplayText("Create Tasks");

        var extractTasks = Assign(tasksJson, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null && result.TryGetValue("tasksJson", out var t))
                return (object)(t?.ToString() ?? "");
            return (object)"";
        }, "ExtractTasks", "Extract Tasks");

        // ================================================================
        // 6. Review Tasks (4-role: architect, senior dev, dev, QA)
        // ================================================================
        var reviewTasks = new DispatchWorkflow
        {
            Id = "ReviewTasks",
            Name = "Review Tasks",
            WorkflowDefinitionId = new("task-review"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["tasksJson"] = tasksJson.Get(ctx),
                ["planJson"] = planJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        reviewTasks.SetDisplayText("Review Tasks");

        var extractTaskReview = Assign(taskReviewDecision, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null)
            {
                if (result.TryGetValue("decision", out var d)) return (object)(d?.ToString() ?? "needsHuman");
                if (result.TryGetValue("tasksJson", out var t)) tasksJson.Set(ctx, t?.ToString() ?? "");
            }
            return (object)"needsHuman";
        }, "ExtractTaskReview", "Extract Task Review");

        var taskReviewOutcome = new FlowSwitch<string>
        {
            Id = "TaskReviewOutcome",
            Name = "Tasks Approved?",
            Expression = new(ctx => taskReviewDecision.Get(ctx)),
            Cases = { { "approved", "Approved" }, { "needsChanges", "NeedsChanges" } },
            Default = "NeedsHuman",
        };
        taskReviewOutcome.SetDisplayText("Tasks Approved?");

        // ================================================================
        // 7. Create Branch
        // ================================================================
        var createBranch = new DispatchWorkflow
        {
            Id = "CreateBranch",
            Name = "Create Branch",
            WorkflowDefinitionId = new("branch-creation"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["baseBranch"] = baseBranch.Get(ctx),
                ["workItemJson"] = workItemJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        createBranch.SetDisplayText("Create Branch");

        var extractBranch = Assign(branchName, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null && result.TryGetValue("branchName", out var b))
                return (object)(b?.ToString() ?? "");
            return (object)"";
        }, "ExtractBranch", "Extract Branch");

        // ================================================================
        // 8. TDD Cycle (sub-workflow — processes tasks in dependency order)
        // ================================================================
        var tddCycle = new DispatchWorkflow
        {
            Id = "TddCycle",
            Name = "TDD Cycle",
            WorkflowDefinitionId = new("tdd-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["tasksJson"] = tasksJson.Get(ctx),
                ["contextIds"] = contextIds.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        tddCycle.SetDisplayText("TDD Cycle");

        // ================================================================
        // 9. Create PR
        // ================================================================
        var createPR = new DispatchWorkflow
        {
            Id = "CreatePR",
            Name = "Create PR",
            WorkflowDefinitionId = new("pull-request"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["baseBranch"] = baseBranch.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["planJson"] = planJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        createPR.SetDisplayText("Create PR");

        var extractPR = Assign(prNumber, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null)
            {
                if (result.TryGetValue("prNumber", out var n) && n is int num) prNumber.Set(ctx, num);
                if (result.TryGetValue("prUrl", out var u)) prUrl.Set(ctx, u?.ToString() ?? "");
            }
            return (object)prNumber.Get(ctx);
        }, "ExtractPR", "Extract PR");

        // ================================================================
        // 10. CI Check (sub-workflow)
        // ================================================================
        var ciCheck = new DispatchWorkflow
        {
            Id = "CICheck",
            Name = "CI Check",
            WorkflowDefinitionId = new("ci-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        ciCheck.SetDisplayText("CI Check");

        // ================================================================
        // 11. Code Review (sub-workflow)
        // ================================================================
        var codeReview = new DispatchWorkflow
        {
            Id = "CodeReview",
            Name = "Code Review",
            WorkflowDefinitionId = new("code-review"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        codeReview.SetDisplayText("Code Review");

        // ================================================================
        // 12. Merge (sub-workflow)
        // ================================================================
        var merge = new DispatchWorkflow
        {
            Id = "Merge",
            Name = "Merge",
            WorkflowDefinitionId = new("merge"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        merge.SetDisplayText("Merge");

        // ================================================================
        // Exit paths — all report to orchestrator
        // ================================================================
        var reportSuccess = new ReportCycleResultActivity
        {
            Id = "ReportSuccess", Name = "Report Success",
            Reason = new("success"), IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
        };
        reportSuccess.SetDisplayText("Report Success");

        var reportDeferred = new ReportCycleResultActivity
        {
            Id = "ReportDeferred", Name = "Report Deferred",
            Reason = new("deferred"), IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
        };
        reportDeferred.SetDisplayText("Report Deferred");

        var reportSplit = new ReportCycleResultActivity
        {
            Id = "ReportSplit", Name = "Report Split",
            Reason = new("split"), IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
        };
        reportSplit.SetDisplayText("Report Split");

        var reportNeedsHuman = new ReportCycleResultActivity
        {
            Id = "ReportNeedsHuman", Name = "Report Needs Human",
            Reason = new("needsHuman"), IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
        };
        reportNeedsHuman.SetDisplayText("Report Needs Human");

        var reportError = new ReportCycleResultActivity
        {
            Id = "ReportError", Name = "Report Error",
            Reason = new("error"), IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
        };
        reportError.SetDisplayText("Report Error");

        // ================================================================
        // Issue notifications (fire-and-forget sub-workflow dispatches)
        // ================================================================
        var notifyProcessing = NotifyIssue("NotifyProcessing", repository, issueNumber,
            "🤖 Tamma is processing this issue.", new[] { "tamma-processing" });
        var notifyInvalid = NotifyIssue("NotifyInvalid", repository, issueNumber,
            "❌ Cannot process this issue.", new[] { "tamma-error" });
        var notifyContextDone = NotifyIssue("NotifyContextDone", repository, issueNumber,
            "📋 Context gathered. Generating implementation plan...");
        var notifyPlanDone = NotifyIssue("NotifyPlanDone", repository, issueNumber,
            "📝 Plan generated. Sending for panel review...");
        var notifyPlanApproved = NotifyIssue("NotifyPlanApproved", repository, issueNumber,
            "✅ Plan approved. Creating implementation tasks...");
        var notifyDeferred = NotifyIssue("NotifyDeferred", repository, issueNumber,
            "⏸️ Items deferred to new issues. Closing.", new[] { "deferred" }, new[] { "tamma-processing" });
        var notifySplit = NotifyIssue("NotifySplit", repository, issueNumber,
            "🔀 Issue decomposed into sub-issues. Closing.", new[] { "split" }, new[] { "tamma-processing" });
        var notifyNeedsHuman = NotifyIssue("NotifyNeedsHuman", repository, issueNumber,
            "🙋 Needs human decision. See discussion.", new[] { "needs-human" });
        var notifyTasksApproved = NotifyIssue("NotifyTasksApproved", repository, issueNumber,
            "✅ Tasks approved. Starting implementation...");
        var notifyBranchCreated = NotifyIssue("NotifyBranchCreated", repository, issueNumber,
            "🌿 Branch created. Running TDD cycle...");
        var notifyTddDone = NotifyIssue("NotifyTddDone", repository, issueNumber,
            "✅ TDD complete. Creating PR...");
        var notifyCiPassed = NotifyIssue("NotifyCiPassed", repository, issueNumber,
            "✅ CI passed. Starting code review...");
        var notifyMerged = NotifyIssue("NotifyMerged", repository, issueNumber,
            "🎉 PR merged! Issue resolved.", new[] { "tamma-completed" }, new[] { "tamma-processing" });
        var notifyError = NotifyIssue("NotifyError", repository, issueNumber,
            "❌ Error encountered.", new[] { "tamma-error" }, new[] { "tamma-processing" });

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "SingleIssueCycleFlowchart",
            Start = validateItem,
            Activities =
            {
                // Main flow
                validateItem, gatherContext, extractContext,
                generatePlan, extractPlan,
                reviewPlan, extractReviewDecision, reviewOutcome,
                createDeferredIssues, createSplitIssues,
                createTasks, extractTasks,
                reviewTasks, extractTaskReview, taskReviewOutcome,
                createBranch, extractBranch,
                tddCycle, createPR, extractPR,
                ciCheck, codeReview, merge,
                // Notifications (fire-and-forget)
                notifyProcessing, notifyInvalid, notifyContextDone,
                notifyPlanDone, notifyPlanApproved,
                notifyDeferred, notifySplit, notifyNeedsHuman,
                notifyTasksApproved, notifyBranchCreated,
                notifyTddDone, notifyCiPassed, notifyMerged, notifyError,
                // Exit paths
                reportSuccess, reportDeferred, reportSplit,
                reportNeedsHuman, reportError, finish,
            },
            Connections =
            {
                // 1. Validate → notify + continue (parallel)
                ConnectOutcome(validateItem, "Valid", notifyProcessing),
                ConnectOutcome(validateItem, "Valid", gatherContext),
                ConnectOutcome(validateItem, "Invalid", notifyInvalid),
                ConnectOutcome(validateItem, "Invalid", reportError),

                // 2. Gather Context → notify + Generate Plan (parallel)
                Connect(gatherContext, extractContext),
                Connect(extractContext, notifyContextDone),
                Connect(extractContext, generatePlan),

                // 3. Generate Plan → notify + Review Plan (parallel)
                Connect(generatePlan, extractPlan),
                Connect(extractPlan, notifyPlanDone),
                Connect(extractPlan, reviewPlan),

                // 4. Review Plan → Route
                Connect(reviewPlan, extractReviewDecision),
                Connect(extractReviewDecision, reviewOutcome),

                // Approved → notify + Create Tasks (parallel)
                ConnectOutcome(reviewOutcome, "Approved", notifyPlanApproved),
                ConnectOutcome(reviewOutcome, "Approved", createTasks),

                // Defer → notify + create issues + report (parallel)
                ConnectOutcome(reviewOutcome, "Defer", notifyDeferred),
                ConnectOutcome(reviewOutcome, "Defer", createDeferredIssues),
                Connect(createDeferredIssues, reportDeferred),
                Connect(reportDeferred, finish),

                // Split → notify + create issues + report (parallel)
                ConnectOutcome(reviewOutcome, "Split", notifySplit),
                ConnectOutcome(reviewOutcome, "Split", createSplitIssues),
                Connect(createSplitIssues, reportSplit),
                Connect(reportSplit, finish),

                // NeedsHuman → notify + report (parallel)
                ConnectOutcome(reviewOutcome, "NeedsHuman", notifyNeedsHuman),
                ConnectOutcome(reviewOutcome, "NeedsHuman", reportNeedsHuman),
                Connect(reportNeedsHuman, finish),

                // 5. Create Tasks → 6. Review Tasks
                Connect(createTasks, extractTasks),
                Connect(extractTasks, reviewTasks),

                // 6. Review Tasks → Route
                Connect(reviewTasks, extractTaskReview),
                Connect(extractTaskReview, taskReviewOutcome),

                // Tasks approved → notify + Create Branch (parallel)
                ConnectOutcome(taskReviewOutcome, "Approved", notifyTasksApproved),
                ConnectOutcome(taskReviewOutcome, "Approved", createBranch),
                ConnectOutcome(taskReviewOutcome, "NeedsChanges", createTasks),
                ConnectOutcome(taskReviewOutcome, "NeedsHuman", notifyNeedsHuman),
                ConnectOutcome(taskReviewOutcome, "NeedsHuman", reportNeedsHuman),

                // 7. Create Branch → notify + TDD (parallel)
                Connect(createBranch, extractBranch),
                Connect(extractBranch, notifyBranchCreated),
                Connect(extractBranch, tddCycle),

                // 8. TDD → notify + Create PR (parallel)
                Connect(tddCycle, notifyTddDone),
                Connect(tddCycle, createPR),

                // 9. Create PR → CI
                Connect(createPR, extractPR),
                Connect(extractPR, ciCheck),

                // 10. CI → notify + Code Review (parallel)
                Connect(ciCheck, notifyCiPassed),
                Connect(ciCheck, codeReview),

                // 11. Code Review → 12. Merge
                Connect(codeReview, merge),

                // 12. Merge → notify + Report (parallel)
                Connect(merge, notifyMerged),
                Connect(merge, reportSuccess),
                Connect(reportSuccess, finish),

                // Error → notify + report (parallel)
                Connect(reportError, finish),
            }
        };
    }

    /// <summary>
    /// Fire-and-forget dispatch to update-issue-status sub-workflow.
    /// </summary>
    private static DispatchWorkflow NotifyIssue(
        string id,
        Variable<string> repository,
        Variable<int> issueNumber,
        string message,
        string[]? addLabels = null,
        string[]? removeLabels = null)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id,
            Name = $"Notify: {message[..Math.Min(message.Length, 30)]}",
            WorkflowDefinitionId = new("update-issue-status"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["repository"] = repository.Get(ctx),
                    ["issueNumber"] = issueNumber.Get(ctx),
                    ["message"] = message,
                };
                if (addLabels != null) input["addLabels"] = addLabels;
                if (removeLabels != null) input["removeLabels"] = removeLabels;
                return input;
            }),
            WaitForCompletion = new(false), // fire and forget
        };
        dispatch.SetDisplayText($"Notify: {message[..Math.Min(message.Length, 30)]}");
        return dispatch;
    }

    private static SetVariable Assign(Variable variable, Func<Elsa.Expressions.Models.ExpressionExecutionContext, object?> valueFunc, string id, string name)
    {
        var sv = new SetVariable { Id = id, Name = name, Variable = variable, Value = new Input<object?>(valueFunc) };
        sv.SetDisplayText(name);
        return sv;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}

using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.Context;
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

        // Conventions (loaded from repo config)
        var conventions = builder.WithVariable<string>("Conventions", "");

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

        // Review / revision tracking (must be declared before activities that reference them)
        var reviewNotes = builder.WithVariable<string>("ReviewNotes", "");
        var planRevisionCount = builder.WithVariable<int>("PlanRevisionCount", 0);

        // Sub-workflow results
        var subResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 0. Read workflow inputs into variables (no side effects in activity lambdas)
        // ================================================================
        var initInputs = new SetVariable
        {
            Id = "InitInputs",
            Name = "Initialize Inputs",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                botAssignee.Set(ctx, ctx.GetInput<string>("botAssignee") ?? "tamma-bot");
                baseBranch.Set(ctx, ctx.GetInput<string>("baseBranch") ?? "main");
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                return (object)repo;
            }),
        };
        initInputs.SetDisplayText("Initialize Inputs");

        // ================================================================
        // 0b. Read repo conventions
        // ================================================================
        var readConventions = new ReadRepoConventionsActivity
        {
            Id = "ReadRepoConventions",
            Name = "Read Repo Conventions",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            Conventions = new Output<string>(conventions),
        };
        readConventions.SetDisplayText("Read Repo Conventions");

        // ================================================================
        // 1. Validate Work Item
        // ================================================================
        var validateItem = new ValidateWorkItemActivity
        {
            Id = "ValidateWorkItem",
            Name = "Validate Work Item",
            WorkItemJson = new Input<string>(ctx => workItemJson.Get(ctx)),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
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
                ["conventions"] = conventions.Get(ctx),
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
                ["reviewNotes"] = reviewNotes.Get(ctx),
                ["revisionNumber"] = planRevisionCount.Get(ctx),
                ["conventions"] = conventions.Get(ctx),
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
                ["conventions"] = conventions.Get(ctx),
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
                if (result.TryGetValue("decision", out var d))
                {
                    var decision = d?.ToString() ?? "needsHuman";
                    if (result.TryGetValue("planJson", out var p)) planJson.Set(ctx, p?.ToString() ?? "");
                    if (result.TryGetValue("reviewNotes", out var notes)) reviewNotes.Set(ctx, notes?.ToString() ?? "");
                    return (object)decision;
                }
            }
            return (object)"needsHuman";
        }, "ExtractReviewDecision", "Extract Review Decision");

        // Review outcome routing
        var reviewOutcome = new FlowSwitch
        {
            Id = "ReviewOutcome",
            Name = "Review Outcome",
            Cases =
            {
                new FlowSwitchCase("Approved", ctx => reviewDecision.Get(ctx) == "approved"),
                new FlowSwitchCase("NeedsModification", ctx => reviewDecision.Get(ctx) == "needsModification"),
                new FlowSwitchCase("Defer", ctx => reviewDecision.Get(ctx) == "defer"),
                new FlowSwitchCase("Split", ctx => reviewDecision.Get(ctx) == "split"),
                new FlowSwitchCase("NeedsHuman", ctx => true),
            },
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
                ["conventions"] = conventions.Get(ctx),
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
                ["conventions"] = conventions.Get(ctx),
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

        var taskReviewOutcome = new FlowSwitch
        {
            Id = "TaskReviewOutcome",
            Name = "Tasks Approved?",
            Cases =
            {
                new FlowSwitchCase("Approved", ctx => taskReviewDecision.Get(ctx) == "approved"),
                new FlowSwitchCase("NeedsChanges", ctx => taskReviewDecision.Get(ctx) == "needsChanges"),
                new FlowSwitchCase("NeedsHuman", ctx => true),
            },
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
        // 8. Create PR (draft, with implementation plan .md files)
        // ================================================================
        var createPR = new DispatchWorkflow
        {
            Id = "CreatePR",
            Name = "Create Draft PR",
            WorkflowDefinitionId = new("pull-request"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["baseBranch"] = baseBranch.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["planJson"] = planJson.Get(ctx),
                ["draft"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        createPR.SetDisplayText("Create Draft PR");

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
        // 9. Create Test Cases (from tasks, committed to PR branch)
        // ================================================================
        var createTestCases = new DispatchWorkflow
        {
            Id = "CreateTestCases",
            Name = "Create Test Cases",
            WorkflowDefinitionId = new("test-case-creation"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["tasksJson"] = tasksJson.Get(ctx),
                ["contextIds"] = contextIds.Get(ctx),
                ["conventions"] = conventions.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        createTestCases.SetDisplayText("Create Test Cases");

        // ================================================================
        // 10. TDD Loop (for each task in dependency order)
        // Each task: red → green → CI → refactor → commit
        // ================================================================
        var currentTaskIndex = builder.WithVariable<int>("CurrentTaskIndex", 0);
        var totalTasks = builder.WithVariable<int>("TotalTasks", 0);
        var currentTaskJson = builder.WithVariable<string>("CurrentTaskJson", "");

        var initTaskLoop = Assign(totalTasks, ctx =>
        {
            // Parse tasks array to get count
            try
            {
                var tasks = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(tasksJson.Get(ctx));
                var count = tasks.GetArrayLength();
                currentTaskIndex.Set(ctx, 0);
                return (object)count;
            }
            catch { return (object)0; }
        }, "InitTaskLoop", "Init Task Loop");

        var hasMoreTasks = new FlowDecision(ctx => currentTaskIndex.Get(ctx) < totalTasks.Get(ctx))
        {
            Id = "HasMoreTasks",
            Name = "More Tasks?"
        };
        hasMoreTasks.SetDisplayText("More Tasks?");

        var extractCurrentTask = Assign(currentTaskJson, ctx =>
        {
            try
            {
                var tasks = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(tasksJson.Get(ctx));
                var idx = currentTaskIndex.Get(ctx);
                return (object)tasks[idx].GetRawText();
            }
            catch { return (object)"{}"; }
        }, "ExtractCurrentTask", "Extract Current Task");

        var tddForTask = new DispatchWorkflow
        {
            Id = "TddForTask",
            Name = "TDD for Task",
            WorkflowDefinitionId = new("tdd-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["taskJson"] = currentTaskJson.Get(ctx),
                ["contextIds"] = contextIds.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["conventions"] = conventions.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        tddForTask.SetDisplayText("TDD for Task");

        var incrementTask = Assign(currentTaskIndex, ctx =>
            (object)(currentTaskIndex.Get(ctx) + 1),
            "IncrementTask", "Next Task");

        // ================================================================
        // 11a. Dispatch Code Review (fire & forget — LLM reviews the PR)
        // ================================================================
        var dispatchCodeReview = new DispatchWorkflow
        {
            Id = "DispatchCodeReview",
            Name = "Dispatch Code Review",
            WorkflowDefinitionId = new("code-review"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["conventions"] = conventions.Get(ctx),
            }),
            WaitForCompletion = new(false), // fire & forget
        };
        dispatchCodeReview.SetDisplayText("Dispatch Code Review");

        // ================================================================
        // 11b. Wait for PR Approval (bookmark — blocks until approved)
        // ================================================================
        var waitForApproval = new WaitForPRApprovalActivity
        {
            Id = "WaitForPRApproval",
            Name = "Wait for PR Approval",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            PRNumber = new Input<int>(ctx => prNumber.Get(ctx)),
        };
        waitForApproval.SetDisplayText("Wait for PR Approval");

        // ================================================================
        // 12. Dispatch Merge (fire & forget — handles merge, CI on main, conflicts)
        // ================================================================
        var dispatchMerge = new DispatchWorkflow
        {
            Id = "DispatchMerge",
            Name = "Dispatch Merge",
            WorkflowDefinitionId = new("merge"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
            }),
            WaitForCompletion = new(false), // fire & forget
        };
        dispatchMerge.SetDisplayText("Dispatch Merge");

        // ================================================================
        // 13. Wait for PR Merged (bookmark — blocks until merged)
        // ================================================================
        var mergeSha = builder.WithVariable<string>("MergeSha", "");
        var waitForMerged = new WaitForPRMergedActivity
        {
            Id = "WaitForPRMerged",
            Name = "Wait for PR Merged",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            PRNumber = new Input<int>(ctx => prNumber.Get(ctx)),
            MergeSha = new Output<string?>(mergeSha),
        };
        waitForMerged.SetDisplayText("Wait for PR Merged");

        // ================================================================
        // 14. Update & Close Issue
        // ================================================================
        var closeIssue = NotifyIssue("CloseIssue", repository, issueNumber,
            "🎉 PR merged! Issue resolved.",
            new[] { "tamma-completed" }, new[] { "tamma-processing" });

        // ================================================================
        // 15. Deployment Pipeline (sub-workflow — QA → UAT → Prod)
        // ================================================================
        var deploymentPipeline = new DispatchWorkflow
        {
            Id = "DeploymentPipeline",
            Name = "Deployment Pipeline",
            WorkflowDefinitionId = new("deployment-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["mergeSha"] = mergeSha.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
            }),
            WaitForCompletion = new(true), // wait — need deployment result before reporting
            Result = new(subResult),
        };
        deploymentPipeline.SetDisplayText("Deployment Pipeline");

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
        var notifyPlanRevision = NotifyIssue("NotifyPlanRevision", repository, issueNumber,
            "🔄 Plan needs modification. Revising...");
        var notifyTaskRevision = NotifyIssue("NotifyTaskRevision", repository, issueNumber,
            "🔄 Tasks need changes. Revising...");

        // Revision counters and max checks
        var incrementPlanRevision = Assign(planRevisionCount, ctx =>
            (object)(planRevisionCount.Get(ctx) + 1),
            "IncrPlanRevision", "Increment Plan Revision");

        var planMaxRevisionsCheck = new FlowDecision(ctx => planRevisionCount.Get(ctx) >= 3)
        {
            Id = "PlanMaxRevisions",
            Name = "Plan Max Revisions?"
        };
        planMaxRevisionsCheck.SetDisplayText("Plan Max Revisions?");

        var taskRevisionCount = builder.WithVariable<int>("TaskRevisionCount", 0);
        var incrementTaskRevision = Assign(taskRevisionCount, ctx =>
            (object)(taskRevisionCount.Get(ctx) + 1),
            "IncrTaskRevision", "Increment Task Revision");

        var taskMaxRevisionsCheck = new FlowDecision(ctx => taskRevisionCount.Get(ctx) >= 3)
        {
            Id = "TaskMaxRevisions",
            Name = "Task Max Revisions?"
        };
        taskMaxRevisionsCheck.SetDisplayText("Task Max Revisions?");
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
            Start = initInputs,
            Activities =
            {
                // Main flow
                initInputs, readConventions, validateItem, gatherContext, extractContext,
                generatePlan, extractPlan,
                reviewPlan, extractReviewDecision, reviewOutcome,
                createDeferredIssues, createSplitIssues,
                createTasks, extractTasks,
                reviewTasks, extractTaskReview, taskReviewOutcome,
                createBranch, extractBranch,
                createPR, extractPR,
                createTestCases,
                initTaskLoop, hasMoreTasks, extractCurrentTask, tddForTask, incrementTask,
                dispatchCodeReview, waitForApproval,
                dispatchMerge, waitForMerged, closeIssue, deploymentPipeline,
                // Notifications (fire-and-forget)
                notifyProcessing, notifyInvalid, notifyContextDone,
                notifyPlanDone, notifyPlanApproved,
                notifyDeferred, notifySplit, notifyNeedsHuman,
                notifyPlanRevision, notifyTaskRevision,
                incrementPlanRevision, planMaxRevisionsCheck,
                incrementTaskRevision, taskMaxRevisionsCheck,
                notifyTasksApproved, notifyBranchCreated,
                notifyTddDone, notifyMerged, notifyError,
                // Exit paths
                reportSuccess, reportDeferred, reportSplit,
                reportNeedsHuman, reportError, finish,
            },
            Connections =
            {
                // 0. Init Inputs → Read Conventions → Validate
                Connect(initInputs, readConventions),
                Connect(readConventions, validateItem),

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
                // NeedsModification → increment → max check → loop or escalate
                ConnectOutcome(reviewOutcome, "NeedsModification", notifyPlanRevision),
                ConnectOutcome(reviewOutcome, "NeedsModification", incrementPlanRevision),
                Connect(incrementPlanRevision, planMaxRevisionsCheck),
                ConnectOutcome(planMaxRevisionsCheck, "False", generatePlan), // loop back
                ConnectOutcome(planMaxRevisionsCheck, "True", notifyNeedsHuman), // escalate
                ConnectOutcome(planMaxRevisionsCheck, "True", reportNeedsHuman),

                // NeedsHuman
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
                // Task NeedsChanges → increment → max check → loop or escalate
                ConnectOutcome(taskReviewOutcome, "NeedsChanges", notifyTaskRevision),
                ConnectOutcome(taskReviewOutcome, "NeedsChanges", incrementTaskRevision),
                Connect(incrementTaskRevision, taskMaxRevisionsCheck),
                ConnectOutcome(taskMaxRevisionsCheck, "False", createTasks), // loop back
                ConnectOutcome(taskMaxRevisionsCheck, "True", notifyNeedsHuman), // escalate
                ConnectOutcome(taskMaxRevisionsCheck, "True", reportNeedsHuman),

                // Task NeedsHuman
                ConnectOutcome(taskReviewOutcome, "NeedsHuman", notifyNeedsHuman),
                ConnectOutcome(taskReviewOutcome, "NeedsHuman", reportNeedsHuman),

                // 7. Create Branch → notify + Create Draft PR (parallel)
                Connect(createBranch, extractBranch),
                Connect(extractBranch, notifyBranchCreated),
                Connect(extractBranch, createPR),

                // 8. Create Draft PR → 9. Create Test Cases
                Connect(createPR, extractPR),
                Connect(extractPR, createTestCases),

                // 9. Create Test Cases → 10. TDD Loop
                Connect(createTestCases, initTaskLoop),
                Connect(initTaskLoop, hasMoreTasks),

                // TDD Loop: more tasks? → extract task → TDD → increment → loop
                ConnectOutcome(hasMoreTasks, "True", extractCurrentTask),
                Connect(extractCurrentTask, tddForTask),
                Connect(tddForTask, incrementTask),
                Connect(incrementTask, hasMoreTasks), // loop back

                // TDD Loop done → notify + dispatch code review + wait for approval (parallel)
                ConnectOutcome(hasMoreTasks, "False", notifyTddDone),
                ConnectOutcome(hasMoreTasks, "False", dispatchCodeReview),
                ConnectOutcome(hasMoreTasks, "False", waitForApproval),

                // 11. PR Approved → Dispatch Merge + Wait for Merged (parallel)
                Connect(waitForApproval, dispatchMerge),
                Connect(waitForApproval, waitForMerged),

                // 13. Merged → Close Issue + Deployment Pipeline (parallel)
                Connect(waitForMerged, closeIssue),
                Connect(waitForMerged, deploymentPipeline),

                // 15. Deployment done → Report Success → Finish
                Connect(deploymentPipeline, notifyMerged),
                Connect(deploymentPipeline, reportSuccess),
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

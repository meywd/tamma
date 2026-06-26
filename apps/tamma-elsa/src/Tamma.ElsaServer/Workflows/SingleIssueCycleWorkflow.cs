using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.AgentDispatch;
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

        // CORRECTNESS (completeness audit 2026-06-22, SingleIssueCycle.md §Missing #1) —
        // continue-with-incidents so a FAULTED awaited sub-workflow / activity does NOT
        // halt the whole cycle with an incident (which would leave the issue stuck on
        // `tamma-processing` with no terminal reported to the orchestrator — the
        // silent-failure hole). Instead, each awaited DispatchWorkflow's result is
        // inspected (extract* + a `*Ok?` FlowDecision); an absent / invalid critical
        // output routes to the shared LOUD fail-the-cycle sink (notifyError +
        // EmitCycleEvent CYCLE.STEP_FAILED + reportError). Mirrors the
        // MergeApprovalWorkflow / CleanUpFailedTenantWorkflow precedent.
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        // ================================================================
        // Variables
        // ================================================================
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var botAssignee = builder.WithVariable<string>("BotAssignee", "tamma-bot");
        var baseBranch = builder.WithVariable<string>("BaseBranch", "main");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        // Deployment mode (dev | business) — drives the deployment pipeline's
        // production approval gate (Business Mode requires human approval before
        // prod). Read from input; defaults to dev when absent.
        var mode = builder.WithVariable<string>("Mode", "");
        // Operator force-flag for the prod approval gate (Deployment:RequireProdApproval).
        // Threaded from DispatchCycleActivity so an operator can force the gate even
        // in single-user/dev mode. Read from input; defaults false.
        var requireProdApproval = builder.WithVariable<bool>("RequireProdApproval", false);

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
        // Branch-creation gating (IMPORTANT-5): the cycle must NOT proceed to
        // createPR on a failed branch (empty head → doomed PR + false "branch
        // created" notify). The branch sub-workflow reports success / errorCode /
        // exitReason; we capture them to route a loud terminal on failure.
        var branchSuccess = builder.WithVariable<bool>("BranchSuccess", false);
        var branchErrorCode = builder.WithVariable<string>("BranchErrorCode", "");
        var branchErrorReason = builder.WithVariable<string>("BranchErrorReason", "");
        var prNumber = builder.WithVariable<int>("PRNumber", 0);
        var prUrl = builder.WithVariable<string>("PRUrl", "");
        var exitReason = builder.WithVariable<string>("ExitReason", "");

        // Fail-the-cycle sink context (Phase A): which step failed + the underlying
        // detail surfaced on the loud CYCLE.STEP_FAILED audit row. Defaults are never
        // empty (no silent failure) — every error route stamps a real stepId/detail.
        var failedStepId = builder.WithVariable<string>("FailedStepId", "unknown-step");
        var failedDetail = builder.WithVariable<string>("FailedDetail", "step produced no usable result");

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
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                mode.Set(ctx, ctx.GetInput<string>("mode") ?? "");
                requireProdApproval.Set(ctx, ctx.GetInput<bool>("requireProdApproval"));
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
                ["tenantId"] = tenantId.Get(ctx),
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
                ["tenantId"] = tenantId.Get(ctx),
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
                ["tenantId"] = tenantId.Get(ctx),
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
                ["tenantId"] = tenantId.Get(ctx),
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
                ["tenantId"] = tenantId.Get(ctx),
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
                // P1 §Missing #9 — capture the revised tasksJson BEFORE returning the
                // decision so the needsChanges loop re-reviews the REVISED tasks, not
                // the stale set. (Previously this line was dead code after the return.)
                if (result.TryGetValue("tasksJson", out var t)) tasksJson.Set(ctx, t?.ToString() ?? "");
                if (result.TryGetValue("decision", out var d)) return (object)(d?.ToString() ?? "needsHuman");
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
                // Thread the tenant so BRANCH.CREATED.* events are tenant-scoped
                // in the durable DCB drain (single-user → empty → platform-scope).
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        createBranch.SetDisplayText("Create Branch");

        var extractBranch = Assign(branchName, ctx =>
        {
            var result = subResult.Get(ctx);

            // Capture the branch step's success + failure classification so the
            // gate below can route a loud terminal (never a doomed PR). An
            // unreadable/absent result is treated as failure (no false success).
            var success = result != null
                && result.TryGetValue("success", out var s)
                && s is true;
            branchSuccess.Set(ctx, success);
            branchErrorCode.Set(ctx, result?.GetValueOrDefault("errorCode")?.ToString() ?? "branch-creation-failed");
            branchErrorReason.Set(ctx, result?.GetValueOrDefault("exitReason")?.ToString() ?? "branch creation failed");

            if (result != null && result.TryGetValue("branchName", out var b))
                return (object)(b?.ToString() ?? "");
            return (object)"";
        }, "ExtractBranch", "Extract Branch");

        // Gate the cycle on the branch step's success (IMPORTANT-5). Only a
        // created branch proceeds to createPR + the branch-created notify; a
        // failure routes to a loud terminal (no empty-head PR, no false notify).
        // Defaults to "Failed" so an unreadable result can never slip into PR
        // creation.
        var branchOutcomeSwitch = new FlowSwitch
        {
            Id = "BranchOutcomeSwitch",
            Name = "Branch Outcome",
            Cases =
            {
                new FlowSwitchCase("Created", ctx => branchSuccess.Get(ctx)),
                new FlowSwitchCase("Failed", ctx => true), // failure + any unreadable result
            },
        };
        branchOutcomeSwitch.SetDisplayText("Branch Outcome");

        var notifyBranchFailed = NotifyIssue("NotifyBranchFailed", repository, issueNumber,
            "❌ Branch creation failed. Needs human attention.",
            new[] { "tamma-error", "needs-human" }, new[] { "tamma-processing" });

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
                ["issueTitle"] = ExtractWorkItemTitle(workItemJson.Get(ctx)),
                ["planJson"] = planJson.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
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
                ["tenantId"] = tenantId.Get(ctx),
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

        // ────────────────────────────────────────────────────────────────
        // Story 19-5 AC-6: Replace direct tdd-cycle sub-workflow dispatch
        // with mode-aware ExecuteAgentActivity. The activity selects
        // LocalExecutor (CLI / self-hosted) or GitHubActionsExecutor (SaaS)
        // via AgentExecutorFactory at runtime. The same workflow definition
        // now works unchanged in both deployment modes.
        //
        // Input mapping (workflow vars -> AgentExecutionRequest):
        //   Repository    = repository
        //   BranchName    = branchName
        //   IssueNumber   = issueNumber
        //   IssueTitle    = workItemJson (title is carried in workItemJson)
        //   Task          = "implement"  (per-task TDD iteration)
        //   PlanJson      = currentTaskJson (the one-task slice)
        //   SessionId     = deterministic session id for this task
        //   AgentProvider = default "claude-code"
        //   TimeoutMinutes = 30 (TDD task default)
        //
        // Output: AgentExecutionResult is set on the activity's outputs and
        //   the "LastAgentExecutionResult" workflow variable. The loop
        //   simply advances to the next task on either outcome (matches
        //   prior behaviour of the DispatchWorkflow which did not branch).
        // ────────────────────────────────────────────────────────────────
        var tddForTask = new ExecuteAgentActivity
        {
            Id = "TddForTask",
            Name = "TDD for Task (agent execution)",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            BranchName = new Input<string>(ctx => branchName.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            IssueTitle = new Input<string>(ctx => workItemJson.Get(ctx)),
            Task = new Input<string>("implement"),
            PlanJson = new Input<string>(ctx => currentTaskJson.Get(ctx)),
            SessionId = new Input<string>(ctx =>
                $"adl-{issueNumber.Get(ctx)}-task-{currentTaskIndex.Get(ctx)}"),
            AgentProvider = new Input<string>("claude-code"),
            AgentConfigJson = new Input<string?>(ctx => conventions.Get(ctx)),
            TimeoutMinutes = new Input<int>(30),
            ModeOverride = new Input<string?>(_ => null),
        };
        tddForTask.SetDisplayText("TDD for Task (agent execution)");

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
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(false), // fire & forget
        };
        dispatchCodeReview.SetDisplayText("Dispatch Code Review");

        // ================================================================
        // 11b-12. Merge-Approval Gate (sub-workflow — the human APPROVAL_GATE)
        //
        // Replaces the prior bare WaitForPRApprovalActivity (binary approve) +
        // fire-and-forget "merge" dispatch with the 3-way merge/test/reject gate
        // (merge-approval), delivering the PRD test/merge decision point and the
        // FR-19/FR-34 audit trail. On the Merge decision the gate dispatches the
        // SAME "merge" workflow (WaitForCompletion=true) the loop used before; on
        // Test it re-runs CI then re-decides; on Reject/Invalid it labels/escalates.
        // We wait for the gate so the cycle does not race ahead of the human.
        //
        // CRITICAL: the cycle MUST branch on the gate's `outcome` output. Only the
        // "merge" outcome may reach WaitForPRMerged (which blocks on a
        // `pr-merged-{pr}` webhook that fires ONLY on a real merge). reject /
        // escalated finish at a loud human-handoff / error terminal — wiring those
        // into WaitForPRMerged would hang the cycle forever (no webhook will ever
        // fire), leaving the issue stuck `tamma-processing` with no terminal
        // reported to the orchestrator.
        // ================================================================
        var mergeApprovalGate = new DispatchWorkflow
        {
            Id = "MergeApprovalGate",
            Name = "Merge Approval Gate",
            WorkflowDefinitionId = new("merge-approval"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["prUrl"] = prUrl.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true), // block until the human decides + the gate acts
            Result = new(subResult),       // capture the gate's outputs (incl. `outcome`)
        };
        mergeApprovalGate.SetDisplayText("Merge Approval Gate");

        // Gate outcome: "merge" | "reject" | "escalated" (MergeApprovalWorkflow's
        // `outcome` output). Defaults to "escalated" when the gate returns nothing
        // parseable so an unreadable result is treated as a loud non-merge (never a
        // silent merge), keeping the cycle on a terminal path.
        var gateOutcome = builder.WithVariable<string>("GateOutcome", "escalated");
        var extractGateOutcome = Assign(gateOutcome, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null && result.TryGetValue("outcome", out var o))
            {
                var token = o?.ToString();
                if (!string.IsNullOrWhiteSpace(token)) return (object)token;
            }
            return (object)"escalated";
        }, "ExtractGateOutcome", "Extract Gate Outcome");

        // Branch the cycle on the gate outcome. ONLY "merge" continues to
        // WaitForPRMerged; everything else reaches a loud terminal.
        var gateOutcomeSwitch = new FlowSwitch
        {
            Id = "GateOutcomeSwitch",
            Name = "Gate Outcome",
            Cases =
            {
                new FlowSwitchCase("Merge", ctx => gateOutcome.Get(ctx) == "merge"),
                new FlowSwitchCase("Reject", ctx => gateOutcome.Get(ctx) == "reject"),
                new FlowSwitchCase("Escalated", ctx => true), // escalated + any unknown
            },
        };
        gateOutcomeSwitch.SetDisplayText("Gate Outcome");

        // Non-merge terminals: reject = human handoff; escalated = error report.
        var notifyMergeRejected = NotifyIssue("NotifyMergeRejected", repository, issueNumber,
            "🚫 PR rejected at the merge-approval gate. Needs human follow-up.",
            new[] { "tamma-rejected", "needs-human" }, new[] { "tamma-processing" });
        var notifyMergeEscalated = NotifyIssue("NotifyMergeEscalated", repository, issueNumber,
            "⚠️ Merge-approval gate escalated (failed merge / invalid decision). Needs human attention.",
            new[] { "tamma-error", "needs-human" }, new[] { "tamma-processing" });

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
                // Thread mode + tenant so the pipeline's production approval gate
                // is mode-aware (Business Mode → human approval) and its DCB
                // events / bookmarks are tenant-scoped. requireProdApproval lets an
                // operator force the gate even in dev mode.
                ["mode"] = mode.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
                ["requireProdApproval"] = requireProdApproval.Get(ctx),
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

        // ================================================================
        // Cycle-scoped DCB events (Phase A §Missing #1) — emitted at the
        // orchestration boundaries via the durable engine event drain
        // (TammaEventEmitter, NOT a direct IEventRepository). The composed
        // activities already auto-emit their own per-step events; these stamp the
        // cycle's own lifecycle so time-travel debugging can see when the roundabout
        // started, which step failed it, and whether it completed.
        // ================================================================
        var emitCycleStarted = new EmitCycleEventActivity
        {
            Id = "EmitCycleStarted", Name = "Emit CYCLE.STARTED",
            EventType = new Input<string>(_ => CycleEvents.Started),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repository.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
        };
        emitCycleStarted.SetDisplayText("Emit CYCLE.STARTED");

        var emitCycleCompleted = new EmitCycleEventActivity
        {
            Id = "EmitCycleCompleted", Name = "Emit CYCLE.COMPLETED",
            EventType = new Input<string>(_ => CycleEvents.Completed),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repository.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
        };
        emitCycleCompleted.SetDisplayText("Emit CYCLE.COMPLETED");

        // ================================================================
        // Shared LOUD fail-the-cycle sink (Phase A §Missing #1/#2). EVERY result
        // gate's failure edge converges here: stamp the cycle's CYCLE.STEP_FAILED
        // audit row (with the failing stepId + the underlying detail), fire the
        // tamma-error notification, then reportError → finish. No swallowed
        // COMPLETED, no proceed-with-empty-data, no dangling edge.
        // ================================================================
        var emitStepFailed = new EmitCycleEventActivity
        {
            Id = "EmitStepFailed", Name = "Emit CYCLE.STEP_FAILED",
            EventType = new Input<string>(_ => CycleEvents.StepFailed),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repository.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            StepId = new Input<string?>(ctx => failedStepId.Get(ctx)),
            ErrorDetail = new Input<string?>(ctx => failedDetail.Get(ctx)),
        };
        emitStepFailed.SetDisplayText("Emit CYCLE.STEP_FAILED");

        // ================================================================
        // Result-validation gates (Phase A §Missing #1/#2) — one per awaited
        // sub-workflow whose critical output the cycle MUST have to proceed. Each
        // gate's False edge routes to the shared fail-the-cycle sink (no empty/plain
        // fallback, no proceed-with-empty-data). The plan/task REVIEW steps already
        // self-validate to "needsHuman" (a loud terminal), and branch creation is
        // already gated by branchOutcomeSwitch — so those are not re-gated here.
        // ================================================================
        var contextOk = StepGate("ContextOk", "Context OK?",
            ctx => !string.IsNullOrWhiteSpace(poSummary.Get(ctx)) || !string.IsNullOrWhiteSpace(contextIds.Get(ctx)),
            failedStepId, failedDetail, "context-gathering",
            _ => "context-gathering returned no summary or context ids");

        var planOk = StepGate("PlanOk", "Plan OK?",
            ctx => !string.IsNullOrWhiteSpace(planJson.Get(ctx)),
            failedStepId, failedDetail, "plan-generation",
            _ => "plan-generation returned an empty plan");

        var tasksOk = StepGate("TasksOk", "Tasks OK?",
            ctx => HasTasks(tasksJson.Get(ctx)),
            failedStepId, failedDetail, "task-creation",
            _ => "task-creation returned no tasks (empty or unparseable task list)");
        // NOTE (honestly-DEFERRED follow-up, SingleIssueCycle.md §Missing #6):
        // WaitForPRMergedActivity / WaitForPRApprovalActivity create unbounded
        // `pr-merged-{pr}` / `pr-approval-{pr}` bookmarks with NO SLA timeout. If a
        // merge/approval webhook never arrives the loop hangs. This is NOT a
        // regression (the real merge now runs synchronously inside merge-approval),
        // but before this cycle runs in a live unattended loop a durable
        // `context.DelayFor` SLA timer must be added to those waits. Tracked follow-up.

        var prOk = StepGate("PrOk", "PR OK?",
            ctx => prNumber.Get(ctx) > 0,
            failedStepId, failedDetail, "pull-request",
            _ => "pull-request returned no PR number (cannot wait on a non-existent PR)");

        var deployOk = StepGate("DeployOk", "Deploy OK?",
            ctx => IsDeploySuccessful(subResult.Get(ctx)),
            failedStepId, failedDetail, "deployment-pipeline",
            _ => "deployment-pipeline did not report a successful deployment");

        // ================================================================
        // TDD per-task FAILURE → tdd-with-debug-retry (Phase A §Missing #3/#4).
        // A failed agent run no longer advances the loop silently (false success).
        // It dispatches the EXISTING tdd-with-debug-retry sub-workflow (bounded
        // debug-retry) for the one-task slice; on its success the loop advances, on
        // its failure the cycle fails LOUD (needsHuman handoff). LLM/agent work stays
        // mediated — no direct provider call here.
        // ================================================================
        var dispatchTddRetry = new DispatchWorkflow
        {
            Id = "DispatchTddRetry",
            Name = "TDD Debug Retry (failed task)",
            WorkflowDefinitionId = new("tdd-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}-task-{currentTaskIndex.Get(ctx)}",
                ["planJson"] = currentTaskJson.Get(ctx),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        dispatchTddRetry.SetDisplayText("TDD Debug Retry (failed task)");

        var tddRetryOk = StepGate("TddRetryOk", "TDD Recovered?",
            ctx =>
            {
                var result = subResult.Get(ctx);
                return result != null && result.TryGetValue("success", out var s)
                    && (s is true || string.Equals(s?.ToString(), "True", StringComparison.OrdinalIgnoreCase));
            },
            failedStepId, failedDetail, "tdd-with-debug-retry",
            ctx => subResult.Get(ctx)?.GetValueOrDefault("errorMessage")?.ToString()
                   ?? "TDD did not converge for a task after debug retries");

        var notifyTddRetry = NotifyIssue("NotifyTddRetry", repository, issueNumber,
            "🔁 A task failed TDD — running bounded debug-retry...");
        var notifyTddFailed = NotifyIssue("NotifyTddFailed", repository, issueNumber,
            "❌ A task could not converge in TDD. Needs human attention.",
            new[] { "tamma-error", "needs-human" }, new[] { "tamma-processing" });

        // ================================================================
        // CI gate after the TDD loop, before approval (Phase A §Missing #4) —
        // dispatch the EXISTING ci-with-debug-retry sub-workflow (bounded CI
        // debug-retry) for the PR branch. Passed → code review + merge gate;
        // failed → fail-the-cycle sink (no merge of red CI). Wires the previously
        // orphaned notifyCiPassed milestone notification.
        // ================================================================
        var ciGate = new DispatchWorkflow
        {
            Id = "CiGate",
            Name = "CI Gate (ci-with-debug-retry)",
            WorkflowDefinitionId = new("ci-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        ciGate.SetDisplayText("CI Gate (ci-with-debug-retry)");

        var ciOk = StepGate("CiOk", "CI Passed?",
            ctx =>
            {
                var result = subResult.Get(ctx);
                return result != null && result.TryGetValue("passed", out var p)
                    && (p is true || string.Equals(p?.ToString(), "True", StringComparison.OrdinalIgnoreCase));
            },
            failedStepId, failedDetail, "ci-with-debug-retry",
            ctx => subResult.Get(ctx)?.GetValueOrDefault("errorMessage")?.ToString()
                   ?? "CI did not pass after debug retries");

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
                initInputs, readConventions, validateItem,
                emitCycleStarted,
                gatherContext, extractContext, contextOk,
                generatePlan, extractPlan, planOk,
                reviewPlan, extractReviewDecision, reviewOutcome,
                createDeferredIssues, createSplitIssues,
                createTasks, extractTasks, tasksOk,
                reviewTasks, extractTaskReview, taskReviewOutcome,
                createBranch, extractBranch, branchOutcomeSwitch, notifyBranchFailed,
                createPR, extractPR, prOk,
                createTestCases,
                initTaskLoop, hasMoreTasks, extractCurrentTask, tddForTask, incrementTask,
                dispatchTddRetry, tddRetryOk, notifyTddRetry, notifyTddFailed,
                ciGate, ciOk, notifyCiPassed,
                dispatchCodeReview, mergeApprovalGate,
                extractGateOutcome, gateOutcomeSwitch,
                notifyMergeRejected, notifyMergeEscalated,
                waitForMerged, closeIssue, deploymentPipeline, deployOk,
                emitCycleCompleted,
                // Notifications (fire-and-forget)
                notifyProcessing, notifyInvalid, notifyContextDone,
                notifyPlanDone, notifyPlanApproved,
                notifyDeferred, notifySplit, notifyNeedsHuman,
                notifyPlanRevision, notifyTaskRevision,
                incrementPlanRevision, planMaxRevisionsCheck,
                incrementTaskRevision, taskMaxRevisionsCheck,
                notifyTasksApproved, notifyBranchCreated,
                notifyTddDone, notifyMerged, notifyError,
                // Shared fail-the-cycle sink
                emitStepFailed,
                // Exit paths
                reportSuccess, reportDeferred, reportSplit,
                reportNeedsHuman, reportError, finish,
            },
            Connections =
            {
                // 0. Init Inputs → Read Conventions → Validate
                Connect(initInputs, readConventions),
                Connect(readConventions, validateItem),

                // 1. Validate → notify + emit CYCLE.STARTED + continue (parallel)
                ConnectOutcome(validateItem, "Valid", notifyProcessing),
                ConnectOutcome(validateItem, "Valid", emitCycleStarted),
                Connect(emitCycleStarted, gatherContext),
                ConnectOutcome(validateItem, "Invalid", notifyInvalid),
                ConnectOutcome(validateItem, "Invalid", reportError),

                // 2. Gather Context → extract → GATE (no-false-success) → notify +
                //    Generate Plan. An empty context fails the cycle LOUD (sink).
                Connect(gatherContext, extractContext),
                Connect(extractContext, contextOk),
                ConnectOutcome(contextOk, "True", notifyContextDone),
                ConnectOutcome(contextOk, "True", generatePlan),
                ConnectOutcome(contextOk, "False", emitStepFailed),

                // 3. Generate Plan → extract → GATE → notify + Review Plan.
                //    An empty plan fails the cycle LOUD (no review-of-nothing).
                Connect(generatePlan, extractPlan),
                Connect(extractPlan, planOk),
                ConnectOutcome(planOk, "True", notifyPlanDone),
                ConnectOutcome(planOk, "True", reviewPlan),
                ConnectOutcome(planOk, "False", emitStepFailed),

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

                // 5. Create Tasks → extract → GATE → 6. Review Tasks.
                //    No tasks fails the cycle LOUD (no review-of-nothing).
                Connect(createTasks, extractTasks),
                Connect(extractTasks, tasksOk),
                ConnectOutcome(tasksOk, "True", reviewTasks),
                ConnectOutcome(tasksOk, "False", emitStepFailed),

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

                // 7. Create Branch → capture result → gate on success (IMPORTANT-5).
                //    The branch step must succeed before the cycle proceeds to PR
                //    creation; a failure routes to a loud terminal (no empty-head
                //    PR, no false "branch created" notify). Both outcomes reach Finish.
                Connect(createBranch, extractBranch),
                Connect(extractBranch, branchOutcomeSwitch),

                // Created → notify + Create Draft PR (parallel)
                ConnectOutcome(branchOutcomeSwitch, "Created", notifyBranchCreated),
                ConnectOutcome(branchOutcomeSwitch, "Created", createPR),

                // Failed → loud human-handoff/error terminal (never PR creation)
                ConnectOutcome(branchOutcomeSwitch, "Failed", notifyBranchFailed),
                ConnectOutcome(branchOutcomeSwitch, "Failed", reportError),

                // 8. Create Draft PR → extract → GATE → 9. Create Test Cases.
                //    prNumber<=0 fails the cycle LOUD (never wait on a non-existent PR).
                Connect(createPR, extractPR),
                Connect(extractPR, prOk),
                ConnectOutcome(prOk, "True", createTestCases),
                ConnectOutcome(prOk, "False", emitStepFailed),

                // 9. Create Test Cases → 10. TDD Loop
                Connect(createTestCases, initTaskLoop),
                Connect(initTaskLoop, hasMoreTasks),

                // TDD Loop: more tasks? → extract task → TDD → (recover|advance) → loop
                // Story 19-5 AC-6: ExecuteAgentActivity exposes Completed/Failed.
                // Completed → advance the loop. Failed → NO LONGER advances silently
                // (the false-success hole, §Missing #3): it routes through the EXISTING
                // tdd-with-debug-retry sub-workflow (bounded debug-retry). Recovered →
                // advance; not converged → fail the cycle LOUD (needs-human sink).
                ConnectOutcome(hasMoreTasks, "True", extractCurrentTask),
                Connect(extractCurrentTask, tddForTask),
                ConnectOutcome(tddForTask, "Completed", incrementTask),
                ConnectOutcome(tddForTask, "Failed", notifyTddRetry),
                ConnectOutcome(tddForTask, "Failed", dispatchTddRetry),
                Connect(dispatchTddRetry, tddRetryOk),
                ConnectOutcome(tddRetryOk, "True", incrementTask),
                // Not converged → loud needs-human handoff (notify + the shared sink,
                // which reports error). Never advance a still-broken task into a PR.
                ConnectOutcome(tddRetryOk, "False", notifyTddFailed),
                ConnectOutcome(tddRetryOk, "False", emitStepFailed),
                Connect(incrementTask, hasMoreTasks), // loop back

                // TDD Loop done → notify + CI GATE before merge (§Missing #4).
                // ci-with-debug-retry runs the PR branch through CI with bounded
                // debug-retry; only a PASS proceeds to code review + the merge gate.
                // A CI failure fails the cycle LOUD (no merge of red CI).
                ConnectOutcome(hasMoreTasks, "False", notifyTddDone),
                ConnectOutcome(hasMoreTasks, "False", ciGate),
                Connect(ciGate, ciOk),
                ConnectOutcome(ciOk, "False", emitStepFailed),
                // CI passed → notify + dispatch code review + merge-approval gate.
                ConnectOutcome(ciOk, "True", notifyCiPassed),
                ConnectOutcome(ciOk, "True", dispatchCodeReview),
                ConnectOutcome(ciOk, "True", mergeApprovalGate),

                // 11b-12. Merge-Approval Gate (human merge/test/reject + acts on it)
                //         → branch on the gate `outcome`. ONLY merge → WaitForPRMerged.
                //         The gate dispatches the real "merge" workflow on approval;
                //         the loop then blocks on the merge webhook via waitForMerged
                //         before closing the issue. reject / escalated must NEVER reach
                //         waitForMerged (no webhook would ever fire → permanent hang) —
                //         they finish at a loud human-handoff / error terminal instead.
                Connect(mergeApprovalGate, extractGateOutcome),
                Connect(extractGateOutcome, gateOutcomeSwitch),

                // merge → wait for the real merge webhook (the only path to success)
                ConnectOutcome(gateOutcomeSwitch, "Merge", waitForMerged),

                // reject → human-handoff terminal (loud), never waitForMerged
                ConnectOutcome(gateOutcomeSwitch, "Reject", notifyMergeRejected),
                ConnectOutcome(gateOutcomeSwitch, "Reject", reportNeedsHuman),

                // escalated (incl. a failed merge sub-workflow, CRITICAL-2) → error
                // terminal (loud), never waitForMerged
                ConnectOutcome(gateOutcomeSwitch, "Escalated", notifyMergeEscalated),
                ConnectOutcome(gateOutcomeSwitch, "Escalated", reportError),

                // 13. Merged → Close Issue + Deployment Pipeline (parallel)
                Connect(waitForMerged, closeIssue),
                Connect(waitForMerged, deploymentPipeline),

                // 15. Deployment done → GATE on the deploy result (§Missing #11).
                //     A deployment failure must NOT report success — it routes to the
                //     loud sink. A successful deploy → notify + emit CYCLE.COMPLETED +
                //     report success → finish.
                Connect(deploymentPipeline, deployOk),
                ConnectOutcome(deployOk, "True", notifyMerged),
                ConnectOutcome(deployOk, "True", emitCycleCompleted),
                Connect(emitCycleCompleted, reportSuccess),
                Connect(reportSuccess, finish),
                ConnectOutcome(deployOk, "False", emitStepFailed),

                // Shared LOUD fail-the-cycle sink — every result gate's False edge +
                // the not-converged TDD path converge here: audit CYCLE.STEP_FAILED,
                // notify tamma-error, report error, finish. No dangling edge / hang.
                Connect(emitStepFailed, notifyError),
                Connect(emitStepFailed, reportError),

                // Error → finish (the binary-validate Invalid path + the sink).
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

    /// <summary>
    /// Best-effort extraction of the work item's title from the work-item JSON
    /// so the PR step can render <c>[ADL] #n: {title}</c>. Returns "" when the
    /// JSON is absent / malformed / has no title field (the PR step degrades to
    /// a title-less form rather than failing).
    /// </summary>
    private static string ExtractWorkItemTitle(string? workItemJson)
    {
        if (string.IsNullOrWhiteSpace(workItemJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(workItemJson);
            foreach (var name in new[] { "title", "Title", "name", "Name" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(name, out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    private static SetVariable Assign(Variable variable, Func<Elsa.Expressions.Models.ExpressionExecutionContext, object?> valueFunc, string id, string name)
    {
        var sv = new SetVariable { Id = id, Name = name, Variable = variable, Value = new Input<object?>(valueFunc) };
        sv.SetDisplayText(name);
        return sv;
    }

    /// <summary>
    /// A result-validation gate for an awaited sub-workflow (Phase A §Missing #1/#2).
    /// The <paramref name="isValid"/> predicate inspects the captured critical output;
    /// when it returns <c>false</c> it ALSO stamps <paramref name="failedStepId"/> /
    /// <paramref name="failedDetail"/> (via <paramref name="onFail"/>) so the shared
    /// fail-the-cycle sink surfaces WHICH step failed and WHY — never an empty/plain
    /// fallback, never a silent proceed. The <c>True</c> outcome continues the cycle;
    /// the <c>False</c> outcome must be wired to the error sink.
    /// </summary>
    /// <summary>
    /// Deploy-gate predicate (fail-closed). The <c>deployment-pipeline</c> sub-workflow
    /// reports its verdict under the <c>deploymentStatus</c> output key: <c>"success"</c>
    /// is the only passing value; every failure value (<c>"failed"</c>, <c>"failed:qa"</c>,
    /// <c>"failed:uat"</c>, <c>"failed:production"</c>) and ANY missing/null/unrecognised
    /// result FAILS the cycle. There is deliberately NO "no explicit signal → success"
    /// fallback — an absent or unrecognised deploy verdict must NOT report success.
    /// </summary>
    internal static bool IsDeploySuccessful(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (result.TryGetValue("deploymentStatus", out var ds))
            return string.Equals(ds?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
        // No recognised deployment verdict → fail the cycle (fail-closed).
        return false;
    }

    /// <summary>
    /// Tasks-gate predicate (fail-closed). <c>task-creation</c> emits a JSON array under
    /// <c>tasksJson</c>; on failure it emits the empty-array sentinel <c>"[]"</c> (a
    /// non-blank string that a bare <c>IsNullOrWhiteSpace</c> check would wrongly pass,
    /// letting the TDD loop run zero iterations and a PR be created/merged with no
    /// implementation). Pass only when the payload parses to a JSON array with at least
    /// one element; an empty/blank/unparseable/non-array payload FAILS the cycle.
    /// </summary>
    internal static bool HasTasks(string? tasksJson)
    {
        if (string.IsNullOrWhiteSpace(tasksJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(tasksJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static FlowDecision StepGate(
        string id, string name,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, bool> isValid,
        Variable<string> failedStepId, Variable<string> failedDetail,
        string stepId, Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> detail)
    {
        var gate = new FlowDecision(ctx =>
        {
            if (isValid(ctx)) return true;
            // Side-effect on the failing branch: record the loud failure context.
            failedStepId.Set(ctx, stepId);
            var d = detail(ctx);
            failedDetail.Set(ctx, string.IsNullOrWhiteSpace(d) ? $"{stepId} produced no usable result" : d);
            return false;
        })
        { Id = id, Name = name };
        gate.SetDisplayText(name);
        return gate;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}

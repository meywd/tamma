using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Single Issue Cycle workflow — the 14-step autonomous development cycle
/// for one GitHub issue, implemented as a Flowchart for visual clarity in ELSA Studio.
///
/// Flow:
///   SelectIssue → GatherContext → GeneratePlan → (approval) → CreateBranch
///   → TddCycle → (debug retry loop) → CreatePR → TestingPipeline → (CI retry loop)
///   → ReviewFixCheck → MergeApproval → MergePR → Finish
///
/// Each step dispatches to a sub-workflow and routes based on outcomes.
/// Debugging integration at TDD (3x) and CI (3x) failure points.
/// </summary>
public class SingleIssueCycleWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Single Issue Cycle";
        builder.DefinitionId = "single-issue-cycle";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "14-step autonomous development cycle for one issue";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueLabels = builder.WithVariable<string[]>("IssueLabels", Array.Empty<string>());
        var botAssignee = builder.WithVariable<string>("BotAssignee", "tamma-bot");
        var baseBranch = builder.WithVariable<string>("BaseBranch", "main");

        var issueJson = builder.WithVariable<string>("IssueJson", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var issueTitle = builder.WithVariable<string>("IssueTitle", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "");
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var prNumber = builder.WithVariable<int>("PrNumber", 0);
        var prUrl = builder.WithVariable<string>("PrUrl", "");

        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);

        // DispatchWorkflow result capture variables
        var issueResult = builder.WithVariable<IDictionary<string, object>?>();
        var contextResult = builder.WithVariable<IDictionary<string, object>?>();
        var planResult = builder.WithVariable<IDictionary<string, object>?>();
        var branchResult = builder.WithVariable<IDictionary<string, object>?>();
        var tddResult = builder.WithVariable<IDictionary<string, object>?>();
        var prResult = builder.WithVariable<IDictionary<string, object>?>();
        var testResult = builder.WithVariable<IDictionary<string, object>?>();
        var reviewResult = builder.WithVariable<IDictionary<string, object>?>();
        var mergeApprovalResult = builder.WithVariable<IDictionary<string, object>?>();
        var mergeResult = builder.WithVariable<IDictionary<string, object>?>();
        var exitReason = builder.WithVariable<string>("ExitReason", "");

        // ================================================================
        // INIT: Capture config from parent inputs
        // ================================================================
        var initConfig = new SetVariable
        {
            Id = "InitConfig",
            Name = "Init Config",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var labels = ctx.GetInput<string[]>("issueLabels");
                if (labels != null) issueLabels.Set(ctx, labels);
                var bot = ctx.GetInput<string>("botAssignee");
                if (!string.IsNullOrEmpty(bot)) botAssignee.Set(ctx, bot);
                var bb = ctx.GetInput<string>("baseBranch");
                if (!string.IsNullOrEmpty(bb)) baseBranch.Set(ctx, bb);
                return (object)(ctx.GetInput<string>("repository") ?? "");
            })
        };
        initConfig.SetDisplayText("Init Config");

        // ================================================================
        // Step 1: Issue Selection
        // ================================================================
        var selectIssue = new DispatchWorkflow
        {
            Id = "DispatchIssueSelection",
            Name = "Select Issue",
            WorkflowDefinitionId = new("issue-selection"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueLabels"] = issueLabels.Get(ctx),
                ["botAssignee"] = botAssignee.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(issueResult)
        };
        selectIssue.SetDisplayText("Select Issue");

        var extractIssue = new SetVariable
        {
            Id = "ExtractIssueData",
            Name = "Extract Issue Data",
            Variable = issueNumber,
            Value = new Input<object?>(ctx =>
            {
                var result = issueResult.Get(ctx);
                if (result != null)
                {
                    if (result.TryGetValue("issueJson", out var ij))
                        issueJson.Set(ctx, ij?.ToString() ?? "");
                    if (result.TryGetValue("issueTitle", out var it))
                        issueTitle.Set(ctx, it?.ToString() ?? "");
                    if (result.TryGetValue("issueNumber", out var num) && num is int n)
                        return (object)n;
                }
                return (object)0;
            })
        };
        extractIssue.SetDisplayText("Extract Issue Data");

        var hasIssue = new FlowDecision(ctx => issueNumber.Get(ctx) > 0)
        { Id = "HasIssue", Name = "Issue Found?" };
        hasIssue.SetDisplayText("Issue Found?");

        // ================================================================
        // Step 2: Context Gathering (existing workflow)
        // ================================================================
        var gatherContext = new DispatchWorkflow
        {
            Id = "DispatchContextGathering",
            Name = "Gather Context",
            WorkflowDefinitionId = new("context-gathering"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["issueTitle"] = issueTitle.Get(ctx),
                ["issueBody"] = issueJson.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(contextResult)
        };
        gatherContext.SetDisplayText("Gather Context");

        var extractContext = new SetVariable
        {
            Id = "ExtractContextData",
            Name = "Extract Context Data",
            Variable = contextJson,
            Value = new Input<object?>(ctx =>
            {
                var result = contextResult.Get(ctx);
                if (result != null && result.TryGetValue("contextJson", out var cj))
                    return cj?.ToString() ?? "{}";
                return (object)"{}";
            })
        };
        extractContext.SetDisplayText("Extract Context Data");

        // ================================================================
        // Step 3: Plan Generation (with approval bookmark)
        // ================================================================
        var generatePlan = new DispatchWorkflow
        {
            Id = "DispatchPlanGeneration",
            Name = "Generate Plan",
            WorkflowDefinitionId = new("plan-generation"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["issueNumber"] = issueNumber.Get(ctx),
                ["issueTitle"] = issueTitle.Get(ctx),
                ["issueBody"] = issueJson.Get(ctx),
                ["contextJson"] = contextJson.Get(ctx),
                ["repository"] = repository.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(planResult)
        };
        generatePlan.SetDisplayText("Generate Plan");

        var extractPlan = new SetVariable
        {
            Id = "ExtractPlanData",
            Name = "Extract Plan Data",
            Variable = planJson,
            Value = new Input<object?>(ctx =>
            {
                var result = planResult.Get(ctx);
                if (result != null && result.TryGetValue("planJson", out var pj))
                    return pj?.ToString() ?? "{}";
                return (object)"{}";
            })
        };
        extractPlan.SetDisplayText("Extract Plan Data");

        var planApproved = new FlowDecision(ctx =>
        {
            var result = planResult.Get(ctx);
            if (result != null && result.TryGetValue("approved", out var a))
                return a is true || a?.ToString() == "True";
            return false;
        })
        { Id = "PlanApproved", Name = "Plan Approved?" };
        planApproved.SetDisplayText("Plan Approved?");

        // ================================================================
        // Step 4: Branch Creation
        // ================================================================
        var createBranch = new DispatchWorkflow
        {
            Id = "DispatchBranchCreation",
            Name = "Create Branch",
            WorkflowDefinitionId = new("branch-creation"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["issueTitle"] = issueTitle.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(branchResult)
        };
        createBranch.SetDisplayText("Create Branch");

        var extractBranch = new SetVariable
        {
            Id = "ExtractBranchData",
            Name = "Extract Branch Data",
            Variable = branchName,
            Value = new Input<object?>(ctx =>
            {
                var result = branchResult.Get(ctx);
                if (result != null && result.TryGetValue("branchName", out var bn))
                    return bn?.ToString() ?? "";
                return (object)"";
            })
        };
        extractBranch.SetDisplayText("Extract Branch Data");

        var branchCreated = new FlowDecision(ctx => !string.IsNullOrEmpty(branchName.Get(ctx)))
        { Id = "BranchCreated", Name = "Branch Created?" };
        branchCreated.SetDisplayText("Branch Created?");

        // ================================================================
        // Steps 5-7: TDD Cycle (dispatched to sub-workflow)
        // ================================================================
        var dispatchTddRetry = new DispatchWorkflow
        {
            Id = "DispatchTddWithDebugRetry",
            Name = "TDD with Debug Retry",
            WorkflowDefinitionId = new("tdd-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["planJson"] = planJson.Get(ctx),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5,
                ["issueNumber"] = issueNumber.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        dispatchTddRetry.SetDisplayText("TDD with Debug Retry");

        var tddRetrySuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddRetrySuccess", Name = "TDD Passed?" };
        tddRetrySuccess.SetDisplayText("TDD Passed?");

        // ================================================================
        // Step 8: Create PR
        // ================================================================
        var createPr = new DispatchWorkflow
        {
            Id = "DispatchCreatePR",
            Name = "Create PR",
            WorkflowDefinitionId = new("pull-request"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["baseBranch"] = baseBranch.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["issueTitle"] = issueTitle.Get(ctx),
                ["planJson"] = planJson.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(prResult)
        };
        createPr.SetDisplayText("Create PR");

        var extractPr = new SetVariable
        {
            Id = "ExtractPrData",
            Name = "Extract PR Data",
            Variable = prNumber,
            Value = new Input<object?>(ctx =>
            {
                var result = prResult.Get(ctx);
                if (result != null)
                {
                    if (result.TryGetValue("prUrl", out var u))
                        prUrl.Set(ctx, u?.ToString() ?? "");
                    if (result.TryGetValue("prNumber", out var n) && n is int num)
                        return (object)num;
                }
                return (object)0;
            })
        };
        extractPr.SetDisplayText("Extract PR Data");

        var prCreated = new FlowDecision(ctx => prNumber.Get(ctx) > 0)
        { Id = "PrCreated", Name = "PR Created?" };
        prCreated.SetDisplayText("PR Created?");

        // ================================================================
        // Step 9: CI Pipeline (dispatched to sub-workflow)
        // ================================================================
        var dispatchCiRetry = new DispatchWorkflow
        {
            Id = "DispatchCiWithDebugRetry",
            Name = "CI with Debug Retry",
            WorkflowDefinitionId = new("ci-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["skillLevel"] = 5,
                // NOTE: ciRetryCount is passed through to preserve existing behavior.
                // This means the counter persists across re-entries (review-fix, merge re-test).
                // This is likely a bug — re-entry should reset the counter.
                // Fix tracked as a separate ticket.
                ["ciRetryCount"] = ciRetryCount.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(testResult)
        };
        dispatchCiRetry.SetDisplayText("CI with Debug Retry");

        // Extract ciRetryCount from sub-workflow output to preserve across re-entries
        var extractCiRetryCount = new SetVariable
        {
            Id = "ExtractCiRetryCount",
            Name = "Extract CI Retry Count",
            Variable = ciRetryCount,
            Value = new Input<object?>(ctx =>
            {
                var result = testResult.Get(ctx);
                if (result != null && result.TryGetValue("ciRetryCount", out var c) && c is int count)
                    return (object)count;
                return (object)ciRetryCount.Get(ctx);
            })
        };
        extractCiRetryCount.SetDisplayText("Extract CI Retry Count");

        var ciRetryPassed = new FlowDecision(ctx =>
        {
            var result = testResult.Get(ctx);
            if (result != null && result.TryGetValue("passed", out var p))
                return p is true || p?.ToString() == "True";
            return false;
        })
        { Id = "CiRetryPassed", Name = "CI Passed?" };
        ciRetryPassed.SetDisplayText("CI Passed?");

        // ================================================================
        // Step 10: Review Fix Check
        // ================================================================
        var reviewFixCheck = new DispatchWorkflow
        {
            Id = "DispatchReviewFix",
            Name = "Review Fix Check",
            WorkflowDefinitionId = new("review-fix"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(reviewResult)
        };
        reviewFixCheck.SetDisplayText("Review Fix Check");

        var hasReviewComments = new FlowDecision(ctx =>
        {
            var result = reviewResult.Get(ctx);
            if (result != null && result.TryGetValue("hasComments", out var h))
                return h is true || h?.ToString() == "True";
            return false;
        })
        { Id = "HasReviewComments", Name = "Has Comments?" };
        hasReviewComments.SetDisplayText("Has Comments?");

        // ================================================================
        // Step 11: Merge Approval (bookmark)
        // ================================================================
        var mergeApproval = new DispatchWorkflow
        {
            Id = "DispatchMergeApproval",
            Name = "Merge Approval",
            WorkflowDefinitionId = new("merge-approval"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["issueNumber"] = issueNumber.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["prUrl"] = prUrl.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(mergeApprovalResult)
        };
        mergeApproval.SetDisplayText("Merge Approval");

        var mergeDecision = new FlowDecision(ctx =>
        {
            var result = mergeApprovalResult.Get(ctx);
            if (result != null && result.TryGetValue("decision", out var d))
                return d?.ToString() == "merge";
            return false;
        })
        { Id = "MergeDecision", Name = "Merge Approved?" };
        mergeDecision.SetDisplayText("Merge Approved?");

        var testDecision = new FlowDecision(ctx =>
        {
            var result = mergeApprovalResult.Get(ctx);
            if (result != null && result.TryGetValue("decision", out var d))
                return d?.ToString() == "test";
            return false;
        })
        { Id = "TestDecision", Name = "Run More Tests?" };
        testDecision.SetDisplayText("Run More Tests?");

        // ================================================================
        // Step 12: Merge PR
        // ================================================================
        var mergePr = new DispatchWorkflow
        {
            Id = "DispatchMergePR",
            Name = "Merge PR",
            WorkflowDefinitionId = new("merge-complete"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumber.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["branchName"] = branchName.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(mergeResult)
        };
        mergePr.SetDisplayText("Merge PR");

        var mergeSuccess = new FlowDecision(ctx =>
        {
            var result = mergeResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "MergeSuccess", Name = "Merged?" };
        mergeSuccess.SetDisplayText("Merged?");

        // ================================================================
        // Finish reason SetVariable nodes (one per exit path)
        // ================================================================
        var setReasonSuccess = new SetVariable
        {
            Id = "SetReasonSuccess",
            Name = "Set Reason: Success",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"success")
        };
        setReasonSuccess.SetDisplayText("Set Reason: Success");

        var setReasonNoIssues = new SetVariable
        {
            Id = "SetReasonNoIssues",
            Name = "Set Reason: No Issues",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"noIssues")
        };
        setReasonNoIssues.SetDisplayText("Set Reason: No Issues");

        var setReasonPlanRejected = new SetVariable
        {
            Id = "SetReasonPlanRejected",
            Name = "Set Reason: Plan Rejected",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"plan_rejected")
        };
        setReasonPlanRejected.SetDisplayText("Set Reason: Plan Rejected");

        var setReasonReviewRejected = new SetVariable
        {
            Id = "SetReasonReviewRejected",
            Name = "Set Reason: Review Rejected",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"review_rejected")
        };
        setReasonReviewRejected.SetDisplayText("Set Reason: Review Rejected");

        var setReasonError = new SetVariable
        {
            Id = "SetReasonError",
            Name = "Set Reason: Error",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"error")
        };
        setReasonError.SetDisplayText("Set Reason: Error");

        var setReasonTddFailed = new SetVariable
        {
            Id = "SetReasonTddFailed",
            Name = "Set Reason: TDD Failed",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"tddFailed")
        };
        setReasonTddFailed.SetDisplayText("Set Reason: TDD Failed");

        var setReasonCiFailed = new SetVariable
        {
            Id = "SetReasonCiFailed",
            Name = "Set Reason: CI Failed",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"ciFailed")
        };
        setReasonCiFailed.SetDisplayText("Set Reason: CI Failed");

        var setReasonMergeFailed = new SetVariable
        {
            Id = "SetReasonMergeFailed",
            Name = "Set Reason: Merge Failed",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"mergeFailed")
        };
        setReasonMergeFailed.SetDisplayText("Set Reason: Merge Failed");

        // ================================================================
        // Shared Finish Sequence — all exit paths converge here
        // ================================================================
        var sharedFinish = new Sequence
        {
            Id = "SharedFinishSequence",
            Name = "Shared Finish",
            Activities =
            {
                // Always set exitReason output
                WithLabel(new SetOutput { Id = "SetOutputExitReason", Name = "Set Exit Reason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)exitReason.Get(ctx)) }, "Set Exit Reason"),
                // Always set finishReason output (same value, explicit name for analytics)
                WithLabel(new SetOutput { Id = "SetOutputFinishReason", Name = "Set Finish Reason", OutputName = new("finishReason"), OutputValue = new(ctx => (object)exitReason.Get(ctx)) }, "Set Finish Reason"),
                // Derive success flag from reason
                WithLabel(new SetOutput { Id = "SetOutputSuccess", Name = "Set Success Flag", OutputName = new("success"), OutputValue = new(ctx => (object)(exitReason.Get(ctx) == "success")) }, "Set Success Flag"),
                // Conditionally set success-specific outputs (issueNumber, prNumber, mergeSha)
                WithLabel(new SetOutput { Id = "SetOutputIssueNumber", Name = "Set Issue Number", OutputName = new("issueNumber"), OutputValue = new(ctx => (object)issueNumber.Get(ctx)) }, "Set Issue Number"),
                WithLabel(new SetOutput { Id = "SetOutputPrNumber", Name = "Set PR Number", OutputName = new("prNumber"), OutputValue = new(ctx => (object)prNumber.Get(ctx)) }, "Set PR Number"),
                WithLabel(new SetOutput { Id = "SetOutputMergeSha", Name = "Set Merge SHA", OutputName = new("mergeSha"), OutputValue = new(ctx =>
                {
                    var r = mergeResult.Get(ctx);
                    return (object)(r != null && r.TryGetValue("mergeSha", out var ms) ? ms?.ToString() ?? "" : "");
                }) }, "Set Merge SHA")
            }
        };
        sharedFinish.SetDisplayText("Shared Finish");

        var finish = new Finish { Id = "Finish", Name = "Complete: Issue Cycle Done" };
        finish.SetDisplayText("Complete: Issue Cycle Done");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "SingleIssueCycleFlowchart",
            Name = "Single Issue Cycle Flowchart",
            Start = initConfig,
            Activities =
            {
                // Init
                initConfig,

                // Step 1: Issue Selection
                selectIssue, extractIssue, hasIssue,

                // Step 2: Context Gathering
                gatherContext, extractContext,

                // Step 3: Plan Generation
                generatePlan, extractPlan, planApproved,

                // Step 4: Branch Creation
                createBranch, extractBranch, branchCreated,

                // Steps 5-7: TDD Cycle (sub-workflow)
                dispatchTddRetry, tddRetrySuccess,

                // Step 8: Create PR
                createPr, extractPr, prCreated,

                // Step 9: CI Pipeline (sub-workflow)
                dispatchCiRetry, extractCiRetryCount, ciRetryPassed,

                // Step 10: Review Fix
                reviewFixCheck, hasReviewComments,

                // Step 11: Merge Approval
                mergeApproval, mergeDecision, testDecision,

                // Step 12: Merge PR
                mergePr, mergeSuccess,

                // Finish reason nodes + shared finish
                setReasonSuccess, setReasonNoIssues, setReasonPlanRejected, setReasonReviewRejected,
                setReasonError, setReasonTddFailed, setReasonCiFailed,
                setReasonMergeFailed, sharedFinish, finish
            },

            Connections =
            {
                // --- INIT ---
                Connect(initConfig, selectIssue),

                // --- Step 1: Issue Selection ---
                Connect(selectIssue, extractIssue),
                Connect(extractIssue, hasIssue),
                ConnectOutcome(hasIssue, "True", gatherContext),
                ConnectOutcome(hasIssue, "False", setReasonNoIssues),

                // --- Step 2: Context Gathering ---
                Connect(gatherContext, extractContext),
                Connect(extractContext, generatePlan),

                // --- Step 3: Plan Generation ---
                Connect(generatePlan, extractPlan),
                Connect(extractPlan, planApproved),
                ConnectOutcome(planApproved, "True", createBranch),
                ConnectOutcome(planApproved, "False", setReasonPlanRejected),

                // --- Step 4: Branch Creation ---
                Connect(createBranch, extractBranch),
                Connect(extractBranch, branchCreated),
                ConnectOutcome(branchCreated, "True", dispatchTddRetry),
                ConnectOutcome(branchCreated, "False", setReasonError),

                // --- Steps 5-7: TDD Cycle (sub-workflow) ---
                Connect(dispatchTddRetry, tddRetrySuccess),
                ConnectOutcome(tddRetrySuccess, "True", createPr),
                ConnectOutcome(tddRetrySuccess, "False", setReasonTddFailed),

                // --- Step 8: Create PR ---
                Connect(createPr, extractPr),
                Connect(extractPr, prCreated),
                ConnectOutcome(prCreated, "True", dispatchCiRetry),
                ConnectOutcome(prCreated, "False", setReasonError),

                // --- Step 9: CI Pipeline (sub-workflow) ---
                Connect(dispatchCiRetry, extractCiRetryCount),
                Connect(extractCiRetryCount, ciRetryPassed),
                ConnectOutcome(ciRetryPassed, "True", reviewFixCheck),
                ConnectOutcome(ciRetryPassed, "False", setReasonCiFailed),

                // --- Step 10: Review Fix Check ---
                Connect(reviewFixCheck, hasReviewComments),
                ConnectOutcome(hasReviewComments, "True", dispatchCiRetry), // re-run CI after fixes
                ConnectOutcome(hasReviewComments, "False", mergeApproval),

                // --- Step 11: Merge Approval ---
                Connect(mergeApproval, mergeDecision),
                ConnectOutcome(mergeDecision, "True", mergePr),
                ConnectOutcome(mergeDecision, "False", testDecision),
                ConnectOutcome(testDecision, "True", dispatchCiRetry), // re-run tests
                ConnectOutcome(testDecision, "False", setReasonReviewRejected),

                // --- Step 12: Merge PR ---
                Connect(mergePr, mergeSuccess),
                ConnectOutcome(mergeSuccess, "True", setReasonSuccess),
                ConnectOutcome(mergeSuccess, "False", setReasonMergeFailed),

                // --- All reason nodes lead to shared finish, then terminal ---
                Connect(setReasonSuccess, sharedFinish),
                Connect(setReasonNoIssues, sharedFinish),
                Connect(setReasonPlanRejected, sharedFinish),
                Connect(setReasonReviewRejected, sharedFinish),
                Connect(setReasonError, sharedFinish),
                Connect(setReasonTddFailed, sharedFinish),
                Connect(setReasonCiFailed, sharedFinish),
                Connect(setReasonMergeFailed, sharedFinish),
                Connect(sharedFinish, finish)
            }
        };
    }

    // ================================================================
    // Helper methods
    // ================================================================

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

}

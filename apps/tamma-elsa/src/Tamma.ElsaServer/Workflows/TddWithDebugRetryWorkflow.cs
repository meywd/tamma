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
/// TDD with Debug Retry sub-workflow — encapsulates the TDD cycle dispatch
/// with up to 3 debug retry iterations on failure.
///
/// Flow:
///   tddCycle -> tddSuccess?
///     YES -> finish(success=true)
///     NO  -> tddDebugGuard (< max, default 3)?
///       NO  -> finish(success=false, errorMessage)
///       YES -> incrementTddDebug -> dispatchTddDebugging -> (loop to tddCycle)
///
/// Inputs:  storyId, planJson, repositoryUrl, branchName, skillLevel
/// Outputs: success (bool), errorMessage (string)
/// </summary>
public class TddWithDebugRetryWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "TDD with Debug Retry";
        builder.DefinitionId = "tdd-with-debug-retry";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Dispatches TDD cycle with up to 3 debug retry iterations on failure";

        // ================================================================
        // Variables
        // ================================================================
        var storyId = builder.WithVariable<string>("StoryId", "");
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var repositoryUrl = builder.WithVariable<string>("RepositoryUrl", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var skillLevel = builder.WithVariable<int>("SkillLevel", 5);
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var tddDebugAttempt = builder.WithVariable<int>("TddDebugAttempt", 0);
        var maxRetries = builder.WithVariable<int>("MaxRetries", 3);

        // DispatchWorkflow result capture
        var tddResult = builder.WithVariable<IDictionary<string, object>?>();
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // INIT: Capture inputs
        // ================================================================
        var initInputs = new SetVariable
        {
            Id = "InitTddRetryInputs",
            Name = "Init Inputs",
            Variable = storyId,
            Value = new Input<object?>(ctx =>
            {
                var plan = ctx.GetInput<string>("planJson");
                if (!string.IsNullOrEmpty(plan)) planJson.Set(ctx, plan);
                var repo = ctx.GetInput<string>("repositoryUrl");
                if (!string.IsNullOrEmpty(repo)) repositoryUrl.Set(ctx, repo);
                var branch = ctx.GetInput<string>("branchName");
                if (!string.IsNullOrEmpty(branch)) branchName.Set(ctx, branch);
                var skill = ctx.GetInput<int>("skillLevel");
                if (skill > 0) skillLevel.Set(ctx, skill);
                var issue = ctx.GetInput<int>("issueNumber");
                if (issue > 0) issueNumber.Set(ctx, issue);
                var inputMaxRetries = ctx.GetInput<int?>("maxRetries");
                if (inputMaxRetries.HasValue) maxRetries.Set(ctx, inputMaxRetries.Value);
                return (object)(ctx.GetInput<string>("storyId") ?? "");
            })
        };
        initInputs.SetDisplayText("Init Inputs");

        // ================================================================
        // TDD Cycle (dispatch to existing tdd-cycle workflow)
        // ================================================================
        var tddCycle = new DispatchWorkflow
        {
            Id = "DispatchTddCycle",
            Name = "TDD Cycle",
            WorkflowDefinitionId = new("tdd-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = storyId.Get(ctx),
                ["taskDescription"] = planJson.Get(ctx),
                ["taskFiles"] = new List<string>(),
                ["repositoryUrl"] = repositoryUrl.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        tddCycle.SetDisplayText("TDD Cycle");

        // ================================================================
        // TDD Success check
        // ================================================================
        var tddSuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddSuccess", Name = "TDD Passed?" };
        tddSuccess.SetDisplayText("TDD Passed?");

        // ================================================================
        // TDD Debug retry guard (< 3 attempts)
        // ================================================================
        var tddDebugGuard = new FlowDecision(ctx => tddDebugAttempt.Get(ctx) < maxRetries.Get(ctx))
        { Id = "TddDebugGuard", Name = "TDD Debug < Max?" };
        tddDebugGuard.SetDisplayText("TDD Debug < Max?");

        // ================================================================
        // Increment TDD debug counter
        // ================================================================
        var incrementTddDebug = new SetVariable
        {
            Id = "IncrTddDebug",
            Name = "Increment TDD Debug",
            Variable = tddDebugAttempt,
            Value = new Input<object?>(ctx => (object)(tddDebugAttempt.Get(ctx) + 1))
        };
        incrementTddDebug.SetDisplayText("Increment TDD Debug");

        // ================================================================
        // Dispatch debugging for TDD failure
        // ================================================================
        var dispatchTddDebugging = new DispatchWorkflow
        {
            Id = "DispatchTddDebugging",
            Name = "Debug TDD Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = storyId.Get(ctx),
                ["debugContextMode"] = "TddFailure",
                ["errorOutput"] = GetTddErrorOutput(tddResult.Get(ctx)),
                ["repositoryUrl"] = repositoryUrl.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchTddDebugging.SetDisplayText("Debug TDD Failure");

        // ================================================================
        // Finish: Success outputs
        // ================================================================
        var finishSuccessOutputs = new Sequence
        {
            Id = "TddRetryFinishSuccess",
            Name = "Finish Success",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetTddRetrySuccess", Name = "Set Success", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Set Success"),
                WithLabel(new SetOutput { Id = "SetTddRetryErrorEmpty", Name = "Set Error Empty", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"") }, "Set Error Empty")
            }
        };
        finishSuccessOutputs.SetDisplayText("Finish Success");

        // ================================================================
        // Finish: Failure outputs
        // ================================================================
        var finishFailureOutputs = new Sequence
        {
            Id = "TddRetryFinishFailure",
            Name = "Finish Failure",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetTddRetryFailed", Name = "Set Failed", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Set Failed"),
                WithLabel(new SetOutput { Id = "SetTddRetryErrorMsg", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(ctx => (object)$"TDD debug retry limit reached ({maxRetries.Get(ctx)} attempts)") }, "Set Error Message")
            }
        };
        finishFailureOutputs.SetDisplayText("Finish Failure");

        var finish = new Finish { Id = "TddRetryFinish", Name = "Complete: TDD Retry Done" };
        finish.SetDisplayText("Complete: TDD Retry Done");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TddWithDebugRetryFlowchart",
            Name = "TDD with Debug Retry Flowchart",
            Start = initInputs,
            Activities =
            {
                initInputs,
                tddCycle, tddSuccess, tddDebugGuard,
                incrementTddDebug, dispatchTddDebugging,
                finishSuccessOutputs, finishFailureOutputs, finish
            },
            Connections =
            {
                // Init -> TDD Cycle
                Connect(initInputs, tddCycle),

                // TDD Cycle -> Success check
                Connect(tddCycle, tddSuccess),

                // TDD passed -> finish success
                ConnectOutcome(tddSuccess, "True", finishSuccessOutputs),

                // TDD failed -> debug guard
                ConnectOutcome(tddSuccess, "False", tddDebugGuard),

                // Debug retries remaining -> increment + debug
                ConnectOutcome(tddDebugGuard, "True", incrementTddDebug),
                ConnectOutcome(tddDebugGuard, "False", finishFailureOutputs),

                // Increment -> dispatch debugging -> loop back to TDD
                Connect(incrementTddDebug, dispatchTddDebugging),
                Connect(dispatchTddDebugging, tddCycle),

                // Both finish outputs -> terminal
                Connect(finishSuccessOutputs, finish),
                Connect(finishFailureOutputs, finish)
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

    private static string GetTddErrorOutput(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("errorMessage", out var err))
            return err?.ToString() ?? "TDD cycle failed";
        return "TDD cycle failed with unknown error";
    }
}

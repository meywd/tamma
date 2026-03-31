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
/// CI with Debug Retry sub-workflow — encapsulates the CI testing pipeline dispatch
/// with up to 3 debug retry iterations on failure.
///
/// Flow:
///   testingPipeline -> testsPassed?
///     YES -> finish(passed=true, ciRetryCount)
///     NO  -> ciRetryGuard (< 3)?
///       NO  -> finish(passed=false, errorMessage, ciRetryCount)
///       YES -> incrementCiRetry -> dispatchCiDebugging -> (loop to testingPipeline)
///
/// Inputs:  repository, branchName, issueNumber, skillLevel, ciRetryCount
/// Outputs: passed (bool), errorMessage (string), ciRetryCount (int)
///
/// NOTE: ciRetryCount is passed through to preserve existing behavior.
/// This means the counter persists across re-entries (review-fix, merge re-test).
/// This is likely a bug — re-entry should reset the counter.
/// Fix tracked as a separate ticket.
/// </summary>
public class CiWithDebugRetryWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "CI with Debug Retry";
        builder.DefinitionId = "ci-with-debug-retry";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Dispatches CI testing pipeline with up to 3 debug retry iterations on failure";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var skillLevel = builder.WithVariable<int>("SkillLevel", 5);
        // NOTE: ciRetryCount is passed through to preserve existing behavior.
        // This means the counter persists across re-entries (review-fix, merge re-test).
        // This is likely a bug — re-entry should reset the counter.
        // Fix tracked as a separate ticket.
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);

        // DispatchWorkflow result capture
        var testResult = builder.WithVariable<IDictionary<string, object>?>();
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // INIT: Capture inputs
        // ================================================================
        var initInputs = new SetVariable
        {
            Id = "InitCiRetryInputs",
            Name = "Init Inputs",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var branch = ctx.GetInput<string>("branchName");
                if (!string.IsNullOrEmpty(branch)) branchName.Set(ctx, branch);
                var issue = ctx.GetInput<int>("issueNumber");
                if (issue > 0) issueNumber.Set(ctx, issue);
                var skill = ctx.GetInput<int>("skillLevel");
                if (skill > 0) skillLevel.Set(ctx, skill);
                // Always set ciRetryCount — 0 is a valid value meaning "no retries yet"
                ciRetryCount.Set(ctx, ctx.GetInput<int>("ciRetryCount"));
                return (object)(ctx.GetInput<string>("repository") ?? "");
            })
        };
        initInputs.SetDisplayText("Init Inputs");

        // ================================================================
        // Testing Pipeline (dispatch to existing testing-pipeline workflow)
        // ================================================================
        var testingPipeline = new DispatchWorkflow
        {
            Id = "DispatchTestingPipeline",
            Name = "Testing Pipeline",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = Guid.NewGuid(),
                ["Repository"] = repository.Get(ctx),
                ["Branch"] = branchName.Get(ctx),
                ["SkillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(testResult)
        };
        testingPipeline.SetDisplayText("Testing Pipeline");

        // ================================================================
        // Tests Passed check
        // ================================================================
        var testsPassed = new FlowDecision(ctx =>
        {
            var result = testResult.Get(ctx);
            if (result != null && result.TryGetValue("passed", out var p))
                return p is true || p?.ToString() == "True";
            return false;
        })
        { Id = "TestsPassed", Name = "Tests Passed?" };
        testsPassed.SetDisplayText("Tests Passed?");

        // ================================================================
        // CI retry guard (< 3 retries)
        // ================================================================
        var ciRetryGuard = new FlowDecision(ctx => ciRetryCount.Get(ctx) < 3)
        { Id = "CiRetryGuard", Name = "CI Retries < 3?" };
        ciRetryGuard.SetDisplayText("CI Retries < 3?");

        // ================================================================
        // Increment CI retry counter
        // ================================================================
        var incrementCiRetry = new SetVariable
        {
            Id = "IncrCiRetry",
            Name = "Increment CI Retry",
            Variable = ciRetryCount,
            Value = new Input<object?>(ctx => (object)(ciRetryCount.Get(ctx) + 1))
        };
        incrementCiRetry.SetDisplayText("Increment CI Retry");

        // ================================================================
        // Dispatch debugging for CI failure
        // ================================================================
        var dispatchCiDebugging = new DispatchWorkflow
        {
            Id = "DispatchCiDebugging",
            Name = "Debug CI Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["debugContextMode"] = "RuntimeError",
                ["errorOutput"] = GetTestErrorOutput(testResult.Get(ctx)),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchCiDebugging.SetDisplayText("Debug CI Failure");

        // ================================================================
        // Finish: Pass outputs
        // ================================================================
        var finishPassOutputs = new Sequence
        {
            Id = "CiRetryFinishPass",
            Name = "Finish Pass",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetCiRetryPassed", Name = "Set Passed", OutputName = new("passed"), OutputValue = new(_ => (object)true) }, "Set Passed"),
                WithLabel(new SetOutput { Id = "SetCiRetryErrorEmpty", Name = "Set Error Empty", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"") }, "Set Error Empty"),
                WithLabel(new SetOutput { Id = "SetCiRetryCountPass", Name = "Set CI Retry Count", OutputName = new("ciRetryCount"), OutputValue = new(ctx => (object)ciRetryCount.Get(ctx)) }, "Set CI Retry Count")
            }
        };
        finishPassOutputs.SetDisplayText("Finish Pass");

        // ================================================================
        // Finish: Failure outputs
        // ================================================================
        var finishFailOutputs = new Sequence
        {
            Id = "CiRetryFinishFail",
            Name = "Finish Fail",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetCiRetryFailed", Name = "Set Failed", OutputName = new("passed"), OutputValue = new(_ => (object)false) }, "Set Failed"),
                WithLabel(new SetOutput { Id = "SetCiRetryErrorMsg", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"CI debug retry limit reached (3 attempts)") }, "Set Error Message"),
                WithLabel(new SetOutput { Id = "SetCiRetryCountFail", Name = "Set CI Retry Count", OutputName = new("ciRetryCount"), OutputValue = new(ctx => (object)ciRetryCount.Get(ctx)) }, "Set CI Retry Count")
            }
        };
        finishFailOutputs.SetDisplayText("Finish Fail");

        var finish = new Finish { Id = "CiRetryFinish", Name = "Complete: CI Retry Done" };
        finish.SetDisplayText("Complete: CI Retry Done");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "CiWithDebugRetryFlowchart",
            Name = "CI with Debug Retry Flowchart",
            Start = initInputs,
            Activities =
            {
                initInputs,
                testingPipeline, testsPassed, ciRetryGuard,
                incrementCiRetry, dispatchCiDebugging,
                finishPassOutputs, finishFailOutputs, finish
            },
            Connections =
            {
                // Init -> Testing Pipeline
                Connect(initInputs, testingPipeline),

                // Testing Pipeline -> Pass check
                Connect(testingPipeline, testsPassed),

                // Tests passed -> finish pass
                ConnectOutcome(testsPassed, "True", finishPassOutputs),

                // Tests failed -> retry guard
                ConnectOutcome(testsPassed, "False", ciRetryGuard),

                // Retries remaining -> increment + debug
                ConnectOutcome(ciRetryGuard, "True", incrementCiRetry),
                ConnectOutcome(ciRetryGuard, "False", finishFailOutputs),

                // Increment -> dispatch debugging -> loop back to testing
                Connect(incrementCiRetry, dispatchCiDebugging),
                Connect(dispatchCiDebugging, testingPipeline),

                // Both finish outputs -> terminal
                Connect(finishPassOutputs, finish),
                Connect(finishFailOutputs, finish)
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

    private static string GetTestErrorOutput(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("errorMessage", out var err))
            return err?.ToString() ?? "Testing pipeline failed";
        return "Testing pipeline failed with unknown error";
    }
}

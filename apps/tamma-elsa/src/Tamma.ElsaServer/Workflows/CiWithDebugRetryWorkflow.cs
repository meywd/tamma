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
///     NO  -> ciRetryGuard (< max, default 3)?
///       NO  -> finish(passed=false, errorMessage, ciRetryCount)
///       YES -> incrementCiRetry -> dispatchCiDebugging -> (loop to testingPipeline)
///
/// Inputs:  repository, branchName, issueNumber, skillLevel
/// Outputs: passed (bool), errorMessage (string), ciRetryCount (int)
///
/// ciRetryCount is always reset to 0 on entry so that each invocation
/// (including re-entries from review-fix or merge re-test) gets the full
/// retry budget.
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
        // ciRetryCount is always reset to 0 on workflow entry (see initInputs below)
        // so each invocation gets the full retry budget regardless of prior history.
        //
        // Story 12-5e investigation (2026-04-15): Verified that this variable is
        // declared at workflow-builder scope (persisted by Elsa across suspend/resume).
        // The initInputs activity resets it to 0 on every entry. SingleIssueCycleWorkflow
        // dispatches a fresh ci-with-debug-retry instance per CI check phase, so there
        // is no cross-invocation counter leakage. The originally reported bug (counter
        // persisting across re-entries) was stale — the reset logic was already correct.
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);
        var maxRetries = builder.WithVariable<int>("MaxRetries", 3);

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
                // Always reset ciRetryCount to 0 on entry so each invocation
                // (including re-entries from review-fix or merge re-test) gets full retry budget.
                // Verified correct per Story 12-5e investigation — no bug present.
                ciRetryCount.Set(ctx, 0);
                var inputMaxRetries = ctx.GetInput<int?>("maxRetries");
                if (inputMaxRetries.HasValue) maxRetries.Set(ctx, inputMaxRetries.Value);
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
        var ciRetryGuard = new FlowDecision(ctx => ciRetryCount.Get(ctx) < maxRetries.Get(ctx))
        { Id = "CiRetryGuard", Name = "CI Retries < Max?" };
        ciRetryGuard.SetDisplayText("CI Retries < Max?");

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
                ["skillLevel"] = skillLevel.Get(ctx),
                // Story 39-15 (D4) — capture the prior attempt's typed diagnosis id (additive
                // debugging output) so a re-diagnosis supersedes the previous one.
                ["priorDiagnosisDocumentId"] = ReadDiagnosisDocumentId(debugResult.Get(ctx)),
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
                WithLabel(new SetOutput { Id = "SetCiRetryErrorMsg", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(ctx => (object)$"CI debug retry limit reached ({maxRetries.Get(ctx)} attempts)") }, "Set Error Message"),
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

    /// <summary>
    /// Story 39-15 (D4) — read the additive <c>diagnosisDocumentId</c> from a prior debugging
    /// dispatch result. Empty on the first attempt / a null result. Pure; exposed for testing.
    /// </summary>
    public static string ReadDiagnosisDocumentId(IDictionary<string, object>? debugResult)
        => debugResult != null && debugResult.TryGetValue("diagnosisDocumentId", out var d)
            ? d?.ToString() ?? ""
            : "";
}

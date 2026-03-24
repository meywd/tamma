using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.TDD.Models;

namespace Tamma.Activities.TDD;

/// <summary>
/// ELSA activity that guards the RED phase of TDD.
/// Verifies that newly written tests FAIL (which is correct TDD behavior).
/// If tests pass, it means they don't test anything meaningful — triggers rewrite.
/// Produces "TestsFail" outcome (correct) or "TestsPass" outcome (needs rewrite).
/// </summary>
[Activity(
    "Tamma.TDD",
    "Check Tests Fail",
    "Guard: verify tests fail in RED phase (correct TDD behavior)",
    Kind = ActivityKind.Task
)]
[FlowNode("TestsFail", "TestsPass")]
public class CheckTestsFailActivity : Activity
{
    private readonly ILogger<CheckTestsFailActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Test run result to evaluate</summary>
    [Input(Description = "Test run result from running the new tests")]
    public Input<TestRunResult> TestRunResult { get; set; } = default!;

    /// <summary>Current rewrite attempt number (0-based)</summary>
    [Input(Description = "Current rewrite attempt number", DefaultValue = 0)]
    public Input<int> RewriteAttempt { get; set; } = new(0);

    /// <summary>Maximum allowed rewrite attempts</summary>
    [Input(Description = "Maximum rewrite attempts", DefaultValue = 2)]
    public Input<int> MaxRewriteAttempts { get; set; } = new(2);

    /// <summary>Whether tests correctly failed</summary>
    [Output(Description = "Whether tests correctly failed (true = correct TDD behavior)")]
    public Output<bool> TestsCorrectlyFail { get; set; } = default!;

    /// <summary>Whether max rewrites have been exhausted</summary>
    [Output(Description = "Whether max rewrite attempts have been exhausted")]
    public Output<bool> MaxRewritesExhausted { get; set; } = default!;

    [JsonConstructor]
    public CheckTestsFailActivity() { }

    public CheckTestsFailActivity(ILogger<CheckTestsFailActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var testResult = TestRunResult.Get(context);
        var rewriteAttempt = RewriteAttempt.Get(context);
        var maxRewrites = MaxRewriteAttempts.Get(context);

        var testsFailed = !testResult.AllPassed && testResult.FailedTests > 0;

        if (testsFailed)
        {
            // Correct TDD behavior: tests fail because implementation doesn't exist yet
            _logger?.LogInformation(
                "TDD RED phase guard PASSED: {FailedCount}/{TotalCount} tests correctly fail for session {SessionId}",
                testResult.FailedTests, testResult.TotalTests, sessionId);

            TestsCorrectlyFail.Set(context, true);
            MaxRewritesExhausted.Set(context, false);
            await context.CompleteActivityWithOutcomesAsync("TestsFail");
        }
        else
        {
            // Tests pass = bad tests (they don't actually test new behavior)
            var exhausted = rewriteAttempt >= maxRewrites;

            if (exhausted)
            {
                _logger?.LogWarning(
                    "TDD RED phase: Tests still pass after {Attempts} rewrite attempts for session {SessionId}. " +
                    "Task may be pre-implemented. Proceeding with warning.",
                    rewriteAttempt, sessionId);
            }
            else
            {
                _logger?.LogWarning(
                    "TDD RED phase guard FAILED: All {TotalCount} tests pass for session {SessionId}. " +
                    "Tests don't test new behavior. Rewrite attempt {Attempt}/{MaxAttempts}.",
                    testResult.TotalTests, sessionId, rewriteAttempt + 1, maxRewrites);
            }

            TestsCorrectlyFail.Set(context, false);
            MaxRewritesExhausted.Set(context, exhausted);
            await context.CompleteActivityWithOutcomesAsync("TestsPass");
        }
    }
}

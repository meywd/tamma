using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Activities.Debug.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Debug;

/// <summary>
/// Collects test results: which tests fail, which pass, and coverage gaps.
/// For TddFailure mode, this is the primary context. Part of the parallel Fork.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Collect Test Results",
    "Gather test pass/fail results and coverage gaps",
    Kind = ActivityKind.Task
)]
public class CollectTestResultsActivity : CodeActivity<TestResultsContext>
{
    private readonly ILogger<CollectTestResultsActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    /// <summary>Repository URL</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Branch name")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    /// <summary>Pre-existing error output (may contain test results)</summary>
    [Input(Description = "Error output that may contain test results")]
    public Input<string> ErrorOutput { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public CollectTestResultsActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: the CI test summary is read through the CI mediation endpoint
    /// (<c>POST /api/v1/ci/{owner}/{repo}/test-runs</c>) via <see cref="TammaApiClient"/>.
    /// </summary>
    public CollectTestResultsActivity(
        ILogger<CollectTestResultsActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repositoryUrl = RepositoryUrl.Get(context);
        var branchName = BranchName.Get(context);
        var mode = DebugContextMode.Get(context);
        var errorOutput = ErrorOutput.Get(context) ?? string.Empty;
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Collecting test results for {Repository}:{Branch} in mode {Mode}",
            repositoryUrl, branchName, mode);

        try
        {
            var result = new TestResultsContext();

            if (!string.IsNullOrEmpty(repositoryUrl))
            {
                var testResponse = await apiClient.TriggerTestsAsync(
                    repositoryUrl,
                    new CiTriggerTestsRequest { Branch = branchName, CorrelationId = correlationId },
                    tenantId, ct);
                if (testResponse is null || !testResponse.Success)
                    throw new InvalidOperationException(
                        testResponse?.FailureReason ?? "ci mediation endpoint unavailable");
                var testRun = GitMediationMapping.ToTestRun(testResponse.TestRun);

                result.TotalTests = testRun.TotalTests;
                result.PassingTests = testRun.PassedTests;
                result.FailingTests = testRun.FailedTests;

                result.FailingTestDetails = testRun.FailedTestDetails
                    .Select(t =>
                    {
                        var detail = $"FAIL: {t.TestName}";
                        if (!string.IsNullOrEmpty(t.ErrorMessage))
                            detail += $"\n  Error: {t.ErrorMessage}";
                        if (!string.IsNullOrEmpty(t.StackTrace))
                            detail += $"\n  Stack: {t.StackTrace}";
                        return detail;
                    })
                    .ToList();

                // For TddFailure mode, provide extra detail
                if (mode == "TddFailure" && result.FailingTests > 0)
                {
                    _logger?.LogInformation(
                        "TddFailure mode: {FailCount}/{TotalCount} tests failing",
                        result.FailingTests, result.TotalTests);
                }
            }

            // Also parse test results from error output if present
            if (!string.IsNullOrWhiteSpace(errorOutput) && result.FailingTestDetails.Count == 0)
            {
                var parsedFailures = ParseTestFailuresFromOutput(errorOutput);
                if (parsedFailures.Count > 0)
                {
                    result.FailingTestDetails = parsedFailures;
                    result.FailingTests = parsedFailures.Count;
                }
            }

            _logger?.LogInformation(
                "Collected test results: {Pass}/{Total} passing, {Fail} failing",
                result.PassingTests, result.TotalTests, result.FailingTests);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to collect test results");
            context.SetResult(new TestResultsContext
            {
                FailingTestDetails = new List<string>
                {
                    $"Test result collection failed: {ex.Message}"
                }
            });
        }
    }

    private static List<string> ParseTestFailuresFromOutput(string output)
    {
        var failures = new List<string>();
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Contains("test", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("spec", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("assert", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(trimmed);
            }
        }

        return failures;
    }
}

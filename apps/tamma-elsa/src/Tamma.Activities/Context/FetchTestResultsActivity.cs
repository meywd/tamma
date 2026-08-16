using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Activities.Context.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Context;

/// <summary>
/// Fetches test results from the CI/CD pipeline for the story branch.
/// Returns pass/fail counts, coverage percentage, and details of failing tests.
/// </summary>
[Activity(
    "Tamma.Context",
    "Fetch Test Results",
    "Retrieve test results and coverage from CI/CD for the story branch",
    Kind = ActivityKind.Task
)]
public class FetchTestResultsActivity : CodeActivity<TestResultsData>
{
    private readonly ILogger<FetchTestResultsActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    /// <summary>Repository URL (e.g., owner/repo)</summary>
    [Input(Description = "Repository URL or identifier")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Story ID used to derive the branch name</summary>
    [Input(Description = "Story ID for branch naming")]
    public Input<string> StoryId { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public FetchTestResultsActivity()
    {
    }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: the CI test summary is read through the CI mediation endpoint
    /// (<c>POST /api/v1/ci/{owner}/{repo}/test-runs</c>) via <see cref="TammaApiClient"/>.
    /// </summary>
    public FetchTestResultsActivity(
        ILogger<FetchTestResultsActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repositoryUrl = RepositoryUrl.Get(context);
        var storyId = StoryId.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Fetching test results for story {StoryId} from {Repo}",
            storyId, repositoryUrl);

        try
        {
            if (string.IsNullOrEmpty(repositoryUrl))
            {
                context.SetResult(new TestResultsData
                {
                    Success = false,
                    ErrorMessage = "Repository URL is empty"
                });
                return;
            }

            var branchName = $"feature/{storyId}";
            var testResponse = await apiClient.TriggerTestsAsync(
                repositoryUrl,
                new CiTriggerTestsRequest { Branch = branchName, CorrelationId = correlationId },
                tenantId, ct);
            if (testResponse is null || !testResponse.Success)
                throw new InvalidOperationException(
                    testResponse?.FailureReason ?? "ci mediation endpoint unavailable");
            var testResults = GitMediationMapping.ToTestRun(testResponse.TestRun);

            context.SetResult(new TestResultsData
            {
                TotalTests = testResults.TotalTests,
                PassingTests = testResults.PassedTests,
                FailingTests = testResults.FailedTests,
                SkippedTests = testResults.SkippedTests,
                CoveragePercentage = testResults.CoveragePercentage ?? 0,
                FailingTestDetails = testResults.FailedTestDetails
                    .Select(t => new FailingTestDetail
                    {
                        TestName = t.TestName,
                        ErrorMessage = t.ErrorMessage ?? "No error message",
                        StackTrace = t.StackTrace
                    })
                    .ToList(),
                Success = true
            });

            _logger?.LogInformation(
                "Test results for story {StoryId}: {Passed}/{Total} passing, {Coverage:F1}% coverage",
                storyId, testResults.PassedTests, testResults.TotalTests,
                testResults.CoveragePercentage ?? 0);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to fetch test results for story {StoryId}", storyId);
            context.SetResult(new TestResultsData
            {
                Success = false,
                ErrorMessage = $"Failed to fetch test results: {ex.Message}"
            });
        }
    }
}

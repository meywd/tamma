using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Context.Models;
using Tamma.Core.Interfaces;

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
    private readonly IIntegrationService? _integrationService;

    /// <summary>Repository URL (e.g., owner/repo)</summary>
    [Input(Description = "Repository URL or identifier")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Story ID used to derive the branch name</summary>
    [Input(Description = "Story ID for branch naming")]
    public Input<string> StoryId { get; set; } = default!;

    [JsonConstructor]
    public FetchTestResultsActivity()
    {
    }

    public FetchTestResultsActivity(
        ILogger<FetchTestResultsActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repositoryUrl = RepositoryUrl.Get(context);
        var storyId = StoryId.Get(context);

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
            var testResults = await _integrationService!.TriggerTestsAsync(
                repositoryUrl, branchName);

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

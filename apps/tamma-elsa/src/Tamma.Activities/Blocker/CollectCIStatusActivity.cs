using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Blocker.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Collects CI status signals: build/test results and failure history.
/// Designed for parallel execution within the Blocker Diagnosis workflow's Fork/Join.
/// Failed collection does not block — returns a signal with CollectionSucceeded=false.
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Collect CI Status",
    "Check build and test results for blocker diagnosis",
    Kind = ActivityKind.Task
)]
public class CollectCIStatusActivity : CodeActivity<CIStatusSignal>
{
    private readonly ILogger<CollectCIStatusActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch name to check</summary>
    [Input(Description = "Branch name to check")]
    public Input<string> BranchName { get; set; } = default!;

    [JsonConstructor]
    public CollectCIStatusActivity() { }

    public CollectCIStatusActivity(
        ILogger<CollectCIStatusActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var branchName = BranchName.Get(context);

        _logger?.LogInformation(
            "Collecting CI status signals for {Repository}/{Branch}",
            repository, branchName);

        var signal = new CIStatusSignal();

        try
        {
            var buildStatus = await _integrationService!.GetBuildStatusAsync(repository, branchName);
            signal.BuildStatus = buildStatus.Status;
            signal.BuildError = buildStatus.Error;

            var testResults = await _integrationService.TriggerTestsAsync(repository, branchName);
            signal.TotalTests = testResults.TotalTests;
            signal.PassedTests = testResults.PassedTests;
            signal.FailedTests = testResults.FailedTests;
            signal.FailingTestNames = testResults.FailedTestDetails
                .Select(t => t.TestName)
                .ToList();
            signal.CoveragePercentage = testResults.CoveragePercentage;
            signal.CollectionSucceeded = true;

            _logger?.LogInformation(
                "CI status collected: Build={BuildStatus}, Tests={Passed}/{Total}",
                signal.BuildStatus, signal.PassedTests, signal.TotalTests);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to collect CI status signals — continuing with partial data");
            signal.CollectionSucceeded = false;
            signal.Error = ex.Message;
        }

        context.SetResult(signal);
    }
}

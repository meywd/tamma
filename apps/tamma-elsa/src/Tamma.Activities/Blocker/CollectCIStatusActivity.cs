using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Activities.Blocker.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

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
    private readonly TammaApiClient? _apiClient;
    private readonly IConfiguration? _configuration;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch name to check</summary>
    [Input(Description = "Branch name to check")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public CollectCIStatusActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: build status + the CI test summary are read through the CI
    /// mediation endpoints (<c>GET /api/v1/ci/{owner}/{repo}/build-status</c> and
    /// <c>POST .../test-runs</c>) via <see cref="TammaApiClient"/>, where the per-tenant
    /// token lives.
    /// </summary>
    public CollectCIStatusActivity(
        ILogger<CollectCIStatusActivity> logger,
        TammaApiClient apiClient,
        IConfiguration configuration)
    {
        _logger = logger;
        _apiClient = apiClient;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var branchName = BranchName.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Collecting CI status signals for {Repository}/{Branch}",
            repository, branchName);

        var signal = new CIStatusSignal();

        try
        {
            // AC3: cap the collector at the configured deadline (default 15s) so a slow
            // CI / test trigger cannot block the parallel signal join.
            var completedInTime = await BlockerSignalTimeout.RunAsync(_configuration, async () =>
            {
                var buildStatusResponse = await apiClient.GetBuildStatusAsync(
                    repository, branchName, correlationId, tenantId, ct);
                if (buildStatusResponse is null || !buildStatusResponse.Success)
                    throw new InvalidOperationException(
                        buildStatusResponse?.FailureReason ?? "ci mediation endpoint unavailable");
                var buildStatus = GitMediationMapping.ToBuildStatus(buildStatusResponse.BuildStatus);
                signal.BuildStatus = buildStatus.Status;
                signal.BuildError = buildStatus.Error;

                var testResponse = await apiClient.TriggerTestsAsync(
                    repository,
                    new CiTriggerTestsRequest { Branch = branchName, CorrelationId = correlationId },
                    tenantId, ct);
                if (testResponse is null || !testResponse.Success)
                    throw new InvalidOperationException(
                        testResponse?.FailureReason ?? "ci mediation endpoint unavailable");
                var testResults = GitMediationMapping.ToTestRun(testResponse.TestRun);
                signal.TotalTests = testResults.TotalTests;
                signal.PassedTests = testResults.PassedTests;
                signal.FailedTests = testResults.FailedTests;
                signal.FailingTestNames = testResults.FailedTestDetails
                    .Select(t => t.TestName)
                    .ToList();
                signal.CoveragePercentage = testResults.CoveragePercentage;
            });

            if (completedInTime)
            {
                signal.CollectionSucceeded = true;
                _logger?.LogInformation(
                    "CI status collected: Build={BuildStatus}, Tests={Passed}/{Total}",
                    signal.BuildStatus, signal.PassedTests, signal.TotalTests);
            }
            else
            {
                signal.CollectionSucceeded = false;
                signal.Error = "CI status collection timed out";
                _logger?.LogWarning("CI status collection timed out — continuing with partial data");
            }
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

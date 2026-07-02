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
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Collects inactivity signals: time since last meaningful activity.
/// Designed for parallel execution within the Blocker Diagnosis workflow's Fork/Join.
/// Failed collection does not block — returns a signal with CollectionSucceeded=false.
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Collect Inactivity",
    "Measure time since last meaningful activity for blocker diagnosis",
    Kind = ActivityKind.Task
)]
public class CollectInactivityActivity : CodeActivity<InactivitySignal>
{
    private readonly ILogger<CollectInactivityActivity>? _logger;
    private readonly TammaApiClient? _apiClient;
    private readonly IConfiguration? _configuration;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch name to check</summary>
    [Input(Description = "Branch name to check")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Threshold in minutes for considering inactivity</summary>
    [Input(Description = "Inactivity threshold in minutes", DefaultValue = 30)]
    public Input<int> InactivityThresholdMinutes { get; set; } = new(30);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public CollectInactivityActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: recent commits (an activity proxy) are read through
    /// <c>GET /api/v1/git/{owner}/{repo}/commits</c> via <see cref="TammaApiClient"/>.
    /// </summary>
    public CollectInactivityActivity(
        ILogger<CollectInactivityActivity> logger,
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
        var thresholdMinutes = InactivityThresholdMinutes.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));
        var correlationId = context.WorkflowExecutionContext.Id;

        _logger?.LogInformation(
            "Collecting inactivity signals for {Repository}/{Branch}",
            repository, branchName);

        var signal = new InactivitySignal();
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();

        try
        {
            // AC3: cap the collector at the configured deadline (default 15s) so a slow
            // commits call cannot block the parallel signal join.
            var completedInTime = await BlockerSignalTimeout.RunAsync(_configuration, async () =>
            {
                // Use recent commits as a proxy for activity
                var since = DateTime.UtcNow.AddHours(-24);
                var commitsResponse = await apiClient.GetCommitsAsync(
                    repository, branchName, since, correlationId, tenantId, context.CancellationToken);
                if (commitsResponse is null || !commitsResponse.Success)
                    throw new InvalidOperationException(
                        commitsResponse?.FailureReason ?? "git mediation endpoint unavailable");
                var commits = GitMediationMapping.ToCommits(commitsResponse.Commits);

                if (commits.Any())
                {
                    var lastCommitTime = commits.Max(c => c.Timestamp);
                    signal.LastActivityTime = lastCommitTime;
                    signal.LastActivityType = "commit";
                    signal.TimeSinceLastActivity = DateTime.UtcNow - lastCommitTime;
                }
                else
                {
                    signal.TimeSinceLastActivity = TimeSpan.FromHours(24);
                    signal.LastActivityType = "none";
                }

                signal.IsInactive = signal.TimeSinceLastActivity.TotalMinutes > thresholdMinutes;
            });

            if (completedInTime)
            {
                signal.CollectionSucceeded = true;
                _logger?.LogInformation(
                    "Inactivity collected: TimeSince={TimeSince}min, IsInactive={IsInactive}",
                    signal.TimeSinceLastActivity.TotalMinutes, signal.IsInactive);
            }
            else
            {
                signal.CollectionSucceeded = false;
                signal.Error = "Inactivity collection timed out";
                _logger?.LogWarning("Inactivity collection timed out — continuing with partial data");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to collect inactivity signals — continuing with partial data");
            signal.CollectionSucceeded = false;
            signal.Error = ex.Message;
        }

        context.SetResult(signal);
    }
}

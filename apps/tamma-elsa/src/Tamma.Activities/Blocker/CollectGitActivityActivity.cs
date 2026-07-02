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
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Collects git activity signals: commit frequency, file changes, time since last commit.
/// Designed for parallel execution within the Blocker Diagnosis workflow's Fork/Join.
/// Failed collection does not block — returns a signal with CollectionSucceeded=false.
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Collect Git Activity",
    "Check commit frequency and file changes for blocker diagnosis",
    Kind = ActivityKind.Task
)]
public class CollectGitActivityActivity : CodeActivity<GitActivitySignal>
{
    private readonly ILogger<CollectGitActivityActivity>? _logger;
    private readonly TammaApiClient? _apiClient;
    private readonly IConfiguration? _configuration;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch name to check</summary>
    [Input(Description = "Branch name to check")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Lookback period in hours for recent commits</summary>
    [Input(Description = "Lookback period in hours", DefaultValue = 24)]
    public Input<int> LookbackHours { get; set; } = new(24);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public CollectGitActivityActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: commits + file changes are read through
    /// <c>GET /api/v1/git/{owner}/{repo}/commits</c> and <c>/file-changes</c> via
    /// <see cref="TammaApiClient"/>, where the per-tenant token lives.
    /// </summary>
    public CollectGitActivityActivity(
        ILogger<CollectGitActivityActivity> logger,
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
        var lookbackHours = LookbackHours.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));
        var correlationId = context.WorkflowExecutionContext.Id;

        _logger?.LogInformation(
            "Collecting git activity signals for {Repository}/{Branch}",
            repository, branchName);

        var signal = new GitActivitySignal();
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();

        try
        {
            // AC3: cap the collector at the configured deadline (default 15s) so a slow
            // GitHub call cannot block the parallel signal join.
            var completedInTime = await BlockerSignalTimeout.RunAsync(_configuration, async () =>
            {
                var since = DateTime.UtcNow.AddHours(-lookbackHours);
                var commitsResponse = await apiClient.GetCommitsAsync(
                    repository, branchName, since, correlationId, tenantId, context.CancellationToken);
                if (commitsResponse is null || !commitsResponse.Success)
                    throw new InvalidOperationException(
                        commitsResponse?.FailureReason ?? "git mediation endpoint unavailable");
                var commits = GitMediationMapping.ToCommits(commitsResponse.Commits);

                signal.RecentCommitCount = commits.Count;
                signal.LastCommitTime = commits.Any() ? commits.Max(c => c.Timestamp) : null;
                signal.TimeSinceLastCommit = signal.LastCommitTime.HasValue
                    ? DateTime.UtcNow - signal.LastCommitTime.Value
                    : TimeSpan.FromHours(lookbackHours);

                var fileChangesResponse = await apiClient.GetFileChangesAsync(
                    repository, branchName, correlationId, tenantId, context.CancellationToken);
                if (fileChangesResponse is null || !fileChangesResponse.Success)
                    throw new InvalidOperationException(
                        fileChangesResponse?.FailureReason ?? "git mediation endpoint unavailable");
                var fileChanges = GitMediationMapping.ToFileChanges(fileChangesResponse.FileChanges);
                signal.FilesChanged = fileChanges.Count;
                signal.TotalAdditions = fileChanges.Sum(f => f.Additions);
                signal.TotalDeletions = fileChanges.Sum(f => f.Deletions);
                signal.ChangedFiles = fileChanges.Select(f => f.FilePath).ToList();
            });

            if (completedInTime)
            {
                signal.CollectionSucceeded = true;
                _logger?.LogInformation(
                    "Git activity collected: {CommitCount} commits, {FilesChanged} files changed",
                    signal.RecentCommitCount, signal.FilesChanged);
            }
            else
            {
                signal.CollectionSucceeded = false;
                signal.Error = "Git activity collection timed out";
                _logger?.LogWarning("Git activity collection timed out — continuing with partial data");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to collect git activity signals — continuing with partial data");
            signal.CollectionSucceeded = false;
            signal.Error = ex.Message;
        }

        context.SetResult(signal);
    }
}

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
    private readonly IIntegrationService? _integrationService;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch name to check</summary>
    [Input(Description = "Branch name to check")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Lookback period in hours for recent commits</summary>
    [Input(Description = "Lookback period in hours", DefaultValue = 24)]
    public Input<int> LookbackHours { get; set; } = new(24);

    [JsonConstructor]
    public CollectGitActivityActivity() { }

    public CollectGitActivityActivity(
        ILogger<CollectGitActivityActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var branchName = BranchName.Get(context);
        var lookbackHours = LookbackHours.Get(context);

        _logger?.LogInformation(
            "Collecting git activity signals for {Repository}/{Branch}",
            repository, branchName);

        var signal = new GitActivitySignal();

        try
        {
            var since = DateTime.UtcNow.AddHours(-lookbackHours);
            var commits = await _integrationService!.GetGitHubCommitsAsync(repository, branchName, since);

            signal.RecentCommitCount = commits.Count;
            signal.LastCommitTime = commits.Any() ? commits.Max(c => c.Timestamp) : null;
            signal.TimeSinceLastCommit = signal.LastCommitTime.HasValue
                ? DateTime.UtcNow - signal.LastCommitTime.Value
                : TimeSpan.FromHours(lookbackHours);

            var fileChanges = await _integrationService.GetGitHubFileChangesAsync(repository, branchName);
            signal.FilesChanged = fileChanges.Count;
            signal.TotalAdditions = fileChanges.Sum(f => f.Additions);
            signal.TotalDeletions = fileChanges.Sum(f => f.Deletions);
            signal.ChangedFiles = fileChanges.Select(f => f.FilePath).ToList();
            signal.CollectionSucceeded = true;

            _logger?.LogInformation(
                "Git activity collected: {CommitCount} commits, {FilesChanged} files changed",
                signal.RecentCommitCount, signal.FilesChanged);
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

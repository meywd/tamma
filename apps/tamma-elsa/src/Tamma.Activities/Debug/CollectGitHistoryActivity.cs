using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Debug;

/// <summary>
/// Collects git history context: recent commits, diffs, and blame information.
/// For RuntimeError mode, emphasizes recent changes. Part of the parallel Fork.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Collect Git History",
    "Gather recent commits, diffs, and blame info for debugging",
    Kind = ActivityKind.Task
)]
public class CollectGitHistoryActivity : CodeActivity<GitHistoryContext>
{
    private readonly ILogger<CollectGitHistoryActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Repository URL</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Branch name")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    [JsonConstructor]
    public CollectGitHistoryActivity() { }

    public CollectGitHistoryActivity(
        ILogger<CollectGitHistoryActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repositoryUrl = RepositoryUrl.Get(context);
        var branchName = BranchName.Get(context);
        var mode = DebugContextMode.Get(context);

        _logger?.LogInformation(
            "Collecting git history for {Repository}:{Branch} in mode {Mode}",
            repositoryUrl, branchName, mode);

        try
        {
            var result = new GitHistoryContext();

            if (_integrationService != null && !string.IsNullOrEmpty(repositoryUrl))
            {
                // For RuntimeError, look at a wider time range
                var since = mode == "RuntimeError"
                    ? DateTime.UtcNow.AddDays(-3)
                    : DateTime.UtcNow.AddDays(-1);

                var commits = await _integrationService.GetGitHubCommitsAsync(
                    repositoryUrl, branchName, since);

                result.RecentCommits = commits
                    .OrderByDescending(c => c.Timestamp)
                    .Take(20)
                    .Select(c => $"[{c.Sha[..7]}] {c.Timestamp:yyyy-MM-dd HH:mm} {c.Author}: {c.Message}")
                    .ToList();

                // Build diff summary from commits
                var totalAdditions = commits.Sum(c => c.Additions);
                var totalDeletions = commits.Sum(c => c.Deletions);
                var allFiles = commits.SelectMany(c => c.Files).Distinct().ToList();

                result.DiffSummary = $"{commits.Count} commits, " +
                    $"+{totalAdditions}/-{totalDeletions} lines, " +
                    $"{allFiles.Count} files changed: {string.Join(", ", allFiles.Take(10))}";

                // Blame info is simulated — in production would call git blame API
                result.BlameInfo = commits
                    .Take(5)
                    .Select(c => $"{c.Author} last modified {string.Join(", ", c.Files.Take(3))} " +
                        $"in commit {c.Sha[..7]}")
                    .ToList();
            }

            _logger?.LogInformation(
                "Collected {CommitCount} commits in git history",
                result.RecentCommits.Count);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to collect git history");
            context.SetResult(new GitHistoryContext
            {
                DiffSummary = $"Git history collection failed: {ex.Message}"
            });
        }
    }
}

using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Activities.Debug.Models;
using Tamma.Activities.LlmCall;

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

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public CollectGitHistoryActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: recent commits are read through the git mediation endpoint
    /// (<c>GET /api/v1/git/{owner}/{repo}/commits</c>) via <see cref="TammaApiClient"/>.
    /// </summary>
    public CollectGitHistoryActivity(
        ILogger<CollectGitHistoryActivity> logger,
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
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Collecting git history for {Repository}:{Branch} in mode {Mode}",
            repositoryUrl, branchName, mode);

        try
        {
            var result = new GitHistoryContext();

            if (!string.IsNullOrEmpty(repositoryUrl))
            {
                // For RuntimeError, look at a wider time range
                var since = mode == "RuntimeError"
                    ? DateTime.UtcNow.AddDays(-3)
                    : DateTime.UtcNow.AddDays(-1);

                var commitsResponse = await apiClient.GetCommitsAsync(
                    repositoryUrl, branchName, since, correlationId, tenantId, ct);
                if (commitsResponse is null || !commitsResponse.Success)
                    throw new InvalidOperationException(
                        commitsResponse?.FailureReason ?? "git mediation endpoint unavailable");
                var commits = GitMediationMapping.ToCommits(commitsResponse.Commits);

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

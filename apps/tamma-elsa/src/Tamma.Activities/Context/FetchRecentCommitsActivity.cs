using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Activities.Context.Models;
using Tamma.Activities.LlmCall;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Context;

/// <summary>
/// Fetches recent commits from the repository branch related to the story.
/// Returns commit metadata including changed files, which feeds into file relevance scoring.
/// </summary>
[Activity(
    "Tamma.Context",
    "Fetch Recent Commits",
    "Retrieve recent commits from the story branch",
    Kind = ActivityKind.Task
)]
public class FetchRecentCommitsActivity : CodeActivity<RecentCommitsResult>
{
    private readonly ILogger<FetchRecentCommitsActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    /// <summary>Repository URL (e.g., owner/repo)</summary>
    [Input(Description = "Repository URL or identifier")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Story ID used to derive the branch name (feature/{storyId})</summary>
    [Input(Description = "Story ID for branch naming")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Number of days to look back for commits</summary>
    [Input(Description = "Days to look back", DefaultValue = 7)]
    public Input<int> DaysBack { get; set; } = new(7);

    /// <summary>Maximum number of commits to return</summary>
    [Input(Description = "Maximum commits to return", DefaultValue = 10)]
    public Input<int> MaxCommits { get; set; } = new(10);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public FetchRecentCommitsActivity()
    {
    }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: recent commits are read through
    /// <c>GET /api/v1/git/{owner}/{repo}/commits</c> via <see cref="TammaApiClient"/>.
    /// </summary>
    public FetchRecentCommitsActivity(
        ILogger<FetchRecentCommitsActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repositoryUrl = RepositoryUrl.Get(context);
        var storyId = StoryId.Get(context);
        var daysBack = DaysBack.Get(context);
        var maxCommits = MaxCommits.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();

        _logger?.LogInformation(
            "Fetching recent commits for story {StoryId} from {Repo}",
            storyId, repositoryUrl);

        try
        {
            if (string.IsNullOrEmpty(repositoryUrl))
            {
                context.SetResult(new RecentCommitsResult
                {
                    Success = false,
                    ErrorMessage = "Repository URL is empty"
                });
                return;
            }

            var branchName = $"feature/{storyId}";
            var since = DateTime.UtcNow.AddDays(-daysBack);

            var commitsResponse = await apiClient.GetCommitsAsync(
                repositoryUrl, branchName, since, correlationId, tenantId, context.CancellationToken);
            if (commitsResponse is null || !commitsResponse.Success)
                throw new InvalidOperationException(
                    commitsResponse?.FailureReason ?? "git mediation endpoint unavailable");
            var commits = GitMediationMapping.ToCommits(commitsResponse.Commits);

            var entries = commits
                .Take(maxCommits)
                .Select(c => new CommitEntry
                {
                    Sha = c.Sha,
                    Message = c.Message,
                    Author = c.Author,
                    Timestamp = c.Timestamp,
                    Files = c.Files,
                    Additions = c.Additions,
                    Deletions = c.Deletions
                })
                .ToList();

            context.SetResult(new RecentCommitsResult
            {
                Commits = entries,
                TotalCommits = entries.Count,
                Success = true
            });

            _logger?.LogInformation(
                "Fetched {Count} commits for story {StoryId}",
                entries.Count, storyId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to fetch recent commits for story {StoryId}", storyId);
            context.SetResult(new RecentCommitsResult
            {
                Success = false,
                ErrorMessage = $"Failed to fetch commits: {ex.Message}"
            });
        }
    }
}

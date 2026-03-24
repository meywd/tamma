using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Integration;

/// <summary>
/// ELSA activity for GitHub operations.
/// Supports creating branches, monitoring commits, creating PRs, and merging.
/// </summary>
[Activity(
    "Tamma.Integration",
    "GitHub Integration",
    "Perform GitHub operations like branch creation, PR management, and commit monitoring",
    Kind = ActivityKind.Task
)]
public class GitHubActivity : CodeActivity<GitHubOperationResult>
{
    private readonly ILogger<GitHubActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>GitHub action to perform</summary>
    [Input(Description = "Action: CreateBranch, MonitorCommits, CreatePullRequest, MergePullRequest, GetFileChanges")]
    public Input<GitHubAction> Action { get; set; } = default!;

    /// <summary>Repository in format owner/repo</summary>
    [Input(Description = "Repository in format owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Story ID (used for branch naming)</summary>
    [Input(Description = "Story ID for branch naming")]
    public Input<string?> StoryId { get; set; } = default!;

    /// <summary>Branch name (optional, defaults to feature/{storyId})</summary>
    [Input(Description = "Branch name")]
    public Input<string?> BranchName { get; set; } = default!;

    /// <summary>Pull request number (for merge operations)</summary>
    [Input(Description = "Pull request number")]
    public Input<int?> PullRequestNumber { get; set; } = default!;

    /// <summary>Pull request title (for create PR)</summary>
    [Input(Description = "Pull request title")]
    public Input<string?> PrTitle { get; set; } = default!;

    /// <summary>Pull request body (for create PR)</summary>
    [Input(Description = "Pull request body")]
    public Input<string?> PrBody { get; set; } = default!;

    [JsonConstructor]
    public GitHubActivity() { }

    public GitHubActivity(
        ILogger<GitHubActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    /// <summary>
    /// Execute the GitHub operation
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var action = Action.Get(context);
        var repository = Repository.Get(context);
        var storyId = StoryId.Get(context);
        var branchName = BranchName.Get(context) ?? (storyId != null ? $"feature/{storyId}" : null);
        var prNumber = PullRequestNumber.Get(context);
        var prTitle = PrTitle.Get(context);
        var prBody = PrBody.Get(context);

        _logger?.LogInformation(
            "Executing GitHub action {Action} on repository {Repository}",
            action, repository);

        try
        {
            GitHubOperationResult result = action switch
            {
                GitHubAction.CreateBranch => await CreateBranch(repository, branchName!),
                GitHubAction.MonitorCommits => await MonitorCommits(repository, branchName!),
                GitHubAction.CreatePullRequest => await CreatePullRequest(repository, branchName!, prTitle!, prBody!),
                GitHubAction.MergePullRequest => await MergePullRequest(repository, prNumber!.Value),
                GitHubAction.GetFileChanges => await GetFileChanges(repository, branchName!),
                GitHubAction.RunTests => await RunTests(repository, branchName!),
                _ => new GitHubOperationResult { Success = false, Message = $"Unknown action: {action}" }
            };

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GitHub operation failed");
            context.SetResult(new GitHubOperationResult
            {
                Success = false,
                Message = $"Operation failed: {ex.Message}"
            });
        }
    }

    private async Task<GitHubOperationResult> CreateBranch(string repository, string branchName)
    {
        var result = await _integrationService!.CreateGitHubBranchAsync(repository, branchName);
        return new GitHubOperationResult
        {
            Success = result.Success,
            Message = result.Success ? $"Created branch: {branchName}" : result.Error,
            BranchName = result.BranchName,
            BranchUrl = result.BranchUrl
        };
    }

    private async Task<GitHubOperationResult> MonitorCommits(string repository, string branchName)
    {
        var commits = await _integrationService!.GetGitHubCommitsAsync(
            repository, branchName, DateTime.UtcNow.AddHours(-1));

        return new GitHubOperationResult
        {
            Success = true,
            Message = $"Found {commits.Count} commits in the last hour",
            CommitCount = commits.Count,
            Commits = commits.Select(c => new CommitInfo
            {
                Sha = c.Sha,
                Message = c.Message,
                Author = c.Author,
                Timestamp = c.Timestamp
            }).ToList()
        };
    }

    private async Task<GitHubOperationResult> CreatePullRequest(
        string repository, string branchName, string title, string body)
    {
        var result = await _integrationService!.CreateGitHubPullRequestAsync(repository, new CreatePullRequestRequest
        {
            Title = title,
            Body = body,
            Head = branchName,
            Base = "main"
        });

        return new GitHubOperationResult
        {
            Success = result.Success,
            Message = result.Success ? $"Created PR #{result.Number}" : result.Error,
            PullRequestNumber = result.Number,
            PullRequestUrl = result.Url
        };
    }

    private async Task<GitHubOperationResult> MergePullRequest(string repository, int prNumber)
    {
        var result = await _integrationService!.MergeGitHubPullRequestAsync(repository, prNumber);

        return new GitHubOperationResult
        {
            Success = result.Success,
            Message = result.Success ? $"Merged PR #{prNumber}" : result.Error,
            MergeSha = result.MergeSha
        };
    }

    private async Task<GitHubOperationResult> GetFileChanges(string repository, string branchName)
    {
        var changes = await _integrationService!.GetGitHubFileChangesAsync(repository, branchName);

        return new GitHubOperationResult
        {
            Success = true,
            Message = $"Found {changes.Count} changed files",
            FileChanges = changes.Select(c => new FileChangeResult
            {
                FilePath = c.FilePath,
                ChangeType = c.ChangeType,
                Additions = c.Additions,
                Deletions = c.Deletions
            }).ToList()
        };
    }

    private async Task<GitHubOperationResult> RunTests(string repository, string branchName)
    {
        var result = await _integrationService!.TriggerTestsAsync(repository, branchName);

        return new GitHubOperationResult
        {
            Success = result.FailedTests == 0,
            Message = $"Tests: {result.PassedTests}/{result.TotalTests} passed",
            TestsPassed = result.PassedTests,
            TestsFailed = result.FailedTests,
            CoveragePercentage = result.CoveragePercentage
        };
    }
}

/// <summary>
/// GitHub actions available
/// </summary>
public enum GitHubAction
{
    CreateBranch,
    MonitorCommits,
    CreatePullRequest,
    MergePullRequest,
    GetFileChanges,
    RunTests
}

/// <summary>
/// Commit information
/// </summary>
public class CommitInfo
{
    public string Sha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// File change result
/// </summary>
public class FileChangeResult
{
    public string FilePath { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

/// <summary>
/// Result of a GitHub operation
/// </summary>
public class GitHubOperationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? BranchName { get; set; }
    public string? BranchUrl { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? PullRequestUrl { get; set; }
    public string? MergeSha { get; set; }
    public int CommitCount { get; set; }
    public List<CommitInfo> Commits { get; set; } = new();
    public List<FileChangeResult> FileChanges { get; set; } = new();
    public int TestsPassed { get; set; }
    public int TestsFailed { get; set; }
    public double? CoveragePercentage { get; set; }
}

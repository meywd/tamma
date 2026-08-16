using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
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
    private readonly TammaApiClient? _apiClient;

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

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public GitHubActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: every GitHub op (branch / commits / PR / merge / file-changes /
    /// tests) routes through the git/CI mediation endpoints via
    /// <see cref="TammaApiClient"/>, where the per-tenant token lives.
    /// </summary>
    public GitHubActivity(
        ILogger<GitHubActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    /// <summary>
    /// Execute the GitHub operation
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var action = Action.Get(context);
        var repository = Repository.Get(context);
        var storyId = StoryId.GetOrDefault(context);
        var branchName = BranchName.GetOrDefault(context) ?? (storyId != null ? $"feature/{storyId}" : null);
        var prNumber = PullRequestNumber.GetOrDefault(context);
        var prTitle = PrTitle.GetOrDefault(context);
        var prBody = PrBody.GetOrDefault(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Executing GitHub action {Action} on repository {Repository}",
            action, repository);

        try
        {
            GitHubOperationResult result = action switch
            {
                GitHubAction.CreateBranch => await CreateBranch(apiClient, repository, branchName!, correlationId, tenantId, ct),
                GitHubAction.MonitorCommits => await MonitorCommits(apiClient, repository, branchName!, correlationId, tenantId, ct),
                GitHubAction.CreatePullRequest => await CreatePullRequest(apiClient, repository, branchName!, prTitle!, prBody!, correlationId, tenantId, ct),
                GitHubAction.MergePullRequest => await MergePullRequest(apiClient, repository, prNumber!.Value, correlationId, tenantId, ct),
                GitHubAction.GetFileChanges => await GetFileChanges(apiClient, repository, branchName!, correlationId, tenantId, ct),
                GitHubAction.RunTests => await RunTests(apiClient, repository, branchName!, correlationId, tenantId, ct),
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

    private static async Task<GitHubOperationResult> CreateBranch(
        TammaApiClient apiClient, string repository, string branchName,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        var response = await apiClient.CreateBranchAsync(repository, new GitCreateBranchRequest
        {
            BranchName = branchName,
            BaseRef = CreateBranchActivity.DefaultBaseBranch,
            CorrelationId = correlationId,
        }, tenantId, ct);

        // A null / failed response (guard 403 / token 503 / auth 401 / transport) maps to
        // the same soft failure the composite surfaced (Success=false + reason).
        var success = response?.Success ?? false;
        return new GitHubOperationResult
        {
            Success = success,
            Message = success
                ? $"Created branch: {response!.BranchRef ?? branchName}"
                : (response?.FailureReason ?? "git mediation endpoint unavailable"),
            BranchName = response?.BranchRef ?? (success ? branchName : null),
            // BranchUrl is not carried by the git-mediation wire response.
            BranchUrl = null
        };
    }

    private static async Task<GitHubOperationResult> MonitorCommits(
        TammaApiClient apiClient, string repository, string branchName,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        var response = await apiClient.GetCommitsAsync(
            repository, branchName, DateTime.UtcNow.AddHours(-1), correlationId, tenantId, ct);
        if (response is null || !response.Success)
            throw new InvalidOperationException(
                response?.FailureReason ?? "git mediation endpoint unavailable");

        var commits = GitMediationMapping.ToCommits(response.Commits);
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

    private static async Task<GitHubOperationResult> CreatePullRequest(
        TammaApiClient apiClient, string repository, string branchName, string title, string body,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        var response = await apiClient.CreatePullRequestAsync(repository, new GitCreatePrRequest
        {
            Title = title,
            Body = body,
            HeadRef = branchName,
            BaseRef = "main",
            CorrelationId = correlationId,
        }, tenantId, ct);

        var success = response?.Success ?? false;
        return new GitHubOperationResult
        {
            Success = success,
            Message = success
                ? $"Created PR #{response!.PrNumber}"
                : (response?.FailureReason ?? "git mediation endpoint unavailable"),
            PullRequestNumber = response?.PrNumber,
            PullRequestUrl = response?.PrUrl
        };
    }

    private static async Task<GitHubOperationResult> MergePullRequest(
        TammaApiClient apiClient, string repository, int prNumber,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        var response = await apiClient.MergePullRequestAsync(repository, prNumber, new GitMergePrRequest
        {
            CorrelationId = correlationId,
        }, tenantId, ct);

        var success = response?.Success ?? false;
        return new GitHubOperationResult
        {
            Success = success,
            Message = success
                ? $"Merged PR #{prNumber}"
                : (response?.FailureReason ?? "git mediation endpoint unavailable"),
            MergeSha = response?.MergeSha
        };
    }

    private static async Task<GitHubOperationResult> GetFileChanges(
        TammaApiClient apiClient, string repository, string branchName,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        var response = await apiClient.GetFileChangesAsync(repository, branchName, correlationId, tenantId, ct);
        if (response is null || !response.Success)
            throw new InvalidOperationException(
                response?.FailureReason ?? "git mediation endpoint unavailable");

        var changes = GitMediationMapping.ToFileChanges(response.FileChanges);
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

    private static async Task<GitHubOperationResult> RunTests(
        TammaApiClient apiClient, string repository, string branchName,
        string correlationId, string? tenantId, CancellationToken ct)
    {
        var response = await apiClient.TriggerTestsAsync(repository, new CiTriggerTestsRequest
        {
            Branch = branchName,
            CorrelationId = correlationId,
        }, tenantId, ct);
        if (response is null || !response.Success)
            throw new InvalidOperationException(
                response?.FailureReason ?? "ci mediation endpoint unavailable");

        var result = GitMediationMapping.ToTestRun(response.TestRun);
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

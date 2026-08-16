using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Review.Models;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Review;

/// <summary>
/// Creates a pull request for the junior developer's feature branch.
/// Gathers file changes and commits, builds a descriptive PR body,
/// and creates the PR via the GitHub integration service.
/// </summary>
[Activity(
    "Tamma.Review",
    "Create PR",
    "Create a pull request for the junior developer's code",
    Kind = ActivityKind.Task
)]
public class CreatePRActivity : CodeActivity<PRCreationResult>
{
    private readonly ILogger<CreatePRActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly TammaApiClient? _apiClient;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID for the PR</summary>
    [Input(Description = "Story ID for the PR")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Base branch to merge into (default: main)</summary>
    [Input(Description = "Base branch to merge into", DefaultValue = "main")]
    public Input<string> BaseBranch { get; set; } = new("main");

    /// <summary>Head branch containing the changes</summary>
    [Input(Description = "Head branch containing the changes (default: feature/{storyId})")]
    public Input<string?> HeadBranch { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public CreatePRActivity() { }

    /// <summary>
    /// Story 38 (Phase 2) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no git token: file changes + commits are read and the PR is created through
    /// the git-mediation endpoints via <see cref="TammaApiClient"/>. The PR body is
    /// composed engine-side (pure, token-free).
    /// </summary>
    public CreatePRActivity(
        ILogger<CreatePRActivity> logger,
        IMentorshipSessionRepository repository,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _repository = repository;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var baseBranch = BaseBranch.Get(context);
        var headBranch = HeadBranch.GetOrDefault(context) ?? $"feature/{storyId}";
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Creating PR for session {SessionId}, story {StoryId}, branch {HeadBranch}",
            sessionId, storyId, headBranch);

        try
        {
            var story = await _repository!.GetStoryByIdAsync(storyId);
            var junior = await _repository.GetJuniorByIdAsync(juniorId);

            if (story == null || junior == null)
            {
                context.SetResult(new PRCreationResult
                {
                    Success = false,
                    Error = "Story or junior developer not found"
                });
                return;
            }

            if (string.IsNullOrEmpty(story.RepositoryUrl))
            {
                context.SetResult(new PRCreationResult
                {
                    Success = false,
                    Error = "No repository URL configured for story"
                });
                return;
            }

            // Gather file changes and commits for the PR body
            var fileChangesResponse = await apiClient.GetFileChangesAsync(
                story.RepositoryUrl, headBranch, correlationId, tenantId, ct);
            if (fileChangesResponse is null || !fileChangesResponse.Success)
                throw new InvalidOperationException(
                    fileChangesResponse?.FailureReason ?? "git mediation endpoint unavailable");
            var fileChanges = GitMediationMapping.ToFileChanges(fileChangesResponse.FileChanges);

            var commitsResponse = await apiClient.GetCommitsAsync(
                story.RepositoryUrl, headBranch, DateTime.UtcNow.AddDays(-14), correlationId, tenantId, ct);
            if (commitsResponse is null || !commitsResponse.Success)
                throw new InvalidOperationException(
                    commitsResponse?.FailureReason ?? "git mediation endpoint unavailable");
            var commits = GitMediationMapping.ToCommits(commitsResponse.Commits);

            var prBody = BuildPRBody(story, junior, fileChanges, commits);

            // Create the pull request
            var prResponse = await apiClient.CreatePullRequestAsync(
                story.RepositoryUrl,
                new GitCreatePrRequest
                {
                    Title = $"[{story.Id}] {story.Title}",
                    Body = prBody,
                    HeadRef = headBranch,
                    BaseRef = baseBranch,
                    Labels = new List<string> { "mentorship", "code-review" },
                    CorrelationId = correlationId,
                }, tenantId, ct);

            if (prResponse is null || !prResponse.Success)
            {
                var error = prResponse?.FailureReason ?? "git mediation endpoint unavailable";
                _logger?.LogWarning("Failed to create PR: {Error}", error);
                context.SetResult(new PRCreationResult
                {
                    Success = false,
                    Error = $"Failed to create PR: {error}"
                });
                return;
            }

            // Log the event
            await _repository.LogEventAsync(new MentorshipEvent
            {
                SessionId = sessionId,
                EventType = EventTypes.CodeReviewPrepared,
                StateFrom = MentorshipState.QUALITY_GATE_CHECK,
                StateTo = MentorshipState.PREPARE_CODE_REVIEW
            });

            _logger?.LogInformation(
                "Created PR #{PRNumber} for session {SessionId}",
                prResponse.PrNumber, sessionId);

            context.SetResult(new PRCreationResult
            {
                Success = true,
                PRNumber = prResponse.PrNumber,
                PRUrl = prResponse.PrUrl,
                HeadBranch = headBranch,
                BaseBranch = baseBranch
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error creating PR for session {SessionId}", sessionId);
            context.SetResult(new PRCreationResult
            {
                Success = false,
                Error = $"PR creation failed: {ex.Message}"
            });
        }
    }

    private static string BuildPRBody(
        Story story,
        JuniorDeveloper junior,
        List<GitHubFileChange> fileChanges,
        List<GitHubCommit> commits)
    {
        var totalAdditions = fileChanges.Sum(f => f.Additions);
        var totalDeletions = fileChanges.Sum(f => f.Deletions);

        return $@"## Summary
Implementation of story **{story.Id}**: {story.Title}

{story.Description ?? "No description provided."}

## Changes
- **Files changed:** {fileChanges.Count}
- **Additions:** +{totalAdditions}
- **Deletions:** -{totalDeletions}
- **Commits:** {commits.Count}

### Modified Files
{string.Join("\n", fileChanges.Take(15).Select(f => $"- `{f.FilePath}` ({f.ChangeType})"))}
{(fileChanges.Count > 15 ? $"\n... and {fileChanges.Count - 15} more files" : "")}

## Testing
- [ ] Unit tests added/updated
- [ ] All existing tests pass
- [ ] Manual testing completed

## Mentorship Info
- **Developer:** {junior.Name} (Skill Level: {junior.SkillLevel})
- **Mentored by:** Tamma Autonomous Mentorship System

---
*This PR was created as part of the Tamma code review sub-workflow.*";
    }
}

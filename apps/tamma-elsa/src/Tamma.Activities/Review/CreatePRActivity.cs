using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
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
    private readonly IIntegrationService? _integrationService;

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

    [JsonConstructor]
    public CreatePRActivity() { }

    public CreatePRActivity(
        ILogger<CreatePRActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var baseBranch = BaseBranch.Get(context);
        var headBranch = HeadBranch.Get(context) ?? $"feature/{storyId}";

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
            var fileChanges = await _integrationService!.GetGitHubFileChangesAsync(
                story.RepositoryUrl, headBranch);
            var commits = await _integrationService.GetGitHubCommitsAsync(
                story.RepositoryUrl, headBranch, DateTime.UtcNow.AddDays(-14));

            var prBody = BuildPRBody(story, junior, fileChanges, commits);

            // Create the pull request
            var prResult = await _integrationService.CreateGitHubPullRequestAsync(
                story.RepositoryUrl,
                new CreatePullRequestRequest
                {
                    Title = $"[{story.Id}] {story.Title}",
                    Body = prBody,
                    Head = headBranch,
                    Base = baseBranch,
                    Labels = new List<string> { "mentorship", "code-review" }
                });

            if (!prResult.Success)
            {
                _logger?.LogWarning("Failed to create PR: {Error}", prResult.Error);
                context.SetResult(new PRCreationResult
                {
                    Success = false,
                    Error = $"Failed to create PR: {prResult.Error}"
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
                prResult.Number, sessionId);

            context.SetResult(new PRCreationResult
            {
                Success = true,
                PRNumber = prResult.Number,
                PRUrl = prResult.Url,
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

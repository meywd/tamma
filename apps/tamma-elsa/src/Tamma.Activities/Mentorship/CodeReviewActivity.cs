using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.LlmCall;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Mentorship;

/// <summary>
/// ELSA activity to manage the code review process.
/// Creates pull requests, requests reviews, and monitors review status.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Code Review",
    "Manage the code review process including PR creation and review monitoring",
    Kind = ActivityKind.Task
)]
public class CodeReviewActivity : CodeActivity<CodeReviewOutput>
{
    private readonly ILogger<CodeReviewActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;
    private readonly IAnalyticsService? _analyticsService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the story being reviewed</summary>
    [Input(Description = "ID of the story being reviewed")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>ID of the junior developer</summary>
    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Action to perform: Prepare, Monitor, or RequestChanges</summary>
    [Input(Description = "Review action: Prepare, Monitor, RequestChanges")]
    public Input<CodeReviewAction> Action { get; set; } = default!;

    /// <summary>Existing pull request number (for Monitor/RequestChanges)</summary>
    [Input(Description = "Existing PR number")]
    public Input<int?> PullRequestNumber { get; set; } = default!;

    [JsonConstructor]
    public CodeReviewActivity() { }

    public CodeReviewActivity(
        ILogger<CodeReviewActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService,
        IAnalyticsService analyticsService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
        _analyticsService = analyticsService;
    }

    /// <summary>
    /// Execute the code review activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var action = Action.Get(context);
        var prNumber = PullRequestNumber.Get(context);

        _logger?.LogInformation(
            "Code review action {Action} for junior {JuniorId} on story {StoryId}",
            action, juniorId, storyId);

        try
        {
            var story = await _repository!.GetStoryByIdAsync(storyId);
            var junior = await _repository.GetJuniorByIdAsync(juniorId);

            if (story == null || junior == null)
            {
                context.SetResult(new CodeReviewOutput
                {
                    Success = false,
                    Status = ReviewStatus.Error,
                    NextState = MentorshipState.FAILED,
                    Message = "Missing required entities"
                });
                return;
            }

            CodeReviewOutput result = action switch
            {
                CodeReviewAction.Prepare => await PrepareCodeReview(context, sessionId, story, junior),
                CodeReviewAction.Monitor => await MonitorCodeReview(context, sessionId, story, junior, prNumber),
                CodeReviewAction.RequestChanges => await HandleReviewChanges(context, sessionId, story, junior, prNumber),
                _ => new CodeReviewOutput
                {
                    Success = false,
                    Status = ReviewStatus.Error,
                    NextState = MentorshipState.FAILED,
                    Message = $"Unknown action: {action}"
                }
            };

            // Log the event
            await _repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = action switch
                {
                    CodeReviewAction.Prepare => Tamma.Core.Entities.EventTypes.CodeReviewPrepared,
                    CodeReviewAction.Monitor => Tamma.Core.Entities.EventTypes.CodeReviewMonitored,
                    _ => Tamma.Core.Entities.EventTypes.CodeReviewUpdate
                },
                StateFrom = MentorshipState.QUALITY_GATE_CHECK,
                StateTo = result.NextState
            });

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during code review for session {SessionId}", sessionId);

            context.SetResult(new CodeReviewOutput
            {
                Success = false,
                Status = ReviewStatus.Error,
                NextState = MentorshipState.ESCALATE_TO_SENIOR,
                Message = ex.Message
            });
        }
    }

    private async Task<CodeReviewOutput> PrepareCodeReview(
        ActivityExecutionContext context,
        Guid sessionId,
        Tamma.Core.Entities.Story story,
        Tamma.Core.Entities.JuniorDeveloper junior)
    {
        await _repository!.UpdateStateAsync(sessionId, MentorshipState.PREPARE_CODE_REVIEW);

        if (string.IsNullOrEmpty(story.RepositoryUrl))
        {
            return new CodeReviewOutput
            {
                Success = false,
                Status = ReviewStatus.Error,
                NextState = MentorshipState.FAILED,
                Message = "No repository configured for story"
            };
        }

        // Get file changes for PR description
        var fileChanges = await _integrationService!.GetGitHubFileChangesAsync(
            story.RepositoryUrl,
            $"feature/{story.Id}");

        // Get recent commits for context
        var commits = await _integrationService!.GetGitHubCommitsAsync(
            story.RepositoryUrl,
            $"feature/{story.Id}",
            DateTime.UtcNow.AddDays(-7));

        // Build PR description
        var prBody = BuildPullRequestBody(story, junior, fileChanges, commits);

        // Create the pull request
        var prResult = await _integrationService!.CreateGitHubPullRequestAsync(
            story.RepositoryUrl,
            new CreatePullRequestRequest
            {
                Title = $"[{story.Id}] {story.Title}",
                Body = prBody,
                Head = $"feature/{story.Id}",
                Base = "main",
                Labels = new List<string> { "mentorship", "junior-developer" }
            });

        if (!prResult.Success)
        {
            return new CodeReviewOutput
            {
                Success = false,
                Status = ReviewStatus.Error,
                NextState = MentorshipState.DIAGNOSE_BLOCKER,
                Message = $"Failed to create PR: {prResult.Error}"
            };
        }

        // Notify junior about PR creation
        if (!string.IsNullOrEmpty(junior.SlackId))
        {
            await NotifyPullRequestCreated(context, junior.SlackId, prResult, story.Title);
        }

        // Record analytics
        await _analyticsService!.RecordMetricAsync(sessionId, "pr_created", 1);

        _logger?.LogInformation(
            "Created pull request #{PrNumber} for story {StoryId}",
            prResult.Number, story.Id);

        return new CodeReviewOutput
        {
            Success = true,
            Status = ReviewStatus.Pending,
            PullRequestNumber = prResult.Number,
            PullRequestUrl = prResult.Url,
            FileChanges = fileChanges.Select(f => new FileChangeInfo
            {
                FilePath = f.FilePath,
                ChangeType = f.ChangeType,
                Additions = f.Additions,
                Deletions = f.Deletions
            }).ToList(),
            CommitCount = commits.Count,
            NextState = MentorshipState.MONITOR_REVIEW,
            Message = $"PR #{prResult.Number} created successfully"
        };
    }

    private async Task<CodeReviewOutput> MonitorCodeReview(
        ActivityExecutionContext context,
        Guid sessionId,
        Tamma.Core.Entities.Story story,
        Tamma.Core.Entities.JuniorDeveloper junior,
        int? prNumber)
    {
        await _repository!.UpdateStateAsync(sessionId, MentorshipState.MONITOR_REVIEW);

        if (!prNumber.HasValue || string.IsNullOrEmpty(story.RepositoryUrl))
        {
            return new CodeReviewOutput
            {
                Success = false,
                Status = ReviewStatus.Error,
                NextState = MentorshipState.PREPARE_CODE_REVIEW,
                Message = "PR number or repository URL missing"
            };
        }

        // Simulate checking PR status (in production, would call GitHub API)
        var reviewStatus = await SimulateReviewStatusCheck(story.RepositoryUrl, prNumber.Value);

        switch (reviewStatus.Status)
        {
            case ReviewStatus.Approved:
                // Notify junior about approval — Story 38-3b: enqueue via the API
                // seam (engine holds no Slack credential); fire-and-forget, fail-soft.
                if (!string.IsNullOrEmpty(junior.SlackId))
                {
                    await MediatedSlack.QueueDirectMessageAsync(
                        context,
                        junior.SlackId,
                        $"Great news! Your PR #{prNumber} has been approved! Proceeding to merge.",
                        "Success", "SendDirect", context.CancellationToken);
                }

                await _analyticsService!.RecordMetricAsync(sessionId, "pr_approved", 1);

                return new CodeReviewOutput
                {
                    Success = true,
                    Status = ReviewStatus.Approved,
                    PullRequestNumber = prNumber,
                    NextState = MentorshipState.MERGE_AND_COMPLETE,
                    Message = "PR approved, ready to merge"
                };

            case ReviewStatus.ChangesRequested:
                // Notify junior about requested changes
                if (!string.IsNullOrEmpty(junior.SlackId))
                {
                    await NotifyChangesRequested(context, junior.SlackId, prNumber.Value, reviewStatus.Comments);
                }

                return new CodeReviewOutput
                {
                    Success = true,
                    Status = ReviewStatus.ChangesRequested,
                    PullRequestNumber = prNumber,
                    ReviewComments = reviewStatus.Comments,
                    NextState = MentorshipState.GUIDE_FIXES,
                    Message = $"{reviewStatus.Comments.Count} changes requested"
                };

            case ReviewStatus.Pending:
                return new CodeReviewOutput
                {
                    Success = true,
                    Status = ReviewStatus.Pending,
                    PullRequestNumber = prNumber,
                    NextState = MentorshipState.MONITOR_REVIEW,
                    Message = "Review still pending"
                };

            default:
                return new CodeReviewOutput
                {
                    Success = false,
                    Status = ReviewStatus.Error,
                    NextState = MentorshipState.ESCALATE_TO_SENIOR,
                    Message = "Unable to determine review status"
                };
        }
    }

    private async Task<CodeReviewOutput> HandleReviewChanges(
        ActivityExecutionContext context,
        Guid sessionId,
        Tamma.Core.Entities.Story story,
        Tamma.Core.Entities.JuniorDeveloper junior,
        int? prNumber)
    {
        await _repository!.UpdateStateAsync(sessionId, MentorshipState.GUIDE_FIXES);

        if (!prNumber.HasValue)
        {
            return new CodeReviewOutput
            {
                Success = false,
                Status = ReviewStatus.Error,
                NextState = MentorshipState.PREPARE_CODE_REVIEW,
                Message = "PR number missing"
            };
        }

        // Get the review comments that need to be addressed
        var reviewStatus = await SimulateReviewStatusCheck(story.RepositoryUrl ?? "", prNumber.Value);

        // Generate guidance for each comment
        var commentGuidance = GenerateReviewCommentGuidance(reviewStatus.Comments);

        // Send guidance to junior
        if (!string.IsNullOrEmpty(junior.SlackId) && commentGuidance.Any())
        {
            var message = $@"**Tamma: Code Review Feedback Guide**

Here's help addressing the review feedback on PR #{prNumber}:

{string.Join("\n\n", commentGuidance.Select((g, i) => $"*Issue {i + 1}:* {g.Comment}\n*Guidance:* {g.Guidance}"))}

After making changes, the PR will be re-reviewed automatically.";

            // Story 38-3b — enqueue via the API seam (engine holds no Slack
            // credential); fire-and-forget, fail-soft.
            await MediatedSlack.QueueDirectMessageAsync(
                context, junior.SlackId, message, "Info", "SendGuidance", context.CancellationToken);
        }

        await _analyticsService!.RecordMetricAsync(sessionId, "review_changes_guided", commentGuidance.Count);

        return new CodeReviewOutput
        {
            Success = true,
            Status = ReviewStatus.ChangesRequested,
            PullRequestNumber = prNumber,
            ReviewComments = reviewStatus.Comments,
            NextState = MentorshipState.START_IMPLEMENTATION, // Back to implementation to make fixes
            Message = $"Guidance provided for {commentGuidance.Count} review comments"
        };
    }

    private string BuildPullRequestBody(
        Tamma.Core.Entities.Story story,
        Tamma.Core.Entities.JuniorDeveloper junior,
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
{string.Join("\n", fileChanges.Take(10).Select(f => $"- `{f.FilePath}` ({f.ChangeType})"))}
{(fileChanges.Count > 10 ? $"\n... and {fileChanges.Count - 10} more files" : "")}

## Testing
- [ ] Unit tests added/updated
- [ ] All existing tests pass
- [ ] Manual testing completed

## Acceptance Criteria
{FormatAcceptanceCriteria(story.AcceptanceCriteria?.RootElement.GetRawText())}

## Mentorship Info
- **Developer:** {junior.Name} (Skill Level: {junior.SkillLevel})
- **Mentored by:** Tamma Autonomous Mentorship System

---
*This PR was created as part of the Tamma mentorship workflow.*";
    }

    private string FormatAcceptanceCriteria(string? criteria)
    {
        if (string.IsNullOrEmpty(criteria))
            return "- [ ] No acceptance criteria defined";

        try
        {
            var items = System.Text.Json.JsonSerializer.Deserialize<List<string>>(criteria);
            return string.Join("\n", items?.Select(c => $"- [ ] {c}") ?? new[] { "- [ ] Criteria not parseable" });
        }
        catch
        {
            return $"- [ ] {criteria}";
        }
    }

    private async Task<ReviewStatusResult> SimulateReviewStatusCheck(string repositoryUrl, int prNumber)
    {
        // In production, this would call GitHub API to get actual review status
        // For now, simulate with random outcomes weighted towards approval
        var roll = Random.Shared.Next(100);

        if (roll < 60) // 60% approval rate
        {
            return new ReviewStatusResult
            {
                Status = ReviewStatus.Approved,
                Comments = new List<ReviewComment>()
            };
        }
        else if (roll < 85) // 25% changes requested
        {
            return new ReviewStatusResult
            {
                Status = ReviewStatus.ChangesRequested,
                Comments = GenerateSimulatedReviewComments()
            };
        }
        else // 15% still pending
        {
            return new ReviewStatusResult
            {
                Status = ReviewStatus.Pending,
                Comments = new List<ReviewComment>()
            };
        }
    }

    private List<ReviewComment> GenerateSimulatedReviewComments()
    {
        var possibleComments = new List<(string Comment, string File, int Line)>
        {
            ("Consider adding null check here", "Service.cs", 45),
            ("This could be simplified using LINQ", "Repository.cs", 78),
            ("Missing unit test for edge case", "Controller.cs", 23),
            ("Variable name could be more descriptive", "Model.cs", 15),
            ("Consider extracting this into a separate method", "Handler.cs", 102),
            ("Add XML documentation for public method", "Api.cs", 67)
        };

        var count = Random.Shared.Next(1, 4);

        return possibleComments
            .OrderBy(_ => Random.Shared.Next())
            .Take(count)
            .Select(c => new ReviewComment
            {
                Comment = c.Comment,
                FilePath = c.File,
                LineNumber = c.Line,
                Author = "senior-reviewer"
            })
            .ToList();
    }

    private List<CommentGuidance> GenerateReviewCommentGuidance(List<ReviewComment> comments)
    {
        return comments.Select(c => new CommentGuidance
        {
            Comment = c.Comment,
            Guidance = GetGuidanceForComment(c.Comment)
        }).ToList();
    }

    private string GetGuidanceForComment(string comment)
    {
        var commentLower = comment.ToLower();

        if (commentLower.Contains("null check"))
            return "Add a null check using `if (variable == null)` or the null-conditional operator `?.`";

        if (commentLower.Contains("linq"))
            return "Look for loops that could be replaced with `.Where()`, `.Select()`, or `.FirstOrDefault()`";

        if (commentLower.Contains("test"))
            return "Add a test case that covers the specific scenario mentioned. Follow existing test patterns.";

        if (commentLower.Contains("variable name") || commentLower.Contains("descriptive"))
            return "Rename the variable to describe what it contains, not its type. E.g., `userList` -> `activeUsers`";

        if (commentLower.Contains("extract"))
            return "Create a new private method with a descriptive name that handles this logic. Keep methods focused.";

        if (commentLower.Contains("documentation"))
            return "Add `/// <summary>` XML docs describing what the method does and its parameters.";

        return "Review the comment and apply the suggested change. Ask if you need clarification.";
    }

    private static async Task NotifyPullRequestCreated(
        ActivityExecutionContext context, string slackId, GitHubPullRequestResult pr, string storyTitle)
    {
        var message = $@"**Tamma: Pull Request Created**

Your code is ready for review!

*Story:* {storyTitle}
*PR:* #{pr.Number}
*Link:* {pr.Url}

A reviewer will look at your code soon. You'll be notified when there's feedback.";

        // Story 38-3b — enqueue via the API seam (engine holds no Slack credential);
        // fire-and-forget, fail-soft.
        await MediatedSlack.QueueDirectMessageAsync(
            context, slackId, message, "Info", "SendDirect", context.CancellationToken);
    }

    private static async Task NotifyChangesRequested(
        ActivityExecutionContext context, string slackId, int prNumber, List<ReviewComment> comments)
    {
        var message = $@"**Tamma: Review Feedback Received**

Your PR #{prNumber} has received feedback. {comments.Count} change(s) requested:

{string.Join("\n", comments.Select(c => $"- **{c.FilePath}:{c.LineNumber}** - {c.Comment}"))}

I'll send you guidance on how to address each item shortly.";

        // Story 38-3b — enqueue via the API seam (engine holds no Slack credential);
        // fire-and-forget, fail-soft.
        await MediatedSlack.QueueDirectMessageAsync(
            context, slackId, message, "Warning", "SendDirect", context.CancellationToken);
    }
}

/// <summary>
/// Code review actions
/// </summary>
public enum CodeReviewAction
{
    /// <summary>Prepare and create a pull request</summary>
    Prepare,

    /// <summary>Monitor existing PR status</summary>
    Monitor,

    /// <summary>Handle requested changes</summary>
    RequestChanges
}

/// <summary>
/// Review status
/// </summary>
public enum ReviewStatus
{
    Pending,
    Approved,
    ChangesRequested,
    Error
}

/// <summary>
/// Review status check result
/// </summary>
public class ReviewStatusResult
{
    public ReviewStatus Status { get; set; }
    public List<ReviewComment> Comments { get; set; } = new();
}

/// <summary>
/// Individual review comment
/// </summary>
public class ReviewComment
{
    public string Comment { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string Author { get; set; } = string.Empty;
}

/// <summary>
/// Guidance for a review comment
/// </summary>
public class CommentGuidance
{
    public string Comment { get; set; } = string.Empty;
    public string Guidance { get; set; } = string.Empty;
}

/// <summary>
/// File change information
/// </summary>
public class FileChangeInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

/// <summary>
/// Output model for code review activity
/// </summary>
public class CodeReviewOutput
{
    public bool Success { get; set; }
    public ReviewStatus Status { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? PullRequestUrl { get; set; }
    public List<FileChangeInfo> FileChanges { get; set; } = new();
    public int CommitCount { get; set; }
    public List<ReviewComment> ReviewComments { get; set; } = new();
    public MentorshipState NextState { get; set; }
    public string? Message { get; set; }
}

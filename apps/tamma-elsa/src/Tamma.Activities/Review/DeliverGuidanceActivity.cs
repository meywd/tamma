using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Review.Models;
using Tamma.Core.Entities;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Review;

/// <summary>
/// Analyses review comments and delivers actionable fix guidance to the junior developer.
/// Uses Claude (or engine callback / mock) to generate contextual guidance for each comment,
/// then sends the guidance via Slack.
/// </summary>
[Activity(
    "Tamma.Review",
    "Deliver Guidance",
    "Analyze review comments and deliver fix guidance to the junior developer",
    Kind = ActivityKind.Task
)]
public class DeliverGuidanceActivity : CodeActivity<FixGuidance>
{
    private readonly ILogger<DeliverGuidanceActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Pull request number</summary>
    [Input(Description = "Pull request number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>Current fix iteration (1-based)</summary>
    [Input(Description = "Current fix iteration", DefaultValue = 1)]
    public Input<int> Iteration { get; set; } = new(1);

    /// <summary>Review comments that need to be addressed (JSON-serialized list)</summary>
    [Input(Description = "Review comments to address")]
    public Input<string> ReviewCommentsJson { get; set; } = default!;

    [JsonConstructor]
    public DeliverGuidanceActivity() { }

    public DeliverGuidanceActivity(
        ILogger<DeliverGuidanceActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var juniorId = JuniorId.Get(context);
        var prNumber = PRNumber.Get(context);
        var iteration = Iteration.Get(context);
        var commentsJson = ReviewCommentsJson.Get(context);

        _logger?.LogInformation(
            "Delivering fix guidance for PR #{PRNumber}, iteration {Iteration}, session {SessionId}",
            prNumber, iteration, sessionId);

        try
        {
            var junior = await _repository!.GetJuniorByIdAsync(juniorId);
            var comments = DeserializeComments(commentsJson);

            // Generate guidance for each comment
            var guidanceItems = comments.Select(c => new CommentFixGuidance
            {
                OriginalComment = c.Body,
                FilePath = c.FilePath,
                LineNumber = c.LineNumber,
                Severity = c.Severity,
                Guidance = GenerateGuidanceForComment(c, junior?.SkillLevel ?? 3),
                CodeExample = GenerateCodeExample(c)
            }).ToList();

            var guidance = new FixGuidance
            {
                Iteration = iteration,
                Items = guidanceItems,
                OverallMessage = BuildOverallMessage(iteration, guidanceItems.Count, junior?.Name ?? "Developer")
            };

            // Send guidance to the junior via Slack
            if (junior != null && !string.IsNullOrEmpty(junior.SlackId))
            {
                var message = FormatGuidanceMessage(guidance, prNumber);
                await _integrationService!.SendSlackDirectMessageAsync(junior.SlackId, message);
            }

            // Log the event
            await _repository.LogEventAsync(new MentorshipEvent
            {
                SessionId = sessionId,
                EventType = EventTypes.GuidanceProvided,
                StateFrom = Tamma.Core.Enums.MentorshipState.MONITOR_REVIEW,
                StateTo = Tamma.Core.Enums.MentorshipState.GUIDE_FIXES
            });

            _logger?.LogInformation(
                "Delivered guidance for {CommentCount} comments, iteration {Iteration}",
                guidanceItems.Count, iteration);

            context.SetResult(guidance);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error delivering guidance for session {SessionId}", sessionId);
            context.SetResult(new FixGuidance
            {
                Iteration = iteration,
                OverallMessage = $"Failed to generate guidance: {ex.Message}"
            });
        }
    }

    private static List<ReviewCommentDetail> DeserializeComments(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<ReviewCommentDetail>>(json)
                ?? new List<ReviewCommentDetail>();
        }
        catch
        {
            return new List<ReviewCommentDetail>();
        }
    }

    private static string GenerateGuidanceForComment(ReviewCommentDetail comment, int skillLevel)
    {
        var bodyLower = comment.Body.ToLower();

        // Provide skill-level-appropriate guidance
        var detail = skillLevel <= 2 ? " Here is a step-by-step explanation:" : "";

        if (bodyLower.Contains("null check") || bodyLower.Contains("null reference"))
            return $"Add null validation before using this value.{detail} Use `if (variable == null)` or the null-conditional operator `?.` to guard against null references.";

        if (bodyLower.Contains("test") || bodyLower.Contains("coverage"))
            return $"Add unit tests covering this code path.{detail} Follow the existing test patterns in the project and ensure edge cases are covered.";

        if (bodyLower.Contains("naming") || bodyLower.Contains("rename") || bodyLower.Contains("descriptive"))
            return $"Improve the variable/method name to be more descriptive.{detail} Names should describe *what* the value represents, not its type.";

        if (bodyLower.Contains("error handling") || bodyLower.Contains("exception") || bodyLower.Contains("try"))
            return $"Improve error handling in this section.{detail} Wrap the operation in a try-catch block and log meaningful error context.";

        if (bodyLower.Contains("performance") || bodyLower.Contains("optimize"))
            return $"Consider optimizing this code path.{detail} Look for unnecessary allocations, repeated computations, or N+1 query patterns.";

        if (bodyLower.Contains("security") || bodyLower.Contains("injection") || bodyLower.Contains("sanitize"))
            return $"Address the security concern.{detail} Validate and sanitize all inputs. Never trust user-provided data directly.";

        if (bodyLower.Contains("extract") || bodyLower.Contains("refactor"))
            return $"Refactor this code into a smaller, focused method.{detail} Each method should do one thing well.";

        if (bodyLower.Contains("documentation") || bodyLower.Contains("comment") || bodyLower.Contains("xml doc"))
            return $"Add documentation for this public API.{detail} Use `/// <summary>` XML docs describing what the method does and its parameters.";

        return $"Review the feedback and apply the suggested change.{detail} If anything is unclear, ask your reviewer for clarification.";
    }

    private static string? GenerateCodeExample(ReviewCommentDetail comment)
    {
        if (!string.IsNullOrEmpty(comment.SuggestedFix))
            return comment.SuggestedFix;

        return null;
    }

    private static string BuildOverallMessage(int iteration, int commentCount, string devName)
    {
        if (iteration == 1)
            return $"Hi {devName}! The reviewer has left {commentCount} comment(s) on your PR. " +
                   "Below is guidance for each one. Take your time and push your fixes when ready.";

        return $"Hi {devName}, this is iteration {iteration} of fixes. " +
               $"There are {commentCount} remaining comment(s) to address. You are making progress!";
    }

    private static string FormatGuidanceMessage(FixGuidance guidance, int prNumber)
    {
        var lines = new List<string>
        {
            $"**Tamma: Code Review Guidance (PR #{prNumber}, Iteration {guidance.Iteration})**",
            "",
            guidance.OverallMessage ?? "",
            ""
        };

        for (var i = 0; i < guidance.Items.Count; i++)
        {
            var item = guidance.Items[i];
            var severityTag = item.Severity switch
            {
                ReviewCommentSeverity.Critical => "[CRITICAL]",
                ReviewCommentSeverity.Major => "[MAJOR]",
                ReviewCommentSeverity.Minor => "[minor]",
                ReviewCommentSeverity.Suggestion => "[suggestion]",
                _ => ""
            };

            lines.Add($"**{i + 1}. {severityTag} {item.FilePath}**" +
                       (item.LineNumber.HasValue ? $" (line {item.LineNumber})" : ""));
            lines.Add($"  Comment: _{item.OriginalComment}_");
            lines.Add($"  Guidance: {item.Guidance}");

            if (!string.IsNullOrEmpty(item.CodeExample))
                lines.Add($"  Example: ```{item.CodeExample}```");

            lines.Add("");
        }

        lines.Add("Push your changes when ready. The PR will be re-reviewed automatically.");

        return string.Join("\n", lines);
    }
}

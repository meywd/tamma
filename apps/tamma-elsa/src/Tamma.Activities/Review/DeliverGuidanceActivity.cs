using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.Review.Models;
using Tamma.Core.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Review;

/// <summary>
/// Delivers actionable, skill-level-aware fix guidance to the junior developer.
///
/// Story 7-1D AC7 (completeness audit 2026-06-22, <c>CodeReview.md</c> §Missing #4):
/// the guidance text is now produced by the MEDIATED LLM upstream — the workflow runs
/// <c>AnalyzeChanges</c> (<c>llm-call</c> role=senior_developer / action=code-review) then
/// <c>GenerateGuidance</c> (<c>llm-call</c> role=senior_developer / action=mentor-feedback,
/// "explain to a Level {skillLevel} developer …") via <c>DispatchWorkflow("llm-call")</c>,
/// and passes the LLM output into <see cref="GuidanceText"/>. This activity ONLY formats +
/// delivers that text (the prior in-process keyword heuristics are removed — there is no
/// in-engine provider call here).
///
/// <para><b>Fail-closed:</b> if the upstream LLM produced no usable guidance text, this
/// activity routes to the <c>Failed</c> outcome (the workflow escalates) rather than
/// silently delivering empty/placeholder guidance. Outcomes: <c>Delivered</c> /
/// <c>Failed</c>.</para>
/// </summary>
[Activity(
    "Tamma.Review",
    "Deliver Guidance",
    "Deliver mediated-LLM fix guidance to the junior developer",
    Kind = ActivityKind.Task
)]
[FlowNode("Delivered", "Failed")]
public class DeliverGuidanceActivity : Activity
{
    private readonly ILogger<DeliverGuidanceActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;

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

    /// <summary>
    /// The skill-level-aware fix guidance produced by the mediated LLM
    /// (<c>GenerateGuidance</c> llm-call). This is the content delivered to the junior —
    /// the activity no longer generates guidance itself.
    /// </summary>
    [Input(Description = "Mediated-LLM guidance text to deliver")]
    public Input<string?> GuidanceText { get; set; } = new((string?)null);

    /// <summary>The guidance delivered to the junior</summary>
    [Output(Description = "Guidance delivered")]
    public Output<FixGuidance?> Result { get; set; } = default!;

    [JsonConstructor]
    public DeliverGuidanceActivity() { }

    public DeliverGuidanceActivity(
        ILogger<DeliverGuidanceActivity> logger,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var juniorId = JuniorId.Get(context);
        var prNumber = PRNumber.Get(context);
        var iteration = Iteration.Get(context);
        var commentsJson = ReviewCommentsJson.Get(context);
        var guidanceText = GuidanceText.Get(context);

        _logger?.LogInformation(
            "Delivering fix guidance for PR #{PRNumber}, iteration {Iteration}, session {SessionId}",
            prNumber, iteration, sessionId);

        // Fail-closed: never ship empty guidance. If the mediated LLM produced nothing
        // usable, route to escalation instead of silently delivering a placeholder.
        if (string.IsNullOrWhiteSpace(guidanceText))
        {
            _logger?.LogWarning(
                "No LLM guidance text for PR #{PRNumber}, iteration {Iteration}; routing to escalation",
                prNumber, iteration);
            Result.Set(context, null);
            await context.CompleteActivityWithOutcomesAsync("Failed");
            return;
        }

        try
        {
            var junior = await _repository!.GetJuniorByIdAsync(juniorId);
            var comments = DeserializeComments(commentsJson);

            var guidance = new FixGuidance
            {
                Iteration = iteration,
                Items = comments.Select(c => new CommentFixGuidance
                {
                    OriginalComment = c.Body,
                    FilePath = c.FilePath,
                    LineNumber = c.LineNumber,
                    Severity = c.Severity,
                    Guidance = string.Empty, // per-comment detail is folded into OverallMessage (the LLM output)
                    CodeExample = c.SuggestedFix
                }).ToList(),
                OverallMessage = guidanceText.Trim()
            };

            // Send guidance to the junior via Slack — Story 38-3b: enqueue the DM
            // intent via the API seam (engine holds no Slack credential);
            // fire-and-forget, fail-soft.
            if (junior != null && !string.IsNullOrEmpty(junior.SlackId))
            {
                var message = FormatGuidanceMessage(guidance, prNumber);
                await MediatedSlack.QueueDirectMessageAsync(
                    context, junior.SlackId, message, "Info", "SendGuidance", context.CancellationToken);
            }

            // Log the mentorship event (retained alongside the DCB event the workflow emits)
            await _repository.LogEventAsync(new MentorshipEvent
            {
                SessionId = sessionId,
                EventType = EventTypes.GuidanceProvided,
                StateFrom = Tamma.Core.Enums.MentorshipState.MONITOR_REVIEW,
                StateTo = Tamma.Core.Enums.MentorshipState.GUIDE_FIXES
            });

            _logger?.LogInformation(
                "Delivered guidance for {CommentCount} comments, iteration {Iteration}",
                guidance.Items.Count, iteration);

            Result.Set(context, guidance);
            await context.CompleteActivityWithOutcomesAsync("Delivered");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error delivering guidance for session {SessionId}", sessionId);
            Result.Set(context, null);
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
    }

    private static List<ReviewCommentDetail> DeserializeComments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<ReviewCommentDetail>();
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

    private static string FormatGuidanceMessage(FixGuidance guidance, int prNumber)
    {
        var lines = new List<string>
        {
            $"**Tamma: Code Review Guidance (PR #{prNumber}, Iteration {guidance.Iteration})**",
            "",
            guidance.OverallMessage ?? "",
            ""
        };

        if (guidance.Items.Count > 0)
        {
            lines.Add("**Review comments addressed:**");
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
                lines.Add($"  _{item.OriginalComment}_");

                if (!string.IsNullOrEmpty(item.CodeExample))
                    lines.Add($"  Suggested fix: ```{item.CodeExample}```");
            }
            lines.Add("");
        }

        lines.Add("Push your changes when ready. The PR will be re-reviewed automatically.");

        return string.Join("\n", lines);
    }
}

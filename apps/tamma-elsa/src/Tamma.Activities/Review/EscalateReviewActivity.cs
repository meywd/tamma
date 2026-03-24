using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Review.Models;
using Tamma.Core.Entities;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Review;

/// <summary>
/// Bookmark-based blocking activity that escalates the review to a senior developer.
/// Creates an escalation record, notifies via Slack, then suspends the workflow
/// with a bookmark named "escalate-{sessionId}-{prNumber}" until a senior responds.
///
/// Outcomes:
///   - Resolved: the senior resolved the escalation (approved or fixed)
///   - Rejected: the senior rejected the PR entirely
/// </summary>
[Activity(
    "Tamma.Review",
    "Escalate Review",
    "Escalate the review to a senior developer and wait for response (bookmark-based)",
    Kind = ActivityKind.Task
)]
[FlowNode("Resolved", "Rejected")]
public class EscalateReviewActivity : Activity
{
    private readonly ILogger<EscalateReviewActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Pull request number</summary>
    [Input(Description = "Pull request number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Escalation reason</summary>
    [Input(Description = "Reason for escalation")]
    public Input<EscalationReason> Reason { get; set; } = default!;

    /// <summary>Number of fix iterations attempted</summary>
    [Input(Description = "Fix iterations attempted", DefaultValue = 0)]
    public Input<int> IterationsAttempted { get; set; } = new(0);

    /// <summary>Escalation message / unresolved comments</summary>
    [Input(Description = "Escalation message")]
    public Input<string?> EscalationMessage { get; set; } = default!;

    /// <summary>The escalation response from the senior</summary>
    [Output(Description = "Senior's escalation response")]
    public Output<EscalationResponsePayload?> EscalationResponse { get; set; } = default!;

    [JsonConstructor]
    public EscalateReviewActivity() { }

    public EscalateReviewActivity(
        ILogger<EscalateReviewActivity> logger,
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
        var prNumber = PRNumber.Get(context);
        var juniorId = JuniorId.Get(context);
        var reason = Reason.Get(context);
        var iterations = IterationsAttempted.Get(context);
        var message = EscalationMessage.Get(context);

        _logger?.LogInformation(
            "Escalating review for PR #{PRNumber}, reason: {Reason}, session {SessionId}",
            prNumber, reason, sessionId);

        try
        {
            // Log the escalation event
            await _repository!.LogEventAsync(new MentorshipEvent
            {
                SessionId = sessionId,
                EventType = EventTypes.EscalationTriggered,
                StateFrom = Core.Enums.MentorshipState.GUIDE_FIXES,
                StateTo = Core.Enums.MentorshipState.ESCALATE_TO_SENIOR
            });

            // Notify the junior
            var junior = await _repository.GetJuniorByIdAsync(juniorId);
            if (junior != null && !string.IsNullOrEmpty(junior.SlackId))
            {
                var reasonText = reason switch
                {
                    EscalationReason.MaxIterationsReached =>
                        $"You have reached the maximum number of fix iterations ({iterations}).",
                    EscalationReason.ReviewTimeout =>
                        "The review has timed out waiting for a response.",
                    EscalationReason.CriticalIssue =>
                        "A critical issue was found that requires senior review.",
                    EscalationReason.MergeConflict =>
                        "There is a merge conflict that needs senior assistance.",
                    _ => "The review requires senior developer input."
                };

                await _integrationService!.SendSlackDirectMessageAsync(
                    junior.SlackId,
                    $"**Tamma: Review Escalated**\n\n" +
                    $"PR #{prNumber} has been escalated to a senior developer.\n" +
                    $"Reason: {reasonText}\n\n" +
                    "A senior developer will review and respond. This is a normal part of " +
                    "the development process and a great learning opportunity!");
            }

            // Send escalation notification to a senior channel
            await _integrationService!.SendSlackMessageAsync(
                "senior-review",
                $"**Tamma: Escalation Required**\n\n" +
                $"PR #{prNumber} needs senior review.\n" +
                $"Developer: {junior?.Name ?? juniorId}\n" +
                $"Reason: {reason}\n" +
                $"Iterations attempted: {iterations}\n" +
                $"{(message != null ? $"Details: {message}" : "")}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Error during escalation notification for session {SessionId}", sessionId);
            // Continue to create the bookmark even if notifications fail
        }

        var bookmarkName = $"escalate-{sessionId}-{prNumber}";

        _logger?.LogInformation(
            "Creating escalation bookmark {BookmarkName}", bookmarkName);

        // Create the bookmark — workflow suspends here waiting for senior response
        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnEscalationResponseAsync,
                AutoBurn = true
            });
    }

    private async ValueTask OnEscalationResponseAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);

        var input = context.WorkflowInput;

        var responderId = input.TryGetValue("ResponderId", out var rid) ? rid?.ToString() ?? "" : "";
        var action = input.TryGetValue("Action", out var act) ? act?.ToString() ?? "" : "";
        var responseMessage = input.TryGetValue("Message", out var msg) ? msg?.ToString() : null;
        var fixCommitSha = input.TryGetValue("FixCommitSha", out var sha) ? sha?.ToString() : null;

        var response = new EscalationResponsePayload
        {
            SessionId = sessionId.ToString(),
            PRNumber = prNumber,
            ResponderId = responderId,
            Action = action,
            Message = responseMessage,
            FixCommitSha = fixCommitSha
        };

        EscalationResponse.Set(context, response);

        _logger?.LogInformation(
            "Escalation response received for PR #{PRNumber}: action={Action} by {Responder}",
            prNumber, action, responderId);

        var outcome = action.ToLowerInvariant() switch
        {
            "approve" or "fix" => "Resolved",
            "reject" => "Rejected",
            _ => "Resolved"
        };

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}

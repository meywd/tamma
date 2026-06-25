using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Review.Models;
using Microsoft.Extensions.Configuration;
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
///   - TimedOut: the senior-response SLA expired with no response (durable timeout)
///
/// <para><b>Durable senior-response SLA timeout (review fix 2026-06-25, code-review P0).</b> Two
/// resume paths are armed when the activity suspends: the escalation bookmark
/// (<c>escalate-{session}-{pr}</c>) resumed by the senior → <see cref="Resolved"/> /
/// <see cref="Rejected"/>; and a DURABLE delay bookmark via
/// <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
/// at the SLA (<c>CodeReview:EscalationTimeoutMinutes</c>, default 1440 = 24h) →
/// <see cref="TimedOut"/>. The build-out armed ONLY the escalation bookmark, so a never-answered
/// escalation suspended forever — there was no path to a terminal at all. The Delay bookmark is
/// EF-persisted and re-armed by <c>Elsa.Scheduling</c>'s startup task on rehydration, so the SLA
/// survives a host restart inside the (default 24h) window. Whichever path resumes first
/// completes the activity; Elsa burns the remaining bookmark (no orphaned timer / double-resume).
/// Mirrors <see cref="Tamma.Activities.Blocker.EscalateToSeniorActivity"/>.</para>
/// </summary>
[Activity(
    "Tamma.Review",
    "Escalate Review",
    "Escalate the review to a senior developer and wait for response (bookmark-based)",
    Kind = ActivityKind.Task
)]
[FlowNode("Resolved", "Rejected", "TimedOut")]
public class EscalateReviewActivity : Activity
{
    private readonly ILogger<EscalateReviewActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;
    private readonly IConfiguration? _configuration;

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
                StateFrom = Tamma.Core.Enums.MentorshipState.GUIDE_FIXES,
                StateTo = Tamma.Core.Enums.MentorshipState.ESCALATE_TO_SENIOR
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

        // 1) Escalation bookmark — resumed by the senior's response (API/Slack). The workflow
        //    suspends here until this resumes OR the durable SLA delay below fires.
        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnEscalationResponseAsync,
                AutoBurn = true
            });

        // 2) Durable senior-response SLA — a DelayFor (Delay) bookmark that Elsa.Scheduling's
        //    startup task RE-ARMS after a host restart (EF-persisted, not an in-memory timer).
        //    A never-answered escalation now terminates as a real TimedOut even across a VPS
        //    restart inside the (default 24h) SLA window — it no longer suspends forever.
        var slaMinutes = _configuration?.GetValue<int?>("CodeReview:EscalationTimeoutMinutes") ?? 1440;
        context.DelayFor(TimeSpan.FromMinutes(Math.Max(1, slaMinutes)), OnTimeoutAsync);

        _logger?.LogInformation(
            "Escalation awaiting senior; durable SLA timeout armed at +{SlaMinutes}min for PR #{PRNumber}",
            slaMinutes, prNumber);
    }

    /// <summary>
    /// Durable timeout path: the senior-response SLA elapsed with no response. The Delay
    /// bookmark scheduler resumes the activity here (and re-arms across a host restart). Takes
    /// the <c>TimedOut</c> outcome instead of suspending forever; the still-armed escalation
    /// bookmark is burned on completion. <see cref="EscalationResponse"/> is left null — there
    /// was no senior response.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var prNumber = PRNumber.Get(context);

        _logger?.LogWarning(
            "Senior-response SLA expired (durable timeout) for PR #{PRNumber}, session {SessionId} — taking the TimedOut outcome",
            prNumber, sessionId);

        EscalationResponse.Set(context, null);
        await context.CompleteActivityWithOutcomesAsync("TimedOut");
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

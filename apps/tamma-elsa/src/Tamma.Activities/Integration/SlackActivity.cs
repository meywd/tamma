using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Integration;

/// <summary>
/// ELSA activity for Slack communication.
///
/// <para>Story 38-3 (Epic 38, Class D) — this activity is a THIN
/// <see cref="TammaApiClient"/> client. It holds no Slack credential and makes no
/// Slack HTTP call: it formats the message engine-side (pure, token-free), then
/// enqueues the notification INTENT via
/// <c>TammaApiClient.QueueSlackNotificationAsync</c> →
/// <c>POST /api/v1/notifications/slack</c>, where the API writes a
/// <c>slack_outbox</c> row and the out-of-band <c>OutboxSlackSender</c> (the sole
/// webhook-credential holder) performs the transport out of band.</para>
///
/// <para><b>Output contract:</b> the <see cref="SlackOperationResult"/> shape is
/// preserved for the mentorship workflows that read it, but <c>Success</c> now
/// means "enqueued" (fire-and-forget), NOT "delivered". <c>WaitingForResponse</c>
/// stays <c>true</c> only for the assessment action (and only when the enqueue
/// succeeded). The local <c>MentorshipEvent</c> session-log write is kept — it is a
/// local repository write, not an external call. Fail-soft: an enqueue failure
/// (API down / non-2xx) returns <c>Success=false</c> and does NOT throw, so a
/// missing Slack post never breaks a mentorship session.</para>
/// </summary>
[Activity(
    "Tamma.Integration",
    "Slack Communication",
    "Send messages via Slack to channels or users",
    Kind = ActivityKind.Task
)]
public class SlackActivity : CodeActivity<SlackOperationResult>
{
    private readonly ILogger<SlackActivity>? _logger;
    private readonly TammaApiClient? _apiClient;
    private readonly IMentorshipSessionRepository? _repository;

    /// <summary>Slack action to perform</summary>
    [Input(Description = "Action: SendChannel, SendDirect, SendAssessment, SendGuidance, SendNotification")]
    public Input<SlackAction> Action { get; set; } = default!;

    /// <summary>Target channel name (for channel messages)</summary>
    [Input(Description = "Channel name")]
    public Input<string?> Channel { get; set; } = default!;

    /// <summary>Target user ID (for direct messages)</summary>
    [Input(Description = "User Slack ID")]
    public Input<string?> UserId { get; set; } = default!;

    /// <summary>Message content</summary>
    [Input(Description = "Message content")]
    public Input<string> Message { get; set; } = default!;

    /// <summary>Session ID for context</summary>
    [Input(Description = "Session ID")]
    public Input<Guid?> SessionId { get; set; } = default!;

    /// <summary>Message type for formatting</summary>
    [Input(Description = "Message type: Info, Warning, Success, Error")]
    public Input<MessageType> MessageType { get; set; } = new(Integration.MessageType.Info);

    /// <summary>Tenant id (GUID string) for the X-Tenant-Id scope; empty = single-user/platform.</summary>
    [Input(Description = "Tenant id (GUID string) for notification scope; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public SlackActivity() { }

    /// <summary>
    /// Story 38-3 — thin-client DI constructor. The activity injects no
    /// Slack-credential-holding integration service and no Slack token: it delegates
    /// the post to <c>POST /api/v1/notifications/slack</c> via
    /// <see cref="TammaApiClient"/>.
    /// </summary>
    public SlackActivity(
        ILogger<SlackActivity> logger,
        TammaApiClient apiClient,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _apiClient = apiClient;
        _repository = repository;
    }

    /// <summary>
    /// Execute the Slack operation — map inputs → intent → enqueue.
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var action = Action.Get(context);
        var channel = Channel.Get(context);
        var userId = UserId.Get(context);
        var message = Message.Get(context);
        var sessionId = SessionId.Get(context);
        var messageType = MessageType.Get(context);
        var tenantId = NormalizeTenant(TenantId.Get(context));

        _logger?.LogInformation("Executing Slack action {Action} (mediated)", action);

        var api = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var repository = _repository ?? context.GetService<IMentorshipSessionRepository>();

        var result = await ExecuteCoreAsync(
            action, channel, userId, message, messageType, sessionId, tenantId,
            (req, tid, ct) => api.QueueSlackNotificationAsync(req, tid, ct),
            repository,
            context.CancellationToken,
            _logger).ConfigureAwait(false);

        context.SetResult(result);
    }

    /// <summary>
    /// Pure-ish, testable execution core (no Elsa context): format the message,
    /// enqueue the intent via <paramref name="enqueue"/>, write the mentorship
    /// session event on enqueue-success, and project the <see cref="SlackOperationResult"/>.
    /// NEVER throws — any failure becomes <c>Success=false</c> (fail-soft, AC10).
    /// </summary>
    public static async Task<SlackOperationResult> ExecuteCoreAsync(
        SlackAction action,
        string? channel,
        string? userId,
        string message,
        MessageType messageType,
        Guid? sessionId,
        string? tenantId,
        Func<SlackNotificationRequest, string?, CancellationToken, Task<bool>> enqueue,
        IMentorshipSessionRepository? repository,
        CancellationToken ct,
        ILogger? logger = null)
    {
        try
        {
            var plan = BuildPlan(action, channel, userId, message, messageType);
            if (plan is null)
            {
                return new SlackOperationResult { Success = false, Message = $"Unknown action: {action}" };
            }

            var request = new SlackNotificationRequest
            {
                Action = action.ToString(),
                Channel = plan.Channel,
                UserId = plan.TargetUserId,
                Message = plan.Body,
                MessageType = messageType.ToString(),
                SessionId = sessionId,
            };

            var queued = await enqueue(request, tenantId, ct).ConfigureAwait(false);

            // Local session log — a local repository write, kept from the legacy
            // activity (not an external call). Only on enqueue-success.
            if (sessionId.HasValue && queued && repository is not null)
            {
                await repository.LogEventAsync(new MentorshipEvent
                {
                    SessionId = sessionId.Value,
                    EventType = EventTypes.Info,
                }).ConfigureAwait(false);
            }

            return new SlackOperationResult
            {
                Success = queued,
                Message = queued ? plan.SuccessMessage : "Notification queue failed",
                Destination = plan.Destination,
                // Fire-and-forget: nothing to await except the assessment action,
                // and only when the enqueue actually succeeded.
                WaitingForResponse = queued && plan.WaitingForResponse,
            };
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Slack operation failed");
            return new SlackOperationResult
            {
                Success = false,
                Message = $"Operation failed: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Map a <see cref="SlackAction"/> + inputs into a token-free notification plan:
    /// the formatted body, the channel/target, the destination label, the
    /// success message, and whether the action awaits a response. Returns
    /// <c>null</c> for an unknown action.
    /// </summary>
    public static SlackNotificationPlan? BuildPlan(
        SlackAction action, string? channel, string? userId, string message, MessageType messageType)
    {
        return action switch
        {
            SlackAction.SendChannel => new SlackNotificationPlan
            {
                Body = FormatMessage(message, messageType),
                Channel = channel,
                TargetUserId = null,
                Destination = channel,
                SuccessMessage = $"Message queued for #{channel}",
                WaitingForResponse = false,
            },
            SlackAction.SendDirect => new SlackNotificationPlan
            {
                Body = FormatMessage(message, messageType),
                Channel = null,
                TargetUserId = userId,
                Destination = userId,
                SuccessMessage = $"DM queued for @{userId}",
                WaitingForResponse = false,
            },
            SlackAction.SendAssessment => new SlackNotificationPlan
            {
                Body = FormatAssessment(message),
                Channel = null,
                TargetUserId = userId,
                Destination = userId,
                SuccessMessage = "Assessment request queued",
                WaitingForResponse = true,
            },
            SlackAction.SendGuidance => new SlackNotificationPlan
            {
                Body = FormatGuidance(message),
                Channel = null,
                TargetUserId = userId,
                Destination = userId,
                SuccessMessage = "Guidance queued",
                WaitingForResponse = false,
            },
            SlackAction.SendNotification => new SlackNotificationPlan
            {
                Body = FormatMessage(message, messageType),
                Channel = string.IsNullOrEmpty(channel) ? null : channel,
                TargetUserId = string.IsNullOrEmpty(userId) ? null : userId,
                Destination = userId ?? channel ?? "unknown",
                SuccessMessage = "Notification queued",
                WaitingForResponse = false,
            },
            _ => null,
        };
    }

    /// <summary>Normalize a tenant-id input to a non-empty trimmed string or null.</summary>
    internal static string? NormalizeTenant(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    internal static string FormatMessage(string message, MessageType type)
    {
        var emoji = type switch
        {
            Integration.MessageType.Info => ":information_source:",
            Integration.MessageType.Warning => ":warning:",
            Integration.MessageType.Success => ":white_check_mark:",
            Integration.MessageType.Error => ":x:",
            Integration.MessageType.Celebration => ":tada:",
            _ => ""
        };

        return $"{emoji} {EscapeSlack(message)}";
    }

    internal static string FormatAssessment(string assessmentContent)
        => $@"**Tamma Assessment Request** :clipboard:

{EscapeSlack(assessmentContent)}

Please respond to this message with your answers.
_This assessment will help me understand your current understanding and provide better guidance._";

    internal static string FormatGuidance(string guidanceContent)
        => $@"**Tamma Guidance** :bulb:

{EscapeSlack(guidanceContent)}

_Reply if you have questions or need more help!_";

    /// <summary>
    /// Neutralize Slack control characters in an UNTRUSTED message body before it is
    /// interpolated into a posted body. Slack's documented escaping (<c>&amp;</c> →
    /// <c>&amp;amp;</c>, <c>&lt;</c> → <c>&amp;lt;</c>, <c>&gt;</c> → <c>&amp;gt;</c>)
    /// renders broadcast/mention tokens like <c>&lt;!channel&gt;</c>, <c>&lt;!here&gt;</c>,
    /// <c>&lt;@U…&gt;</c> literally, so issue/task/AI-derived content can't expand into
    /// pings beyond the intended audience. Applied ONLY to caller-supplied text — never
    /// to our own emoji/label prefixes. Order matters: escape <c>&amp;</c> first so the
    /// replacements it introduces are not double-escaped.
    /// </summary>
    internal static string EscapeSlack(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}

/// <summary>
/// Slack actions available
/// </summary>
public enum SlackAction
{
    SendChannel,
    SendDirect,
    SendAssessment,
    SendGuidance,
    SendNotification
}

/// <summary>
/// Message type for formatting
/// </summary>
public enum MessageType
{
    Info,
    Warning,
    Success,
    Error,
    Celebration
}

/// <summary>
/// Token-free plan for a single Slack notification, produced by
/// <see cref="SlackActivity.BuildPlan"/> — the formatted body plus the routing +
/// output metadata the thin client maps into the wire request and the
/// <see cref="SlackOperationResult"/>.
/// </summary>
public sealed class SlackNotificationPlan
{
    public string Body { get; init; } = string.Empty;
    public string? Channel { get; init; }
    public string? TargetUserId { get; init; }
    public string? Destination { get; init; }
    public string SuccessMessage { get; init; } = string.Empty;
    public bool WaitingForResponse { get; init; }
}

/// <summary>
/// Result of a Slack operation
/// </summary>
public class SlackOperationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Destination { get; set; }
    public bool WaitingForResponse { get; set; }
}

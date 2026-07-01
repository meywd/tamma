namespace Tamma.Api.Services.Notifications;

/// <summary>
/// Story 38-3 — DCB event type constants for the Slack notification outbox
/// terminal outcomes. Emitted from <c>Tamma.Api</c> (where the credential + the
/// tenant store live) to <c>platform_events</c> via <c>IPlatformEventRepository</c>
/// — the CP-outbox audit plane, NOT the tenant event store (mirrors the
/// <c>OutboxSmtpSender</c> platform-path audit). Payloads are key-free.
/// </summary>
public static class NotificationSlackEventTypes
{
    /// <summary>Terminal success — the Slack post was delivered by the sender.</summary>
    public const string Sent = "NOTIFICATION.SLACK.SENT.SUCCESS";

    /// <summary>A delivery attempt failed (transient-with-backoff or terminal).</summary>
    public const string Failed = "NOTIFICATION.SLACK.SEND.FAILED";
}

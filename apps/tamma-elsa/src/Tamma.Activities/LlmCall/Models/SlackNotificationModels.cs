using System.Text.Json.Serialization;

namespace Tamma.Activities.LlmCall.Models;

/// <summary>
/// Story 38-3 (Epic 38, Class D) — the engine→API wire contract for the Slack
/// notification mediation endpoint <c>POST /api/v1/notifications/slack</c>.
///
/// <para>The engine's thin <c>SlackActivity</c> maps its inputs into this record
/// and posts it via <c>TammaApiClient.QueueSlackNotificationAsync</c>. The
/// message body is ALREADY formatted engine-side (pure emoji/assessment/guidance
/// templating — no secret), so the API only writes the outbox row + drains it.
/// The row carries NO Slack credential; the webhook lives only in
/// <c>OutboxSlackSender</c>. The acting tenant is the authenticated
/// <c>X-Tenant-Id</c> the client attaches — this body carries no tenant
/// authority.</para>
///
/// <para>Camel-case <see cref="JsonPropertyName"/> matches the API's default
/// web JSON serialization; the record lives in <c>Tamma.Activities</c> because
/// the reference graph runs <c>Tamma.Api → Tamma.Activities</c>, so both the
/// engine client and the API endpoint bind the SAME type.</para>
/// </summary>
public sealed record SlackNotificationRequest
{
    /// <summary>SendChannel | SendDirect | SendAssessment | SendGuidance | SendNotification.</summary>
    [JsonPropertyName("action")] public string Action { get; init; } = string.Empty;

    /// <summary>Target channel for a channel post.</summary>
    [JsonPropertyName("channel")] public string? Channel { get; init; }

    /// <summary>Target Slack user id for a DM (NOT a Tamma UserId).</summary>
    [JsonPropertyName("userId")] public string? UserId { get; init; }

    /// <summary>The already-formatted message body (token-free).</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;

    /// <summary>Info | Warning | Success | Error | Celebration (carried for audit).</summary>
    [JsonPropertyName("messageType")] public string MessageType { get; init; } = "Info";

    /// <summary>Mentorship session context (logged; never transmitted to Slack).</summary>
    [JsonPropertyName("sessionId")] public Guid? SessionId { get; init; }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Logging;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 38-3 (Epic 38, Class D) — the internal, engine-only Slack notification
/// mediation endpoint (<c>POST /api/v1/notifications/slack</c>). Same auth plane
/// as <c>/api/v1/llm/call</c> / <c>/api/v1/git/...</c> / <c>/api/v1/agent-dispatch/...</c>:
/// <list type="bullet">
///   <item><b>Auth</b> — the <c>EngineServiceOnly</c> policy (the engine posts the
///     service-scope <c>Tamma:ApiToken</c> Bearer via <c>TammaEngineAuthHandler</c>).
///     A missing/invalid bearer ⇒ 401; a user JWT ⇒ 403 — both BEFORE the handler.</item>
///   <item><b>Tenant scope</b> — the acting tenant is the auth-derived
///     <see cref="ITenantContext"/> (X-Tenant-Id), NEVER the request body. There is
///     no tenant↔repo guard (Slack is not repo-scoped).</item>
///   <item><b>Outbox, not transport</b> — the handler writes ONE <c>pending</c>
///     <see cref="SlackOutboxMessage"/> row and returns 202. It NEVER calls Slack
///     synchronously and NEVER reads the webhook credential; the out-of-band
///     <c>OutboxSlackSender</c> is the sole token-holder.</item>
/// </list>
/// </summary>
public static class NotificationEndpoints
{
    /// <summary>Slack's practical block-text limit; the CP outbox body is capped here.</summary>
    private const int MaxBodyLength = 4000;

    /// <summary>Suffix appended when an over-cap body is truncated.</summary>
    private const string TruncationMarker = "… [truncated]";

    /// <summary>
    /// Handle <c>POST /api/v1/notifications/slack</c>. Binds the engine's
    /// <see cref="SlackNotificationRequest"/> (already-formatted body), scopes the
    /// outbox row by the auth-derived tenant (SaaS → <c>TenantId</c>; single-user →
    /// <c>TenantId</c> null, no per-user identity at the service-principal plane),
    /// persists intent, and returns 202 + <c>{ outboxId, outboxIds }</c>.
    ///
    /// <para>A <c>SendNotification</c> intent can carry BOTH a channel and a target
    /// user; each is written as an INDEPENDENT single-target row (channel XOR DM) so
    /// the legs claim, retry, and fail on their own — a channel-leg retry never
    /// re-sends the DM, and a DM failure never blocks the channel post. A request with
    /// neither target is rejected 400 (it could only burn every retry on "no channel
    /// or target user"). The body is length-capped so an unbounded engine payload
    /// can't bloat a control-plane row.</para>
    /// </summary>
    public static async Task<IResult> QueueSlack(
        SlackNotificationRequest request,
        ITenantContext tenantContext,
        ISlackOutboxRepository outbox,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.NotificationEndpoints");

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(new { error = "message is required" });
        }

        var channel = string.IsNullOrWhiteSpace(request.Channel) ? null : request.Channel;
        var targetUser = string.IsNullOrWhiteSpace(request.UserId) ? null : request.UserId;

        // A row with neither a channel nor a target user can never deliver — reject
        // up-front instead of writing a row that fails all its retries.
        if (channel is null && targetUser is null)
        {
            return Results.BadRequest(new { error = "channel or userId is required" });
        }

        // Cap the persisted body — Slack's practical block limit is ~4000 chars and an
        // unbounded engine payload should not grow the control-plane row without bound.
        var body = request.Message.Length > MaxBodyLength
            ? string.Concat(request.Message.AsSpan(0, MaxBodyLength - TruncationMarker.Length), TruncationMarker)
            : request.Message;

        // The AUTHORITATIVE owner scope is the auth-derived ambient tenant
        // (X-Tenant-Id) — the body carries no tenant authority. null ⇒
        // single-user / platform scope.
        var tenantId = tenantContext.TenantId;
        var messageType = string.IsNullOrWhiteSpace(request.MessageType) ? "Info" : request.MessageType;

        // Split a both-targets intent into independent single-target rows.
        var rows = new List<SlackOutboxMessage>(2);
        if (channel is not null)
        {
            rows.Add(NewRow(tenantId, channel, targetUser: null, messageType, body));
        }
        if (targetUser is not null)
        {
            rows.Add(NewRow(tenantId, channel: null, targetUser, messageType, body));
        }

        var savedIds = new List<Guid>(rows.Count);
        foreach (var row in rows)
        {
            var saved = await outbox.EnqueueAsync(row, ct).ConfigureAwait(false);
            savedIds.Add(saved.Id);

            logger.LogInformation(
                "slack-notification queued: outboxId={OutboxId}, action={Action}, channel={Channel}, "
                + "targetUser={TargetUser}, sessionId={SessionId}, tenantId={TenantId}",
                saved.Id,
                LogSanitizer.Clean(request.Action),
                LogSanitizer.Clean(saved.Channel),
                LogSanitizer.Clean(saved.TargetUserId),
                request.SessionId,
                saved.TenantId);
        }

        var primaryId = savedIds[0];
        return Results.Accepted(
            $"/api/v1/notifications/slack/{primaryId}",
            new { outboxId = primaryId, outboxIds = savedIds });
    }

    /// <summary>Build one single-target (channel XOR DM) pending outbox row.</summary>
    private static SlackOutboxMessage NewRow(
        Guid? tenantId, string? channel, string? targetUser, string messageType, string body)
        => new()
        {
            TenantId = tenantId,
            UserId = null, // engine-service principal carries no per-user identity; TenantId is the scope.
            Channel = channel,
            TargetUserId = targetUser,
            MessageType = messageType,
            Body = body,
        };
}

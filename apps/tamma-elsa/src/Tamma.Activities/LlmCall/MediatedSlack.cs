using Elsa.Extensions;
using Elsa.Workflows;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Redaction;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Story 38-3b (Epic 38, Class D) — the shared engine→API Slack seam the cut-over
/// domain activities (Mentorship / Review / Blocker / Assessment) use INSTEAD of a
/// direct <c>IIntegrationService.SendSlack*Async</c> call (a rule-1 violation: the
/// engine holds no Slack credential).
///
/// <para>It enqueues a Slack notification INTENT via
/// <see cref="TammaApiClient.QueueSlackNotificationAsync"/> →
/// <c>POST /api/v1/notifications/slack</c>, where the API writes a
/// <c>slack_outbox</c> row and the out-of-band <c>OutboxSlackSender</c> (the sole
/// webhook-credential holder) performs the transport. The engine holds NO Slack
/// token; this seam mirrors <see cref="MediatedLlmText"/> for the LLM path.</para>
///
/// <para><b>Fire-and-forget + fail-soft (a deliberate, correct semantics change).</b>
/// No legacy caller branched on the Slack return value, but the old composite
/// <c>IntegrationService.SendSlack*Async</c> actually THREW on failure (unset
/// <c>Slack:WebhookUrl</c> or a transport error), so a notification failure sitting
/// inside an activity's success <c>try</c> could flip that activity to its Failure
/// outcome. This seam INTENTIONALLY makes Slack non-fatal: a false enqueue (API down /
/// non-2xx) or an unwired client is returned to the caller but NEVER throws and NEVER
/// changes the activity's outcome — a missing Slack post must not break a
/// mentorship/review run (and this also fixes the latent "PR merged but reported
/// Failed because the notify threw" bug). Each activity formats its own body engine-side;
/// this seam does no re-formatting BUT is the exactly-once point at which the untrusted
/// body is hardened against Slack control tokens via
/// <see cref="SlackTextSanitizer.Escape"/> — so <c>&lt;!channel&gt;</c> / <c>&lt;!here&gt;</c> /
/// <c>&lt;@Uxxxx&gt;</c> / <c>&lt;!subteam^Sxxx&gt;</c> derived from issue titles or LLM output
/// can never trigger a broadcast/mention ping. The escape is applied when the request is
/// BUILT (before it is enqueued to <c>slack_outbox</c>), and neither the notification
/// endpoint nor <c>OutboxSlackSender</c> re-escape, so every mediated send is escaped
/// exactly once. (The engine-side <c>SlackActivity</c> escapes in its own formatters via
/// the same shared helper; it does not use this seam.)</para>
/// </summary>
internal static class MediatedSlack
{
    /// <summary>
    /// Enqueue a Slack DIRECT MESSAGE intent for <paramref name="slackUserId"/> from
    /// inside an activity. Resolves the <see cref="TammaApiClient"/> and the ambient
    /// tenant scope from <paramref name="context"/>; fire-and-forget, never throws.
    /// </summary>
    public static Task<bool> QueueDirectMessageAsync(
        ActivityExecutionContext context, string slackUserId, string message,
        string messageType, string action, CancellationToken ct)
        => EnqueueAsync(
            context.GetService<TammaApiClient>(),
            ResolveTenantId(context),
            BuildDirectMessage(slackUserId, message, messageType, action),
            ct);

    /// <summary>
    /// Enqueue a Slack CHANNEL POST intent for <paramref name="channel"/> from inside
    /// an activity. Same seam + fail-soft contract as
    /// <see cref="QueueDirectMessageAsync"/>.
    /// </summary>
    public static Task<bool> QueueChannelMessageAsync(
        ActivityExecutionContext context, string channel, string message,
        string messageType, string action, CancellationToken ct)
        => EnqueueAsync(
            context.GetService<TammaApiClient>(),
            ResolveTenantId(context),
            BuildChannelMessage(channel, message, messageType, action),
            ct);

    /// <summary>
    /// Pure builder: a single-target DM request (channel = null). The untrusted
    /// <paramref name="message"/> is hardened against Slack control tokens exactly once
    /// here via <see cref="SlackTextSanitizer.Escape"/> — see the class remarks.
    /// </summary>
    public static SlackNotificationRequest BuildDirectMessage(
        string slackUserId, string message, string messageType, string action)
        => new()
        {
            Action = action,
            Channel = null,
            UserId = slackUserId,
            Message = SlackTextSanitizer.Escape(message),
            MessageType = string.IsNullOrWhiteSpace(messageType) ? "Info" : messageType,
        };

    /// <summary>
    /// Pure builder: a single-target channel-post request (userId = null). The untrusted
    /// <paramref name="message"/> is hardened against Slack control tokens exactly once
    /// here via <see cref="SlackTextSanitizer.Escape"/> — see the class remarks.
    /// </summary>
    public static SlackNotificationRequest BuildChannelMessage(
        string channel, string message, string messageType, string action)
        => new()
        {
            Action = action,
            Channel = channel,
            UserId = null,
            Message = SlackTextSanitizer.Escape(message),
            MessageType = string.IsNullOrWhiteSpace(messageType) ? "Info" : messageType,
        };

    /// <summary>
    /// Context-free enqueue (unit-tested with a fake <see cref="TammaApiClient"/>).
    /// A null client (engine unwired) or a false enqueue returns <c>false</c> — never
    /// throws — so the caller stays fire-and-forget and its outcome is unchanged.
    /// </summary>
    public static async Task<bool> EnqueueAsync(
        TammaApiClient? api, string? tenantId, SlackNotificationRequest request, CancellationToken ct)
    {
        if (api is null)
            return false;
        return await api.QueueSlackNotificationAsync(request, tenantId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the tenant scope (X-Tenant-Id) from the workflow's ambient tenant
    /// variable — identical convention to <see cref="MediatedLlmText"/> /
    /// <c>EventPersistenceMiddleware</c>: read <c>TenantId</c> (legacy fallback
    /// <c>AccountId</c>) as an <c>object</c> (it may be a <see cref="Guid"/> or a
    /// string) and coerce to a canonical Guid string. An empty / unset / non-Guid
    /// value ⇒ single-user / platform scope (null); the endpoint handles null.
    /// </summary>
    internal static string? ResolveTenantId(ActivityExecutionContext context)
    {
        var raw = context.GetVariable<object?>("TenantId")
                  ?? context.GetVariable<object?>("AccountId");
        return raw switch
        {
            Guid g when g != Guid.Empty => g.ToString(),
            string s when Guid.TryParse(s, out var p) && p != Guid.Empty => p.ToString(),
            _ => null,
        };
    }
}

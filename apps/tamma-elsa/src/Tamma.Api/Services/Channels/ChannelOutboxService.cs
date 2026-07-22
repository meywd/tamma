using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Tamma.Activities.Documents;
using Tamma.Api.Hubs;
using Tamma.Api.Services.Access;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Channels;

/// <summary>
/// Story 39-18 (D4/D6/D7/D8) — the write path for the channel outbox. Validates the
/// kind→audience pairing (fail-loud), fans out role-addressed task messages to one
/// row per resolver-approved recipient (<see cref="ITaskAudienceResolver"/>),
/// persists BEFORE any hub send (the transport is never the source of truth — AC6),
/// emits <c>GUIDANCE.*</c> fail-loud, refuses a direct conversation-kind enqueue (chat
/// is 39-19's, D8), THEN best-effort publishes to the hub group (never throws into the
/// caller — the <c>ILlmRunStreamBus</c> producer invariant: a hub send to zero
/// connections is a silent no-op, which is exactly the degraded case).
/// </summary>
public sealed class ChannelOutboxService
{
    private readonly IChannelOutboxRepository _outbox;
    private readonly IEventRepository _events;
    private readonly ITaskAudienceResolver _audience;
    private readonly IHubContext<OrchestratorChannelHub, IOrchestratorChannelClient> _orchestratorHub;
    private readonly IHubContext<UserChannelHub, IUserChannelClient> _userHub;
    private readonly ILogger<ChannelOutboxService> _logger;

    public ChannelOutboxService(
        IChannelOutboxRepository outbox,
        IEventRepository events,
        ITaskAudienceResolver audience,
        IHubContext<OrchestratorChannelHub, IOrchestratorChannelClient> orchestratorHub,
        IHubContext<UserChannelHub, IUserChannelClient> userHub,
        ILogger<ChannelOutboxService> logger)
    {
        _outbox = outbox;
        _events = events;
        _audience = audience;
        _orchestratorHub = orchestratorHub;
        _userHub = userHub;
        _logger = logger;
    }

    /// <summary>
    /// Persist + best-effort publish a channel envelope. Fans out user-audience
    /// task messages to per-recipient rows. Returns the persisted rows (for tests /
    /// callers). Throws <see cref="TammaError"/> (<c>CHANNEL.MESSAGE.INVALID</c>) on a
    /// bad kind→audience pairing or a direct conversation-kind enqueue.
    /// </summary>
    public async Task<IReadOnlyList<ChannelOutboxMessage>> EnqueueAsync(ChannelEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.TenantId == Guid.Empty)
            throw Invalid("tenant", "A channel message requires a non-empty tenant id (server-derived).");

        var kind = ChannelMessageKinds.KindOf(envelope.Message);

        // D8 — conversation kinds are minted ONLY by 39-19's chat service (which has
        // already recorded them). A direct conversation enqueue is refused so nothing
        // can cross un-evented (AC6).
        if (kind == ChannelMessageKinds.AgentConversation)
            throw Invalid(kind, "Conversation kinds are recorded + enqueued only by the 39-19 chat service, never directly.");

        // Server-derive-and-validate the audience: the canonical audience for the kind
        // must match the envelope's audience (never trusted blindly from a payload).
        var canonical = ChannelMessageKinds.AudienceFor(kind)
            ?? throw Invalid(kind, $"'{kind}' is not a direct-enqueue channel kind.");
        if (envelope.Audience != canonical)
            throw Invalid(kind,
                $"kind '{kind}' must travel on the '{canonical.ToWire()}' channel, not '{envelope.Audience.ToWire()}'.");

        var rows = envelope.Audience == ChannelAudience.User
            ? await PersistUserFanOutAsync(envelope, kind, ct)
            : new[] { await PersistSingleAsync(envelope, kind, envelope.RecipientUserId, ct) };

        // GUIDANCE.* is THIS story's only event family (D8) — fail-loud (the event IS
        // part of the operation), appended AFTER the row persists, BEFORE publish.
        await MaybeEmitGuidanceAsync(envelope, kind);

        // Best-effort hub publish — never throws into the caller (degraded = the row waits).
        foreach (var row in rows)
            await PublishBestEffortAsync(row, envelope, ct);

        return rows;
    }

    /// <summary>
    /// Re-publish a stale outbox row (the sweeper path): deserialize the stored
    /// envelope and best-effort push it to the hub group again. Covers
    /// crash-between-persist-and-publish and missed reconnect races. Never throws.
    /// </summary>
    public async Task RepublishAsync(ChannelOutboxMessage row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ChannelEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ChannelEnvelope>(row.PayloadJson, DocumentJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "channel_outbox sweeper: row {MessageId} payload undeserializable; skipping.", row.Id);
            return;
        }
        if (envelope is null) return;
        await PublishBestEffortAsync(row, envelope, ct);
    }

    // ── persistence ─────────────────────────────────────────────────────────

    private async Task<ChannelOutboxMessage> PersistSingleAsync(
        ChannelEnvelope envelope, string kind, Guid? recipientUserId, CancellationToken ct)
    {
        var perRecipient = envelope with { RecipientUserId = recipientUserId };
        var row = new ChannelOutboxMessage
        {
            Id = envelope.MessageId,
            TenantId = envelope.TenantId,
            Audience = envelope.Audience.ToWire(),
            RecipientUserId = recipientUserId,
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(perRecipient, DocumentJson.Options),
            DecisionSessionId = DecisionSessionIdOf(envelope.Message),
            CreatedAt = envelope.CreatedAt.UtcDateTime,
        };
        return await _outbox.EnqueueAsync(row, ct);
    }

    private async Task<ChannelOutboxMessage[]> PersistUserFanOutAsync(
        ChannelEnvelope envelope, string kind, CancellationToken ct)
    {
        // Only role-addressed task messages fan out; any other user-audience kind is a
        // single directed row. Today only task-assigned is role-addressed.
        if (envelope.Message is not TaskAssigned task)
            return new[] { await PersistSingleAsync(envelope, kind, envelope.RecipientUserId, ct) };

        var taskRef = new TaskRef(envelope.TenantId, InitiatorUserId: null, RepoKey: null, IssueId: task.IssueId);
        var members = await _audience.EligibleAudienceAsync(taskRef, task.AssignedRole);

        var rows = new List<ChannelOutboxMessage>(members.Count);
        var seen = new HashSet<Guid>();
        foreach (var member in members)
        {
            if (!seen.Add(member.UserId)) continue; // one row per user, even across roles.
            // Each recipient gets its own row (per-user ack) with a distinct message id
            // so acks/dedup are per-recipient, not shared.
            var perRecipient = envelope with { MessageId = UuidV7.NewGuid(), RecipientUserId = member.UserId };
            var row = new ChannelOutboxMessage
            {
                Id = perRecipient.MessageId,
                TenantId = envelope.TenantId,
                Audience = envelope.Audience.ToWire(),
                RecipientUserId = member.UserId,
                Kind = kind,
                PayloadJson = JsonSerializer.Serialize(perRecipient, DocumentJson.Options),
                DecisionSessionId = task.DecisionSessionId,
                CreatedAt = envelope.CreatedAt.UtcDateTime,
            };
            rows.Add(await _outbox.EnqueueAsync(row, ct));
        }

        if (rows.Count == 0)
            _logger.LogInformation(
                "channel_outbox task fan-out resolved zero recipients for issue {Issue} role {Role} — nothing enqueued (fail-closed).",
                task.IssueId, task.AssignedRole);

        return rows.ToArray();
    }

    // ── guidance events (D8) ────────────────────────────────────────────────

    private async Task MaybeEmitGuidanceAsync(ChannelEnvelope envelope, string kind)
    {
        switch (envelope.Message)
        {
            case GuidanceQuery q:
                await AppendGuidanceAsync(envelope, ChannelEvents.GuidanceRequested, q.CorrelationId, new Dictionary<string, object?>
                {
                    ["queryId"] = q.QueryId.ToString(),
                    ["question"] = Bounded(q.Question, 512),
                    ["correlationId"] = q.CorrelationId,
                });
                break;
            case GuidanceReply r:
                await AppendGuidanceAsync(envelope, ChannelEvents.GuidanceProvided, correlationId: null, new Dictionary<string, object?>
                {
                    ["queryId"] = r.QueryId.ToString(),
                    ["replyDigest"] = Bounded(r.Reply, 256),
                });
                break;
        }
    }

    private async Task AppendGuidanceAsync(
        ChannelEnvelope envelope, string type, string? correlationId, Dictionary<string, object?> data)
    {
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = envelope.TenantId.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(correlationId)) tags["correlationId"] = correlationId;
        if (envelope.RecipientUserId is { } uid) tags["userId"] = uid.ToString();

        // FAIL-LOUD — the event IS part of the operation (the 39-8 disposition posture),
        // not a best-effort side-car; an append failure propagates.
        await _events.AppendAsync(new DomainEvent
        {
            Type = type,
            TenantId = envelope.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }

    // ── publish ─────────────────────────────────────────────────────────────

    private async Task PublishBestEffortAsync(ChannelOutboxMessage row, ChannelEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var perRecipient = envelope with { MessageId = row.Id, RecipientUserId = row.RecipientUserId };
            if (envelope.Audience == ChannelAudience.Orchestrator)
            {
                await _orchestratorHub.Clients
                    .Group(OrchestratorChannelHub.GroupFor(envelope.TenantId))
                    .Receive(perRecipient);
            }
            else if (row.RecipientUserId is { } recipient)
            {
                await _userHub.Clients
                    .Group(UserChannelHub.UserGroupFor(envelope.TenantId, recipient))
                    .Receive(perRecipient);
            }

            // Deliberately do NOT mark the row delivered here. A SignalR group send to
            // zero connections is a silent no-op — the transport cannot confirm anyone
            // actually received it. The row stays `pending` (degraded-mode contract,
            // AC7) and is transitioned to `delivered` only by the hub's connect-time
            // replay (OnConnectedAsync → Receive → MarkDeliveredAsync), which fires
            // exactly when a real consumer is on the wire. Ack is idempotent and the
            // sweeper re-publishes stale rows, so a live consumer that received the
            // write-time push still settles the row via its Ack.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A hub send to zero connections is a silent no-op; a genuine publish error
            // must not throw into the caller — the row stays pending and the sweeper /
            // connect-time replay delivers it.
            _logger.LogWarning(ex,
                "channel_outbox publish best-effort failed for row {MessageId} (row stays pending; replay/sweeper covers it).",
                row.Id);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Guid? DecisionSessionIdOf(ChannelMessage message) => message switch
    {
        AcceptanceRequested a => a.Request.DecisionSessionId,
        TaskAssigned t => t.DecisionSessionId,
        _ => null,
    };

    private static string Bounded(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    private static TammaError Invalid(string field, string message) =>
        new(
            "CHANNEL.MESSAGE.INVALID",
            message,
            new Dictionary<string, object?> { ["field"] = field },
            retryable: false,
            severity: TammaErrorSeverity.High);
}

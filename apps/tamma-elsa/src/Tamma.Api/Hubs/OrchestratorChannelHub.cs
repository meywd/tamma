using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Channels;
using Tamma.Api.Services.Documents;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Data.Repositories;

namespace Tamma.Api.Hubs;

/// <summary>
/// Story 39-18 (D2/D5/D7) — the workflow↔orchestrator SignalR hub. Engine/agent
/// traffic only: gated by the <c>OrchestratorChannel</c> policy (service-principal OR
/// orchestrator-claim; a tenant member/admin/owner JWT fails it). Groups are derived
/// from the connection's claims ONLY — no client method takes a group name, so a
/// forged group-join is structurally impossible (D5).
///
/// <para>Decision methods DELEGATE to the 39-8 idempotent resume surface via the
/// shared <see cref="DocumentDecisionSubmissionService"/> — the hub NEVER applies a
/// decision or mutates outbox/decision state itself (D7). The 404/409 gate-not-waiting
/// result is returned to the caller so the agent observes idempotency outcomes.</para>
/// </summary>
[Authorize("OrchestratorChannel")]
public sealed class OrchestratorChannelHub : Hub<IOrchestratorChannelClient>
{
    // Connect-time replay is bounded — an orchestrator that has been offline a long
    // time drains its backlog across reconnects rather than in one unbounded read.
    private const int ReplayLimit = 500;

    private readonly IChannelOutboxRepository _outbox;
    private readonly DocumentDecisionSubmissionService _decisions;
    private readonly EscalationDispositionService _escalations;
    private readonly ChannelOutboxService _channels;
    private readonly IOrchestratorChatRelay _chat;
    private readonly ILogger<OrchestratorChannelHub> _logger;

    public OrchestratorChannelHub(
        IChannelOutboxRepository outbox,
        DocumentDecisionSubmissionService decisions,
        EscalationDispositionService escalations,
        ChannelOutboxService channels,
        IOrchestratorChatRelay chat,
        ILogger<OrchestratorChannelHub> logger)
    {
        _outbox = outbox;
        _decisions = decisions;
        _escalations = escalations;
        _channels = channels;
        _chat = chat;
        _logger = logger;
    }

    /// <summary>The orchestrator group for a tenant. Derived server-side, never from a client.</summary>
    public static string GroupFor(Guid tenantId) => $"orchestrator:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        var tenantId = ResolveTenantId();
        if (tenantId == Guid.Empty)
        {
            // The policy admitted the principal but no tenant could be derived — abort
            // rather than joining a wildcard group.
            _logger.LogWarning("OrchestratorChannelHub connect with no derivable tenant id; aborting.");
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(tenantId));
        await ReplayUnackedAsync(tenantId);
        await base.OnConnectedAsync();
    }

    /// <summary>Idempotently ack a delivered orchestrator-audience row.</summary>
    public async Task<AckResult> Ack(Guid messageId)
    {
        var tenantId = ResolveTenantId();
        if (tenantId == Guid.Empty) return new AckResult(false);
        var acked = await _outbox.AckAsync(tenantId, messageId, recipientUserId: null);
        return new AckResult(acked);
    }

    /// <summary>
    /// Submit a 39-5 decision JSON for the suspended gate on <paramref name="sessionId"/>.
    /// Delegates to the shared resume service with the server-derived orchestrator
    /// channel (D7). The hub applies nothing itself; the 404/409 result is returned.
    /// </summary>
    public Task<DecisionSubmitResult> SubmitDecision(Guid sessionId, string decisionJson, string? feedback)
    {
        var tenantId = NullableTenant();
        var deciderId = ResolveDecider();
        // Channel is server-derived from the connection principal (→ orchestrator).
        var channel = ApprovalChannels.Derive(Context.User ?? new ClaimsPrincipal());
        var kind = ParseDecisionKind(decisionJson);
        return _decisions.ResumeAsync(sessionId, decisionJson, feedback, tenantId, deciderId, channel, kind);
    }

    /// <summary>Disposition an escalation. Delegates to 39-8's <see cref="EscalationDispositionService"/>.</summary>
    public async Task<EscalationDispositionSubmitResult> SubmitEscalationDisposition(
        string escalationId, string disposition, string? note)
    {
        var tenantId = NullableTenant();
        var deciderId = ResolveDecider();
        var channel = ApprovalChannels.Derive(Context.User ?? new ClaimsPrincipal());

        Tamma.Core.Documents.EscalationDisposition parsed;
        try
        {
            parsed = EscalationDispositionExtensions.Parse(disposition);
        }
        catch (TammaError)
        {
            return new EscalationDispositionSubmitResult(false, NotFound: false, AlreadyResolved: false, Invalid: true, 0);
        }

        var result = await _escalations.DispositionAsync(tenantId, escalationId, parsed, note, deciderId, channel);
        return result.Outcome switch
        {
            EscalationDispositionOutcome.NotFound => new EscalationDispositionSubmitResult(false, true, false, false, 0),
            EscalationDispositionOutcome.AlreadyResolved => new EscalationDispositionSubmitResult(false, false, true, false, 0),
            _ => new EscalationDispositionSubmitResult(true, false, false, false, result.DurationMs),
        };
    }

    /// <summary>
    /// Answer a guidance query. Enqueues the reply outbox row + <c>GUIDANCE.PROVIDED</c>
    /// event (via <see cref="ChannelOutboxService"/>) and publishes back to the
    /// requesting workflow's watchers.
    /// </summary>
    public async Task SubmitGuidanceReply(Guid queryId, string reply)
    {
        var tenantId = ResolveTenantId();
        if (tenantId == Guid.Empty) return;

        var envelope = new ChannelEnvelope(
            MessageId: UuidV7.NewGuid(),
            TenantId: tenantId,
            Audience: ChannelAudience.Orchestrator,
            RecipientUserId: null,
            Message: new GuidanceReply(queryId, reply),
            CreatedAt: DateTimeOffset.UtcNow);

        await _channels.EnqueueAsync(envelope, Context.ConnectionAborted);
    }

    /// <summary>
    /// The agent→user chat leg. Hands off to 39-19's chat service (records SENT, then
    /// relays). Until 39-19 lands the stand-in refuses with an agent-offline result.
    /// </summary>
    public async Task<ChatRelayResult> SendAgentReply(AgentConversationMessage message)
    {
        var tenantId = ResolveTenantId();
        if (tenantId == Guid.Empty) return ChatRelayResult.Offline;
        return await _chat.RelayAgentReplyAsync(tenantId, message, Context.ConnectionAborted);
    }

    // ── internals ─────────────────────────────────────────────────────────

    private async Task ReplayUnackedAsync(Guid tenantId)
    {
        var rows = await _outbox.ListUnackedAsync(tenantId, ChannelAudience.Orchestrator.ToWire(), recipientUserId: null, ReplayLimit);
        foreach (var row in rows)
        {
            ChannelEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ChannelEnvelope>(row.PayloadJson, DocumentJson.Options);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "channel_outbox replay: row {MessageId} has an undeserializable payload; skipping.", row.Id);
                continue;
            }
            if (envelope is null) continue;

            await Clients.Caller.Receive(envelope);
            await _outbox.MarkDeliveredAsync(tenantId, row.Id);
        }
    }

    private Guid ResolveTenantId()
    {
        var claim = Context.User?.FindFirst("tenantId")?.Value;
        if (Guid.TryParse(claim, out var fromClaim) && fromClaim != Guid.Empty)
            return fromClaim;

        // Pure service-principal fallback (no tenantId claim): a required `tenant`
        // query-string value. Orchestrator-claim principals carry their tenant in the
        // claim set (the 39-17 contract), so this only serves the service path.
        var http = Context.GetHttpContext();
        var query = http?.Request.Query["tenant"].ToString();
        return Guid.TryParse(query, out var fromQuery) ? fromQuery : Guid.Empty;
    }

    private Guid? NullableTenant()
    {
        var tenantId = ResolveTenantId();
        return tenantId == Guid.Empty ? null : tenantId;
    }

    private string ResolveDecider() =>
        Context.User?.FindFirst("email")?.Value
        ?? Context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? Context.User?.FindFirst("name")?.Value
        ?? Context.UserIdentifier
        ?? "orchestrator";

    private static string ParseDecisionKind(string decisionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(decisionJson);
            return doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() ?? "unknown"
                : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }
}

/// <summary>Idempotent ack outcome — <c>Acked=true</c> only when THIS call transitioned the row.</summary>
public sealed record AckResult(bool Acked);

/// <summary>Escalation disposition outcome surfaced to the hub caller (maps to 200/400/404/409).</summary>
public sealed record EscalationDispositionSubmitResult(
    bool Resolved, bool NotFound, bool AlreadyResolved, bool Invalid, long DurationMs);

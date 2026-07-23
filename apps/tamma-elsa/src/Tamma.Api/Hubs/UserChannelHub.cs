using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Tamma.Api.Auth;
using Tamma.Api.Services.Channels;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Data.Repositories;

namespace Tamma.Api.Hubs;

/// <summary>
/// Story 39-18 (D5/D9) — the user↔orchestrator/platform SignalR hub. Dashboard users
/// only: gated by <c>MemberAccess</c> (any authenticated tenant member). Groups —
/// <c>tenant:{t}</c> and <c>user:{t}:{u}</c> — are computed from the JWT claims in
/// <see cref="OnConnectedAsync"/>; NO client method takes a group name, so a client
/// can never subscribe itself into another tenant's or user's traffic (forged join is
/// structurally impossible).
///
/// <para>There is deliberately NO task-action method (D7): acting on a task travels
/// today's REST resume endpoints, not the hub. The only client→server method is the
/// user→agent chat leg, which stamps the user id from the claims (never the payload)
/// and hands off to 39-19's chat service (agent-offline stand-in until it lands).</para>
/// </summary>
[Authorize("MemberAccess")]
public sealed class UserChannelHub : Hub<IUserChannelClient>
{
    private const int ReplayLimit = 500;

    private readonly IChannelOutboxRepository _outbox;
    private readonly IOrchestratorChatRelay _chat;
    private readonly ILogger<UserChannelHub> _logger;

    public UserChannelHub(
        IChannelOutboxRepository outbox,
        IOrchestratorChatRelay chat,
        ILogger<UserChannelHub> logger)
    {
        _outbox = outbox;
        _chat = chat;
        _logger = logger;
    }

    /// <summary>The tenant-wide group for a tenant.</summary>
    public static string TenantGroupFor(Guid tenantId) => $"tenant:{tenantId}";

    /// <summary>The per-user group — the grain task/chat traffic targets.</summary>
    public static string UserGroupFor(Guid tenantId, Guid userId) => $"user:{tenantId}:{userId}";

    public override async Task OnConnectedAsync()
    {
        var (tenantId, userId) = ResolvePrincipal();
        if (tenantId == Guid.Empty || userId == Guid.Empty)
        {
            _logger.LogWarning("UserChannelHub connect with no derivable tenant/user id; aborting.");
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroupFor(tenantId));
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupFor(tenantId, userId));
        await ReplayUnackedAsync(tenantId, userId);
        await base.OnConnectedAsync();
    }

    /// <summary>Idempotently ack a delivered row addressed to THIS user (per-user ack).</summary>
    public async Task<AckResult> Ack(Guid messageId)
    {
        var (tenantId, userId) = ResolvePrincipal();
        if (tenantId == Guid.Empty || userId == Guid.Empty) return new AckResult(false);
        var acked = await _outbox.AckAsync(tenantId, messageId, userId);
        return new AckResult(acked);
    }

    /// <summary>
    /// The user→agent chat leg. Stamps <see cref="AgentConversationMessage.UserId"/>
    /// from the connection claims (NEVER trusts the payload), then hands to 39-19's
    /// chat service (records RECEIVED + enqueues toward the orchestrator). Until 39-19
    /// lands the stand-in refuses with an agent-offline result.
    /// </summary>
    public async Task<ChatRelayResult> SendAgentMessage(AgentConversationMessage message)
    {
        var (tenantId, userId) = ResolvePrincipal();
        if (tenantId == Guid.Empty || userId == Guid.Empty) return ChatRelayResult.Offline;

        // Server-stamp the user id + direction — the payload's own fields are ignored.
        var stamped = message with { UserId = userId, Direction = "user->agent" };
        return await _chat.RelayUserMessageAsync(tenantId, userId, stamped, Context.ConnectionAborted);
    }

    // ── internals ─────────────────────────────────────────────────────────

    private async Task ReplayUnackedAsync(Guid tenantId, Guid userId)
    {
        var rows = await _outbox.ListUnackedAsync(tenantId, ChannelAudience.User.ToWire(), userId, ReplayLimit);
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

    private (Guid TenantId, Guid UserId) ResolvePrincipal()
    {
        var user = Context.User;
        var tenantRaw = user?.FindFirst("tenantId")?.Value;
        var userId = user?.GetUserId() ?? Guid.Empty;
        var tenantId = Guid.TryParse(tenantRaw, out var t) ? t : Guid.Empty;
        return (tenantId, userId);
    }
}

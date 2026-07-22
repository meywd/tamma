using Tamma.Core.Documents.Channels;

namespace Tamma.Api.Services.Channels;

/// <summary>
/// Story 39-18 — the seam the hubs' chat legs hand off to. In the finished system
/// this is 39-19's <c>OrchestratorChatService</c> (the SOLE chat recorder/entry
/// point: record <c>CHAT.MESSAGE.RECEIVED</c> → agent turn → record
/// <c>CHAT.MESSAGE.SENT</c> → relay). Until 39-19 lands, chat relay is OFF: the
/// registered <see cref="AgentOfflineChatRelay"/> refuses every message and records
/// nothing (D8 / the risk note — AC6 over feature completeness). This story emits NO
/// <c>CHAT.*</c> events and the outbox refuses a direct conversation-kind enqueue.
/// </summary>
public interface IOrchestratorChatRelay
{
    /// <summary>
    /// Relay a user→agent message. <paramref name="userId"/> is server-stamped from
    /// the connection claims (never trusted from the payload).
    /// </summary>
    Task<ChatRelayResult> RelayUserMessageAsync(Guid tenantId, Guid userId, AgentConversationMessage message, CancellationToken ct);

    /// <summary>Relay an agent→user reply (from the orchestrator hub's chat leg).</summary>
    Task<ChatRelayResult> RelayAgentReplyAsync(Guid tenantId, AgentConversationMessage message, CancellationToken ct);
}

/// <summary>Outcome of a chat relay attempt. <c>Accepted=false</c> + <c>Reason</c> when the agent is offline.</summary>
public sealed record ChatRelayResult(bool Accepted, string? Reason)
{
    public static ChatRelayResult Offline { get; } =
        new(false, "agent-offline: chat relay is disabled until Story 39-19's OrchestratorChatService lands.");
}

/// <summary>
/// The stand-in registered until 39-19 lands: refuses every chat message with an
/// agent-offline result and records nothing (no <c>CHAT.*</c>, no outbox row) — so
/// nothing can cross un-evented (AC6) structurally.
/// </summary>
public sealed class AgentOfflineChatRelay : IOrchestratorChatRelay
{
    public Task<ChatRelayResult> RelayUserMessageAsync(Guid tenantId, Guid userId, AgentConversationMessage message, CancellationToken ct)
        => Task.FromResult(ChatRelayResult.Offline);

    public Task<ChatRelayResult> RelayAgentReplyAsync(Guid tenantId, AgentConversationMessage message, CancellationToken ct)
        => Task.FromResult(ChatRelayResult.Offline);
}

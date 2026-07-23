using Tamma.Core.Documents.Channels;

namespace Tamma.Api.Hubs;

/// <summary>
/// Story 39-18 (D5) — the typed server→client contract for the user↔orchestrator
/// hub. One method: <see cref="Receive"/> carries Task View traffic
/// (<c>task-assigned</c>) and chat relay (<c>agent-conversation</c>); the <c>kind</c>
/// discriminates. No client method takes a group name — per-user isolation is derived
/// server-side from the principal.
/// </summary>
public interface IUserChannelClient
{
    /// <summary>Deliver a channel envelope to a connected dashboard user.</summary>
    Task Receive(ChannelEnvelope envelope);
}

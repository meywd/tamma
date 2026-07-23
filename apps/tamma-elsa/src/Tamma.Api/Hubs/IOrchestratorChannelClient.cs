using Tamma.Core.Documents.Channels;

namespace Tamma.Api.Hubs;

/// <summary>
/// Story 39-18 (D5) — the typed server→client contract for the workflow↔orchestrator
/// hub. One method: <see cref="Receive"/> — the <c>kind</c> discriminator on the
/// envelope's message tells the orchestrator agent what arrived (acceptance request,
/// escalation, guidance query). There is deliberately NO client method that takes a
/// group name (forged group-join is structurally impossible).
/// </summary>
public interface IOrchestratorChannelClient
{
    /// <summary>Deliver a channel envelope to the connected orchestrator agent.</summary>
    Task Receive(ChannelEnvelope envelope);
}

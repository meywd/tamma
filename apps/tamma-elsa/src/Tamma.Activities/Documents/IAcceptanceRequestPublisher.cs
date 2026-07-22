using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-6 (Design Decision D6) — the seam the ACCEPT stage publishes an
/// <see cref="AcceptanceRequest"/> through on the workflow↔orchestrator channel.
/// Stubbed by <see cref="LoggingAcceptanceRequestPublisher"/> until Story 39-18
/// swaps in the outbox + SignalR delivery behind this SAME interface (its D3 reuses
/// 39-5's canonical <see cref="AcceptanceRequest"/> by reference — one record, one
/// name).
///
/// <para>Publishing is decoupled from suspending: the 39-8 gate suspends the
/// lifecycle regardless of whether delivery succeeds, matching 39-18's rule "no
/// orchestrator connected ⇒ the request waits, never defaulted". A publish transport
/// error is logged and swallowed by <see cref="PublishAcceptanceRequestActivity"/>;
/// only the ABSENCE of a registered publisher is a fail-loud programming error.</para>
/// </summary>
public interface IAcceptanceRequestPublisher
{
    /// <summary>Publish an acceptance request to the orchestrator channel.</summary>
    Task PublishAsync(AcceptanceRequest request, CancellationToken ct);
}

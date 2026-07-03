namespace Tamma.Api.Services.Streaming;

/// <summary>
/// Story 32-23 (AC4) — one frame published onto the in-process run bus and
/// written to a subscriber as a single SSE <c>event:/data:</c> frame.
///
/// <para><see cref="Seq"/> is a per-run monotonic counter the bus assigns on
/// publish — it is NOT the per-tenant <c>domain_events.SequenceNumber</c>
/// (a separate per-schema BIGSERIAL that would be meaningless as a stream
/// cursor). Producers construct a frame with <c>Seq = 0</c>; the bus stamps
/// the real value in <c>ILlmRunStreamBus.PublishAsync</c>.</para>
///
/// <para><see cref="Payload"/> MUST be key-free — it is run through
/// <see cref="RunStreamFrameScrubber"/> (an allowlist mirroring
/// <c>AdminTenantEventsSseEndpoint.ScrubEvent</c>) before it is written to
/// the wire so a leaked secret / prompt body / tool argument can never reach
/// an observer (AC9).</para>
/// </summary>
public sealed record RunStreamFrame(
    string Type,
    string CorrelationId,
    long Seq,
    object Payload);

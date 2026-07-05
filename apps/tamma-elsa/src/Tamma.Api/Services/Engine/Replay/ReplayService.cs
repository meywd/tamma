using Microsoft.Extensions.Logging;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Engine.Replay;

/// <summary>
/// Story 4-8 — the default <see cref="IReplayService"/>. Reuses Story 4-7's
/// tenant-scoped, parameterized
/// <see cref="IEventRepository.ListByCorrelationIdAsync"/> as the event source
/// (all of a run's events, ordered by <c>SequenceNumber</c>, served by
/// <c>ix_domain_events_tags_correlationid</c>), then folds the ordered slice into a
/// <see cref="ReplayResult"/> via the pure
/// <see cref="ReplayReconstructor"/>.
///
/// <para>No new storage, no migration: replay is a read over the existing DCB
/// <c>domain_events</c> store. No Elsa runtime, no external credential — a pure
/// event fold.</para>
/// </summary>
public sealed class ReplayService(
    IEventRepository events,
    ILogger<ReplayService> logger) : IReplayService
{
    public async Task<ReplayResult?> ReplayAsync(
        Guid tenantId,
        string correlationId,
        long? upToSequence,
        DateTimeOffset? upToTimestamp,
        long? fromSequence)
    {
        // Fail closed on a missing tenant — never a cross-tenant read. The endpoint
        // already 404s a null tenant; this is defence-in-depth for internal callers.
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        // Story 4-7 event source: ALL of the run's events, tenant-scoped, ordered
        // oldest-first by SequenceNumber. A read — nothing is executed or written.
        var all = await events.ListByCorrelationIdAsync(tenantId, correlationId);
        if (all.Count == 0)
        {
            // Unknown run for this tenant (or another tenant's run — the tenant-scoped
            // read simply returns nothing). The endpoint maps null → 404.
            logger.LogInformation(
                "Replay requested for unknown run {CorrelationId} (tenant {TenantId}); no events.",
                correlationId, tenantId);
            return null;
        }

        // Pure fold to the chosen point (point-in-time). SliceUpTo + Reconstruct are
        // deterministic and side-effect-free.
        var slice = ReplayReconstructor.SliceUpTo(all, upToSequence, upToTimestamp);

        ReplayDelta? delta = null;
        if (fromSequence is { } fromSeq)
        {
            // AC6 — the diff is a pure comparison of two prefix folds of the same run.
            var fromSlice = ReplayReconstructor.SliceUpTo(all, fromSeq, null);
            var fromResult = ReplayReconstructor.Reconstruct(correlationId, fromSlice, all.Count);
            var toResult = ReplayReconstructor.Reconstruct(correlationId, slice, all.Count);
            delta = ReplayReconstructor.Diff(fromResult, toResult);
        }

        var result = ReplayReconstructor.Reconstruct(correlationId, slice, all.Count, delta);

        logger.LogInformation(
            "Replay reconstructed run {CorrelationId} (tenant {TenantId}): {Replayed}/{Total} events, step {Step}, status {Status}.",
            correlationId, tenantId, result.EventsReplayed, result.TotalEvents,
            result.StepReached, result.Status);

        return result;
    }
}

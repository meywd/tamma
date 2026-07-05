namespace Tamma.Api.Services.Engine.Replay;

/// <summary>
/// Story 4-8 — reconstructs a run's point-in-time state by folding its ordered DCB
/// event slice. The RECONSTRUCTION half that Story 4-7 deferred.
///
/// <para>READ-ONLY by construction: the only data access is
/// <see cref="Tamma.Data.Repositories.IEventRepository.ListByCorrelationIdAsync"/>
/// (a tenant-scoped read); the fold
/// (<see cref="ReplayReconstructor.Reconstruct"/>) re-executes no activity and
/// mutates nothing. Tenant-scoped: an empty tenant fails closed (returns
/// <c>null</c> — the run is not visible), never a cross-tenant read.</para>
/// </summary>
public interface IReplayService
{
    /// <summary>
    /// Reconstruct the state of run <paramref name="correlationId"/> within
    /// <paramref name="tenantId"/> as of the chosen point.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the run. <see cref="Guid.Empty"/>
    /// fails closed (returns <c>null</c>).</param>
    /// <param name="correlationId">The run / workflow-instance correlation id.</param>
    /// <param name="upToSequence">Replay up to and including this
    /// <c>SequenceNumber</c> (point-in-time). Null replays the whole run.</param>
    /// <param name="upToTimestamp">Replay up to and including this timestamp
    /// (point-in-time). Null replays the whole run.</param>
    /// <param name="fromSequence">When set, the result carries a
    /// <see cref="ReplayDelta"/> diff of everything after this point up to the
    /// replay point (AC6).</param>
    /// <returns>The reconstructed <see cref="ReplayResult"/>, or <c>null</c> when the
    /// run is unknown to this tenant (no events) — the endpoint maps that to 404.</returns>
    Task<ReplayResult?> ReplayAsync(
        Guid tenantId,
        string correlationId,
        long? upToSequence,
        DateTimeOffset? upToTimestamp,
        long? fromSequence);
}

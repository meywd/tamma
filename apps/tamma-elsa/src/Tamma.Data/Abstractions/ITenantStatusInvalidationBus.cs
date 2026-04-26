namespace Tamma.Data.Abstractions;

/// <summary>
/// Round-2 follow-up — cluster-wide tenant-status cache invalidation
/// fan-out. The Story 28-8 <c>ITenantStatusCache</c> is per-pod by
/// design (in-memory LRU). Calls to <c>Invalidate</c> on pod A do NOT
/// inform sibling pods, so for up to <c>TtlSeconds</c> (default 10s)
/// pod B continues serving the previous status.
///
/// <para>This bus closes the gap by publishing a NOTIFY on the control-
/// plane Postgres connection whenever a pod flips a tenant's status.
/// Every pod runs a <c>TenantStatusInvalidationListener</c> which
/// LISTENs on the same channel and applies the invalidation locally
/// when a notification arrives — including the publishing pod, but
/// re-invalidating an already-evicted entry is idempotent.</para>
///
/// <para><b>Best-effort semantics</b>: the publish call MUST NOT fail
/// the originating admin action if the NOTIFY round-trip dies. The
/// caller has already invalidated its own pod-local cache — the
/// cluster fan-out is a freshness optimisation, not a correctness
/// boundary. Implementations should swallow + log connection errors
/// at WARN.</para>
///
/// <para>The Postgres-backed implementation lives in
/// <c>Tamma.Data.Pooling.PostgresTenantStatusInvalidationBus</c>; the
/// no-op implementation (registered when no CP connection string is
/// configured, e.g. tests / local dev) is
/// <c>NullTenantStatusInvalidationBus</c>.</para>
/// </summary>
public interface ITenantStatusInvalidationBus
{
    /// <summary>
    /// Fan out a tenant-status invalidation to the cluster. Idempotent:
    /// subscribers de-duplicate by re-running <c>Invalidate</c> on
    /// receipt, which is a no-op when the entry is already absent.
    ///
    /// <para>Best-effort: a transient failure (Postgres unreachable,
    /// channel name stale) is logged + swallowed. The originating pod
    /// has already evicted its local entry — degradation is bounded
    /// by the cache TTL, not the success of this call.</para>
    /// </summary>
    /// <param name="tenantId">Tenant whose cached status is now stale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PublishAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

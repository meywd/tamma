using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for the control-plane <see cref="PlatformEvent"/>
/// audit log. Append-only; reads are filterable by type prefix, tenant,
/// user, or time range.
///
/// <para>The CP analogue of <see cref="IEventRepository"/> but bound to
/// <see cref="ControlPlaneDbContext"/> so events that fire before/after a
/// tenant DB exists (or that touch multiple tenants) have a durable home
/// per Doc 01 §5.1–5.2 and Story 28-6 §AC1.</para>
///
/// <para>Append semantics include dedupe: the
/// <c>partial unique (tenant_id, type, tags->>'step', tags->>'attempt')
/// where type LIKE 'TENANT.PROVISION.STEP_%'</c> index from Story 28-1
/// (added by 28-6) makes step-replay idempotent — a duplicate insert is
/// silently swallowed so workflow retries don't double-emit lifecycle
/// events.</para>
/// </summary>
public interface IPlatformEventRepository
{
    /// <summary>
    /// Insert a new <see cref="PlatformEvent"/> in append-only mode.
    /// Returns the persisted entity (with generated <c>Id</c> +
    /// <c>CreatedAt</c>) on success, or <c>null</c> when the row
    /// collided with the partial unique step-dedup index — indicating
    /// the event was already recorded by a previous attempt and the
    /// caller should treat the operation as a no-op (idempotent).
    /// </summary>
    Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Read a single event by id.
    /// </summary>
    Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Filtered query against the platform event log. Every parameter is
    /// optional; <c>null</c> means "don't filter on this column".
    /// Results are ordered by <c>CreatedAt DESC</c> (most-recent first)
    /// and capped at <paramref name="limit"/> rows.
    /// </summary>
    /// <param name="tenantId">When non-null: rows whose
    /// <see cref="PlatformEvent.TenantId"/> equals this value (or are
    /// platform-only — null tenant — when <paramref name="includePlatformWide"/>
    /// is true).</param>
    /// <param name="userId">Optional <see cref="PlatformEvent.UserId"/>
    /// filter.</param>
    /// <param name="typePrefix">Case-sensitive prefix match against
    /// <see cref="PlatformEvent.Type"/>. Pass <c>"TENANT."</c> to scan
    /// every tenant-lifecycle event in one query.</param>
    /// <param name="since">Lower bound on
    /// <see cref="PlatformEvent.CreatedAt"/> (inclusive).</param>
    /// <param name="includePlatformWide">When true and
    /// <paramref name="tenantId"/> is set, platform-only events
    /// (TenantId IS NULL) are returned alongside tenant-scoped rows.
    /// Defaults to false (strict tenant scope).</param>
    /// <param name="limit">Page size (1..1000). Caller-supplied limits
    /// outside this range are clamped.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<PlatformEvent>> QueryAsync(
        Guid? tenantId = null,
        Guid? userId = null,
        string? typePrefix = null,
        DateTime? since = null,
        bool includePlatformWide = false,
        int limit = 100,
        CancellationToken ct = default);
}

namespace Tamma.Data.Entities;

/// <summary>
/// Story 36-2 — per-tenant resumable cursor for the dimensional analytics
/// projection. Records the highest <see cref="DomainEvent.SequenceNumber"/>
/// already folded into <c>analytics_usage_hourly</c> for a given projection
/// <see cref="Stream"/> (e.g. <c>"dimensional"</c>).
///
/// <para><b>Tenancy:</b> no <c>TenantId</c> column — like the
/// <see cref="AnalyticsUsageHourly"/> fact tables it lives in the per-tenant
/// <c>t_&lt;hex&gt;</c> search-path schema, so a row is physically scoped to
/// its tenant. One row per <see cref="Stream"/> (unique).</para>
///
/// <para><b>Cursor semantics:</b> the projection folds events with
/// <c>SequenceNumber &gt; LastSequenceNumber</c> and advances the checkpoint
/// atomically with the idempotent upsert. <see cref="DomainEvent.SequenceNumber"/>
/// — the monotonic <c>BIGSERIAL</c> total order — is the cursor (never
/// <c>Id</c>/<c>CreatedAt</c>, which are not tie-break-safe across
/// same-millisecond events). The checkpoint is a resumability/skip
/// optimisation; idempotency itself is guaranteed by the whole-bucket
/// overwrite upsert, so a stale/reset checkpoint can never double-count.</para>
/// </summary>
public class AnalyticsProjectionCheckpoint
{
    /// <summary>Surrogate PK — Postgres <c>gen_random_uuid()</c> default.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Logical projection stream this cursor tracks. The dimensional rollup
    /// uses <see cref="DimensionalStream"/>. Unique — one cursor row per stream.
    /// </summary>
    public string Stream { get; set; } = null!;

    /// <summary>
    /// Highest <see cref="DomainEvent.SequenceNumber"/> already folded into the
    /// dimensional store for <see cref="Stream"/>. Starts at 0 (nothing folded).
    /// </summary>
    public long LastSequenceNumber { get; set; }

    /// <summary>Wall-clock timestamp of the last advance; defaults to <c>now()</c>.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>The <see cref="Stream"/> value used by the dimensional projection.</summary>
    public const string DimensionalStream = "dimensional";
}

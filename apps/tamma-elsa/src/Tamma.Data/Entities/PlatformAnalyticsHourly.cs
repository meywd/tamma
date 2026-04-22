namespace Tamma.Data.Entities;

/// <summary>
/// Story 28-10 — hourly fact table row for platform-wide analytics.
/// Populated by the Elsa <c>HourlyAnalyticsRollupWorkflow</c> (runs at
/// minute 5 of every hour) which fans out a
/// <c>ComputeTenantRollupActivity</c> per active tenant plus one
/// <c>ComputePlatformRollupActivity</c> for the cross-tenant totals.
///
/// <para>Lives on the control-plane DB (<see cref="ControlPlaneDbContext"/>)
/// because it is fleet-wide by design — a single SELECT answers "how many
/// workflows did the platform run this week" without fanning out to every
/// tenant DB. Per-tenant rows carry a non-null <see cref="TenantId"/>; the
/// cross-tenant roll-up carries <see cref="TenantId"/> = <c>null</c>.</para>
///
/// <para>Idempotency: two partial unique indexes over <c>(Hour, TenantId)</c>
/// — one for <c>TenantId IS NOT NULL</c> and one for <c>TenantId IS NULL</c>
/// — ensure a replay of a given hour overwrites (or no-ops) rather than
/// duplicating rows. The activity uses a read-then-upsert shape so a failed
/// hour can be rerun from the runbook without drift.</para>
///
/// <para>All counters are <c>long</c> so they can accumulate year-long
/// totals without overflow (at 1k workflows/sec for 365 days = ~31B, still
/// inside <see cref="long.MaxValue"/>). <see cref="CostUsd"/> is
/// <see cref="decimal"/> with 20,4 precision (same as the DTO
/// <c>CostAggregates</c> cap) so round-tripping from the legacy live
/// aggregation stays lossless.</para>
/// </summary>
public class PlatformAnalyticsHourly
{
    /// <summary>
    /// Surrogate PK — Postgres <c>gen_random_uuid()</c> default. Kept as a
    /// plain Id so the row can be updated in place during a replay (PK
    /// stays stable; the <c>(Hour, TenantId)</c> unique index is the
    /// business key).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// UTC top-of-hour marker. Always truncated to the hour — e.g.
    /// <c>2026-04-18T12:00:00Z</c> for the 12:00–13:00 bucket. The
    /// workflow clamps input to <c>DateTime.UtcNow.Date +
    /// TimeSpan.FromHours(hour)</c> before calling the activities so
    /// callers can't accidentally write a row for a non-aligned minute.
    /// </summary>
    public DateTime Hour { get; set; }

    /// <summary>
    /// Tenant this row describes. <c>null</c> = platform-wide totals
    /// (sum-of-tenants + tenant-directory counts). Partial unique index
    /// on <c>(Hour, TenantId)</c> is the idempotency key.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>Number of workflow instances created in this hour.</summary>
    public long WorkflowsStarted { get; set; }

    /// <summary>Number of workflow instances that reached <c>completed</c> state.</summary>
    public long WorkflowsCompleted { get; set; }

    /// <summary>Number of workflow instances that reached <c>failed</c> state.</summary>
    public long WorkflowsFailed { get; set; }

    /// <summary>
    /// Number of <c>AGENT.DISPATCH.*</c> events attributed to this
    /// tenant (or total across tenants for the platform-wide row) in the
    /// bucket. Epic 19 §7 event stream.
    /// </summary>
    public long AgentDispatches { get; set; }

    /// <summary>Sum of <c>LLM.CALL.SUCCESS.data.inputTokens</c> in the bucket.</summary>
    public long TokensIn { get; set; }

    /// <summary>Sum of <c>LLM.CALL.SUCCESS.data.outputTokens</c> in the bucket.</summary>
    public long TokensOut { get; set; }

    /// <summary>
    /// Sum of <c>LLM.CALL.SUCCESS.data.costUsd</c> in the bucket, capped
    /// at 4 decimals (same precision as
    /// <c>CostAggregates.Last24hUsd</c>). Decimal to avoid float drift
    /// when summing ~10k rows per tenant per hour.
    /// </summary>
    public decimal CostUsd { get; set; }

    /// <summary>
    /// Number of tenants active at bucket end — populated only on the
    /// platform-wide row (<see cref="TenantId"/> is null). Zero on
    /// per-tenant rows.
    /// </summary>
    public int ActiveTenantsAtHourEnd { get; set; }

    /// <summary>
    /// Wall-clock timestamp when this row was written (or last updated on
    /// a replay). Useful for the runbook — ops can see at a glance which
    /// hours were backfilled late.
    /// </summary>
    public DateTime ComputedAt { get; set; }
}

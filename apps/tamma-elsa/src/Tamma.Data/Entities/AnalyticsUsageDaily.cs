using Tamma.Core.Enums;

namespace Tamma.Data.Entities;

/// <summary>
/// Story 36-1 — per-tenant <b>daily</b> roll-up of <see cref="AnalyticsUsageHourly"/>.
///
/// <para>Carries the <b>identical</b> dimension + measure contract as the
/// hourly grain — the ONLY difference is the time bucket is <see cref="Day"/>
/// (UTC midnight) instead of <c>Hour</c>. Keeping the shape byte-for-byte
/// identical makes the daily roll-up (Story 36-2) a pure
/// <c>GROUP BY date_trunc('day', Hour), &lt;dims&gt;</c> — a lossless
/// aggregation of the hourly rows, no measure dropped.</para>
///
/// <para>Lives in the tenant schema (see <see cref="AnalyticsUsageHourly"/> for
/// the full tenancy, population-source, and measure-type rationale). No
/// <c>TenantId</c> column; no events emitted by this story.</para>
/// </summary>
public class AnalyticsUsageDaily
{
    /// <summary>
    /// Surrogate PK — Postgres <c>gen_random_uuid()</c> default. The
    /// <c>UX_analytics_usage_daily_dims</c> unique index over the full
    /// dimension tuple is the business/idempotency key (Story 36-2's upsert
    /// target).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// UTC midnight bucket — e.g. <c>2026-06-17T00:00:00Z</c> for the whole of
    /// 2026-06-17. Stored as <c>timestamp with time zone</c>.
    /// </summary>
    public DateTime Day { get; set; }

    // ── Dimensions (identical to AnalyticsUsageHourly) ──

    /// <summary>AI provider key (e.g. <c>anthropic-claude</c>). Required.</summary>
    public string Provider { get; set; } = null!;

    /// <summary>Agent handle/id this usage is attributed to; <c>null</c> = unattributed.</summary>
    public string? AgentId { get; set; }

    /// <summary>Workflow definition this usage rolls up under; <c>null</c> = none.</summary>
    public Guid? WorkflowDefinitionId { get; set; }

    /// <summary>Repository this usage is attributed to; <c>null</c> = unattributed.</summary>
    public string? RepoId { get; set; }

    /// <summary>
    /// Whether the cost was BYOK (tenant key, unbilled) or platform-fronted
    /// (billable). Persisted as lowercase text (<c>byok</c>/<c>platform</c>).
    /// </summary>
    public CostBasis CostBasis { get; set; }

    // ── Measures (identical to AnalyticsUsageHourly) ──

    /// <summary>Sum of <c>LLM.CALL.SUCCESS.data.inputTokens</c> in the bucket.</summary>
    public long TokensIn { get; set; }

    /// <summary>Sum of <c>LLM.CALL.SUCCESS.data.outputTokens</c> in the bucket.</summary>
    public long TokensOut { get; set; }

    /// <summary>Sum of <c>LLM.CALL.SUCCESS.data.costUsd</c> in the bucket, 4-decimal precision.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>
    /// Portion of <see cref="CostUsd"/> Tamma billed the tenant (only
    /// <see cref="CostBasis.Platform"/> rows are non-zero). 4-decimal precision.
    /// </summary>
    public decimal PlatformBilledUsd { get; set; }

    /// <summary>Number of workflow instances created in this bucket.</summary>
    public long WorkflowsStarted { get; set; }

    /// <summary>Number of workflow instances that reached <c>completed</c> in this bucket.</summary>
    public long WorkflowsCompleted { get; set; }

    /// <summary>Number of workflow instances that reached <c>failed</c> in this bucket.</summary>
    public long WorkflowsFailed { get; set; }

    /// <summary>Number of <c>AGENT.DISPATCH.*</c> events in this bucket.</summary>
    public long AgentDispatches { get; set; }

    /// <summary>Wall-clock write/last-update timestamp; defaults to <c>now()</c>.</summary>
    public DateTime ComputedAt { get; set; }
}

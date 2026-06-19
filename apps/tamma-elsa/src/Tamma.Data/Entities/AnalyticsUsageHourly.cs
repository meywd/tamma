using Tamma.Core.Enums;

namespace Tamma.Data.Entities;

/// <summary>
/// Story 36-1 — per-tenant <b>hourly</b> usage/cost/performance fact row.
///
/// <para>Lives in the tenant schema (the per-tenant <c>t_&lt;hex&gt;</c>
/// search-path schema reached through <see cref="TenantDbContext"/>), NOT the
/// control-plane <c>platform_analytics_hourly</c> table — that one stays the
/// owner-only fleet-wide store (Story 28-10). This per-tenant grain adds the
/// dimensions the single-grain CP row deliberately omits: <see cref="Provider"/>,
/// <see cref="AgentId"/>, <see cref="WorkflowDefinitionId"/>,
/// <see cref="RepoId"/>, and a BYOK-vs-platform <see cref="CostBasis"/>.</para>
///
/// <para><b>Tenancy:</b> no <c>TenantId</c> column — isolation is the schema +
/// connection string (Doc 01 §1.4 target shape), not a column. A row in one
/// tenant's schema is physically unreachable from another tenant's context.</para>
///
/// <para><b>Population (future, Story 36-2 — NOT this story):</b> the projection
/// pipeline fills these tables from the existing DCB source events
/// <c>LLM.CALL.SUCCESS</c> (<c>data.inputTokens</c>/<c>outputTokens</c>/<c>costUsd</c>),
/// <c>AGENT.DISPATCH.SUCCESS|FAILED</c>, and <c>WORKFLOW.STEP_COMPLETED</c> —
/// the same prefixes <c>PlatformAnalyticsService</c> already tracks. This story
/// emits and consumes NO events; the <c>ANALYTICS.PROJECTION.*</c> catalogue is
/// Story 36-2's.</para>
///
/// <para><b>Measure types</b> mirror <see cref="PlatformAnalyticsHourly"/>
/// (counters <c>long</c>, costs <c>decimal(20,4)</c>) so an owner-side
/// reconciliation join across the two stores is lossless.</para>
/// </summary>
public class AnalyticsUsageHourly
{
    /// <summary>
    /// Surrogate PK — Postgres <c>gen_random_uuid()</c> default. The
    /// <c>UX_analytics_usage_hourly_dims</c> unique index over the full
    /// dimension tuple is the business/idempotency key (Story 36-2's upsert
    /// target); the PK stays stable so a row updates in place on re-projection.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// UTC top-of-hour bucket — e.g. <c>2026-06-17T12:00:00Z</c> for the
    /// 12:00–13:00 window. Always truncated to the hour by the (future)
    /// projection. Stored as <c>timestamp with time zone</c>.
    /// </summary>
    public DateTime Hour { get; set; }

    // ── Dimensions ──

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

    // ── Measures (counters: long; cost: decimal(20,4)) ──

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

    /// <summary>
    /// Wall-clock timestamp when this row was written (or last updated on a
    /// re-projection). Useful for the runbook — ops can see which buckets were
    /// backfilled late. Defaults to <c>now()</c>.
    /// </summary>
    public DateTime ComputedAt { get; set; }
}

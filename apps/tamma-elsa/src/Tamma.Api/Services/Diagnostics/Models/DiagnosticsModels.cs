using Tamma.Data.Entities;

namespace Tamma.Api.Services.Diagnostics.Models;

/// <summary>
/// Bucket size for time-series diagnostics aggregation.
/// </summary>
public enum BucketSize
{
    /// <summary>Five-minute buckets.</summary>
    FiveMinutes,
    /// <summary>One-hour buckets.</summary>
    Hour,
    /// <summary>One-day (24h) buckets.</summary>
    Day
}

/// <summary>
/// Filter criteria for querying <see cref="ProviderDiagnostic"/> records.
/// </summary>
/// <remarks>
/// <see cref="TenantId"/> is optional. When <c>null</c> the repository applies
/// EF's global query filter (i.e. scopes to the ambient <c>ITenantContext</c>).
/// </remarks>
public sealed record DiagnosticsFilter
{
    /// <summary>Provider key (e.g. <c>anthropic-claude</c>).</summary>
    public string? ProviderKey { get; init; }

    /// <summary>Tenant id (optional — honours global query filter when null).</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Inclusive lower bound on <c>CreatedAt</c>.</summary>
    public DateTime? From { get; init; }

    /// <summary>Inclusive upper bound on <c>CreatedAt</c>.</summary>
    public DateTime? To { get; init; }

    /// <summary>When set, filters by <c>Success</c> equality.</summary>
    public bool? Success { get; init; }

    /// <summary>Model name filter.</summary>
    public string? Model { get; init; }

    /// <summary>Page size (defaults to 50).</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Row offset (defaults to 0).</summary>
    public int Offset { get; init; }
}

/// <summary>
/// Single time-bucketed aggregation row from <see cref="ProviderDiagnostic"/>.
/// </summary>
/// <param name="BucketStart">Inclusive start of the bucket (UTC).</param>
/// <param name="TotalCalls">Count of diagnostics in the bucket.</param>
/// <param name="SuccessCount">Count of <c>Success == true</c> rows.</param>
/// <param name="SuccessRate">Fraction (0..1) of successful calls, or 0 if bucket empty.</param>
/// <param name="TotalCost">Sum of <c>Cost</c> (USD).</param>
/// <param name="AvgLatencyMs">Average <c>RequestDurationMs</c>.</param>
public sealed record DiagnosticsBucket(
    DateTime BucketStart,
    long TotalCalls,
    long SuccessCount,
    double SuccessRate,
    decimal TotalCost,
    double AvgLatencyMs);

/// <summary>
/// Full time-bucketed report returned by <see cref="DiagnosticsService.GetReportAsync"/>.
/// </summary>
/// <param name="From">Inclusive start of the range.</param>
/// <param name="To">Exclusive end of the range.</param>
/// <param name="BucketSize">Requested bucket size.</param>
/// <param name="Buckets">Ordered buckets (ascending by <see cref="DiagnosticsBucket.BucketStart"/>).</param>
/// <param name="TotalCalls">Grand total call count across all buckets.</param>
/// <param name="TotalCost">Grand total cost across all buckets.</param>
/// <param name="OverallSuccessRate">Fraction of successful calls across the range, or 0 if empty.</param>
public sealed record DiagnosticsReport(
    DateTime From,
    DateTime To,
    BucketSize BucketSize,
    IReadOnlyList<DiagnosticsBucket> Buckets,
    long TotalCalls,
    decimal TotalCost,
    double OverallSuccessRate);

/// <summary>
/// Budget configuration for an account (tenant). Currently materialised from
/// app configuration; future work will persist it per tenant.
/// </summary>
/// <param name="LimitUsd">Total budget cap for the current period.</param>
/// <param name="AlertThreshold">Fraction (0..1) that triggers the <c>ShouldAlert</c> flag.</param>
/// <param name="PeriodStart">Inclusive start of the current budget period.</param>
/// <param name="PeriodEnd">Exclusive end of the current budget period.</param>
public sealed record BudgetConfig(
    decimal LimitUsd,
    double AlertThreshold,
    DateTime PeriodStart,
    DateTime PeriodEnd);

/// <summary>
/// Current-period budget status for an account.
/// </summary>
/// <param name="AccountId">Account (tenant) identifier.</param>
/// <param name="PeriodStart">Inclusive period start (UTC).</param>
/// <param name="PeriodEnd">Exclusive period end (UTC).</param>
/// <param name="Spent">Total spend in the period (USD).</param>
/// <param name="Limit">Budget cap (USD).</param>
/// <param name="Remaining"><c>max(0, Limit - Spent)</c>.</param>
/// <param name="PercentUsed"><c>Spent / Limit * 100</c>, or 0 when limit is 0.</param>
/// <param name="AlertThreshold">Configured alert threshold (0..1).</param>
/// <param name="ShouldAlert">True when <c>PercentUsed / 100 &gt;= AlertThreshold</c>.</param>
/// <param name="IsOverBudget">True when <c>Spent &gt; Limit</c>.</param>
public sealed record BudgetStatus(
    Guid AccountId,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    decimal Spent,
    decimal Limit,
    decimal Remaining,
    double PercentUsed,
    double AlertThreshold,
    bool ShouldAlert,
    bool IsOverBudget);

/// <summary>
/// Aggregation axis for <see cref="IDiagnosticsService.GetDimensionReportAsync"/>.
/// Finding 009.
/// </summary>
public enum DimensionGroup
{
    /// <summary>Group by ProviderKey.</summary>
    Provider,
    /// <summary>Group by Model.</summary>
    Model,
    /// <summary>Group by AgentType (developer / tester / …).</summary>
    AgentType,
}

/// <summary>One row of the per-dimension diagnostics report.</summary>
/// <param name="Key">The grouping value (e.g. "anthropic", "claude-sonnet-4",
/// "developer"). <c>"unknown"</c> when the column is null.</param>
/// <param name="TotalCalls">Number of diagnostics rows in the bucket.</param>
/// <param name="SuccessCount">Subset where <c>Success == true</c>.</param>
/// <param name="ErrorRate">Fraction (0..1) of calls that failed.</param>
/// <param name="TotalCost">Sum of <c>Cost</c> for the bucket (USD).</param>
/// <param name="TotalTokens">Sum of <c>TokensUsed</c>.</param>
/// <param name="AvgLatencyMs">Average <c>RequestDurationMs</c>.</param>
public sealed record DimensionBucket(
    string Key,
    long TotalCalls,
    long SuccessCount,
    double ErrorRate,
    decimal TotalCost,
    long TotalTokens,
    double AvgLatencyMs);

/// <summary>Full per-dimension report.</summary>
public sealed record DimensionReport(
    DateTime From,
    DateTime To,
    DimensionGroup GroupBy,
    IReadOnlyList<DimensionBucket> Groups);

// ──────────────────────────────────────────────────────────────────────────
// Story 23-6 — Provider Diagnostics (Deep). Aggregations over the EXISTING
// per-tenant ProviderDiagnostic table (no new column / table). All figures are
// the calling tenant's OWN recorded usage/spend — the same tenant-scoped
// `Cost` already surfaced by the /diagnostics/report + /query endpoints. This
// report deliberately carries NO platform margin / markup / cost-basis field
// (mirrors the Story 34-5 estimate-leak rule): a tenant sees its usage and its
// own spend, never platform-internal economics.
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// Latency percentile summary for a set of calls (all in milliseconds).
/// Percentiles use the nearest-rank method over the sorted duration list.
/// </summary>
/// <param name="P50">Median request duration (ms).</param>
/// <param name="P95">95th percentile request duration (ms).</param>
/// <param name="P99">99th percentile request duration (ms).</param>
/// <param name="Max">Maximum observed request duration (ms).</param>
/// <param name="Avg">Mean request duration (ms).</param>
public sealed record LatencyPercentiles(
    double P50,
    double P95,
    double P99,
    double Max,
    double Avg);

/// <summary>
/// One error-class row grouped by <see cref="ProviderDiagnostic.ErrorCode"/>
/// (falls back to <c>"unknown"</c> when the provider returned no structured
/// code). Only failed calls contribute.
/// </summary>
/// <param name="ErrorClass">Structured error code (e.g. <c>rate_limit</c>) or <c>"unknown"</c>.</param>
/// <param name="Count">Number of failed calls in this class.</param>
/// <param name="Share">Fraction (0..1) of this provider's total errors in this class.</param>
public sealed record ProviderErrorClass(
    string ErrorClass,
    long Count,
    double Share);

/// <summary>
/// Per-model usage inside a provider — powers the "model availability + cost
/// comparison" view. <c>Cost</c>/<c>Tokens</c> are the tenant's own recorded
/// spend, never a platform margin.
/// </summary>
/// <param name="Model">Model name (e.g. <c>claude-sonnet-4</c>) or <c>"unknown"</c>.</param>
/// <param name="TotalCalls">Calls issued against this model.</param>
/// <param name="SuccessCount">Subset that succeeded.</param>
/// <param name="SuccessRate">Fraction (0..1) of successful calls.</param>
/// <param name="TotalCost">Sum of <c>Cost</c> for this model (USD).</param>
/// <param name="TotalTokens">Sum of <c>TokensUsed</c> for this model.</param>
/// <param name="AvgLatencyMs">Average request duration (ms).</param>
public sealed record ProviderModelUsage(
    string Model,
    long TotalCalls,
    long SuccessCount,
    double SuccessRate,
    decimal TotalCost,
    long TotalTokens,
    double AvgLatencyMs);

/// <summary>
/// Deep per-provider diagnostics summary: latency percentiles, error-class
/// breakdown, token/cost analytics, per-model usage and success/failure rates.
/// </summary>
/// <param name="ProviderKey">Provider key (e.g. <c>anthropic-claude</c>).</param>
/// <param name="TotalCalls">Total calls in the window.</param>
/// <param name="SuccessCount">Successful calls.</param>
/// <param name="FailureCount">Failed calls.</param>
/// <param name="SuccessRate">Fraction (0..1) of successful calls.</param>
/// <param name="ErrorRate">Fraction (0..1) of failed calls.</param>
/// <param name="Latency">Latency percentile summary (ms).</param>
/// <param name="TotalTokens">Sum of <c>TokensUsed</c>.</param>
/// <param name="InputTokens">Sum of <c>InputTokens</c>.</param>
/// <param name="OutputTokens">Sum of <c>OutputTokens</c>.</param>
/// <param name="TotalCost">Sum of <c>Cost</c> (USD) — the tenant's own spend.</param>
/// <param name="Errors">Error-class breakdown (descending by count).</param>
/// <param name="Models">Per-model usage (descending by call count).</param>
public sealed record ProviderDiagnosticSummary(
    string ProviderKey,
    long TotalCalls,
    long SuccessCount,
    long FailureCount,
    double SuccessRate,
    double ErrorRate,
    LatencyPercentiles Latency,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    decimal TotalCost,
    IReadOnlyList<ProviderErrorClass> Errors,
    IReadOnlyList<ProviderModelUsage> Models);

/// <summary>
/// Full deep provider-diagnostics report over the half-open range
/// <c>[from, to)</c>. Ordered by call volume (busiest provider first).
/// </summary>
/// <param name="From">Inclusive start of the range (UTC).</param>
/// <param name="To">Exclusive end of the range (UTC).</param>
/// <param name="Providers">Per-provider summaries (descending by call count).</param>
/// <param name="TotalCalls">Grand total calls across all providers.</param>
/// <param name="TotalErrors">Grand total failed calls across all providers.</param>
/// <param name="TotalTokens">Grand total tokens across all providers.</param>
/// <param name="TotalCost">Grand total spend across all providers (USD).</param>
public sealed record ProviderDiagnosticsDeepReport(
    DateTime From,
    DateTime To,
    IReadOnlyList<ProviderDiagnosticSummary> Providers,
    long TotalCalls,
    long TotalErrors,
    long TotalTokens,
    decimal TotalCost);

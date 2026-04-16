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

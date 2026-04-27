using Tamma.Data.Entities;

namespace Tamma.Data.Defaults;

/// <summary>
/// Code-resident platform defaults for <see cref="BudgetConfig"/>.
///
/// <para>
/// Story 28-1 PR A (Decision #1, <c>.dev/decisions/story-28-1-design-calls.md</c>):
/// replaces the legacy <c>budget_configs.tenant_id IS NULL</c> CP row that
/// previously carried the platform-wide default cap. Reads with
/// <c>tenantId == null</c> now resolve here without hitting the DB.
/// </para>
///
/// <para>
/// Runtime defaults are still configurable per-deployment through
/// <c>IConfiguration</c> in <c>PostgresBudgetConfigProvider</c>
/// (<c>Budget:LimitUsd</c>, <c>Budget:AlertThreshold</c>,
/// <c>Budget:PeriodDays</c>) — that path is unchanged. This class only
/// supplies the row-shaped fallback the repository returns when no
/// tenant-specific override exists, matching the
/// <see cref="BudgetConfig"/> entity defaults.
/// </para>
/// </summary>
public static class BudgetConfigDefaults
{
    /// <summary>
    /// Default cap (USD). Mirrors <see cref="BudgetConfig"/> — zero means
    /// "no enforced cap"; deployment overrides can set a real value via
    /// <c>Budget:LimitUsd</c> in <c>IConfiguration</c>.
    /// </summary>
    public const decimal DefaultLimitUsd = 0m;

    /// <summary>Default alert threshold — fires alerts at 80 % of cap.</summary>
    public const double DefaultAlertThreshold = 0.8;

    /// <summary>Default rolling period — 30 days.</summary>
    public const int DefaultPeriodDays = 30;

    /// <summary>
    /// Build a fresh, mutable <see cref="BudgetConfig"/> snapshot scoped to
    /// the supplied <paramref name="accountId"/>. <see cref="BudgetConfig.TenantId"/>
    /// is left null so callers can tell at a glance that this is the
    /// platform default rather than a stored row.
    /// </summary>
    /// <remarks>
    /// A brand-new object is returned on every call so consumers can mutate
    /// freely without polluting other callers' views.
    /// </remarks>
    public static BudgetConfig Snapshot(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return new BudgetConfig
        {
            Id = Guid.Empty,
            TenantId = null,
            AccountId = accountId,
            LimitUsd = DefaultLimitUsd,
            AlertThreshold = DefaultAlertThreshold,
            PeriodDays = DefaultPeriodDays,
            CreatedAt = DateTime.MinValue,
            UpdatedAt = DateTime.MinValue,
        };
    }
}

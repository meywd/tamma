namespace Tamma.Data.Entities;

/// <summary>
/// Per-tenant (+ optional per-account) budget cap for provider diagnostics.
/// Audit finding providers/005 follow-up — previously held only in-memory,
/// now persisted so overrides survive redeploys.
/// </summary>
/// <remarks>
/// <para>
/// The natural key is <c>(TenantId, AccountId)</c>. <c>TenantId</c> is
/// nullable so a single row with <c>TenantId = NULL</c> can carry the
/// platform-wide default (mirrors the pattern in <see cref="SanitizationRule"/>
/// and <c>agent_configs</c>).
/// </para>
/// <para>
/// <c>AccountId</c> is the opaque scope key the budget API uses (currently
/// the tenant GUID as a string; in the long run we may scope by provider
/// account / BYOK customer). Keeping it as a string here avoids needing a
/// migration when the scope model changes.
/// </para>
/// </remarks>
public class BudgetConfig
{
    public Guid Id { get; set; }

    /// <summary>
    /// Owning tenant (nullable — NULL rows carry the platform default).
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Opaque account scope. Typically the tenant GUID as a string today;
    /// future provider-BYOK flows may use a different key without requiring
    /// a migration.
    /// </summary>
    public string AccountId { get; set; } = null!;

    /// <summary>Budget cap in USD.</summary>
    public decimal LimitUsd { get; set; }

    /// <summary>Fraction of the cap (0..1) at which alerts fire.</summary>
    public double AlertThreshold { get; set; } = 0.8;

    /// <summary>Period length in days (1..366).</summary>
    public int PeriodDays { get; set; } = 30;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

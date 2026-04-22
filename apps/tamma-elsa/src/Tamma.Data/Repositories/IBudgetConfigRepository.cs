using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Repository over <see cref="BudgetConfig"/>. The natural key is
/// <c>(TenantId, AccountId)</c>; <c>TenantId == NULL</c> rows carry the
/// platform-wide default. Audit finding providers/005 (persistence follow-up).
/// </summary>
public interface IBudgetConfigRepository
{
    /// <summary>
    /// Resolve the tenant-specific row by <c>(tenantId, accountId)</c>, or
    /// <c>null</c> when no override is stored.
    /// </summary>
    Task<BudgetConfig?> GetAsync(Guid? tenantId, string accountId, CancellationToken ct = default);

    /// <summary>
    /// Upsert a budget-config row. Returns the stored row with its DB-side
    /// timestamps populated.
    /// </summary>
    Task<BudgetConfig> UpsertAsync(BudgetConfig config, CancellationToken ct = default);

    /// <summary>
    /// Delete a tenant-specific row (or the default row when
    /// <paramref name="tenantId"/> is null). Returns <c>true</c> if a row
    /// was actually removed.
    /// </summary>
    Task<bool> DeleteAsync(Guid? tenantId, string accountId, CancellationToken ct = default);
}

using Tamma.Api.Services.Diagnostics.Models;

namespace Tamma.Api.Services.Diagnostics;

/// <summary>
/// Supplies the active <see cref="BudgetConfig"/> for a given account
/// (tenant). The production implementation will resolve from persistent
/// storage; the default in-memory implementation is sufficient for
/// development and tests.
/// </summary>
public interface IBudgetConfigProvider
{
    /// <summary>
    /// Resolve the budget config for the given account. When no explicit
    /// config has been registered a zero-limit fallback is returned.
    /// </summary>
    BudgetConfig GetConfig(Guid accountId);

    /// <summary>Replace the budget config for an account (primarily for tests).</summary>
    void SetConfig(Guid accountId, BudgetConfig config);
}

using Tamma.Core;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 (AC9) — the single-user no-op billing provider. Registered when
/// <c>ITammaModeProvider.Mode == TammaMode.SingleUser</c>. Reports
/// <see cref="IsEnabled"/> = false so the tenant-create hook and the seed
/// command short-circuit before touching Stripe. The mutating methods throw a
/// clear "billing is SaaS-only" error if a caller ignores <see cref="IsEnabled"/>
/// — but in single-user the hook never calls them, so ZERO Stripe SDK calls
/// occur (asserted in tests).
/// </summary>
public sealed class NullBillingProvider : IBillingProvider
{
    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<BillingCustomer> CreateCustomerAsync(
        Guid tenantId, CustomerDescriptor descriptor, CancellationToken ct = default) =>
        throw SaasOnly();

    /// <inheritdoc />
    public Task<CatalogSyncResult> SyncCatalogAsync(CancellationToken ct = default) =>
        throw SaasOnly();

    private static TammaError SaasOnly() => new(
        "BILLING.SAAS_ONLY",
        "Billing is SaaS-only. The NullBillingProvider is active because this "
        + "Tamma instance runs in single-user mode — there is no Stripe coupling. "
        + "Check IBillingProvider.IsEnabled before calling.",
        retryable: false,
        severity: TammaErrorSeverity.Medium);
}

using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// No-op <see cref="ITenantStatusInvalidationBus"/>. Registered in
/// environments without a control-plane connection string (test
/// fixtures, local laptop dev) so the admin endpoints can call
/// <see cref="ITenantStatusInvalidationBus.PublishAsync"/> unconditionally
/// without paying for an Npgsql round-trip or crashing on a missing
/// connection string.
///
/// <para>Single-pod deployments don't need cluster fan-out — the
/// publishing pod has already invalidated its own cache directly via
/// <c>ITenantStatusCache.Invalidate</c>, so this seam stays correct.</para>
/// </summary>
public sealed class NullTenantStatusInvalidationBus : ITenantStatusInvalidationBus
{
    public ValueTask PublishAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

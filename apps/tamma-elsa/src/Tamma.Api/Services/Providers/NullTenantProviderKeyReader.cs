namespace Tamma.Api.Services.Providers;

/// <summary>
/// No-cabinet <see cref="ITenantProviderKeyReader"/> — always reports "no BYOK
/// key". Wired when the Story 29-2 secret store is not configured (single-user
/// / dev hosts), so the resolver cleanly degrades to the platform leg without a
/// DI-validation failure. There is never a BYOK key to leak.
/// </summary>
public sealed class NullTenantProviderKeyReader : ITenantProviderKeyReader
{
    public Task<TenantProviderKey?> TryReadAsync(
        Guid tenantId, string cabinetName, CancellationToken ct = default) =>
        Task.FromResult<TenantProviderKey?>(null);
}

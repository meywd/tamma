using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Default <see cref="ITenantEndpointDirectory"/> registered when the
/// V2 provisioning surface (Story 30-1) isn't wired. Every call returns
/// <see cref="TenantEndpointResolution.NotApplicable"/> so the LRU
/// pool resolver falls through to the legacy
/// <c>EncryptedConnectionString</c> path.
///
/// <para>Used by:</para>
/// <list type="bullet">
///   <item><description><b>Single-user mode</b> (CLAUDE.md §"Operating
///     Modes"): no V2 providers are wired; the LRU resolver itself is
///     swapped out for the
///     <c>StubTenantConnectionResolver</c> in this mode anyway, so the
///     null directory is just a safety net for stray DI resolutions.</description></item>
///   <item><description><b>Pre-30-3 SaaS mode</b>: V2 types exist
///     (Story 30-1) but no provider is registered yet. Tenants stay on
///     the legacy <c>EncryptedConnectionString</c> path.</description></item>
///   <item><description><b>Tests</b>: the unit suite for the LRU
///     resolver wires this seam by default and replaces it per-test
///     where V2 dispatch is being exercised.</description></item>
/// </list>
/// </summary>
public sealed class NullTenantEndpointDirectory : ITenantEndpointDirectory
{
    /// <summary>Singleton instance — the type carries no state.</summary>
    public static readonly NullTenantEndpointDirectory Instance = new();

    public Task<TenantEndpointResolution> TryResolveAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(TenantEndpointResolution.NotApplicable);
}

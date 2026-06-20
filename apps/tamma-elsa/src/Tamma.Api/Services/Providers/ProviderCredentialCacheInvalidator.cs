using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 32-3 (AC9) — evicts a stale BYOK entry from the
/// <see cref="IProviderCredentialResolver"/> cache when a provider key is
/// rotated. The canonical invalidation trigger is the cabinet's
/// <c>SECRET.ROTATE.ACTIVATED</c> signal (the same event
/// <c>RuntimeSecretResolver</c> documents for its own invalidation); the
/// management API also calls it directly on register / rotate / remove.
///
/// <para>The cabinet does not currently publish <c>SECRET.ROTATE.ACTIVATED</c>
/// as an in-process bus event, so this handler exposes
/// <see cref="HandleRotateActivated(SecretRef)"/> as the single choke point —
/// callers on the rotation / mutation path invoke it, and tests drive it
/// directly to prove the eviction. When the bus event lands, its dispatcher
/// calls the same method.</para>
/// </summary>
public sealed class ProviderCredentialCacheInvalidator
{
    private readonly IProviderCredentialResolver _resolver;
    private readonly ILogger<ProviderCredentialCacheInvalidator> _logger;

    public ProviderCredentialCacheInvalidator(
        IProviderCredentialResolver resolver,
        ILogger<ProviderCredentialCacheInvalidator> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Handle a <c>SECRET.ROTATE.ACTIVATED</c> for <paramref name="reference"/>.
    /// When the ref is a tenant-scoped <c>provider/&lt;name&gt;/api-key</c> row,
    /// evict the matching <c>(tenantId, provider)</c> resolver-cache entry so
    /// the next resolve picks up the rotated version. No-op otherwise.
    /// </summary>
    public void HandleRotateActivated(SecretRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.Scope != SecretScope.Tenant || reference.TenantId is not { } tid)
        {
            return; // platform-scoped rotations are handled by RuntimeSecretResolver
        }

        var provider = ProviderCabinetNames.TryParse(reference.Name);
        if (provider is null)
        {
            return; // not a BYOK provider-key ref
        }

        _resolver.Invalidate(tid, provider);
        _logger.LogInformation(
            "Provider credential cache invalidated on SECRET.ROTATE.ACTIVATED for " +
            "tenant {TenantId} provider {Provider}.", tid, provider);
    }
}

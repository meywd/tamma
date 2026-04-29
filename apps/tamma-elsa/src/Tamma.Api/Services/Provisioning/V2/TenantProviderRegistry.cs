namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Resolves an <see cref="ITenantInfrastructureProvider"/> by its
/// <see cref="ITenantInfrastructureProvider.ProviderKey"/>. The dispatch
/// workflow (Story 30-2) consumes this — there are NO switch statements
/// on provider key anywhere outside this registry (AC10).
/// </summary>
/// <remarks>
/// <para>Backed by an <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// built once at DI registration time. New providers plug in via DI
/// only.</para>
///
/// <para>The registry is platform-scoped (one instance for the whole
/// process) and SaaS-only by behaviour: in single-user mode the only
/// registered provider is <see cref="NullTenantProvider"/>, so every
/// lookup either returns the null seam or throws.</para>
/// </remarks>
public sealed class TenantProviderRegistry
{
    private readonly IReadOnlyDictionary<string, ITenantInfrastructureProvider> _providers;

    public TenantProviderRegistry(IEnumerable<ITenantInfrastructureProvider> providers)
    {
        if (providers is null) throw new ArgumentNullException(nameof(providers));

        var map = new Dictionary<string, ITenantInfrastructureProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null) continue;
            var key = provider.ProviderKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    $"Provider {provider.GetType().FullName} returned an empty ProviderKey.");
            }
            if (!map.TryAdd(key, provider))
            {
                throw new InvalidOperationException(
                    $"Duplicate provider key '{key}' " +
                    $"(types: {map[key].GetType().FullName} and {provider.GetType().FullName}).");
            }
        }
        _providers = map;
    }

    /// <summary>Look up the provider keyed by
    /// <paramref name="providerKey"/>. Throws
    /// <see cref="KeyNotFoundException"/> when no provider is registered
    /// under that key — the workflow surface treats this as a
    /// configuration error and bubbles it to the operator.</summary>
    public ITenantInfrastructureProvider GetProvider(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            throw new ArgumentException("Provider key required.", nameof(providerKey));
        if (!_providers.TryGetValue(providerKey, out var provider))
        {
            throw new KeyNotFoundException(
                $"No tenant infrastructure provider registered under key '{providerKey}'. " +
                $"Registered keys: {string.Join(", ", _providers.Keys)}.");
        }
        return provider;
    }

    /// <summary>Try-pattern variant for callers that want to gracefully
    /// fall back when a key is unknown (e.g. legacy rows).</summary>
    public bool TryGetProvider(string providerKey, out ITenantInfrastructureProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            provider = null;
            return false;
        }
        return _providers.TryGetValue(providerKey, out provider);
    }

    /// <summary>List the capabilities of every registered provider —
    /// the onboarding UI (Story 30-7) reads this to render the
    /// (backend, topology) picker.</summary>
    public IReadOnlyList<ProviderCapabilities> ListCapabilities() =>
        _providers.Values.Select(p => p.GetCapabilities()).ToList();

    /// <summary>The list of registered provider keys, for diagnostics
    /// and admin-endpoint metadata.</summary>
    public IReadOnlyCollection<string> RegisteredKeys => _providers.Keys.ToList();
}

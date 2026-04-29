namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 30-8 — V2 routing seam for
/// <see cref="ITenantConnectionResolver"/>. Maps a tenant id to its
/// runtime <see cref="TenantEndpointResolution"/> by dispatching through
/// the V2 <c>ITenantInfrastructureProvider</c> registered for the
/// tenant's <c>provider_key</c>.
///
/// <para><b>Why this lives in <c>Tamma.Data.Abstractions</c></b>: the
/// LRU pool resolver lives in <c>Tamma.Data.Pooling</c> and cannot
/// depend on <c>Tamma.Api</c> (where the V2 provider types live —
/// <c>Tamma.Api.Services.Provisioning.V2</c>). This abstraction is the
/// dependency-inversion seam: <c>Tamma.Api</c> implements it as
/// <c>V2TenantEndpointDirectory</c> and registers the implementation in
/// DI; <c>Tamma.Data</c> consumes only this contract.</para>
///
/// <para><b>Null seam</b>: the production wiring registers
/// <c>NullTenantEndpointDirectory</c> when no V2 providers exist
/// (single-user mode, dev / test). Every call returns
/// <see cref="TenantEndpointResolution.NotApplicable"/> so the resolver
/// falls back to the legacy decrypt-from-EncryptedConnectionString path
/// without any V2 configuration.</para>
///
/// <para><b>Concurrency</b>: implementations MUST be thread-safe.
/// Multiple resolver cold-misses for the same tenant call this in
/// parallel — implementations either coalesce internally or the
/// resolver's per-tenant build-lock collapses the herd to one call.</para>
/// </summary>
public interface ITenantEndpointDirectory
{
    /// <summary>
    /// Try to resolve the tenant's endpoints via the V2 path.
    ///
    /// <list type="bullet">
    ///   <item><description>Returns
    ///     <see cref="TenantEndpointResolution.NotApplicable"/> when the
    ///     tenant has no <c>provider_key</c> set, or when the registered
    ///     providers don't recognise the key. Caller falls back to the
    ///     legacy path.</description></item>
    ///   <item><description>Returns a populated
    ///     <see cref="TenantEndpointResolution"/> when the V2 provider
    ///     successfully resolved endpoints. Caller uses
    ///     <see cref="TenantEndpointResolution.DatabaseUrl"/> to build
    ///     the per-tenant data source.</description></item>
    ///   <item><description>Throws
    ///     <see cref="TenantNotProvisionedException"/> when the tenant
    ///     has a provider_key set but the provider says the tenant is
    ///     not in a state that yields endpoints (provisioning,
    ///     suspended, deprovisioning, etc). The resolver translates
    ///     this through the same status-aware error path the legacy
    ///     code uses.</description></item>
    ///   <item><description>Throws
    ///     <see cref="TenantNotFoundException"/> when the tenant id is
    ///     unknown to the control plane. Caller surfaces verbatim.</description></item>
    /// </list>
    /// </summary>
    Task<TenantEndpointResolution> TryResolveAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an <see cref="ITenantEndpointDirectory.TryResolveAsync"/>
/// call. Designed as a value type so the hot-path caller doesn't pay an
/// allocation when the V2 path is not applicable.
///
/// <para>Semantics:
/// <list type="bullet">
///   <item><description><see cref="IsApplicable"/> = <c>false</c>: V2
///     path didn't apply — the resolver falls back to the legacy
///     <c>EncryptedConnectionString</c> + <c>IConnectionStringDecryptor</c>
///     path.</description></item>
///   <item><description><see cref="IsApplicable"/> = <c>true</c>: V2
///     path applied; <see cref="DatabaseUrl"/> is the connection string
///     to feed into <c>NpgsqlDataSource</c>.
///     <see cref="EngineUrl"/> / <see cref="ProviderKey"/> are passed
///     through for diagnostic logging only.</description></item>
/// </list></para>
/// </summary>
public readonly struct TenantEndpointResolution
{
    /// <summary>Sentinel for "V2 path didn't match — fall back to
    /// legacy". Keeps callers from minting their own value-type literal.</summary>
    public static readonly TenantEndpointResolution NotApplicable = default;

    /// <summary>True when the V2 directory resolved the tenant; false
    /// when the resolver should fall through to the legacy decrypt
    /// path.</summary>
    public bool IsApplicable { get; }

    /// <summary>Postgres connection string for the tenant's database.
    /// Always set when <see cref="IsApplicable"/> is <c>true</c>.</summary>
    public string? DatabaseUrl { get; }

    /// <summary>Optional engine URL — populated when the provider's
    /// topology includes a per-tenant engine. <c>null</c> for shared-
    /// engine topologies. Recorded for diagnostics; the connection
    /// resolver ignores it.</summary>
    public string? EngineUrl { get; }

    /// <summary>The provider key that resolved the tenant, for
    /// diagnostic logging (e.g. <c>"cranl"</c>, <c>"hetzner"</c>).</summary>
    public string? ProviderKey { get; }

    private TenantEndpointResolution(string databaseUrl, string? engineUrl, string providerKey)
    {
        IsApplicable = true;
        DatabaseUrl = databaseUrl;
        EngineUrl = engineUrl;
        ProviderKey = providerKey;
    }

    /// <summary>Build a resolved endpoint result. Throws when
    /// <paramref name="databaseUrl"/> is null/empty — callers must pre-
    /// validate.</summary>
    public static TenantEndpointResolution Resolved(
        string databaseUrl,
        string? engineUrl,
        string providerKey)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
            throw new ArgumentException("databaseUrl required", nameof(databaseUrl));
        if (string.IsNullOrWhiteSpace(providerKey))
            throw new ArgumentException("providerKey required", nameof(providerKey));
        return new TenantEndpointResolution(databaseUrl, engineUrl, providerKey);
    }
}

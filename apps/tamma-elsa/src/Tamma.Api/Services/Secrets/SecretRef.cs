namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Opaque reference to a secret — the tuple
/// <c>(scope, tenantId?, name)</c> that uniquely identifies a row in
/// the backing store per Story 29-1 AC7.
///
/// <para>This is a cheap value type that callers pass to
/// <see cref="ISecretStore"/> instead of looking up the
/// <see cref="SecretMetadata.Id"/> first. The store resolves the ref
/// to the underlying row.</para>
///
/// <para><b>Invariant</b>: <c>TenantId</c> must be non-null when
/// <c>Scope == SecretScope.Tenant</c>, and null when
/// <c>Scope == SecretScope.Platform</c>. The constructor enforces this
/// — see the static factory methods <see cref="ForPlatform"/> /
/// <see cref="ForTenant"/> for the ergonomic call sites.</para>
/// </summary>
public sealed record SecretRef
{
    public SecretScope Scope { get; }
    public Guid? TenantId { get; }
    public string Name { get; }

    public SecretRef(SecretScope scope, Guid? tenantId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Secret name must be a non-empty slug.", nameof(name));

        switch (scope)
        {
            case SecretScope.Platform when tenantId is not null:
                throw new ArgumentException(
                    "Platform-scoped secrets must not carry a tenant id.",
                    nameof(tenantId));
            case SecretScope.Tenant when tenantId is null:
                throw new ArgumentException(
                    "Tenant-scoped secrets must carry a non-null tenant id.",
                    nameof(tenantId));
        }

        Scope = scope;
        TenantId = tenantId;
        Name = name;
    }

    /// <summary>Build a platform-scoped reference.</summary>
    public static SecretRef ForPlatform(string name) =>
        new(SecretScope.Platform, tenantId: null, name);

    /// <summary>Build a tenant-scoped reference.</summary>
    public static SecretRef ForTenant(Guid tenantId, string name) =>
        new(SecretScope.Tenant, tenantId, name);

    /// <summary>
    /// Render the ref as a stable storage-friendly key. Suitable for
    /// log fields and audit-event tags; not parsed back into a ref.
    /// </summary>
    public string ToStorageKey() => Scope switch
    {
        SecretScope.Platform => $"platform:{Name}",
        SecretScope.Tenant => $"tenant:{TenantId}:{Name}",
        _ => $"unknown:{Name}"
    };
}

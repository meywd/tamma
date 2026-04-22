namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Scope of a secret in the cabinet.
///
/// <para><see cref="Platform"/> secrets belong to the platform (the
/// instance operator) — examples include the shared HMAC, the Cranl API
/// key, the GitHub App private key. They are stored without a tenant
/// binding and only platform admins can list / read / rotate them.</para>
///
/// <para><see cref="Tenant"/> secrets belong to a specific tenant —
/// examples include per-tenant DB role passwords, tenant-scoped webhook
/// HMAC keys, tenant-scoped API keys. They are stored with a non-null
/// tenant binding and only tenant admins (and platform admins acting on
/// behalf of the tenant) can list / read / rotate them.</para>
///
/// <para>The two scopes share a single backing table; the namespacing
/// rule (Story 29-1 AC7) is that <c>Name</c> is unique per
/// <c>(scope, tenantId?)</c> tuple — so two tenants can both have
/// <c>db/app-role</c> and the platform can have <c>db/app-role</c>
/// without collision.</para>
/// </summary>
public enum SecretScope
{
    /// <summary>Platform-wide secret. <c>TenantId</c> must be null.</summary>
    Platform,

    /// <summary>Tenant-scoped secret. <c>TenantId</c> must be non-null.</summary>
    Tenant
}

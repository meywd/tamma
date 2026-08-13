using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-2 AC3 — the seam every Tamma component goes through to
/// talk to a git platform on behalf of a tenant. Hands back a
/// ready-to-use <see cref="IGitPlatformDriver"/> wired to the
/// tenant's configured installation auth, base URL, and effective
/// capability set.
///
/// <para>The resolver owns three responsibilities:</para>
/// <list type="number">
///   <item>Read the per-(tenant, kind) installation row from the
///         <c>tenant_platform_installations</c> registry.</item>
///   <item>Decrypt the installation credential via Story 29's
///         <c>ISecretStore</c> + <c>ISecretStoreBackend</c> seam —
///         every secret read goes through that interface, no bypass.</item>
///   <item>Compose those into a driver instance via the keyed-DI
///         pattern Story 31-1 locked in
///         (<c>IGitPlatformDriverFactory</c> per kind).</item>
/// </list>
///
/// <para>Mode behavior:</para>
/// <list type="bullet">
///   <item><b>single-user mode</b>: rows carry the synthetic
///         single-user tenant id; <see cref="ResolveForTenantAsync"/>
///         returns the lone driver for that tenant + kind.</item>
///   <item><b>SaaS mode</b>: rows carry the real tenant id;
///         <see cref="ResolveForTenantAsync"/> scopes lookups by the
///         caller's tenant id; cross-tenant lookups return null.</item>
/// </list>
///
/// <para>All methods are async + cancellable. The resolver caches the
/// composed driver per <c>(tenantId, kind)</c> key for a configurable
/// TTL; cache invalidation is event-driven on
/// <c>PLATFORM.INSTALLATION.CREDENTIAL_ROTATED</c>,
/// <c>PLATFORM.INSTALLATION.DISCONNECTED</c>, and
/// <c>TENANT.SWITCH_ORG</c> events.</para>
/// </summary>
/// <summary>
/// Epic 31 P2 — which credential tier satisfied a
/// <see cref="IPlatformResolver.ResolveForMediationAsync"/> call.
/// Mediation surfaces this as the <c>credentialSource</c> LABEL
/// (byok / platform) on results + audit events — never the credential.
/// </summary>
public enum MediationCredentialSource
{
    /// <summary>The tenant's own <c>tenant_platform_installations</c>
    /// row (the BYOK tier).</summary>
    TenantInstallation = 1,

    /// <summary>The deployment-level <c>Platform:</c> config section
    /// (single-user activation / the SaaS system tier).</summary>
    PlatformDefault = 2,
}

/// <summary>
/// Epic 31 P2 — a resolved driver plus the tier that produced it.
/// </summary>
public sealed record MediationDriverResolution(
    IGitPlatformDriver Driver,
    MediationCredentialSource Source);

public interface IPlatformResolver
{
    /// <summary>
    /// Resolve the primary driver for a tenant — the row flagged
    /// <c>IsPrimary</c> (or, when only one row exists for the tenant,
    /// that single row regardless of the flag).
    ///
    /// <para>Returns <see langword="null"/> when the tenant has no
    /// configured platform installation. Callers that want a
    /// deterministic fallback should compare the result to null and
    /// either fail fast or fall back to
    /// <see cref="NullGitPlatformDriver"/>.</para>
    /// </summary>
    Task<IGitPlatformDriver?> ResolveForTenantAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Epic 31 P2 — the mediation plane's resolution: tenant
    /// installation first, then the deployment-level <c>Platform:</c>
    /// config tier, else null (mediation fails closed with
    /// <c>GIT_TOKEN_UNAVAILABLE</c>).
    ///
    /// <para>Two-scoping rule (CLAUDE.md) answered explicitly:</para>
    /// <list type="bullet">
    ///   <item><b>single-user mode</b> (<paramref name="tenantId"/> is
    ///         null, or the synthetic personal tenant has no row): the
    ///         SOLE USER owns activation via the <c>Platform:</c>
    ///         config section — resolved as an in-memory installation,
    ///         never persisted (no config↔DB drift; idempotent by
    ///         construction). Source = <see cref="MediationCredentialSource.PlatformDefault"/>.</item>
    ///   <item><b>SaaS mode</b>: the TENANT owns activation via its
    ///         <c>tenant_platform_installations</c> row (unchanged DB
    ///         path). Source = <see cref="MediationCredentialSource.TenantInstallation"/>.
    ///         A tenant without a row falls back to the deployment's
    ///         <c>Platform:</c> config — the same "system tier"
    ///         semantics the pre-P2 <c>GitHub:Token</c> fallback had.</item>
    /// </list>
    /// </summary>
    Task<MediationDriverResolution?> ResolveForMediationAsync(
        Guid? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Resolve the driver for a tenant + explicit kind — used by
    /// callers that already know which platform they want
    /// (e.g. a workflow targeting GitHub Actions specifically). When a
    /// tenant has multiple connected platforms, this is the precise
    /// path; otherwise prefer <see cref="ResolveForTenantAsync(Guid, CancellationToken)"/>.
    /// </summary>
    Task<IGitPlatformDriver?> ResolveForTenantAsync(
        Guid tenantId,
        PlatformKind kind,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve a driver for a webhook delivery. The webhook handler
    /// (Story 31-7) only has the platform-side external id from the
    /// payload — this method finds the matching installation row by
    /// <c>(kind, externalId)</c> and returns the driver if the row
    /// belongs to a known tenant. Returns null when no row matches.
    /// </summary>
    Task<IGitPlatformDriver?> ResolveForWebhookAsync(
        PlatformKind kind,
        string installationExternalId,
        CancellationToken ct = default);

    /// <summary>
    /// Epic 31 review (F-high) — resolve the driver for a SPECIFIC
    /// installation of a tenant, looked up by <c>(kind, externalId)</c> and
    /// TENANT-SCOPED: a row belonging to a different tenant answers null.
    ///
    /// <para>This is the per-repo resolution seam the pre-Epic-31 engine
    /// callback / agent dispatch had (repo → App installation →
    /// installation token): a tenant with the App on MULTIPLE installations
    /// (personal + org, two orgs) cannot ride the tenant-primary driver for
    /// repos of a sibling installation — GitHub App installation tokens
    /// cannot see a sibling installation's repos (404). Callers map the
    /// repo to its installation external id (the App-plane repo registry)
    /// and resolve here, falling back to
    /// <see cref="ResolveForMediationAsync"/> when no per-repo row exists.</para>
    ///
    /// <para>Caching note: the driver cache is keyed <c>(tenant, kind)</c>
    /// and holds the tenant's PRIMARY row's driver — resolving a
    /// NON-primary installation composes WITHOUT touching that cache so the
    /// two installations' drivers can never be served for each other.</para>
    /// </summary>
    Task<IGitPlatformDriver?> ResolveForRepoInstallationAsync(
        Guid tenantId,
        PlatformKind kind,
        string installationExternalId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve a driver for a webhook delivery when the receiver has
    /// the row id directly (e.g. an admin-side replay tool). Returns
    /// null when the row id does not exist or has been soft-deleted.
    /// </summary>
    Task<IGitPlatformDriver?> ResolveByInstallationIdAsync(
        Guid installationRowId, CancellationToken ct = default);

    /// <summary>
    /// Enumerate every connected installation for a tenant. Used by
    /// the dashboard to render the "your connected platforms" panel
    /// and by future multi-platform routing logic. The returned
    /// installations carry the row id, kind, base URL, and external
    /// id — drivers are composed lazily via
    /// <see cref="ResolveForTenantAsync(Guid, PlatformKind, CancellationToken)"/>.
    /// </summary>
    Task<IReadOnlyList<PlatformInstallation>> ListForTenantAsync(
        Guid tenantId, CancellationToken ct = default);
}

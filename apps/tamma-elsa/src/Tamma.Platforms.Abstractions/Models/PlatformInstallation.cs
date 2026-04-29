namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Identity record for a single platform binding owned by a tenant
/// (or, in single-user mode, the lone user).
///
/// <para>This is the missing link the platform brief calls out: in
/// single-user mode there's typically one installation per Tamma
/// instance; in SaaS mode each tenant has its own installation per
/// platform (different GitHub org, different GitLab self-hosted host,
/// etc.). The Story 31-2 platform registry resolves a request's
/// (tenantId, platformKind) → <see cref="PlatformInstallation"/> →
/// driver instance via keyed DI.</para>
/// </summary>
/// <param name="Id">Internal Tamma installation id.</param>
/// <param name="TenantId">
/// Owning tenant. In single-user mode this is the synthetic
/// single-user tenant id (the same value for every binding).
/// </param>
/// <param name="Kind">Which platform driver to use.</param>
/// <param name="BaseUrl">
/// Platform base URL. <c>https://api.github.com</c> for github.com;
/// the self-hosted host for Gitea/Forgejo/GitLab/etc. Never null —
/// drivers must accept a value even for the public hosted platform.
/// </param>
/// <param name="InstallationExternalId">
/// Platform-side identifier (GitHub installation id, GitLab group
/// id, Azure DevOps organization id). Opaque string; drivers parse.
/// </param>
public sealed record PlatformInstallation(
    Guid Id,
    Guid TenantId,
    PlatformKind Kind,
    string BaseUrl,
    string? InstallationExternalId);

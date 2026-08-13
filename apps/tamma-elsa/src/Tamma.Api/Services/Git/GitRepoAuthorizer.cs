using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Logging;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 (AC2) — resolves the GitHub installation that grants access to
/// <c>{repo}</c> (via the Epic 18/28 tenant↔repo registry,
/// <see cref="IInstallationRepository.GetByRepoFullNameAsync"/>) and asserts the
/// installation's tenant equals the acting tenant. Fail-closed:
/// <list type="bullet">
///   <item>no installation for the repo ⇒ denied;</item>
///   <item>the installation belongs to a DIFFERENT tenant ⇒ denied (the
///     cross-tenant write/merge this story exists to prevent).</item>
/// </list>
/// A denial NEVER resolves a token or reaches the platform.
///
/// <para><b>Null-tenant handling is mode-gated (F1).</b> Nullable equality alone
/// fails OPEN in SaaS: a null acting tenant (<c>EngineServiceOnly</c> needs no
/// <c>X-Tenant-Id</c>) against a null-<c>TenantId</c> orphan install
/// (<c>InstallationRouterService</c> persists <c>TenantId=null</c> for unlinked
/// installs) is <c>null == null</c> ⇒ Allow, then the platform static
/// <c>GitHub:Token</c> writes cross-tenant. So:
/// <list type="bullet">
///   <item><b>single-user</b> — the sole user owns everything; a matched
///     null/null is the legit sole-user case ⇒ Allow when
///     <c>installation.TenantId == tenantId</c>.</item>
///   <item><b>SaaS</b> — BOTH the acting tenant AND the installation's tenant
///     must be present and equal. A null acting tenant OR a null-<c>TenantId</c>
///     (orphan) install ⇒ Deny.</item>
/// </list></para>
/// </summary>
public sealed class GitRepoAuthorizer : IGitRepoAuthorizer
{
    private readonly IInstallationRepository _installations;
    private readonly ITammaModeProvider _mode;
    private readonly ILogger<GitRepoAuthorizer> _logger;

    public GitRepoAuthorizer(
        IInstallationRepository installations,
        ITammaModeProvider mode,
        ILogger<GitRepoAuthorizer> logger)
    {
        _installations = installations ?? throw new ArgumentNullException(nameof(installations));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GitRepoAuthorization> AuthorizeAsync(Guid? tenantId, string repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return GitRepoAuthorization.Deny("repo is required");
        }

        var installation = await _installations.GetByRepoFullNameAsync(repo).ConfigureAwait(false);
        if (installation is null)
        {
            _logger.LogWarning(
                "git-mediation guard DENIED: no installation grants access to repo {Repo} (tenant {TenantId})",
                LogSanitizer.Clean(repo), tenantId);
            return GitRepoAuthorization.Deny("no installation grants access to this repository");
        }

        // The acting tenant (X-Tenant-Id) MUST own the installation for {repo}.
        // The null/null allowance is legit ONLY in single-user mode; in SaaS a
        // null acting tenant or a null-TenantId (orphan) install fails OPEN under
        // plain nullable equality, so SaaS requires BOTH ids present and equal.
        //
        // 2026-08-13 (engine-driven E2E): single-user is grant-existence, not id
        // equality. There is exactly ONE principal in single-user mode — the sole
        // user owns every installation row — and the acting tenant may now be
        // their PERSONAL tenant (bound ambient by EnsurePersonalTenantMiddleware
        // for service-plane calls) while grant rows registered before/outside
        // that tenant carry TenantId=null. Strict equality made the sole user
        // "cross-tenant" against their own grant the moment the personal tenant
        // bound. The no-installation deny above still applies unchanged.
        var authorized = _mode.Mode == TammaMode.SaaS
            ? tenantId is { } actingTenant
                && installation.TenantId is { } installTenant
                && installTenant == actingTenant
            : true;

        if (!authorized)
        {
            _logger.LogWarning(
                "git-mediation guard DENIED (cross-tenant): repo {Repo} is not owned by the acting tenant {TenantId} (mode {Mode})",
                LogSanitizer.Clean(repo), tenantId, _mode.Mode);
            return GitRepoAuthorization.Deny("the acting tenant is not authorized for this repository");
        }

        return GitRepoAuthorization.Allow();
    }
}

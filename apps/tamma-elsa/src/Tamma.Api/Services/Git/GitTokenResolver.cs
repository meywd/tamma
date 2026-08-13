using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 (AC3) — BYOK→platform git-token resolution, keyed per-tenant
/// (Epic 28) through the Epic 29 secret cabinet, tenant→system→error.
///
/// <para><b>BYOK</b> — the tenant's git installation credential: the Story 31-2
/// <c>tenant_platform_installations</c> row (platform kind <c>github</c>) carries
/// a <c>SecretRef</c> (scope + name); the active plaintext is read through Epic
/// 29's <see cref="IPlatformCredentialReader"/> (no bypass). A non-empty read ⇒
/// <c>byok</c>.</para>
///
/// <para><b>Platform</b> — the platform-provided default token
/// (<c>GitHub:Token</c>), used where BYOK is not (yet) wired for this tenant ⇒
/// <c>platform</c>. This is the legitimate "system" tier of tenant→system→error,
/// and the resolved token IS what the platform call uses.</para>
///
/// <para><b>Error</b> — neither resolvable ⇒ null ⇒ the mediation returns 503
/// <c>GIT_TOKEN_UNAVAILABLE</c> (fail-closed). NEVER an empty/default token.</para>
/// </summary>
public sealed class GitTokenResolver : IGitTokenResolver
{
    private readonly ITenantPlatformInstallationRepository _installations;
    private readonly IPlatformCredentialReader _credentialReader;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitTokenResolver> _logger;

    public GitTokenResolver(
        ITenantPlatformInstallationRepository installations,
        IPlatformCredentialReader credentialReader,
        IConfiguration configuration,
        ILogger<GitTokenResolver> logger)
    {
        _installations = installations ?? throw new ArgumentNullException(nameof(installations));
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GitTokenResolution?> ResolveAsync(Guid? tenantId, string repo, CancellationToken ct = default)
    {
        // ── tenant tier (BYOK) ──
        if (tenantId is { } tid)
        {
            var byok = await TryResolveByokAsync(tid, ct).ConfigureAwait(false);
            if (byok is not null)
            {
                return new GitTokenResolution(byok, GitCredentialSources.Byok);
            }
        }

        // ── system tier (platform default) ──
        var platformToken = _configuration["GitHub:Token"];
        if (!string.IsNullOrWhiteSpace(platformToken))
        {
            return new GitTokenResolution(platformToken, GitCredentialSources.Platform);
        }

        // ── error tier (fail-closed) ──
        _logger.LogWarning(
            "git token unresolvable (BYOK absent + no platform GitHub:Token) for tenant {TenantId} — failing closed (GIT_TOKEN_UNAVAILABLE)",
            tenantId);
        return null;
    }

    private async Task<string?> TryResolveByokAsync(Guid tenantId, CancellationToken ct)
    {
        // Epic 31 P2 — the BYOK tier resolves the tenant's PRIMARY
        // installation of ANY kind (the old hardcoded "github" filter made
        // every non-GitHub tenant invisible to raw-git/CI credential
        // resolution). The credential plaintext is whatever the driver
        // wire-format stores — a PAT for raw-git use, or a JSON credential
        // reference the raw-git job cannot use directly (callers that need
        // clone/push credentials must tolerate a null here and fall to the
        // platform tier).
        var installation = await _installations
            .GetByTenantPrimaryAsync(tenantId, ct)
            .ConfigureAwait(false);
        if (installation is null || string.IsNullOrWhiteSpace(installation.CredentialSecretName))
        {
            return null;
        }

        var token = await _credentialReader
            .ReadActivePlaintextAsync(
                installation.CredentialSecretScope,
                tenantId,
                installation.CredentialSecretName,
                ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // A JSON credential (e.g. the P2 registry bridge's
        // {"kind":"app",...} App-installation reference) is a DRIVER wire
        // format, not a bearer token — using it as one would send a JSON
        // blob as an Authorization header. Raw-git/CI consumers fall back
        // to the platform tier; the driver plane resolves the same row
        // properly through IPlatformResolver.
        return token.TrimStart().StartsWith('{') ? null : token;
    }
}

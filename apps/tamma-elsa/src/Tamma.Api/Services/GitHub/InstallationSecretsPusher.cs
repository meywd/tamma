using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Per-repo result of an Actions-secrets push. Wire codes preserved from the
/// retired <c>IGitHubSecretsProvisioner</c> seam — in particular
/// <c>github_client_not_configured</c> when no driver / secrets surface
/// resolves, so operators keep seeing exactly which repos still hold a stale
/// key.
/// </summary>
public sealed record SecretProvisionResult(
    string Owner,
    string Repo,
    bool Success,
    string? Error);

/// <summary>
/// Epic 31 P4 M4 — install-time <c>TAMMA_API_KEY</c> provisioning, migrated
/// off the <c>[Obsolete]</c> GitHub-only <c>IGitHubSecretsProvisioner</c>
/// (Octokit + process-singleton App credentials, deleted in this milestone)
/// onto the DRIVER PLANE: the tenant's resolved GitHub driver exposes
/// Story 31-8's <c>ICiSecretsProvisioner</c> via <c>driver.CiSecrets</c>,
/// which encrypts with libsodium sealed-box against the per-installation
/// credential (App token or BYOK PAT — both modes, GHES-aware).
/// </summary>
public interface IInstallationSecretsPusher
{
    /// <summary>
    /// Push <paramref name="secretName"/> = <paramref name="secretValue"/> to
    /// every listed repo through the tenant's resolved GitHub driver. Per-repo
    /// failures never throw; no resolvable driver/secrets surface degrades to
    /// per-repo <c>github_client_not_configured</c> entries (the retired
    /// seam's exact degraded mode).
    /// </summary>
    Task<IReadOnlyList<SecretProvisionResult>> PushAsync(
        Guid tenantId,
        IReadOnlyList<(string Owner, string Repo)> repos,
        string secretName,
        string secretValue,
        CancellationToken ct = default);
}

public sealed class DriverInstallationSecretsPusher : IInstallationSecretsPusher
{
    /// <summary>Wire code preserved from the retired seam's Null impl.</summary>
    public const string NotConfiguredError = "github_client_not_configured";

    private readonly IPlatformResolver _resolver;
    private readonly Tamma.Data.Repositories.IInstallationRepository? _appInstallations;
    private readonly ILogger<DriverInstallationSecretsPusher> _logger;

    public DriverInstallationSecretsPusher(
        IPlatformResolver resolver,
        ILogger<DriverInstallationSecretsPusher> logger,
        Tamma.Data.Repositories.IInstallationRepository? appInstallations = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _appInstallations = appInstallations;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SecretProvisionResult>> PushAsync(
        Guid tenantId,
        IReadOnlyList<(string Owner, string Repo)> repos,
        string secretName,
        string secretValue,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repos);
        if (repos.Count == 0) return Array.Empty<SecretProvisionResult>();

        // Epic 31 review (F-high) — group repos by the App-plane installation
        // that owns them and resolve PER INSTALLATION: a tenant with the App
        // on multiple installations cannot push a sibling installation's
        // repos through the tenant-primary driver (its token cannot see
        // them). Repos without a registry row (BYOK / single-installation)
        // ride the tenant's GitHub-kind driver, the pre-review behavior.
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < repos.Count; i++)
        {
            var key = string.Empty; // "" = tenant-kind fallback tier
            if (_appInstallations is not null)
            {
                try
                {
                    var install = await _appInstallations
                        .GetByRepoFullNameAsync($"{repos[i].Owner}/{repos[i].Repo}")
                        .ConfigureAwait(false);
                    if (install?.TenantId == tenantId)
                    {
                        key = install.InstallationId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Repo→installation lookup failed for {Owner}/{Repo}; using the tenant driver",
                        repos[i].Owner, repos[i].Repo);
                }
            }
            (groups.TryGetValue(key, out var list) ? list : groups[key] = new List<int>()).Add(i);
        }

        var results = new SecretProvisionResult[repos.Count];
        foreach (var (installationExternalId, indexes) in groups)
        {
            var secrets = await ResolveSecretsAsync(tenantId, installationExternalId, ct)
                .ConfigureAwait(false);
            if (secrets is null)
            {
                // The retired seam's degraded mode, verbatim: the repo reports
                // github_client_not_configured so the caller's summary shows
                // exactly which repos still need the secret.
                foreach (var i in indexes)
                {
                    results[i] = new SecretProvisionResult(
                        repos[i].Owner, repos[i].Repo, false, NotConfiguredError);
                }
                continue;
            }

            var targets = indexes
                .Select(i => (CiSecretTarget)new CiSecretTarget.Repo(repos[i].Owner, repos[i].Repo))
                .ToList();

            IReadOnlyList<CiSecretProvisionResult> provisioned;
            try
            {
                provisioned = await secrets.ProvisionSecretAsync(
                    CiSecretScope.Repo,
                    targets,
                    secretName,
                    new RedactedSecret(secretValue),
                    metadata: null,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "CI-secrets push threw for tenant {TenantId}; recording per-repo failures", tenantId);
                foreach (var i in indexes)
                {
                    results[i] = new SecretProvisionResult(
                        repos[i].Owner, repos[i].Repo, false, $"unknown:{ex.GetType().Name}");
                }
                continue;
            }

            // Map back positionally — the provisioner returns one result per
            // target in order.
            for (var j = 0; j < indexes.Count; j++)
            {
                var i = indexes[j];
                results[i] = j < provisioned.Count
                    ? new SecretProvisionResult(
                        repos[i].Owner, repos[i].Repo, provisioned[j].Success, provisioned[j].Error)
                    : new SecretProvisionResult(
                        repos[i].Owner, repos[i].Repo, false, "unknown:missing_result");
            }
        }
        return results;
    }

    /// <summary>Resolve the secrets surface for one group: the specific
    /// installation when the repo registry named one, else the tenant's
    /// GitHub-kind driver; a per-installation miss also falls back to the
    /// tenant driver (never a widened credential — the fallback is the same
    /// tenant's own primary installation).</summary>
    private async Task<ICiSecretsProvisioner?> ResolveSecretsAsync(
        Guid tenantId, string installationExternalId, CancellationToken ct)
    {
        try
        {
            IGitPlatformDriver? driver = null;
            if (installationExternalId.Length > 0)
            {
                driver = await _resolver
                    .ResolveForRepoInstallationAsync(
                        tenantId, PlatformKind.GitHub, installationExternalId, ct)
                    .ConfigureAwait(false);
            }
            driver ??= await _resolver
                .ResolveForTenantAsync(tenantId, PlatformKind.GitHub, ct)
                .ConfigureAwait(false);
            return driver?.CiSecrets;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "GitHub driver resolution failed for tenant {TenantId} during secret push", tenantId);
            return null;
        }
    }
}

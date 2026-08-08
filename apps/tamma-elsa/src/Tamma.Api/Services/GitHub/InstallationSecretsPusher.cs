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
    private readonly ILogger<DriverInstallationSecretsPusher> _logger;

    public DriverInstallationSecretsPusher(
        IPlatformResolver resolver,
        ILogger<DriverInstallationSecretsPusher> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
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

        ICiSecretsProvisioner? secrets = null;
        try
        {
            var driver = await _resolver
                .ResolveForTenantAsync(tenantId, PlatformKind.GitHub, ct)
                .ConfigureAwait(false);
            secrets = driver?.CiSecrets;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "GitHub driver resolution failed for tenant {TenantId} during secret push", tenantId);
        }

        if (secrets is null)
        {
            // The retired seam's degraded mode, verbatim: every repo reports
            // github_client_not_configured so the caller's summary shows
            // exactly which repos still need the secret.
            return repos
                .Select(r => new SecretProvisionResult(r.Owner, r.Repo, false, NotConfiguredError))
                .ToList();
        }

        var targets = repos
            .Select(r => (CiSecretTarget)new CiSecretTarget.Repo(r.Owner, r.Repo))
            .ToList();

        IReadOnlyList<CiSecretProvisionResult> results;
        try
        {
            results = await secrets.ProvisionSecretAsync(
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
            return repos
                .Select(r => new SecretProvisionResult(
                    r.Owner, r.Repo, false, $"unknown:{ex.GetType().Name}"))
                .ToList();
        }

        // Map back positionally — the provisioner returns one result per
        // target in order.
        var mapped = new List<SecretProvisionResult>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var (owner, repo) = i < repos.Count ? repos[i] : ("", "");
            mapped.Add(new SecretProvisionResult(
                owner, repo, results[i].Success, results[i].Error));
        }
        return mapped;
    }
}

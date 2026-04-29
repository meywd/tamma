namespace Tamma.Api.Services.GitHub;

#pragma warning disable CS0618 // Story 31-8: transitional impl of obsolete interface.

/// <summary>
/// Default <see cref="IGitHubSecretsProvisioner"/> when no GitHub App client
/// or libsodium binding is wired. Returns one
/// <c>github_client_not_configured</c> failure per repo so callers can record
/// a per-repo summary that surfaces the gap to operators (matches the
/// pattern <see cref="Tamma.Api.Services.SaaS.ApiKeyRotationService"/>
/// already uses for audit finding 021).
///
/// <para>Audit finding 013 — replace once libsodium (<c>NSec.Cryptography</c>
/// or equivalent) and the GitHub App client are wired.</para>
/// </summary>
public sealed class NullGitHubSecretsProvisioner : IGitHubSecretsProvisioner
{
    public Task<IReadOnlyList<SecretProvisionResult>> ProvisionSecretAsync(
        long installationId,
        IReadOnlyList<(string Owner, string Repo)> repos,
        string secretName,
        string secretValue,
        CancellationToken ct = default)
    {
        var results = (IReadOnlyList<SecretProvisionResult>)repos
            .Select(r => new SecretProvisionResult(
                Owner: r.Owner,
                Repo: r.Repo,
                Success: false,
                Error: "github_client_not_configured"))
            .ToList();
        return Task.FromResult(results);
    }
}

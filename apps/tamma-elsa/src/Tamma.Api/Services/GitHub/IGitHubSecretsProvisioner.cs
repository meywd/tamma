namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Per-repo result of an Actions-secrets push. Mirrors the TS
/// <c>ProvisionResult</c> shape from
/// <c>packages/api/src/services/github-secrets-provisioner.ts</c>.
/// </summary>
public sealed record SecretProvisionResult(
    string Owner,
    string Repo,
    bool Success,
    string? Error);

/// <summary>
/// Encrypts a plaintext secret with a repo's libsodium public key and
/// pushes it to GitHub Actions secrets. Audit finding 013.
///
/// <para>The TS impl used <c>libsodium-wrappers</c> + Octokit's
/// <c>actions.getRepoPublicKey</c> + <c>actions.createOrUpdateRepoSecret</c>,
/// batched 5-at-a-time, and tolerated per-repo failures (archived repos,
/// permission errors). The C# port defers the real implementation to the
/// follow-up GitHub App client port story; until then the
/// <see cref="NullGitHubSecretsProvisioner"/> reports every repo as
/// <c>github_client_not_configured</c> so the operator knows secrets need
/// manual rotation.</para>
/// </summary>
public interface IGitHubSecretsProvisioner
{
    /// <summary>
    /// Provision <c>secretName</c> = <paramref name="secretValue"/> to every
    /// listed repo. Returns a per-repo result list. Per-repo failures do not
    /// throw; the caller decides whether the partial success is acceptable.
    /// </summary>
    Task<IReadOnlyList<SecretProvisionResult>> ProvisionSecretAsync(
        long installationId,
        IReadOnlyList<(string Owner, string Repo)> repos,
        string secretName,
        string secretValue,
        CancellationToken ct = default);
}

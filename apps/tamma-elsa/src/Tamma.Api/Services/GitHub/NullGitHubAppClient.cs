namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Default <see cref="IGitHubAppClient"/> when the App private key is not
/// wired. Every call short-circuits to
/// <see cref="GitHubAppResult{T}.NotConfigured"/>. Audit findings 007, 008,
/// 015 — replaced once the real Octokit-backed implementation lands.
/// </summary>
public sealed class NullGitHubAppClient : IGitHubAppClient
{
    public Task<GitHubAppResult<GitHubInstallationDetails>> GetInstallationAsync(
        long installationId, CancellationToken ct = default)
        => Task.FromResult(GitHubAppResult<GitHubInstallationDetails>.NotConfigured());

    public Task<GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>>
        ListInstallationReposAsync(long installationId, CancellationToken ct = default)
        => Task.FromResult(
            GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>.NotConfigured());
}

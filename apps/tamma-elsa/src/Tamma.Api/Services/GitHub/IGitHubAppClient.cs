namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Result envelope for GitHub App API calls. Mirrors the
/// <c>GitHubCallbackResult</c> pattern used by
/// <see cref="Tamma.Api.Services.Engine.IGitHubEngineCallbackService"/>:
/// when the App private key isn't wired we short-circuit to
/// <see cref="ServiceUnavailable"/> instead of returning bogus data.
///
/// <para>Audit findings 007, 008, 015 — TS used Octokit + App-JWT auth to
/// fetch installation metadata, list repos, and exchange for installation
/// access tokens. The C# port is intentionally deferred until the GitHub
/// App client port story; this contract sets the seam so the install
/// callback (finding 008) and secrets provisioner (finding 013) compile
/// against a stable interface.</para>
/// </summary>
public sealed record GitHubAppResult<T>(bool ServiceUnavailable, T? Result, string? ErrorReason)
{
    public static GitHubAppResult<T> NotConfigured() =>
        new(true, default, "github_client_not_configured");
    public static GitHubAppResult<T> Ok(T value) =>
        new(false, value, null);
    public static GitHubAppResult<T> Failed(string reason) =>
        new(false, default, reason);
}

public sealed record GitHubInstallationDetails(
    long InstallationId,
    string AccountLogin,
    string AccountType,
    long AppId,
    string PermissionsJson,
    DateTime? SuspendedAt);

public sealed record GitHubInstallationRepoDetail(
    long RepoId,
    string Owner,
    string Name,
    string FullName);

/// <summary>
/// GitHub App-authenticated HTTP client surface.
/// </summary>
public interface IGitHubAppClient
{
    /// <summary>
    /// Fetch installation metadata via <c>GET /app/installations/{id}</c>
    /// using App-JWT auth. (Finding 007)
    /// </summary>
    Task<GitHubAppResult<GitHubInstallationDetails>> GetInstallationAsync(
        long installationId, CancellationToken ct = default);

    /// <summary>
    /// List repositories accessible to the installation via the installation
    /// access token. Pages internally via <c>per_page=100</c>. (Finding 007)
    /// </summary>
    Task<GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>>
        ListInstallationReposAsync(long installationId, CancellationToken ct = default);
}

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Octokit;

namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Options required to authenticate as a GitHub App. Read from
/// <c>IConfiguration</c> at DI time. When <see cref="AppId"/> is zero or
/// <see cref="PrivateKeyPem"/> is blank the Null impl wins — this is the
/// documented dev-mode fallback (see
/// <see cref="GitHubInstallationServiceCollectionExtensions"/>).
/// </summary>
public sealed class GitHubAppOptions
{
    public long AppId { get; set; }
    public string PrivateKeyPem { get; set; } = string.Empty;
    public string UserAgent { get; set; } = "Tamma-API";
}

/// <summary>
/// Octokit-backed <see cref="IGitHubAppClient"/>. Handles:
///
/// <list type="bullet">
/// <item>RS256-signed JWT generation for App-level auth (10 min expiry).</item>
/// <item>Installation access-token minting via the GitHub Apps API, cached
/// in-process for ~55 min (GitHub's tokens are valid for 60 min).</item>
/// <item>Paginated repo enumeration (<c>per_page=100</c>).</item>
/// <item>Rate-limit detection: every response exposes
/// <c>GitHubClient.GetLastApiInfo()</c>; we surface exceptions with enough
/// context for callers to back off / retry. Finding 015.</item>
/// </list>
///
/// Audit findings: github 007, 015; engine 005-011, 021.
/// </summary>
public sealed class OctokitGitHubAppClient : IGitHubAppClient, IDisposable
{
    private readonly GitHubAppOptions _options;
    private readonly ILogger<OctokitGitHubAppClient> _logger;
    private readonly IOctokitClientFactory _clientFactory;

    // Installation tokens are valid for 60 min; cache for 55 min to give a
    // safety margin. Key by installation id.
    private static readonly TimeSpan TokenCacheLifetime = TimeSpan.FromMinutes(55);
    private readonly ConcurrentDictionary<long, CachedInstallationToken> _tokenCache = new();

    // Cached RSA key + signing credentials — parsing the PEM on every JWT
    // generation is wasteful (a few ms per call) and the key never changes at
    // runtime.
    private readonly RSA _rsa;
    private readonly SigningCredentials _signingCredentials;

    public OctokitGitHubAppClient(
        GitHubAppOptions options,
        ILogger<OctokitGitHubAppClient> logger,
        IOctokitClientFactory? clientFactory = null)
    {
        _options = options;
        _logger = logger;
        _clientFactory = clientFactory ?? new DefaultOctokitClientFactory();

        if (_options.AppId <= 0)
            throw new ArgumentException("GitHub:AppId must be set.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.PrivateKeyPem))
            throw new ArgumentException("GitHub:PrivateKey must be set.", nameof(options));

        _rsa = RSA.Create();
        _rsa.ImportFromPem(_options.PrivateKeyPem);
        _signingCredentials = new SigningCredentials(
            new RsaSecurityKey(_rsa) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } },
            SecurityAlgorithms.RsaSha256);
    }

    // ─── IGitHubAppClient ────────────────────────────────────────────────────

    public async Task<GitHubAppResult<GitHubInstallationDetails>> GetInstallationAsync(
        long installationId, CancellationToken ct = default)
    {
        try
        {
            var appClient = _clientFactory.CreateAppAuthenticatedClient(_options.UserAgent, GenerateJwt());
            var installation = await appClient.GitHubApps.GetInstallationForCurrent(installationId)
                .WaitAsync(ct).ConfigureAwait(false);

            var permissionsJson = SerializePermissions(installation);
            return GitHubAppResult<GitHubInstallationDetails>.Ok(
                new GitHubInstallationDetails(
                    InstallationId: installation.Id,
                    AccountLogin: installation.Account?.Login ?? "unknown",
                    AccountType: installation.Account?.Type?.ToString() ?? "User",
                    AppId: installation.AppId,
                    PermissionsJson: permissionsJson,
                    SuspendedAt: installation.SuspendedAt?.UtcDateTime));
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "Installation {InstallationId} not found", installationId);
            return GitHubAppResult<GitHubInstallationDetails>.Failed("installation_not_found");
        }
        catch (RateLimitExceededException ex)
        {
            LogRateLimitExceeded(ex, installationId: installationId);
            return GitHubAppResult<GitHubInstallationDetails>.Failed("github_rate_limited");
        }
        catch (AbuseException ex)
        {
            LogAbuseDetected(ex, installationId: installationId);
            return GitHubAppResult<GitHubInstallationDetails>.Failed("github_abuse_detected");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "GitHub API error while fetching installation {InstallationId}: {Status}",
                installationId, (int)ex.StatusCode);
            return GitHubAppResult<GitHubInstallationDetails>.Failed("github_api_error");
        }
    }

    public async Task<GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>>
        ListInstallationReposAsync(long installationId, CancellationToken ct = default)
    {
        try
        {
            var token = await GetOrCreateInstallationTokenAsync(installationId, ct).ConfigureAwait(false);
            var client = _clientFactory.CreateInstallationAuthenticatedClient(_options.UserAgent, token);

            // Octokit's `GitHubAppsInstallations.GetAllRepositoriesForCurrent`
            // handles pagination internally using per_page=100 via ApiOptions.
            var response = await client.GitHubApps.Installation
                .GetAllRepositoriesForCurrent(new ApiOptions { PageSize = 100 })
                .WaitAsync(ct).ConfigureAwait(false);

            var repos = response.Repositories
                .Select(r => new GitHubInstallationRepoDetail(
                    RepoId: r.Id,
                    Owner: r.Owner?.Login ?? string.Empty,
                    Name: r.Name,
                    FullName: r.FullName))
                .ToList();

            return GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>.Ok(repos);
        }
        catch (RateLimitExceededException ex)
        {
            LogRateLimitExceeded(ex, installationId: installationId);
            return GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>.Failed("github_rate_limited");
        }
        catch (AbuseException ex)
        {
            LogAbuseDetected(ex, installationId: installationId);
            return GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>.Failed("github_abuse_detected");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "GitHub API error while listing repos for installation {InstallationId}: {Status}",
                installationId, (int)ex.StatusCode);
            return GitHubAppResult<IReadOnlyList<GitHubInstallationRepoDetail>>.Failed("github_api_error");
        }
    }

    // ─── Public helpers for downstream consumers (engine callback service + provisioner) ────

    /// <summary>
    /// Get an installation-authenticated Octokit client. Token is cached for
    /// ~55 minutes to avoid re-minting on every call.
    /// </summary>
    public async Task<IGitHubClient> GetInstallationClientAsync(long installationId, CancellationToken ct = default)
    {
        var token = await GetOrCreateInstallationTokenAsync(installationId, ct).ConfigureAwait(false);
        return _clientFactory.CreateInstallationAuthenticatedClient(_options.UserAgent, token);
    }

    /// <summary>
    /// Invalidate the cached installation token. Useful when a 401 comes back
    /// mid-request — the caller can retry after forcing a token refresh.
    /// </summary>
    public void InvalidateInstallationToken(long installationId)
    {
        _tokenCache.TryRemove(installationId, out _);
    }

    // ─── Internals ───────────────────────────────────────────────────────────

    private async Task<string> GetOrCreateInstallationTokenAsync(long installationId, CancellationToken ct)
    {
        if (_tokenCache.TryGetValue(installationId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Token;
        }

        var appClient = _clientFactory.CreateAppAuthenticatedClient(_options.UserAgent, GenerateJwt());
        var token = await appClient.GitHubApps.CreateInstallationToken(installationId)
            .WaitAsync(ct).ConfigureAwait(false);

        _tokenCache[installationId] = new CachedInstallationToken(
            token.Token,
            DateTime.UtcNow.Add(TokenCacheLifetime));

        return token.Token;
    }

    /// <summary>
    /// Build a 10-minute-lifetime RS256 JWT for App-level auth. <c>iss</c> is
    /// the App's numeric id. Per GitHub docs, <c>iat</c> can be backdated 60s
    /// to tolerate clock skew.
    /// </summary>
    private string GenerateJwt()
    {
        var now = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: _options.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            audience: null,
            subject: null,
            notBefore: now.AddSeconds(-60),
            expires: now.AddMinutes(9), // 9 min (GitHub caps at 10)
            issuedAt: now.AddSeconds(-60),
            signingCredentials: _signingCredentials);
        return handler.WriteToken(token);
    }

    private static string SerializePermissions(Installation installation)
    {
        if (installation.Permissions is null) return "{}";
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var type = installation.Permissions.GetType();
        foreach (var prop in type.GetProperties())
        {
            var val = prop.GetValue(installation.Permissions);
            if (val is null) continue;
            // Octokit exposes InstallationPermissions as a record of string?
            // properties — e.g. Contents = "read" or null when not granted.
            var s = val.ToString();
            if (!string.IsNullOrEmpty(s))
                dict[ToSnakeCase(prop.Name)] = s;
        }
        return System.Text.Json.JsonSerializer.Serialize(dict);
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private void LogRateLimitExceeded(RateLimitExceededException ex, long? installationId = null)
    {
        var resetAt = ex.Reset;
        var retryIn = resetAt > DateTimeOffset.UtcNow ? resetAt - DateTimeOffset.UtcNow : TimeSpan.Zero;
        _logger.LogWarning(ex,
            "GitHub rate limit exceeded (installation={InstallationId}): limit={Limit} resetAt={ResetAt:o} retryIn={RetryInSec}s",
            installationId, ex.Limit, resetAt, (int)retryIn.TotalSeconds);
    }

    private void LogAbuseDetected(AbuseException ex, long? installationId = null)
    {
        _logger.LogWarning(ex,
            "GitHub abuse/secondary rate limit detected (installation={InstallationId}): retryAfterSec={RetryAfter}",
            installationId, ex.RetryAfterSeconds);
    }

    public void Dispose() => _rsa.Dispose();

    private readonly record struct CachedInstallationToken(string Token, DateTime ExpiresAt);
}

/// <summary>
/// Small factory seam so tests can stub out <see cref="GitHubClient"/>
/// construction without mocking its sealed types. Production code uses
/// <see cref="DefaultOctokitClientFactory"/>.
/// </summary>
public interface IOctokitClientFactory
{
    IGitHubClient CreateAppAuthenticatedClient(string userAgent, string jwt);
    IGitHubClient CreateInstallationAuthenticatedClient(string userAgent, string installationToken);
}

public sealed class DefaultOctokitClientFactory : IOctokitClientFactory
{
    public IGitHubClient CreateAppAuthenticatedClient(string userAgent, string jwt)
        => new GitHubClient(new ProductHeaderValue(userAgent))
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer)
        };

    public IGitHubClient CreateInstallationAuthenticatedClient(string userAgent, string installationToken)
        => new GitHubClient(new ProductHeaderValue(userAgent))
        {
            Credentials = new Credentials(installationToken)
        };
}

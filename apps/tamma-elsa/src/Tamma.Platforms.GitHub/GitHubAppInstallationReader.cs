using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P4 M4 — the GitHub App INSTALLATION-METADATA plane, absorbed into
/// the driver project. This replaces Tamma.Api's Octokit-backed
/// <c>IGitHubAppClient</c> (the last Octokit consumer): the install-linking
/// flow needs two App-authenticated reads that have no home on the
/// platform-neutral <c>IGitPlatformClient</c> because they are GitHub-App
/// concepts, not git-platform concepts:
/// <list type="bullet">
///   <item><c>GET /app/installations/{id}</c> — authoritative installation
///         metadata (account, app id, permissions, suspension).</item>
///   <item><c>GET /installation/repositories</c> — the repos the
///         installation can see (via a minted installation token,
///         paged <c>per_page=100</c>).</item>
/// </list>
///
/// <para>Result envelope semantics are byte-compatible with the retired
/// seam: <c>ServiceUnavailable=true</c> + <c>github_client_not_configured</c>
/// when the App credentials aren't wired (the Null impl), typed error
/// reasons (<c>installation_not_found</c> / <c>github_rate_limited</c> /
/// <c>github_api_error</c>) otherwise — the install callback's degraded-mode
/// behavior is unchanged.</para>
/// </summary>
public sealed record GitHubAppReadResult<T>(bool ServiceUnavailable, T? Result, string? ErrorReason)
{
    public static GitHubAppReadResult<T> NotConfigured() =>
        new(true, default, "github_client_not_configured");
    public static GitHubAppReadResult<T> Ok(T value) =>
        new(false, value, null);
    public static GitHubAppReadResult<T> Failed(string reason) =>
        new(false, default, reason);
}

public sealed record GitHubAppInstallationDetails(
    long InstallationId,
    string AccountLogin,
    string AccountType,
    long AppId,
    string PermissionsJson,
    DateTime? SuspendedAt);

public sealed record GitHubAppInstallationRepo(
    long RepoId,
    string Owner,
    string Name,
    string FullName);

/// <summary>GitHub App-authenticated read surface for installation metadata.</summary>
public interface IGitHubAppInstallationReader
{
    Task<GitHubAppReadResult<GitHubAppInstallationDetails>> GetInstallationAsync(
        long installationId, CancellationToken ct = default);

    Task<GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>>
        ListInstallationReposAsync(long installationId, CancellationToken ct = default);
}

/// <summary>Null impl — wired when <c>GitHub:AppId</c> / <c>GitHub:PrivateKey</c>
/// are absent; every call short-circuits to the documented degraded mode.</summary>
public sealed class NullGitHubAppInstallationReader : IGitHubAppInstallationReader
{
    public Task<GitHubAppReadResult<GitHubAppInstallationDetails>> GetInstallationAsync(
        long installationId, CancellationToken ct = default) =>
        Task.FromResult(GitHubAppReadResult<GitHubAppInstallationDetails>.NotConfigured());

    public Task<GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>>
        ListInstallationReposAsync(long installationId, CancellationToken ct = default) =>
        Task.FromResult(GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>.NotConfigured());
}

/// <summary>
/// REST implementation over plain <see cref="HttpClient"/> — no Octokit, no
/// JWT packages (the hand-rolled RS256 App JWT is the
/// <see cref="GitHubAppTokenMinter"/> shape). Installation tokens are cached
/// ~55 minutes per installation, matching the retired Octokit client.
/// </summary>
public sealed class RestGitHubAppInstallationReader : IGitHubAppInstallationReader
{
    private static readonly TimeSpan TokenCacheLifetime = TimeSpan.FromMinutes(55);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly long _appId;
    private readonly string _privateKeyPem;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, (string Token, DateTimeOffset ExpiresAt)> _tokenCache = new();

    public RestGitHubAppInstallationReader(
        HttpClient http,
        long appId,
        string privateKeyPem,
        string? baseUrl = null,
        TimeProvider? time = null,
        ILogger<RestGitHubAppInstallationReader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        _http = http;
        _appId = appId;
        _privateKeyPem = privateKeyPem;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? GitHubPlatformDriverFactory.DefaultBaseUrl
            : baseUrl.TrimEnd('/');
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        // Fail loud on a malformed key at construction (the minter's rule).
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);
    }

    public async Task<GitHubAppReadResult<GitHubAppInstallationDetails>> GetInstallationAsync(
        long installationId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"{_baseUrl}/app/installations/{installationId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAppJwt());
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return GitHubAppReadResult<GitHubAppInstallationDetails>.Failed("installation_not_found");
            }
            if ((int)resp.StatusCode == 429 || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return GitHubAppReadResult<GitHubAppInstallationDetails>.Failed("github_rate_limited");
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub App installation fetch failed for {InstallationId}: {Status}",
                    installationId, (int)resp.StatusCode);
                return GitHubAppReadResult<GitHubAppInstallationDetails>.Failed("github_api_error");
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)
                .ConfigureAwait(false);
            var account = body.TryGetProperty("account", out var acc)
                && acc.ValueKind == JsonValueKind.Object ? acc : default;
            DateTime? suspendedAt = null;
            if (body.TryGetProperty("suspended_at", out var susp)
                && susp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    susp.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsedSusp))
            {
                suspendedAt = parsedSusp.UtcDateTime;
            }

            return GitHubAppReadResult<GitHubAppInstallationDetails>.Ok(
                new GitHubAppInstallationDetails(
                    InstallationId: body.TryGetProperty("id", out var id) && id.TryGetInt64(out var idv)
                        ? idv : installationId,
                    AccountLogin: ReadString(account, "login") ?? "unknown",
                    AccountType: ReadString(account, "type") ?? "User",
                    AppId: body.TryGetProperty("app_id", out var app) && app.TryGetInt64(out var appv)
                        ? appv : _appId,
                    PermissionsJson: body.TryGetProperty("permissions", out var perms)
                        ? perms.GetRawText() : "{}",
                    SuspendedAt: suspendedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "GitHub App installation fetch threw for {InstallationId}", installationId);
            return GitHubAppReadResult<GitHubAppInstallationDetails>.Failed("github_api_error");
        }
    }

    public async Task<GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>>
        ListInstallationReposAsync(long installationId, CancellationToken ct = default)
    {
        try
        {
            var token = await GetOrCreateInstallationTokenAsync(installationId, ct).ConfigureAwait(false);
            if (token is null)
            {
                return GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>.Failed("github_api_error");
            }

            var repos = new List<GitHubAppInstallationRepo>();
            var page = 1;
            while (true)
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{_baseUrl}/installation/repositories?per_page=100&page={page}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode == 429)
                {
                    return GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>
                        .Failed("github_rate_limited");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "GitHub App repo listing failed for installation {InstallationId}: {Status}",
                        installationId, (int)resp.StatusCode);
                    return GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>
                        .Failed("github_api_error");
                }

                var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)
                    .ConfigureAwait(false);
                if (!body.TryGetProperty("repositories", out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                {
                    break;
                }
                var count = 0;
                foreach (var repo in arr.EnumerateArray())
                {
                    count++;
                    var owner = repo.TryGetProperty("owner", out var o)
                        && o.ValueKind == JsonValueKind.Object
                        ? ReadString(o, "login") ?? "" : "";
                    repos.Add(new GitHubAppInstallationRepo(
                        RepoId: repo.TryGetProperty("id", out var rid) && rid.TryGetInt64(out var ridv) ? ridv : 0,
                        Owner: owner,
                        Name: ReadString(repo, "name") ?? "",
                        FullName: ReadString(repo, "full_name") ?? ""));
                }
                if (count < 100) break;
                page++;
            }

            return GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>.Ok(repos);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "GitHub App repo listing threw for installation {InstallationId}", installationId);
            return GitHubAppReadResult<IReadOnlyList<GitHubAppInstallationRepo>>.Failed("github_api_error");
        }
    }

    private async Task<string?> GetOrCreateInstallationTokenAsync(long installationId, CancellationToken ct)
    {
        if (_tokenCache.TryGetValue(installationId, out var cached)
            && cached.ExpiresAt > _time.GetUtcNow())
        {
            return cached.Token;
        }

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}/app/installations/{installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAppJwt());
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "GitHub App installation-token mint failed for {InstallationId}: {Status}",
                installationId, (int)resp.StatusCode);
            return null;
        }
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)
            .ConfigureAwait(false);
        var token = body.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token)) return null;
        _tokenCache[installationId] = (token, _time.GetUtcNow().Add(TokenCacheLifetime));
        return token;
    }

    /// <summary>RS256 App JWT — the <see cref="GitHubAppTokenMinter"/> shape
    /// (iat backdated 60s, 9-minute expiry, iss = app id).</summary>
    internal string CreateAppJwt()
    {
        var now = _time.GetUtcNow();
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload = JsonSerializer.Serialize(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = _appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var signingInput =
            Base64Url(System.Text.Encoding.UTF8.GetBytes(header))
            + "." + Base64Url(System.Text.Encoding.UTF8.GetBytes(payload));
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);
        var signature = rsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(signingInput),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? ReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}

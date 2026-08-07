using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — installation-access-token minting for App-mode
/// drivers. This is the logic absorbed from
/// <c>Tamma.Api/Services/GitHub/OctokitGitHubAppClient.cs</c>
/// (RS256 App JWT → <c>POST /app/installations/{id}/access_tokens</c>
/// → ~55-minute in-process cache), re-implemented driver-side against
/// plain <see cref="HttpClient"/> so the driver keeps zero Octokit /
/// JWT-package dependencies. The JWT is hand-rolled: base64url(header)
/// + "." + base64url(payload), signed RSA-SHA256/PKCS#1 — exactly the
/// shape GitHub documents for App auth.
///
/// <para>Unlike the old process-singleton client (whose base address
/// was hardcoded to github.com — seam 7 in the execution plan), the
/// minter takes the API base URL from the factory, so GHES and
/// per-tenant Apps work through the same seam.</para>
/// </summary>
internal sealed class GitHubAppTokenMinter
{
    /// <summary>Installation tokens are valid for 60 min; refresh with
    /// a 5-minute safety margin.</summary>
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly long _appId;
    private readonly string _privateKeyPem;
    private readonly long _installationId;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;

    public GitHubAppTokenMinter(
        HttpClient http,
        string baseUrl,
        long appId,
        string privateKeyPem,
        long installationId,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        if (installationId <= 0) throw new ArgumentOutOfRangeException(nameof(installationId));

        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _appId = appId;
        _privateKeyPem = privateKeyPem;
        _installationId = installationId;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        // Fail loud at construction on a malformed key — the factory
        // is the right place for a bad credential to surface, not the
        // first API call.
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);
    }

    /// <summary>Installation id this minter is bound to (test helper).</summary>
    internal long InstallationId => _installationId;

    /// <summary>
    /// Get a valid installation access token, minting a fresh one when
    /// the cache is empty or near expiry. Returns null when minting
    /// fails (surfaced upstream as ServiceUnavailable / AuthExpired by
    /// <see cref="GitHubHttpClient"/>).
    /// </summary>
    public async Task<string?> GetInstallationTokenAsync(
        bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh)
        {
            lock (_gate)
            {
                if (_cachedToken is not null
                    && _cachedTokenExpiresAt - ExpirySafetyMargin > _time.GetUtcNow())
                {
                    return _cachedToken;
                }
            }
        }

        var jwt = CreateAppJwt();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_baseUrl}/app/installations/{_installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub App installation-token mint failed for installation {InstallationId}: {Status}",
                    _installationId, (int)response.StatusCode);
                return null;
            }
            var body = await response.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken: ct)
                .ConfigureAwait(false);
            var token = body.TryGetProperty("token", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token)) return null;

            var expiresAt = body.TryGetProperty("expires_at", out var e)
                && e.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    e.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                ? parsed
                : _time.GetUtcNow().AddMinutes(55);

            lock (_gate)
            {
                _cachedToken = token;
                _cachedTokenExpiresAt = expiresAt;
            }
            return token;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "GitHub App installation-token mint transport failure for installation {InstallationId}",
                _installationId);
            return null;
        }
    }

    /// <summary>
    /// Drop the cached token so the next call re-mints — used by the
    /// refresh-on-401 retry in <see cref="GitHubHttpClient"/>.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cachedToken = null;
            _cachedTokenExpiresAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Build a short-lifetime RS256 JWT for App-level auth. <c>iss</c>
    /// is the App's numeric id; per GitHub docs <c>iat</c> is backdated
    /// 60s to tolerate clock skew and expiry is capped under 10 min
    /// (we use 9, matching the absorbed OctokitGitHubAppClient).
    /// </summary>
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
            Base64Url(Encoding.UTF8.GetBytes(header))
            + "." + Base64Url(Encoding.UTF8.GetBytes(payload));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

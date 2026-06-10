using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.Gitea.Dtos;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Typed HTTP wrapper around <see cref="HttpClient"/> that handles
/// Gitea-specific concerns:
/// <list type="number">
///   <item>Bearer / token Authorization header from
///         <see cref="GiteaAuth"/>, with OAuth2 refresh-on-401 retry.</item>
///   <item>Base URL trimming so callers can pass relative paths
///         (<c>/api/v1/version</c>).</item>
///   <item>Rate-limit header parsing into <see cref="RateLimitInfo"/>
///         (Gitea exposes <c>X-RateLimit-*</c>).</item>
///   <item>Response → <see cref="PlatformResult{T}"/> projection via
///         <see cref="GiteaErrorMapper"/>.</item>
/// </list>
///
/// <para>Stateful: stores the last-observed rate limit so callers can
/// throttle proactively. Threadsafe for the typical "shared instance,
/// many awaiters" pattern; concurrent rate-limit updates may race but
/// the last writer wins, which is the desired semantics.</para>
/// </summary>
internal sealed class GiteaHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly Guid _installationId;
    private readonly string _baseUrl;
    private readonly GiteaOAuth2TokenCache _tokenCache;
    private readonly ILogger _logger;
    private GiteaAuth _auth;

    private RateLimitInfo? _rateLimit;

    public GiteaHttpClient(
        HttpClient http,
        Guid installationId,
        string baseUrl,
        GiteaAuth auth,
        GiteaOAuth2TokenCache tokenCache,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(tokenCache);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _http = http;
        _installationId = installationId;
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = auth;
        _tokenCache = tokenCache;
        _logger = logger ?? NullLogger.Instance;

        if (_http.DefaultRequestHeaders.Accept.Count == 0)
        {
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Tamma-Gitea-Driver", "1.0"));
        }
    }

    /// <summary>Last observed rate-limit headers — null until first call.</summary>
    public RateLimitInfo? LastRateLimit => _rateLimit;

    /// <summary>Base URL after trimming (test helper).</summary>
    internal string BaseUrl => _baseUrl;

    /// <summary>
    /// GET a JSON resource and project to the typed result. Returns
    /// <see cref="PlatformResult{T}.Failed"/> with the appropriate
    /// <see cref="PlatformError"/> on non-2xx.
    /// </summary>
    public async Task<PlatformResult<T>> GetJsonAsync<T>(
        string relativePath,
        CancellationToken ct)
    {
        return await SendAsync<T>(HttpMethod.Get, relativePath, body: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// POST a JSON body and deserialize the JSON response.
    /// </summary>
    public async Task<PlatformResult<T>> PostJsonAsync<T>(
        string relativePath,
        object body,
        CancellationToken ct)
    {
        return await SendAsync<T>(HttpMethod.Post, relativePath, body, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// POST without expecting a JSON response (used by cancel-run +
    /// merge-PR; we treat 2xx as success and return Ok(true)).
    /// </summary>
    public async Task<PlatformResult<bool>> PostNoContentAsync(
        string relativePath,
        object? body,
        CancellationToken ct)
    {
        var response = await SendRawAsync(HttpMethod.Post, relativePath, body, ct)
            .ConfigureAwait(false);
        if (response is null)
        {
            return PlatformResult<bool>.FromServiceUnavailable();
        }
        try
        {
            UpdateRateLimit(response);
            if (response.IsSuccessStatusCode)
            {
                return PlatformResult<bool>.FromOk(true);
            }
            var err = await GiteaErrorMapper.MapAsync(response, ct).ConfigureAwait(false);
            return PlatformResult<bool>.FromError(err);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// GET a binary stream (artifact zip). The caller owns the
    /// returned stream + the underlying response — both are wrapped in
    /// a disposable so disposal cascades.
    /// </summary>
    public async Task<PlatformResult<Stream>> GetStreamAsync(
        string relativePath,
        CancellationToken ct)
    {
        // Stream-mode requires HttpCompletionOption.ResponseHeadersRead
        // so we can read headers + start streaming the body without
        // buffering. Refresh-on-401 happens via a single retry.
        var response = await SendRawForStreamAsync(relativePath, ct)
            .ConfigureAwait(false);
        if (response is null)
        {
            return PlatformResult<Stream>.FromServiceUnavailable();
        }
        try
        {
            UpdateRateLimit(response);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return PlatformResult<Stream>.FromOk(new ResponseOwningStream(body, response));
            }
            var err = await GiteaErrorMapper.MapAsync(response, ct).ConfigureAwait(false);
            response.Dispose();
            return PlatformResult<Stream>.FromError(err);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Build a full URL for a relative path using the trimmed base URL.
    /// Public for use by clients that need to construct paginator URLs.
    /// </summary>
    public string BuildUrl(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        if (!relativePath.StartsWith('/')) relativePath = "/" + relativePath;
        return _baseUrl + relativePath;
    }

    // ─────────────────────────────────────────────────────────────────

    private async Task<PlatformResult<T>> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken ct)
    {
        var response = await SendRawAsync(method, relativePath, body, ct)
            .ConfigureAwait(false);
        if (response is null)
        {
            return PlatformResult<T>.FromServiceUnavailable();
        }
        try
        {
            UpdateRateLimit(response);
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(JsonElement) || typeof(T) == typeof(JsonElement?))
                {
                    var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(raw))
                    {
                        return PlatformResult<T>.FromError(
                            new PlatformError.Unknown("empty body"));
                    }
                    using var doc = JsonDocument.Parse(raw);
                    var clone = doc.RootElement.Clone();
                    return PlatformResult<T>.FromOk((T)(object)clone);
                }
                var typed = await response.Content
                    .ReadFromJsonAsync<T>(JsonOptions, ct)
                    .ConfigureAwait(false);
                if (typed is null)
                {
                    return PlatformResult<T>.FromError(
                        new PlatformError.Unknown("empty body"));
                }
                return PlatformResult<T>.FromOk(typed);
            }

            var err = await GiteaErrorMapper.MapAsync(response, ct).ConfigureAwait(false);
            return PlatformResult<T>.FromError(err);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Core send routine with OAuth2 refresh-on-401 retry. Returns null
    /// when the credential is missing / unrecoverable so the caller
    /// surfaces <see cref="PlatformResult{T}.ServiceUnavailable"/>.
    /// </summary>
    private async Task<HttpResponseMessage?> SendRawAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            HttpRequestMessage? request = null;
            HttpResponseMessage? response = null;
            try
            {
                var token = await ResolveBearerAsync(forceRefresh: attempt > 1, ct).ConfigureAwait(false);
                if (token is null)
                {
                    _logger.LogWarning(
                        "Gitea credential could not be resolved for installation {InstallationId}",
                        _installationId);
                    return null;
                }

                request = new HttpRequestMessage(method, BuildUrl(relativePath));
                request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: JsonOptions);
                }

                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                // Refresh-on-401 happens at most once + only for OAuth2.
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && _auth is GiteaAuth.OAuth2
                    && attempt == 1)
                {
                    _tokenCache.Invalidate(_installationId);
                    response.Dispose();
                    response = null;
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "Gitea HTTP transport failure for {Method} {Path}",
                    method, relativePath);
                response?.Dispose();
                // Fabricate a 503-shaped response so the standard
                // error-mapping path applies. We use a synthetic
                // status here; callers map this to ServiceUnavailable
                // via the standard error mapper.
                return BuildSyntheticServiceUnavailable();
            }
            finally
            {
                request?.Dispose();
            }
        }
    }

    private async Task<HttpResponseMessage?> SendRawForStreamAsync(
        string relativePath,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            HttpRequestMessage? request = null;
            try
            {
                var token = await ResolveBearerAsync(forceRefresh: attempt > 1, ct).ConfigureAwait(false);
                if (token is null) return null;

                request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(relativePath));
                request.Headers.Authorization = new AuthenticationHeaderValue("token", token);

                var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && _auth is GiteaAuth.OAuth2
                    && attempt == 1)
                {
                    _tokenCache.Invalidate(_installationId);
                    response.Dispose();
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "Gitea stream HTTP transport failure for {Path}", relativePath);
                return BuildSyntheticServiceUnavailable();
            }
            finally
            {
                request?.Dispose();
            }
        }
    }

    /// <summary>
    /// Resolve the bearer token to send. For bot tokens this is just
    /// the token. For OAuth2 it's a cached access token, refreshed via
    /// <c>POST /login/oauth/access_token</c> when missing or expired.
    /// </summary>
    private async Task<string?> ResolveBearerAsync(
        bool forceRefresh, CancellationToken ct)
    {
        switch (_auth)
        {
            case GiteaAuth.BotToken bot:
                return bot.Token;
            case GiteaAuth.OAuth2 oauth:
                if (!forceRefresh)
                {
                    var cached = _tokenCache.TryGet(_installationId);
                    if (cached is not null) return cached;
                }
                return await RefreshOAuth2Async(oauth, ct).ConfigureAwait(false);
            default:
                throw new InvalidOperationException(
                    $"unhandled GiteaAuth variant: {_auth.GetType().Name}");
        }
    }

    private async Task<string?> RefreshOAuth2Async(
        GiteaAuth.OAuth2 auth, CancellationToken ct)
    {
        // Build the refresh-token request per Gitea OAuth2 docs.
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = auth.RefreshToken,
            ["client_id"] = auth.ClientId,
            ["client_secret"] = auth.ClientSecret,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post, BuildUrl("/login/oauth/access_token"))
        {
            Content = new FormUrlEncodedContent(form),
        };

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Gitea OAuth2 refresh failed for installation {InstallationId} status={Status}",
                    _installationId, (int)response.StatusCode);
                return null;
            }
            var json = await response.Content
                .ReadAsStringAsync(ct)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var at)
                ? at.GetString() : null;
            if (string.IsNullOrEmpty(accessToken)) return null;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s)
                ? s : 3600;
            // 60-second safety margin per impl-plan §2.
            var ttl = TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60));
            _tokenCache.Set(_installationId, accessToken, ttl);
            return accessToken;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Gitea OAuth2 refresh transport failure");
            return null;
        }
    }

    private void UpdateRateLimit(HttpResponseMessage response)
    {
        int? limit = TryReadInt(response.Headers, "X-RateLimit-Limit");
        int? remaining = TryReadInt(response.Headers, "X-RateLimit-Remaining");
        DateTimeOffset? resetsAt = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
        {
            var first = values.FirstOrDefault();
            if (long.TryParse(first, out var unix))
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }
        if (limit.HasValue || remaining.HasValue || resetsAt.HasValue)
        {
            _rateLimit = new RateLimitInfo(limit, remaining, resetsAt);
        }
    }

    private static int? TryReadInt(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        var first = values.FirstOrDefault();
        return int.TryParse(first, out var n) ? n : null;
    }

    private static HttpResponseMessage BuildSyntheticServiceUnavailable()
    {
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("transport_error", Encoding.UTF8, "text/plain"),
        };
    }

    /// <summary>
    /// Wraps a content stream and the owning <see cref="HttpResponseMessage"/>
    /// so disposing the stream tears down both — keeps the connection
    /// from leaking after caller copies bytes off.
    /// </summary>
    private sealed class ResponseOwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _owner;

        public ResponseOwningStream(Stream inner, HttpResponseMessage owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.ReadAsync(buffer, offset, count, ct);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("artifact stream is read-only");

        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { /* best effort */ }
                try { _owner.Dispose(); } catch { /* best effort */ }
            }
            base.Dispose(disposing);
        }
    }
}

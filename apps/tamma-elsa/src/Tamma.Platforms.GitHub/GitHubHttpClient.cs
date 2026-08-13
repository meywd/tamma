using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — typed HTTP wrapper around
/// <see cref="HttpClient"/> handling GitHub-specific concerns
/// (mirrors the <c>GiteaHttpClient</c> pattern):
/// <list type="number">
///   <item>Bearer Authorization from <see cref="GitHubAuth"/> —
///         static PAT, or App-installation tokens minted by
///         <see cref="GitHubAppTokenMinter"/> with a refresh-on-401
///         retry.</item>
///   <item>Base URL trimming so callers pass relative REST paths
///         (<c>/repos/{o}/{r}</c>). BaseUrl comes from the
///         installation row — <c>https://api.github.com</c> for cloud,
///         the GHES <c>/api/v3</c> root for enterprise.</item>
///   <item>GHES-aware GraphQL endpoint selection
///         (<see cref="ComputeGraphQlUrl"/>): cloud is
///         <c>https://api.github.com/graphql</c>; GHES is
///         <c>https://HOST/api/graphql</c> next to its
///         <c>/api/v3</c> REST root.</item>
///   <item>Rate-limit header capture (<c>X-RateLimit-*</c>) into
///         <see cref="RateLimitInfo"/>.</item>
///   <item>Response → <see cref="PlatformResult{T}"/> projection via
///         <see cref="GitHubErrorMapper"/> — the no-throw driver
///         contract.</item>
/// </list>
/// </summary>
internal sealed class GitHubHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _graphQlUrl;
    private readonly GitHubAuth _auth;
    private readonly GitHubAppTokenMinter? _minter;
    private readonly ILogger _logger;

    private RateLimitInfo? _rateLimit;

    public GitHubHttpClient(
        HttpClient http,
        string baseUrl,
        GitHubAuth auth,
        GitHubAppTokenMinter? minter = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(auth);
        if (auth is GitHubAuth.App && minter is null)
        {
            throw new ArgumentException(
                "App-mode GitHubHttpClient requires a GitHubAppTokenMinter",
                nameof(minter));
        }

        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _graphQlUrl = ComputeGraphQlUrl(_baseUrl);
        _auth = auth;
        _minter = minter;
        _logger = logger ?? NullLogger.Instance;

        if (_http.DefaultRequestHeaders.Accept.Count == 0)
        {
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Tamma-GitHub-Driver", "1.0"));
        }
        if (!_http.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
        {
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    /// <summary>Last observed rate-limit headers — null until first call.</summary>
    public RateLimitInfo? LastRateLimit => _rateLimit;

    /// <summary>Base URL after trimming (test helper).</summary>
    internal string BaseUrl => _baseUrl;

    /// <summary>Resolved GraphQL endpoint (test helper).</summary>
    internal string GraphQlUrl => _graphQlUrl;

    /// <summary>
    /// GHES-aware GraphQL endpoint selection. Cloud
    /// (<c>api.github.com</c>) exposes GraphQL at <c>/graphql</c> on
    /// the API host; GHES exposes REST at
    /// <c>https://HOST/api/v3</c> and GraphQL at
    /// <c>https://HOST/api/graphql</c>.
    /// </summary>
    internal static string ComputeGraphQlUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var trimmed = baseUrl.TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/graphql";
        }
        if (trimmed.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^"/api/v3".Length] + "/api/graphql";
        }
        return trimmed + "/api/graphql";
    }

    public string BuildUrl(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        if (!relativePath.StartsWith('/')) relativePath = "/" + relativePath;
        return _baseUrl + relativePath;
    }

    public Task<PlatformResult<T>> GetJsonAsync<T>(string relativePath, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Get, relativePath, body: null, ct);

    public Task<PlatformResult<T>> PostJsonAsync<T>(string relativePath, object body, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Post, relativePath, body, ct);

    public Task<PlatformResult<T>> PatchJsonAsync<T>(string relativePath, object body, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Patch, relativePath, body, ct);

    public Task<PlatformResult<T>> PutJsonAsync<T>(string relativePath, object body, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Put, relativePath, body, ct);

    public Task<PlatformResult<T>> DeleteJsonAsync<T>(string relativePath, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Delete, relativePath, body: null, ct);

    /// <summary>
    /// POST / DELETE where success carries no meaningful JSON body
    /// (workflow dispatch 204, cancel-run 202, delete-branch 204).
    /// 2xx → Ok(true).
    /// </summary>
    public async Task<PlatformResult<bool>> SendNoContentAsync(
        HttpMethod method, string relativePath, object? body, CancellationToken ct)
    {
        var response = await SendRawAsync(method, relativePath, body, ct).ConfigureAwait(false);
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
            var err = await GitHubErrorMapper.MapAsync(response, ct).ConfigureAwait(false);
            return PlatformResult<bool>.FromError(err);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// GET a binary stream (artifact zip). Caller owns the returned
    /// stream; disposing it tears down the response too.
    /// </summary>
    public async Task<PlatformResult<Stream>> GetStreamAsync(
        string relativePath, CancellationToken ct)
    {
        var response = await SendRawAsync(
            HttpMethod.Get, relativePath, body: null, ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead)
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
            var err = await GitHubErrorMapper.MapAsync(response, ct).ConfigureAwait(false);
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
    /// Run a GraphQL query/mutation against the GHES-aware endpoint.
    /// HTTP failures map through <see cref="GitHubErrorMapper"/>; a
    /// 200 whose body carries a non-empty <c>errors</c> array maps to
    /// <see cref="PlatformError.InvalidRequest"/> code
    /// <c>"graphql_errors"</c> (the live path's
    /// <c>"graphql_errors: …"</c> classification). Returns the
    /// <c>data</c> element on success.
    /// </summary>
    public async Task<PlatformResult<JsonElement>> PostGraphQlAsync(
        string query, object variables, CancellationToken ct)
    {
        var response = await SendRawToUrlAsync(
            HttpMethod.Post, _graphQlUrl, new { query, variables }, ct)
            .ConfigureAwait(false);
        if (response is null)
        {
            return PlatformResult<JsonElement>.FromServiceUnavailable();
        }
        try
        {
            UpdateRateLimit(response);
            if (!response.IsSuccessStatusCode)
            {
                var err = await GitHubErrorMapper.MapAsync(response, ct).ConfigureAwait(false);
                return PlatformResult<JsonElement>.FromError(err);
            }
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
            {
                var hint = raw.Length > 512 ? raw[..512] : raw;
                return PlatformResult<JsonElement>.FromError(
                    new PlatformError.InvalidRequest("graphql_errors", hint));
            }
            if (root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                return PlatformResult<JsonElement>.FromOk(data.Clone());
            }
            return PlatformResult<JsonElement>.FromError(
                new PlatformError.Unknown("GraphQL response carried neither data nor errors"));
        }
        catch (JsonException)
        {
            return PlatformResult<JsonElement>.FromError(
                new PlatformError.Unknown("GraphQL response was not valid JSON"));
        }
        finally
        {
            response.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────

    private async Task<PlatformResult<T>> SendAsync<T>(
        HttpMethod method, string relativePath, object? body, CancellationToken ct)
    {
        var response = await SendRawAsync(method, relativePath, body, ct).ConfigureAwait(false);
        if (response is null)
        {
            return PlatformResult<T>.FromServiceUnavailable();
        }
        try
        {
            UpdateRateLimit(response);
            if (response.IsSuccessStatusCode)
            {
                // Epic 31 review — a 2xx body that does not deserialize into
                // the expected shape must map to a TYPED failure, never throw
                // through the drivers' no-throw PlatformResult contract. The
                // concrete case: GET /contents/{path} on a DIRECTORY answers
                // 200 with a JSON ARRAY, which used to escape as a raw
                // JsonException from ReadFromJsonAsync.
                try
                {
                    if (typeof(T) == typeof(JsonElement))
                    {
                        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(raw))
                        {
                            return PlatformResult<T>.FromError(new PlatformError.Unknown("empty body"));
                        }
                        using var doc = JsonDocument.Parse(raw);
                        return PlatformResult<T>.FromOk((T)(object)doc.RootElement.Clone());
                    }
                    var typed = await response.Content
                        .ReadFromJsonAsync<T>(JsonOptions, ct)
                        .ConfigureAwait(false);
                    if (typed is null)
                    {
                        return PlatformResult<T>.FromError(new PlatformError.Unknown("empty body"));
                    }
                    return PlatformResult<T>.FromOk(typed);
                }
                catch (JsonException ex)
                {
                    return PlatformResult<T>.FromError(new PlatformError.InvalidRequest(
                        "response_shape_mismatch",
                        $"GitHub answered {(int)response.StatusCode} but the body does not match the "
                        + $"expected {typeof(T).Name} shape: {ex.Message}"));
                }
            }

            var err = await GitHubErrorMapper.MapAsync(response, ct).ConfigureAwait(false);
            return PlatformResult<T>.FromError(err);
        }
        finally
        {
            response.Dispose();
        }
    }

    private Task<HttpResponseMessage?> SendRawAsync(
        HttpMethod method, string relativePath, object? body, CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead) =>
        SendRawToUrlAsync(method, BuildUrl(relativePath), body, ct, completionOption);

    /// <summary>
    /// Core send routine with App-token refresh-on-401 retry. Returns
    /// null when the credential could not be resolved / transport
    /// failed unrecoverably, so callers surface
    /// <see cref="PlatformResult{T}.ServiceUnavailable"/>.
    /// </summary>
    private async Task<HttpResponseMessage?> SendRawToUrlAsync(
        HttpMethod method, string absoluteUrl, object? body, CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            HttpRequestMessage? request = null;
            HttpResponseMessage? response = null;
            try
            {
                var token = await ResolveBearerAsync(forceRefresh: attempt > 1, ct)
                    .ConfigureAwait(false);
                if (token is null)
                {
                    _logger.LogWarning(
                        "GitHub credential could not be resolved (mode {Mode})",
                        _auth.GetType().Name);
                    return null;
                }

                request = new HttpRequestMessage(method, absoluteUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: JsonOptions);
                }

                response = await _http
                    .SendAsync(request, completionOption, ct)
                    .ConfigureAwait(false);

                // Refresh-on-401 happens at most once + only for App mode
                // (a PAT that 401s is dead; re-sending it is pointless).
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && _auth is GitHubAuth.App
                    && attempt == 1)
                {
                    _minter!.Invalidate();
                    response.Dispose();
                    response = null;
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "GitHub HTTP transport failure for {Method} {Url}", method, absoluteUrl);
                response?.Dispose();
                // Synthetic 503 so the standard mapping path applies
                // (the GiteaHttpClient pattern).
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("transport_error"),
                };
            }
            finally
            {
                request?.Dispose();
            }
        }
    }

    private async Task<string?> ResolveBearerAsync(bool forceRefresh, CancellationToken ct)
    {
        switch (_auth)
        {
            case GitHubAuth.Pat pat:
                return pat.Token;
            case GitHubAuth.App:
                return await _minter!
                    .GetInstallationTokenAsync(forceRefresh, ct)
                    .ConfigureAwait(false);
            default:
                throw new InvalidOperationException(
                    $"unhandled GitHubAuth variant: {_auth.GetType().Name}");
        }
    }

    private void UpdateRateLimit(HttpResponseMessage response)
    {
        int? limit = TryReadInt(response.Headers, "X-RateLimit-Limit");
        int? remaining = TryReadInt(response.Headers, "X-RateLimit-Remaining");
        DateTimeOffset? resetsAt = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var unix))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        if (limit.HasValue || remaining.HasValue || resetsAt.HasValue)
        {
            _rateLimit = new RateLimitInfo(limit, remaining, resetsAt);
        }
    }

    private static int? TryReadInt(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        return int.TryParse(values.FirstOrDefault(), out var n) ? n : null;
    }

    /// <summary>
    /// Wraps a content stream and its owning
    /// <see cref="HttpResponseMessage"/> so disposal cascades.
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

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
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

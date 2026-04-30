using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 §Step 2 — typed HTTP client wrapping a plain
/// <see cref="HttpClient"/>. Responsibilities:
/// <list type="bullet">
///   <item>Auth header injection per <see cref="GitLabAuth"/> variant.</item>
///   <item>Base-URL normalization so both <c>https://gitlab.com</c> and
///         <c>https://gitlab.example.com/api/v4/</c> resolve correctly.</item>
///   <item>Link-header pagination via <see cref="EnumeratePagesAsync{T}"/>.</item>
///   <item>Rate-limit awareness — exposes <c>Retry-After</c> on 429.</item>
/// </list>
///
/// <para>The client does NOT map errors here (caller does, via
/// <see cref="GitLabErrorMapper"/>). It returns the raw
/// <see cref="HttpResponseMessage"/> + body so the caller can branch on
/// status. This keeps the client a pure transport layer.</para>
/// </summary>
internal sealed class GitLabHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly GitLabAuth _auth;

    /// <summary>
    /// Normalized API base URL. Always ends with <c>/api/v4</c>; the
    /// callers append <c>/projects/...</c> straight after.
    /// </summary>
    public Uri BaseUrl { get; }

    public GitLabHttpClient(HttpClient http, GitLabAuth auth, string baseUrl, bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _http = http;
        _auth = auth;
        _ownsHttpClient = ownsHttpClient;
        BaseUrl = NormalizeBaseUrl(baseUrl);
    }

    /// <summary>
    /// Send a request with auth header injection. Caller owns the
    /// returned response. Body is buffered into a string for error
    /// mapping; callers wanting streamed bodies should use
    /// <see cref="SendStreamingAsync"/>.
    /// </summary>
    public async Task<GitLabHttpResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuth(request);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = response.Content is not null
            ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : null;

        return new GitLabHttpResponse(response, body);
    }

    /// <summary>
    /// Same as <see cref="SendAsync"/> but returns the raw response
    /// without buffering — used for artifact downloads.
    /// </summary>
    public async Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuth(request);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET helper that returns the parsed JSON body and the
    /// <see cref="GitLabHttpResponse"/> so the caller can map errors.
    /// </summary>
    public async Task<(GitLabHttpResponse Response, T? Parsed)> GetJsonAsync<T>(string relativePath, CancellationToken ct)
        where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(relativePath));
        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        T? parsed = null;
        if (resp.Response.IsSuccessStatusCode && !string.IsNullOrEmpty(resp.Body))
        {
            parsed = JsonSerializer.Deserialize<T>(resp.Body, JsonDefaults);
        }
        return (resp, parsed);
    }

    /// <summary>
    /// POST helper for endpoints that take a JSON body.
    /// </summary>
    public async Task<(GitLabHttpResponse Response, TResp? Parsed)> PostJsonAsync<TReq, TResp>(
        string relativePath, TReq body, CancellationToken ct)
        where TResp : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(relativePath))
        {
            Content = JsonContent.Create(body, options: JsonDefaults),
        };
        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        TResp? parsed = null;
        if (resp.Response.IsSuccessStatusCode && !string.IsNullOrEmpty(resp.Body))
        {
            parsed = JsonSerializer.Deserialize<TResp>(resp.Body, JsonDefaults);
        }
        return (resp, parsed);
    }

    /// <summary>
    /// PUT helper.
    /// </summary>
    public async Task<(GitLabHttpResponse Response, TResp? Parsed)> PutJsonAsync<TReq, TResp>(
        string relativePath, TReq body, CancellationToken ct)
        where TResp : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BuildUri(relativePath))
        {
            Content = JsonContent.Create(body, options: JsonDefaults),
        };
        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        TResp? parsed = null;
        if (resp.Response.IsSuccessStatusCode && !string.IsNullOrEmpty(resp.Body))
        {
            parsed = JsonSerializer.Deserialize<TResp>(resp.Body, JsonDefaults);
        }
        return (resp, parsed);
    }

    /// <summary>
    /// DELETE helper.
    /// </summary>
    public async Task<GitLabHttpResponse> DeleteAsync(string relativePath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, BuildUri(relativePath));
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Iterate every page of a paginated GET. Drivers use this for
    /// branches, repos, MRs, jobs, etc.
    ///
    /// <para>GitLab paginates via <c>Link</c> header with <c>rel="next"</c>;
    /// when absent the iteration stops. <paramref name="perPage"/>
    /// defaults to 100 (GitLab's max). The yielded items come out in
    /// order; total iteration is capped by <paramref name="maxItems"/>
    /// (default 10K) as a DoS guard for tenants with deep histories.</para>
    /// </summary>
    public async IAsyncEnumerable<T> EnumeratePagesAsync<T>(
        string startPath,
        int perPage = 100,
        int maxItems = 10_000,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (perPage is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(perPage), perPage, "must be 1..100");
        }

        var separator = startPath.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var firstUrl = BuildUri($"{startPath}{separator}per_page={perPage}").ToString();
        var nextUrl = firstUrl;
        var yielded = 0;

        while (!string.IsNullOrEmpty(nextUrl))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            // Snapshot everything we need from the response BEFORE any
            // yield, then dispose. Holding GitLabHttpResponse across a
            // yield would (a) leak it if the caller stops enumerating
            // early, and (b) keep the underlying HttpResponseMessage
            // alive longer than necessary.
            string? body;
            System.Net.HttpStatusCode status;
            TimeSpan? retryAfter;
            string? linkHeader;
            using (var resp = await SendAsync(req, ct).ConfigureAwait(false))
            {
                status = resp.Response.StatusCode;
                body = resp.Body;
                retryAfter = resp.RetryAfter;
                linkHeader = ExtractNextLink(resp.Response.Headers);
            }

            // Caller responsible for error handling on first page; we
            // surface a 4xx by simply stopping iteration with no items
            // rather than throwing — but a 401/404 on the first page
            // means the caller can't paginate anyway. Bubble up as
            // exception so the integration code translates.
            if (!IsSuccessStatusCode(status))
            {
                throw new GitLabRequestException(status, body, retryAfter);
            }

            var page = !string.IsNullOrEmpty(body)
                ? JsonSerializer.Deserialize<List<T>>(body, JsonDefaults) ?? new List<T>()
                : new List<T>();

            foreach (var item in page)
            {
                yield return item;
                yielded++;
                if (yielded >= maxItems)
                {
                    yield break;
                }
            }

            nextUrl = linkHeader;
        }
    }

    private static bool IsSuccessStatusCode(System.Net.HttpStatusCode code)
        => (int)code is >= 200 and < 300;

    /// <summary>
    /// Build a full URI for a relative API path. Accepts paths with or
    /// without a leading slash.
    /// </summary>
    public Uri BuildUri(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var trimmed = relativePath.StartsWith('/') ? relativePath[1..] : relativePath;
        return new Uri(BaseUrl, trimmed);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        // Defensive: if the caller already attached an auth header (e.g.
        // a re-tried request), don't double-apply.
        switch (_auth)
        {
            case GitLabAuth.PersonalAccessToken pat:
                request.Headers.Remove("PRIVATE-TOKEN");
                request.Headers.Add("PRIVATE-TOKEN", pat.Token);
                break;
            case GitLabAuth.ProjectAccessToken pjt:
                request.Headers.Remove("PRIVATE-TOKEN");
                request.Headers.Add("PRIVATE-TOKEN", pjt.Token);
                break;
            case GitLabAuth.GroupAccessToken gt:
                request.Headers.Remove("PRIVATE-TOKEN");
                request.Headers.Add("PRIVATE-TOKEN", gt.Token);
                break;
            case GitLabAuth.OAuth2 oauth:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauth.AccessToken);
                break;
        }
    }

    /// <summary>
    /// Normalize a base URL to <c>scheme://host/api/v4/</c>. Accepts
    /// any of <c>https://gitlab.example.com</c>,
    /// <c>https://gitlab.example.com/</c>,
    /// <c>https://gitlab.example.com/api/v4</c>,
    /// <c>https://gitlab.example.com/api/v4/</c>.
    /// </summary>
    internal static Uri NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var hasV4 = trimmed.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase);
        var withV4 = hasV4 ? trimmed : trimmed + "/api/v4";
        // Trailing slash so relative URI concat works correctly.
        return new Uri(withV4 + "/");
    }

    /// <summary>
    /// Parse an HTTP <c>Link</c> header for the <c>rel="next"</c> URL.
    /// Returns null when no next link is present.
    /// </summary>
    internal static string? ExtractNextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            return null;
        }
        // Header format: <url1>; rel="next", <url2>; rel="last"
        foreach (var raw in values)
        {
            foreach (var part in raw.Split(','))
            {
                var trimmed = part.Trim();
                var semi = trimmed.IndexOf(';', StringComparison.Ordinal);
                if (semi < 0) continue;
                var urlPart = trimmed[..semi].Trim();
                var relPart = trimmed[(semi + 1)..].Trim();
                if (relPart.Contains("rel=\"next\"", StringComparison.Ordinal) &&
                    urlPart.StartsWith('<') && urlPart.EndsWith('>'))
                {
                    return urlPart[1..^1];
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    internal static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Buffered HTTP response with parsed rate-limit + retry-after
/// metadata.
/// </summary>
internal sealed class GitLabHttpResponse : IDisposable
{
    public HttpResponseMessage Response { get; }
    public string? Body { get; }
    public TimeSpan? RetryAfter { get; }
    public int? RateLimitRemaining { get; }
    public DateTimeOffset? RateLimitResetsAt { get; }

    public GitLabHttpResponse(HttpResponseMessage response, string? body)
    {
        Response = response;
        Body = body;
        RetryAfter = ExtractRetryAfter(response);
        (RateLimitRemaining, RateLimitResetsAt) = ExtractRateLimitHeaders(response);
    }

    public void Dispose() => Response.Dispose();

    private static TimeSpan? ExtractRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta.HasValue) return ra.Delta;
        if (ra.Date.HasValue)
        {
            var delta = ra.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }

    private static (int? Remaining, DateTimeOffset? ResetsAt) ExtractRateLimitHeaders(HttpResponseMessage response)
    {
        int? remaining = null;
        DateTimeOffset? resets = null;

        if (response.Headers.TryGetValues("RateLimit-Remaining", out var rem))
        {
            var rs = rem.FirstOrDefault();
            if (rs is not null && int.TryParse(rs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                remaining = n;
            }
        }

        if (response.Headers.TryGetValues("RateLimit-Reset", out var reset))
        {
            var rs = reset.FirstOrDefault();
            if (rs is not null && long.TryParse(rs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                resets = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }

        return (remaining, resets);
    }
}

/// <summary>
/// Thrown by <see cref="GitLabHttpClient.EnumeratePagesAsync{T}"/>
/// when a page fetch fails. Caller maps via
/// <see cref="GitLabErrorMapper"/>.
/// </summary>
internal sealed class GitLabRequestException : Exception
{
    public HttpStatusCode Status { get; }
    public string? Body { get; }
    public TimeSpan? RetryAfter { get; }

    public GitLabRequestException(HttpStatusCode status, string? body, TimeSpan? retryAfter)
        : base($"GitLab request failed with status {(int)status}")
    {
        Status = status;
        Body = body;
        RetryAfter = retryAfter;
    }
}

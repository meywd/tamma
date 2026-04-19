using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Tamma.Api.Services.Provisioning.Cranl;

/// <summary>
/// Default <see cref="ICranlApiClient"/> implementation. Wraps an injected
/// <see cref="HttpClient"/> (registered via <c>AddHttpClient&lt;ICranlApiClient, CranlApiClient&gt;()</c>);
/// the BaseAddress + Authorization header are set in the registration so
/// individual methods only deal with relative paths and request bodies.
///
/// <para>Retry policy: 429 responses retry 3 times with exponential backoff
/// (250ms → 500ms → 1000ms). Other transient codes (502/503/504) bubble up
/// as <see cref="CranlApiException"/> with <c>IsRetryable = true</c> for
/// callers that own outer retry orchestration (the provisioner's polling
/// loop is the canonical retry boundary; the HTTP client only handles the
/// "rate limit you JUST set" case).</para>
/// </summary>
public sealed class CranlApiClient : ICranlApiClient
{
    // Match the JSON conventions of the Cranl API: snake_case on responses,
    // camelCase on requests. We let JsonPropertyName attributes drive the
    // wire shape and keep the serializer minimal.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    // 429 backoff schedule (ms). Three attempts total: initial + two retries.
    private static readonly TimeSpan[] RateLimitBackoff =
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000)
    };

    private readonly HttpClient _http;
    private readonly ILogger<CranlApiClient> _logger;

    public CranlApiClient(HttpClient http, ILogger<CranlApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ─── Projects ────────────────────────────────────────────────────────────

    public Task<CranlProject> CreateProjectAsync(string name, string organizationId, CancellationToken ct = default)
        => SendJsonAsync<CreateProjectRequest, CranlProject>(
            HttpMethod.Post,
            "projects",
            new CreateProjectRequest { Name = name, OrganizationId = organizationId },
            ct);

    public Task DeleteProjectAsync(string projectId, CancellationToken ct = default)
        => SendNoBodyAsync(HttpMethod.Delete, $"projects/{Uri.EscapeDataString(projectId)}", ct);

    // ─── Databases ───────────────────────────────────────────────────────────

    public Task<CranlDatabase> CreateDatabaseAsync(CreateDatabaseRequest req, CancellationToken ct = default)
        => SendJsonAsync<CreateDatabaseRequest, CranlDatabase>(HttpMethod.Post, "databases", req, ct);

    public Task<CranlDatabase> GetDatabaseAsync(string id, CancellationToken ct = default)
        => SendNoBodyAsync<CranlDatabase>(HttpMethod.Get, $"databases/{Uri.EscapeDataString(id)}", ct);

    public Task DeleteDatabaseAsync(string id, CancellationToken ct = default)
        => SendNoBodyAsync(HttpMethod.Delete, $"databases/{Uri.EscapeDataString(id)}", ct);

    public Task DatabaseLifecycleAsync(string id, string action, CancellationToken ct = default)
        => SendNoBodyAsync(
            HttpMethod.Post,
            $"databases/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(action)}",
            ct);

    // ─── Applications ────────────────────────────────────────────────────────

    public Task<CranlApplication> CreateApplicationAsync(CreateApplicationRequest req, CancellationToken ct = default)
        => SendJsonAsync<CreateApplicationRequest, CranlApplication>(HttpMethod.Post, "applications", req, ct);

    public Task<CranlApplication> GetApplicationAsync(string id, CancellationToken ct = default)
        => SendNoBodyAsync<CranlApplication>(HttpMethod.Get, $"applications/{Uri.EscapeDataString(id)}", ct);

    public Task DeleteApplicationAsync(string id, CancellationToken ct = default)
        => SendNoBodyAsync(HttpMethod.Delete, $"applications/{Uri.EscapeDataString(id)}", ct);

    public Task DeployApplicationAsync(string id, CancellationToken ct = default)
        => SendNoBodyAsync(HttpMethod.Post, $"applications/{Uri.EscapeDataString(id)}/deploy", ct);

    public Task ApplicationLifecycleAsync(string id, string action, CancellationToken ct = default)
        => SendJsonAsync<LifecycleActionRequest, JsonElement>(
            HttpMethod.Post,
            $"applications/{Uri.EscapeDataString(id)}/lifecycle",
            new LifecycleActionRequest { Action = action },
            ct);

    public Task PutEnvironmentAsync(string id, string envText, CancellationToken ct = default)
        => SendJsonAsync<EnvironmentRequest, JsonElement>(
            HttpMethod.Put,
            $"applications/{Uri.EscapeDataString(id)}/environment",
            new EnvironmentRequest { Env = envText },
            ct);

    public Task<CranlAppDomains> GetApplicationDomainsAsync(string id, CancellationToken ct = default)
        => SendNoBodyAsync<CranlAppDomains>(
            HttpMethod.Get,
            $"applications/{Uri.EscapeDataString(id)}/domains",
            ct);

    // ─── Internal HTTP plumbing ──────────────────────────────────────────────

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method, string relativePath, TRequest body, CancellationToken ct)
    {
        async Task<HttpResponseMessage> Build()
        {
            var req = new HttpRequestMessage(method, relativePath)
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }

        using var response = await SendWithRetriesAsync(Build, method, relativePath, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, method, relativePath, ct).ConfigureAwait(false);

        if (typeof(TResponse) == typeof(JsonElement))
        {
            // Caller doesn't care about the payload (e.g. lifecycle actions
            // that return { "success": true }). Read-and-discard so the
            // connection is freed back to the pool.
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return default!;
        }

        var parsed = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (parsed is null)
        {
            throw new CranlApiException(
                response.StatusCode,
                "empty_response",
                $"Cranl returned an empty body for {method} {relativePath}");
        }
        return parsed;
    }

    private async Task<TResponse> SendNoBodyAsync<TResponse>(
        HttpMethod method, string relativePath, CancellationToken ct)
    {
        Task<HttpResponseMessage> Build() => _http.SendAsync(new HttpRequestMessage(method, relativePath), ct);

        using var response = await SendWithRetriesAsync(Build, method, relativePath, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, method, relativePath, ct).ConfigureAwait(false);

        var parsed = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (parsed is null)
        {
            throw new CranlApiException(
                response.StatusCode,
                "empty_response",
                $"Cranl returned an empty body for {method} {relativePath}");
        }
        return parsed;
    }

    private async Task SendNoBodyAsync(HttpMethod method, string relativePath, CancellationToken ct)
    {
        Task<HttpResponseMessage> Build() => _http.SendAsync(new HttpRequestMessage(method, relativePath), ct);

        using var response = await SendWithRetriesAsync(Build, method, relativePath, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, method, relativePath, ct).ConfigureAwait(false);
        // Drain body to free the connection — even DELETE responses carry
        // a `{ "success": true }` payload.
        await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<Task<HttpResponseMessage>> build,
        HttpMethod method,
        string relativePath,
        CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        for (int attempt = 0; attempt < RateLimitBackoff.Length; attempt++)
        {
            response?.Dispose();
            response = await build().ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            // Honour Retry-After (seconds) when present, otherwise fall back
            // to the schedule.
            var delay = ExtractRetryAfter(response) ?? RateLimitBackoff[attempt];
            _logger.LogWarning(
                "Cranl 429 on {Method} {Path} — backing off {Delay}ms (attempt {Attempt}/{Max})",
                method.Method, relativePath, (int)delay.TotalMilliseconds,
                attempt + 1, RateLimitBackoff.Length);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                throw;
            }
        }
        // Final attempt — return whatever the last response was (likely 429).
        return response!;
    }

    private static TimeSpan? ExtractRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return delta;
        if (ra.Date is { } when_)
        {
            var diff = when_ - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task ThrowIfErrorAsync(
        HttpResponseMessage response, HttpMethod method, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var errorMessage = TryExtractErrorField(body);
        var summary = string.IsNullOrEmpty(errorMessage)
            ? $"Cranl {(int)response.StatusCode} on {method.Method} {path}"
            : $"Cranl {(int)response.StatusCode} on {method.Method} {path}: {errorMessage}";

        throw new CranlApiException(response.StatusCode, errorMessage ?? string.Empty, summary);
    }

    private static string? TryExtractErrorField(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<CranlErrorBody>(body, JsonOptions);
            return parsed?.Error;
        }
        catch (JsonException)
        {
            // Not JSON — return the raw body, truncated for safety.
            return body.Length > 200 ? body.Substring(0, 200) + "…" : body;
        }
    }

    /// <summary>
    /// Helper to install the Authorization + UserAgent headers on the
    /// pooled <see cref="HttpClient"/>. Called from the DI extension so
    /// the same client instance carries the credentials on every request.
    /// </summary>
    internal static void ConfigureClient(HttpClient client, CranlOptions options)
    {
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = options.RequestTimeout;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        // Cranl returns JSON; make our preference explicit.
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}

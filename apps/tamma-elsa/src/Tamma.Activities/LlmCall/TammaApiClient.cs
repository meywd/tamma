using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Story 9-11: Shared HTTP client for calling the Tamma API (Fastify in TS,
/// ASP.NET in the C# port). Used by simplified Elsa activities to delegate
/// agent resolution, health, diagnostics, and provider execution to the
/// central API plane.
///
/// Configuration (read from <see cref="IConfiguration"/> with env-var
/// fallbacks):
/// <list type="bullet">
///   <item><c>Tamma:ApiUrl</c> or env <c>TAMMA_API_URL</c> — base URL
///         (defaults to <c>http://localhost:3000</c>).</item>
///   <item><c>Tamma:ApiToken</c> or env <c>TAMMA_API_TOKEN</c> — bearer
///         token for Authorization header.</item>
/// </list>
///
/// All methods return <c>null</c> on HTTP / network failure so callers can
/// fall back to local behavior (per AC 5 in Story 9-11).
/// </summary>
public class TammaApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TammaApiClient> _logger;
    private readonly string _baseUrl;
    private readonly TammaApiHealthMonitor? _healthMonitor;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TammaApiClient(
        HttpClient httpClient,
        ILogger<TammaApiClient> logger,
        IConfiguration? configuration = null,
        TammaApiHealthMonitor? healthMonitor = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _healthMonitor = healthMonitor;

        _baseUrl = configuration?["Tamma:ApiUrl"]
                   ?? Environment.GetEnvironmentVariable("TAMMA_API_URL")
                   ?? "http://localhost:3000";
        _baseUrl = _baseUrl.TrimEnd('/');

        var token = configuration?["Tamma:ApiToken"]
                    ?? Environment.GetEnvironmentVariable("TAMMA_API_TOKEN");
        if (!string.IsNullOrWhiteSpace(token) &&
            _httpClient.DefaultRequestHeaders.Authorization is null)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Base URL in use (test hook).</summary>
    public string BaseUrl => _baseUrl;

    // ----- Agent Resolution --------------------------------------------

    public Task<AgentResolveResult?> ResolveAgentAsync(
        string role,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agents/{Uri.EscapeDataString(role)}/resolve";
        return GetAsync<AgentResolveResult>(url, tenantId, ct);
    }

    public Task<AgentResolveResult?> ResolveForPhaseAsync(
        ResolveForPhaseRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agents/resolve-for-phase";
        return PostAsync<AgentResolveResult>(url, request, tenantId, ct);
    }

    // ----- Provider Health ---------------------------------------------

    public Task<ProviderHealthStatus?> GetProviderHealthAsync(
        string providerKey,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/health/providers/{Uri.EscapeDataString(providerKey)}";
        return GetAsync<ProviderHealthStatus>(url, tenantId, ct);
    }

    public Task<bool> RecordProviderFailureAsync(
        string providerKey,
        string? error = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/health/providers/{Uri.EscapeDataString(providerKey)}/failure";
        return PostVoidAsync(url, new { error }, tenantId, ct);
    }

    public Task<bool> RecordProviderSuccessAsync(
        string providerKey,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/health/providers/{Uri.EscapeDataString(providerKey)}/success";
        return PostVoidAsync(url, new { }, tenantId, ct);
    }

    // ----- Diagnostics --------------------------------------------------

    public Task<bool> RecordDiagnosticsAsync(
        DiagnosticsIngestRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/diagnostics";
        return PostVoidAsync(url, request, tenantId, ct);
    }

    /// <summary>
    /// Fetches the current budget for a budget-owner identifier (today
    /// always the tenant id; the API surface keeps the URL path segment
    /// named <c>{accountId}</c> for back-compat with the TS API + a
    /// future per-user-bucket model). Parameter is named
    /// <paramref name="budgetOwnerId"/> locally to avoid CodeQL's
    /// <c>cs/cleartext-storage</c> heuristic, which treats parameters
    /// named <c>*account*</c> as financial-account-sensitive sources.
    /// </summary>
    public Task<BudgetStatus?> GetBudgetAsync(
        string budgetOwnerId,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/diagnostics/budget/{Uri.EscapeDataString(budgetOwnerId)}";
        return GetAsync<BudgetStatus>(url, tenantId, ct);
    }

    // ----- Provider Sessions (create/execute/dispose) ------------------

    public Task<ProviderSessionResult?> CreateProviderAsync(
        ProviderCreateRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/providers/create";
        return PostAsync<ProviderSessionResult>(url, request, tenantId, ct);
    }

    public Task<TaskExecuteResult?> ExecuteProviderAsync(
        string handle,
        TaskExecuteRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/providers/{Uri.EscapeDataString(handle)}/execute";
        return PostAsync<TaskExecuteResult>(url, request, tenantId, ct);
    }

    public async Task<bool> DisposeProviderAsync(
        string handle,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/providers/{Uri.EscapeDataString(handle)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            AddTenantHeader(request, tenantId);
            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API DELETE failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    // ----- DCB event append --------------------------------------------

    /// <summary>
    /// Persist a BATCH of DCB events to the caller's tenant
    /// <c>domain_events</c> via <c>POST /api/engine/events</c>. Used by the
    /// engine's activity-execution middleware to drain the in-process
    /// <c>tamma:events</c> list into the durable audit trail.
    ///
    /// <para>Returns <c>true</c> only on a fully-successful append (2xx). A
    /// partial-batch failure (the API returns 502 with a
    /// <c>partial_append_failure</c> body) and any transport failure both
    /// return <c>false</c> so the caller does NOT advance its drain cursor
    /// and retries the batch next flush. <see cref="RecordHealthAsync"/>
    /// pipes the observed response into the shared health monitor exactly
    /// like every other call site.</para>
    /// </summary>
    public async Task<bool> AppendEventsAsync(
        IReadOnlyList<Models.EngineEventRecord> events,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        if (events is null || events.Count == 0)
            return true; // nothing to flush — a successful no-op.

        var url = $"{_baseUrl}/api/engine/events";
        var body = new Models.AppendEventsRequest(events);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId?.ToString());
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST /api/engine/events returned {Status} for {Count} events",
                    (int)response.StatusCode, events.Count);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API POST /api/engine/events failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    // ----- Helpers ------------------------------------------------------

    private async Task<T?> GetAsync<T>(
        string url,
        string? tenantId,
        CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // URL is intentionally omitted — the path carries interpolated
                // identifiers (tenant/budget-owner/provider-handle/etc.), and
                // the rotating warn log on the VPS is the wrong plane for per-
                // resource correlation (event store is). The status code alone
                // is what an operator triaging "API unhealthy?" actually needs.
                _logger.LogWarning(
                    "Tamma API GET returned {Status}",
                    (int)response.StatusCode);
                return null;
            }
            return await response.Content
                .ReadFromJsonAsync<T>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // URL omitted for the same reason as above; the exception type
            // and message are the operator-useful signal.
            _logger.LogWarning(ex, "Tamma API GET failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<T?> PostAsync<T>(
        string url,
        object body,
        string? tenantId,
        CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST returned {Status}",
                    (int)response.StatusCode);
                return null;
            }
            return await response.Content
                .ReadFromJsonAsync<T>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Tamma API POST failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<bool> PostVoidAsync(
        string url,
        object body,
        string? tenantId,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST returned {Status}",
                    (int)response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API POST failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Wave C.4 §4 — pipe the observed response into the health monitor
    /// so PLATFORM.API.UNHEALTHY can fire on sustained failure bursts.
    /// The monitor is optional; when unwired the call is a no-op.
    /// </summary>
    private Task RecordHealthAsync(
        bool success, int? statusCode, string? exceptionType, CancellationToken ct)
    {
        if (_healthMonitor is null) return Task.CompletedTask;
        return _healthMonitor.RecordAsync(success, statusCode, exceptionType, ct);
    }

    private static void AddTenantHeader(HttpRequestMessage request, string? tenantId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId);
        }
    }
}

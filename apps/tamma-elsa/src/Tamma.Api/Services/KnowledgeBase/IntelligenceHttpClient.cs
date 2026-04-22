using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Dtos.KnowledgeBase;

namespace Tamma.Api.Services.KnowledgeBase;

/// <summary>
/// Default implementation of <see cref="IIntelligenceHttpClient"/>. Talks to
/// the TS sidecar over HTTP via an <see cref="HttpClient"/> configured through
/// <c>IHttpClientFactory</c>.
///
/// <para>
/// All methods return deserialised <c>JsonElement</c> wrapped in <see cref="object"/>
/// so the caller (KbEndpoints) can forward the sidecar response straight to the
/// dashboard without schema drift. Sidecar failures (timeouts, 5xx, network
/// errors) return a minimal empty payload rather than throwing — the dashboard
/// will render a degraded view and the incident is logged.
/// </para>
/// </summary>
public sealed class IntelligenceHttpClient : IIntelligenceHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly ILogger<IntelligenceHttpClient> _logger;

    public IntelligenceHttpClient(HttpClient http, ILogger<IntelligenceHttpClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ── Index (6) ────────────────────────────────────────────────────────

    public Task<object> GetIndexStatusAsync(CancellationToken ct = default)
        => GetAsync("/kb/index/status", ct);

    public Task<object> TriggerIndexAsync(TriggerIndexRequest? body, CancellationToken ct = default)
        => PostAsync("/kb/index/trigger", body ?? new TriggerIndexRequest(null, null, null), ct);

    public Task<object> GetIndexConfigAsync(CancellationToken ct = default)
        => GetAsync("/kb/index/config", ct);

    public Task<object> UpdateIndexConfigAsync(UpdateIndexConfigRequest body, CancellationToken ct = default)
        => PutAsync("/kb/index/config", body, ct);

    public Task<object> GetIndexStatsAsync(CancellationToken ct = default)
        => GetAsync("/kb/index/stats", ct);

    public Task<object> ClearIndexAsync(CancellationToken ct = default)
        => DeleteAsync("/kb/index", null, ct);

    // ── Vector DB (6) ────────────────────────────────────────────────────

    public Task<object> GetVectorDbStatusAsync(CancellationToken ct = default)
        => GetAsync("/kb/vector-db/status", ct);

    public Task<object> SearchVectorsAsync(VectorSearchRequest body, CancellationToken ct = default)
        => PostAsync("/kb/vector-db/search", body, ct);

    public Task<object> UpsertVectorsAsync(VectorUpsertRequest body, CancellationToken ct = default)
        => PostAsync("/kb/vector-db/upsert", body, ct);

    public Task<object> DeleteVectorsAsync(VectorDeleteRequest body, CancellationToken ct = default)
        => DeleteAsync("/kb/vector-db/delete", body, ct);

    public Task<object> GetVectorCollectionsAsync(CancellationToken ct = default)
        => GetAsync("/kb/vector-db/collections", ct);

    public Task<object> GetVectorStatsAsync(CancellationToken ct = default)
        => GetAsync("/kb/vector-db/stats", ct);

    // ── RAG (4) ──────────────────────────────────────────────────────────

    public Task<object> GetRagConfigAsync(CancellationToken ct = default)
        => GetAsync("/kb/rag/config", ct);

    public Task<object> UpdateRagConfigAsync(UpdateRagConfigRequest body, CancellationToken ct = default)
        => PutAsync("/kb/rag/config", body, ct);

    public Task<object> QueryRagAsync(RagQueryRequest body, CancellationToken ct = default)
        => PostAsync("/kb/rag/query", body, ct);

    public Task<object> GetRagMetricsAsync(CancellationToken ct = default)
        => GetAsync("/kb/rag/metrics", ct);

    // ── MCP (8) ──────────────────────────────────────────────────────────

    public Task<object> ListMcpServersAsync(CancellationToken ct = default)
        => GetAsync("/kb/mcp/servers", ct);

    public Task<object> GetMcpServerAsync(string id, CancellationToken ct = default)
        => GetAsync($"/kb/mcp/servers/{Uri.EscapeDataString(id)}", ct);

    public Task<object> StartMcpServerAsync(string id, CancellationToken ct = default)
        => PostAsync($"/kb/mcp/servers/{Uri.EscapeDataString(id)}/start", new { }, ct);

    public Task<object> StopMcpServerAsync(string id, CancellationToken ct = default)
        => PostAsync($"/kb/mcp/servers/{Uri.EscapeDataString(id)}/stop", new { }, ct);

    public Task<object> GetMcpConfigAsync(CancellationToken ct = default)
        => GetAsync("/kb/mcp/config", ct);

    public Task<object> UpdateMcpConfigAsync(UpdateMcpConfigRequest body, CancellationToken ct = default)
        => PutAsync("/kb/mcp/config", body, ct);

    public Task<object> ListMcpToolsAsync(string? serverName = null, CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(serverName)
            ? "/kb/mcp/tools"
            : $"/kb/mcp/tools?serverName={Uri.EscapeDataString(serverName)}";
        return GetAsync(path, ct);
    }

    public Task<object> InvokeMcpToolAsync(McpInvokeRequest body, CancellationToken ct = default)
        => PostAsync("/kb/mcp/tools/invoke", body, ct);

    // ── Context (3) ──────────────────────────────────────────────────────

    public Task<object> GetContextHistoryAsync(int? limit = null, CancellationToken ct = default)
    {
        var path = limit.HasValue ? $"/kb/context/history?limit={limit.Value}" : "/kb/context/history";
        return GetAsync(path, ct);
    }

    public Task<object> PostContextFeedbackAsync(ContextFeedbackRequest body, CancellationToken ct = default)
        => PostAsync("/kb/context/feedback", body, ct);

    public Task<object> GetContextConfigAsync(CancellationToken ct = default)
        => GetAsync("/kb/context/config", ct);

    // ── Analytics (3) ────────────────────────────────────────────────────

    public Task<object> GetAnalyticsAsync(string? start = null, string? end = null, CancellationToken ct = default)
        => GetAsync(BuildAnalyticsPath("/kb/analytics", start, end), ct);

    public Task<object> GetUsageAsync(string? start = null, string? end = null, CancellationToken ct = default)
        => GetAsync(BuildAnalyticsPath("/kb/analytics/usage", start, end), ct);

    public Task<object> GetCostsAsync(string? start = null, string? end = null, CancellationToken ct = default)
        => GetAsync(BuildAnalyticsPath("/kb/analytics/costs", start, end), ct);

    // ── Internals ────────────────────────────────────────────────────────

    private static string BuildAnalyticsPath(string path, string? start, string? end)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(start)) parts.Add($"start={Uri.EscapeDataString(start)}");
        if (!string.IsNullOrEmpty(end)) parts.Add($"end={Uri.EscapeDataString(end)}");
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    private async Task<object> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            return await ReadBodyAsync(resp, path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Degraded(ex, "GET", path);
        }
    }

    private async Task<object> PostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(path, body, JsonOptions, ct).ConfigureAwait(false);
            return await ReadBodyAsync(resp, path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Degraded(ex, "POST", path);
        }
    }

    private async Task<object> PutAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync(path, body, JsonOptions, ct).ConfigureAwait(false);
            return await ReadBodyAsync(resp, path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Degraded(ex, "PUT", path);
        }
    }

    private async Task<object> DeleteAsync(string path, object? body, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, path);
            if (body is not null)
            {
                req.Content = JsonContent.Create(body, options: JsonOptions);
            }
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return await ReadBodyAsync(resp, path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Degraded(ex, "DELETE", path);
        }
    }

    private async Task<object> ReadBodyAsync(HttpResponseMessage resp, string path, CancellationToken ct)
    {
        if ((int)resp.StatusCode >= 500)
        {
            _logger.LogWarning(
                "Intelligence sidecar responded {Status} for {Path}; returning degraded payload",
                (int)resp.StatusCode, path);
            return DegradedEnvelope();
        }
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return DegradedEnvelope();
        try
        {
            using var doc = JsonDocument.Parse(text);
            // Clone the root so it remains valid after `doc` is disposed.
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse intelligence sidecar response from {Path}", path);
            return DegradedEnvelope();
        }
    }

    private object Degraded(Exception ex, string verb, string path)
    {
        _logger.LogWarning(ex, "Intelligence sidecar {Verb} {Path} failed; returning degraded payload", verb, path);
        return DegradedEnvelope();
    }

    /// <summary>
    /// Shape used when the sidecar is unreachable. Kept minimal so the
    /// dashboard can detect it (by the `degraded=true` flag) and show a
    /// banner instead of crashing.
    /// </summary>
    private static JsonElement DegradedEnvelope()
    {
        using var doc = JsonDocument.Parse("""{"degraded":true,"results":[],"items":[],"history":[],"daily":[],"breakdown":[],"servers":[],"sources":[]}""");
        return doc.RootElement.Clone();
    }
}

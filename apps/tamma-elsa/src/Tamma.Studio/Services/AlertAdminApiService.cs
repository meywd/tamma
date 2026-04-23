using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Studio.Services;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.3) — thin HTTP client for the
/// <c>/api/v1/admin/alerts/*</c>, <c>/api/v1/admin/alert-rules/*</c>,
/// and <c>/api/v1/admin/alert-channels/*</c> surface. Consumed by the
/// Studio admin Alerts pages; all calls go through the Elsa Studio
/// <see cref="System.Net.Http.HttpClient"/> pipeline, so they inherit
/// the auto-login session cookie the same way any other admin call
/// does.
///
/// <para>This is deliberately a <em>thin</em> client — the backend
/// speaks JSON and the DTO shapes mirror the handler return types
/// 1:1. We take the hit of re-declaring the DTOs on the client side
/// (no shared assembly with Tamma.Api) for the same reason the rest
/// of Tamma.Studio does: keeping the WASM bundle lean.</para>
/// </summary>
public sealed class AlertAdminApiService
{
    private readonly HttpClient _http;

    public AlertAdminApiService(HttpClient http)
    {
        _http = http;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Alerts ─────────────────────────────────────────────

    public async Task<AlertListResponse> ListAlertsAsync(
        string? status = null,
        string? severity = null,
        Guid? tenantId = null,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(severity)) qs.Add($"severity={Uri.EscapeDataString(severity)}");
        if (tenantId is Guid tid) qs.Add($"tenantId={tid}");
        if (since is { } s) qs.Add($"since={Uri.EscapeDataString(s.ToString("o"))}");
        qs.Add($"limit={limit}");
        var path = $"/api/v1/admin/alerts?{string.Join('&', qs)}";

        var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AlertListResponse>(JsonOpts, ct)
            .ConfigureAwait(false))!;
    }

    public async Task<AlertDetailResponse> GetAlertAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/v1/admin/alerts/{id}", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AlertDetailResponse>(JsonOpts, ct)
            .ConfigureAwait(false))!;
    }

    public async Task AcknowledgeAsync(Guid id, string? note, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/admin/alerts/{id}/acknowledge",
            new { note }, JsonOpts, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ResolveAsync(Guid id, string resolution, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/v1/admin/alerts/{id}/resolve",
            new { resolution }, JsonOpts, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> TestRaiseAsync(
        string severity, string title, string description,
        string? correlationId, Guid? tenantId, CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            "/api/v1/admin/alerts/_test",
            new { severity, title, description, correlationId, tenantId }, JsonOpts, ct)
            .ConfigureAwait(false);
    }

    // ── Channels ───────────────────────────────────────────

    public async Task<ChannelListResponse> ListChannelsAsync(
        Guid? tenantId = null, string? channelType = null, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (tenantId is Guid tid) qs.Add($"tenantId={tid}");
        if (!string.IsNullOrWhiteSpace(channelType))
            qs.Add($"channelType={Uri.EscapeDataString(channelType)}");
        var suffix = qs.Count == 0 ? string.Empty : "?" + string.Join('&', qs);

        var resp = await _http.GetAsync($"/api/v1/admin/alert-channels{suffix}", ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ChannelListResponse>(JsonOpts, ct)
            .ConfigureAwait(false))!;
    }

    public async Task<HttpResponseMessage> CreateChannelAsync(
        CreateChannelBody body, CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            "/api/v1/admin/alert-channels", body, JsonOpts, ct)
            .ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> UpdateChannelAsync(
        Guid id, UpdateChannelBody body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/v1/admin/alert-channels/{id}")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> DeleteChannelAsync(Guid id, CancellationToken ct = default)
    {
        return await _http.DeleteAsync($"/api/v1/admin/alert-channels/{id}", ct)
            .ConfigureAwait(false);
    }

    // ── Rules ──────────────────────────────────────────────

    public async Task<RuleListResponse> ListRulesAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/api/v1/admin/alert-rules", ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RuleListResponse>(JsonOpts, ct)
            .ConfigureAwait(false))!;
    }

    public async Task<HttpResponseMessage> CreateRuleAsync(
        CreateRuleBody body, CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            "/api/v1/admin/alert-rules", body, JsonOpts, ct)
            .ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> UpdateRuleAsync(
        Guid id, UpdateRuleBody body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/v1/admin/alert-rules/{id}")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        return await _http.DeleteAsync($"/api/v1/admin/alert-rules/{id}", ct)
            .ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> TestFireRuleAsync(
        Guid id, object sampleEvent, CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            $"/api/v1/admin/alert-rules/{id}/_test",
            sampleEvent, JsonOpts, ct).ConfigureAwait(false);
    }
}

// ── DTOs ──────────────────────────────────────────────────────

public sealed record AlertDto(
    Guid Id,
    Guid? RuleId,
    string Severity,
    string Title,
    string Description,
    string? CorrelationId,
    Guid? TenantId,
    string Metadata,
    string Status,
    Guid? AcknowledgedBy,
    DateTime? AcknowledgedAt,
    Guid? ResolvedBy,
    DateTime? ResolvedAt,
    string? Resolution,
    DateTime CreatedAt);

public sealed record AlertListResponse(
    List<AlertDto> Items,
    int Count,
    int Limit);

public sealed record DeliveryAttemptDto(
    Guid Id,
    Guid ChannelId,
    int AttemptNumber,
    string Status,
    string? Error,
    DateTime? DeliveredAt,
    DateTime? NextAttemptAt,
    DateTime CreatedAt);

public sealed record AlertDetailResponse(
    AlertDto Alert,
    List<DeliveryAttemptDto> DeliveryAttempts);

public sealed record ChannelDto(
    Guid Id,
    Guid? TenantId,
    string Name,
    string ChannelType,
    bool IsEnabled,
    string Config,
    Guid? CredentialsSecretId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ChannelListResponse(List<ChannelDto> Items, int Count);

public sealed record CreateChannelBody(
    string Name,
    string ChannelType,
    Guid? TenantId,
    string? Config,
    Guid? CredentialsSecretId);

public sealed record UpdateChannelBody(
    string? Name,
    bool? IsEnabled,
    string? Config);

public sealed record RuleDto(
    Guid Id,
    string Name,
    string EventType,
    string Severity,
    string PredicateJson,
    bool IsEnabled,
    int ThrottleSeconds,
    List<Guid> ChannelIds,
    bool IsBuiltIn,
    string? BuiltInKey,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record RuleListResponse(List<RuleDto> Items, int Count);

public sealed record CreateRuleBody(
    string Name,
    string EventType,
    string Severity,
    string PredicateJson,
    bool IsEnabled,
    int ThrottleSeconds,
    List<Guid> ChannelIds);

public sealed record UpdateRuleBody(
    string? Name,
    string? Severity,
    string? PredicateJson,
    bool? IsEnabled,
    int? ThrottleSeconds,
    List<Guid>? ChannelIds);

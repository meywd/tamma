using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Diagnostics;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.SaaS;

/// <summary>
/// Concrete <see cref="ILlmProxyService"/> that forwards chat requests to
/// Anthropic's <c>/v1/messages</c> endpoint and records token-usage +
/// estimated cost via <see cref="IDiagnosticsService"/>.
///
/// Uses <see cref="IHttpClientFactory"/> with the <c>anthropic</c> named
/// client (configured in <c>Program.cs</c>) so tests can swap a handler.
/// </summary>
public sealed class LlmProxyService : ILlmProxyService
{
    private const string ProviderKey = "anthropic-claude";
    private const string DefaultModel = "claude-sonnet-4.5";
    private const string HttpClientName = "anthropic";

    // Minimal price sheet (USD per 1K tokens). Keep it narrow — real pricing
    // integration is Epic-9/diagnostics territory. Falls back to the sonnet
    // rate for unknown models so we still record *some* cost signal.
    private static readonly Dictionary<string, (decimal InputPer1K, decimal OutputPer1K)> PriceTable
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-4.5"]   = (0.003m, 0.015m),
            ["claude-opus-4.7"]     = (0.015m, 0.075m),
            ["claude-haiku-3.5"]    = (0.00025m, 0.00125m),
        };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IBillingModeTagger _billingModeTagger;
    private readonly IEventRepository _events;
    private readonly ILogger<LlmProxyService> _logger;

    public LlmProxyService(
        IHttpClientFactory httpFactory,
        IDiagnosticsService diagnostics,
        IBillingModeTagger billingModeTagger,
        IEventRepository events,
        ILogger<LlmProxyService> logger)
    {
        _httpFactory = httpFactory;
        _diagnostics = diagnostics;
        _billingModeTagger = billingModeTagger;
        _events = events;
        _logger = logger;
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, Guid? tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is null || request.Messages.Count == 0)
        {
            return Error("invalid_request", "messages[] must contain at least one entry");
        }

        // Story 35-2 — resolve the canonical billing_mode token ONCE for this call
        // (owner-declared mode via the tagger). This proxy does not consume 32-3's
        // credential resolver, so the tag is derived from 34-3's mode alone
        // (AC5) — no competing key path introduced here.
        var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultModel : request.Model!;
        var billingMode = await _billingModeTagger
            .ResolveTagAsync(tenantId, ProviderKey, credentialSource: null, ct);

        // Per-tenant budget enforcement. Anonymous/service calls skip the check.
        if (tenantId is Guid t)
        {
            var budget = await _diagnostics.GetBudgetAsync(t, ct);
            if (budget.IsOverBudget)
            {
                _logger.LogWarning(
                    "LLM chat rejected: tenant {TenantId} over budget (spent={Spent}, limit={Limit})",
                    t, budget.Spent, budget.Limit);
                await EmitUsageEventAsync(
                    tenantId, model, billingMode, success: false, reason: "budget_exceeded",
                    tokensUsed: 0, cost: 0m, durationMs: 0, ct);
                return Error("budget_exceeded", "tenant budget exceeded");
            }
        }

        var client = _httpFactory.CreateClient(HttpClientName);
        var payload = BuildAnthropicPayload(model, request);

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            response = await client.PostAsJsonAsync("/v1/messages", payload, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await SafeReadString(response);
                _logger.LogWarning(
                    "Upstream LLM error: status={Status} body={Body}",
                    (int)response.StatusCode, Truncate(errorBody, 500));

                await RecordDiagnosticAsync(
                    tenantId, model, billingMode, sw.Elapsed.TotalMilliseconds,
                    tokensUsed: 0, cost: 0m, success: false,
                    errorMessage: $"http_{(int)response.StatusCode}", ct);
                await EmitUsageEventAsync(
                    tenantId, model, billingMode, success: false,
                    reason: $"http_{(int)response.StatusCode}",
                    tokensUsed: 0, cost: 0m, durationMs: sw.Elapsed.TotalMilliseconds, ct);

                return Error("upstream_error", $"upstream returned HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var parsed = ParseAnthropicResponse(body);
            var cost = EstimateCost(model, parsed.PromptTokens, parsed.CompletionTokens);

            await RecordDiagnosticAsync(
                tenantId, model, billingMode, sw.Elapsed.TotalMilliseconds,
                tokensUsed: parsed.TotalTokens, cost: cost, success: true,
                errorMessage: null, ct);
            await EmitUsageEventAsync(
                tenantId, model, billingMode, success: true, reason: null,
                tokensUsed: parsed.TotalTokens, cost: cost,
                durationMs: sw.Elapsed.TotalMilliseconds, ct,
                inputTokens: parsed.PromptTokens, outputTokens: parsed.CompletionTokens);

            return new ChatResponse(
                Success: true,
                Text: parsed.Text,
                Model: parsed.Model ?? model,
                PromptTokens: parsed.PromptTokens,
                CompletionTokens: parsed.CompletionTokens,
                TotalTokens: parsed.TotalTokens,
                CostUsd: cost,
                ErrorReason: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "LLM proxy upstream call failed");

            await RecordDiagnosticAsync(
                tenantId, model, billingMode, sw.Elapsed.TotalMilliseconds,
                tokensUsed: 0, cost: 0m, success: false,
                errorMessage: ex.GetType().Name, ct);
            await EmitUsageEventAsync(
                tenantId, model, billingMode, success: false, reason: ex.GetType().Name,
                tokensUsed: 0, cost: 0m, durationMs: sw.Elapsed.TotalMilliseconds, ct);

            return Error("upstream_error", "upstream request failed");
        }
        finally
        {
            response?.Dispose();
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static object BuildAnthropicPayload(string model, ChatRequest request)
    {
        // Anthropic's /v1/messages expects a top-level `system` string plus a
        // `messages[]` of user/assistant turns. Fold system turns into a single
        // concatenated system prompt.
        var systemParts = request.Messages
            .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Content)
            .ToList();

        var messages = request.Messages
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new { role = m.Role.ToLowerInvariant(), content = m.Content })
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxTokens ?? 1024,
            ["messages"] = messages,
        };

        if (systemParts.Count > 0)
            payload["system"] = string.Join("\n\n", systemParts);
        if (request.Temperature is double temp)
            payload["temperature"] = temp;

        return payload;
    }

    private static ParsedResponse ParseAnthropicResponse(JsonElement body)
    {
        string? model = null;
        if (body.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
            model = modelEl.GetString();

        var text = ExtractText(body);

        var promptTokens = 0;
        var completionTokens = 0;
        if (body.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var p) && p.ValueKind == JsonValueKind.Number)
                promptTokens = p.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
                completionTokens = c.GetInt32();
        }

        return new ParsedResponse(text, model, promptTokens, completionTokens, promptTokens + completionTokens);
    }

    private static string? ExtractText(JsonElement body)
    {
        if (!body.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new System.Text.StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!block.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(typeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase)) continue;
            if (block.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                sb.Append(textEl.GetString());
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static decimal EstimateCost(string model, int promptTokens, int completionTokens)
    {
        var price = PriceTable.TryGetValue(model, out var p)
            ? p
            : PriceTable["claude-sonnet-4.5"]; // safe fallback
        var promptCost = promptTokens / 1000m * price.InputPer1K;
        var completionCost = completionTokens / 1000m * price.OutputPer1K;
        return Math.Round(promptCost + completionCost, 6);
    }

    private Task RecordDiagnosticAsync(
        Guid? tenantId,
        string model,
        string billingMode,
        double durationMs,
        int tokensUsed,
        decimal cost,
        bool success,
        string? errorMessage,
        CancellationToken ct)
    {
        var diag = new ProviderDiagnostic
        {
            ProviderKey = ProviderKey,
            RequestDurationMs = durationMs,
            TokensUsed = tokensUsed,
            Cost = cost,
            // Story 34-3 / 35-2 — stamp the per-call billing posture the tagger
            // resolved so the markup engine + analytics never re-bill a BYOK call.
            BillingMode = billingMode,
            TenantId = tenantId,
            Model = model,
            RequestType = "chat",
            Success = success,
            ErrorMessage = errorMessage,
            CreatedAt = DateTime.UtcNow
        };
        return _diagnostics.RecordEventAsync(diag, ct);
    }

    /// <summary>
    /// Story 35-2 — append the usage DCB event (<c>LLM.CALL.SUCCESS</c> /
    /// <c>LLM.CALL.FAILED</c>) tagged with <c>billing_mode</c> so Story 35-3's
    /// metering can split billable (platform) from non-billable (byok) usage off
    /// the event stream. Only emitted for a tenant-scoped call — single-user /
    /// anonymous calls (<c>tenantId == null</c>) carry no billing dimension
    /// (AC8) and the tenant-scoped event store has no null-tenant target.
    /// Best-effort: an audit-append failure never fails the LLM call.
    /// </summary>
    private async Task EmitUsageEventAsync(
        Guid? tenantId,
        string model,
        string billingMode,
        bool success,
        string? reason,
        int tokensUsed,
        decimal cost,
        double durationMs,
        CancellationToken ct,
        int inputTokens = 0,
        int outputTokens = 0)
    {
        if (tenantId is not Guid tid)
        {
            return; // single-user / anonymous — no billable-mode implication.
        }

        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = success ? BillingModeEvents.LlmCallSuccess : BillingModeEvents.LlmCallFailed,
                TenantId = tid,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tid.ToString(),
                    billing_mode = billingMode,
                    provider = ProviderKey,
                    model,
                    reason,
                }),
                Metadata = JsonSerializer.Serialize(new
                {
                    workflowVersion = "1.0.0",
                    eventSource = "system",
                }),
                Data = JsonSerializer.Serialize(new
                {
                    inputTokens,
                    outputTokens,
                    totalTokens = tokensUsed,
                    costUsd = cost,
                    durationMs,
                }),
                CreatedAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Failed to append {EventType} usage event for tenant {TenantId}.",
                success ? BillingModeEvents.LlmCallSuccess : BillingModeEvents.LlmCallFailed, tid);
        }
    }

    private static async Task<string> SafeReadString(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync(); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];

    private static ChatResponse Error(string reason, string detail) =>
        new(
            Success: false,
            Text: null,
            Model: null,
            PromptTokens: 0,
            CompletionTokens: 0,
            TotalTokens: 0,
            CostUsd: 0m,
            ErrorReason: reason);

    private sealed record ParsedResponse(string? Text, string? Model, int PromptTokens, int CompletionTokens, int TotalTokens);
}

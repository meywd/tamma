using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Default <see cref="IProviderClient"/> implementation backed by
/// <see cref="IHttpClientFactory"/>. Each provider key (<c>anthropic</c>,
/// <c>openai</c>, <c>github-copilot</c>, …) resolves through
/// <see cref="ProviderCatalog"/> to a descriptor carrying its named
/// <see cref="HttpClient"/>, chat endpoint path, and wire dialect (Phase 1 of
/// the provider abstraction — this replaced the local provider→client map and
/// the <c>StartsWith("anthropic")</c> dialect branches).
/// </summary>
/// <remarks>
/// <para>
/// The implementation intentionally carries only enough request-shaping
/// logic to dispatch a provider-specific payload and parse a provider-
/// specific response. It is <em>not</em> a full LLM SDK — complete
/// streaming, tool-calling, and context management live in the TS engine
/// and will be ported separately (see audit finding 003).
/// </para>
/// <para>
/// Cost enrichment: the response parser splits input vs output tokens (TS
/// migration 014) and looks up the per-token rate in
/// <see cref="IProviderPricingService"/>. Unknown <c>(provider, model)</c>
/// tuples land at <c>cost = 0</c>; this matches the TS
/// <c>CostCalculator.calculate</c> happy path for local / un-priced models.
/// Wired in for finding 004.
/// </para>
/// </remarks>
public sealed class HttpProviderClient : IProviderClient
{
    private readonly IHttpClientFactory _factory;
    private readonly IProviderPricingService _pricing;
    private readonly ILogger<HttpProviderClient> _logger;

    public HttpProviderClient(
        IHttpClientFactory factory,
        IProviderPricingService pricing,
        ILogger<HttpProviderClient> logger)
    {
        _factory = factory;
        _pricing = pricing;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProviderInvocationResult> InvokeAsync(
        string provider, string model, ExecuteRequest req, CancellationToken ct = default)
    {
        // Providers that require a non-HTTP transport (subprocess for CLI
        // agents, MCP for Zen) cannot be served by this client and surface a
        // stable PROVIDER_NOT_SUPPORTED error so callers don't get an opaque
        // InvalidOperationException from a missing BaseAddress. Tracked as
        // part of finding 003 — Tamma needs a CLI-agent + MCP adapter port
        // before these can be answered.
        if (ProviderCatalog.ResolveNonHttp(provider) is not null)
        {
            throw new ProviderNotSupportedException(provider,
                $"Provider '{provider}' requires a non-HTTP transport " +
                "(CLI subprocess or MCP) that is not yet ported to C#. " +
                "See audit finding 003.");
        }

        var descriptor = ProviderCatalog.Resolve(provider);
        if (descriptor is null)
        {
            // Unknown provider — surface a typed error rather than blindly
            // dispatching against a default HttpClient with no BaseAddress
            // (which would 404 or NRE deep inside HttpRequestMessage).
            throw new ProviderNotSupportedException(provider,
                $"Provider '{provider}' is not registered with the HTTP " +
                "dispatch layer. Add a descriptor to " +
                $"{nameof(ProviderCatalog)}.{nameof(ProviderCatalog.HttpProviders)} " +
                "to enable it.");
        }

        var client = _factory.CreateClient(descriptor.HttpClientName);
        if (client.BaseAddress is null)
        {
            // Defensive: even when a descriptor exists, a missing BaseAddress
            // means the named client wasn't configured (e.g. Azure OpenAI's
            // per-resource URL was never supplied). Fail fast with a clear
            // message instead of producing an opaque "invalid request URI"
            // deep in HttpRequestMessage.
            throw new ProviderNotSupportedException(provider,
                $"Named HttpClient '{descriptor.HttpClientName}' has no BaseAddress. " +
                "Verify the provider's section is configured in appsettings.");
        }
        var stopwatch = Stopwatch.StartNew();

        var payload = BuildPayload(descriptor, model, req);

        // F1 — build the request URI through the single path-preserving join
        // (ProviderCatalog.CombineUrl) instead of posting a root-relative path
        // against BaseAddress, which would discard base-path segments (groq's
        // /openai, openrouter's /api). F3 — a config-overridden base URL
        // (≠ the descriptor default, e.g. "{Section}:BaseUrl" pointing at a
        // proxy) gets the dialect-default path, not the descriptor's
        // provider-specific ChatEndpointPath.
        var baseUrl = client.BaseAddress.ToString();
        var path = ProviderCatalog.ChatPathForBase(descriptor, descriptor.Dialect, baseUrl);
        var requestUri = ProviderCatalog.CombineUrl(baseUrl, path);

        try
        {
            using var response = await client.PostAsJsonAsync(requestUri, payload, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            stopwatch.Stop();
            return ParseResponse(descriptor, provider, model, body, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "Provider invocation failed for {Provider}/{Model} after {Elapsed}ms",
                provider, model, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static object BuildPayload(
        ProviderDescriptor descriptor, string model, ExecuteRequest req)
    {
        // One body builder per dialect (ProviderRequestShaper), selected via
        // the descriptor's closed dialect enum. The single-user-turn mapping
        // and the max_tokens defaults reproduce the legacy payloads
        // byte-for-byte (pinned by ProviderGoldenRequestTests): Anthropic
        // defaulted max_tokens to 1024, the OpenAI shape passed null through.
        var messages = new List<ConversationMessage>
        {
            new() { Role = "user", Content = req.Input },
        };

        var maxTokens = descriptor.Dialect == ProviderWireDialect.Anthropic
            ? req.MaxTokens ?? 1024
            : req.MaxTokens;

        return ProviderRequestShaper.BuildBody(
            descriptor.Dialect, messages, model, maxTokens, req.Temperature, tools: null);
    }

    private ProviderInvocationResult ParseResponse(
        ProviderDescriptor descriptor, string provider, string model, JsonElement body, long durationMs)
    {
        if (descriptor.Dialect == ProviderWireDialect.Anthropic)
        {
            // Anthropic: { content: [{ type: "text", text: "..." }], usage: {...} }
            var content = string.Empty;
            if (body.TryGetProperty("content", out var contentArr) &&
                contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in contentArr.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text))
                    {
                        content += text.GetString() ?? string.Empty;
                    }
                }
            }

            int inputTokens = 0;
            int outputTokens = 0;
            if (body.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var input))
                    inputTokens = input.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var output))
                    outputTokens = output.GetInt32();
            }

            var cost = _pricing.Compute(provider, model, inputTokens, outputTokens);
            return new ProviderInvocationResult(
                content, inputTokens + outputTokens, cost, durationMs,
                inputTokens, outputTokens);
        }

        // OpenAI-style: { choices: [{ message: { content } }], usage: { prompt_tokens, completion_tokens, total_tokens } }
        var choicesContent = string.Empty;
        if (body.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var c))
            {
                choicesContent = c.GetString() ?? string.Empty;
            }
        }

        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        if (body.TryGetProperty("usage", out var uOpenAi))
        {
            if (uOpenAi.TryGetProperty("prompt_tokens", out var pt))
                promptTokens = pt.GetInt32();
            if (uOpenAi.TryGetProperty("completion_tokens", out var ct))
                completionTokens = ct.GetInt32();
            if (uOpenAi.TryGetProperty("total_tokens", out var tt))
                totalTokens = tt.GetInt32();
        }
        // OpenAI sometimes returns only total_tokens; if so, attribute it all
        // to "input" so per-token cost still tracks. The pricing service treats
        // input/output rates as separate columns so this is the right default
        // for unknown splits — it will be a small over-estimate vs the true
        // billed cost when output rates are higher than input rates.
        if (totalTokens == 0)
        {
            totalTokens = promptTokens + completionTokens;
        }
        else if (promptTokens == 0 && completionTokens == 0)
        {
            promptTokens = totalTokens;
        }

        var openAiCost = _pricing.Compute(provider, model, promptTokens, completionTokens);
        return new ProviderInvocationResult(
            choicesContent, totalTokens, openAiCost, durationMs,
            promptTokens, completionTokens);
    }
}

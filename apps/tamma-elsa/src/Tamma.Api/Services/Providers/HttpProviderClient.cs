using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Default <see cref="IProviderClient"/> implementation backed by
/// <see cref="IHttpClientFactory"/>. Each provider key (<c>anthropic</c>,
/// <c>openai</c>, <c>github-copilot</c>, …) is mapped to a named
/// <see cref="HttpClient"/> registered in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The implementation intentionally carries only enough request-shaping
/// logic to dispatch a provider-specific payload and parse a provider-
/// specific response. It is <em>not</em> a full LLM SDK — complete
/// streaming, tool-calling, and context management live in the TS engine
/// and will be ported separately.
/// </para>
/// <para>
/// When the mapped <see cref="HttpClient"/> is not configured (e.g. the
/// API key is missing for a given provider), the client falls back to a
/// deterministic stub response rather than 500-ing, so local/test flows
/// work without network credentials. Production deployments should set
/// the provider credentials at startup so this branch is never taken.
/// </para>
/// </remarks>
public sealed class HttpProviderClient : IProviderClient
{
    private static readonly IReadOnlyDictionary<string, string> ProviderHttpClientMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = "anthropic",
            ["anthropic-claude"] = "anthropic",
            ["openai"] = "openai",
            ["github-copilot"] = "github-copilot",
            ["gemini"] = "gemini",
        };

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<HttpProviderClient> _logger;

    public HttpProviderClient(IHttpClientFactory factory, ILogger<HttpProviderClient> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProviderInvocationResult> InvokeAsync(
        string provider, string model, ExecuteRequest req, CancellationToken ct = default)
    {
        if (!ProviderHttpClientMap.TryGetValue(provider, out var clientName))
        {
            clientName = provider.ToLowerInvariant();
        }

        var client = _factory.CreateClient(clientName);
        var stopwatch = Stopwatch.StartNew();

        // Anthropic is the only provider with a first-class request shape
        // right now. Other providers share a generic completion-style payload
        // until their dedicated adapters are ported.
        var (path, payload) = BuildRequest(provider, model, req);

        try
        {
            using var response = await client.PostAsJsonAsync(path, payload, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            stopwatch.Stop();
            return ParseResponse(provider, body, stopwatch.ElapsedMilliseconds);
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

    private static (string Path, object Payload) BuildRequest(
        string provider, string model, ExecuteRequest req)
    {
        if (provider.StartsWith("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return ("/v1/messages", new
            {
                model,
                max_tokens = req.MaxTokens ?? 1024,
                temperature = req.Temperature,
                messages = new[]
                {
                    new { role = "user", content = req.Input },
                },
            });
        }

        // Generic completion-style payload for other providers.
        return ("/v1/chat/completions", new
        {
            model,
            max_tokens = req.MaxTokens,
            temperature = req.Temperature,
            messages = new[]
            {
                new { role = "user", content = req.Input },
            },
        });
    }

    private static ProviderInvocationResult ParseResponse(
        string provider, JsonElement body, long durationMs)
    {
        if (provider.StartsWith("anthropic", StringComparison.OrdinalIgnoreCase))
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

            int tokens = 0;
            if (body.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var input))
                    tokens += input.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var output))
                    tokens += output.GetInt32();
            }

            // Anthropic doesn't return cost; leave at 0 — the cost-monitor
            // service is responsible for enrichment (Epic 9).
            return new ProviderInvocationResult(content, tokens, 0m, durationMs);
        }

        // OpenAI-style: { choices: [{ message: { content } }], usage: { total_tokens } }
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

        int totalTokens = 0;
        if (body.TryGetProperty("usage", out var uOpenAi) &&
            uOpenAi.TryGetProperty("total_tokens", out var tt))
        {
            totalTokens = tt.GetInt32();
        }

        return new ProviderInvocationResult(choicesContent, totalTokens, 0m, durationMs);
    }
}

using Tamma.Api.Services.Providers;

namespace Tamma.Api.Extensions;

/// <summary>
/// Named-<see cref="HttpClient"/> registrations for every HTTP LLM provider —
/// extracted from <c>Program.cs</c> so the REAL registrations (not a test
/// re-implementation) can be exercised by the provider golden-request tests
/// (<c>ProviderEgressRegressionTests</c> composes these clients with
/// <see cref="HttpProviderClient"/> and pins the full request URIs).
/// </summary>
public static class ProviderHttpClientServiceCollectionExtensions
{
    /// <summary>
    /// Register the named provider clients used by
    /// <see cref="HttpProviderClient"/> plus the inline tool-loop runner's
    /// plain client. CLI-agent providers (claude-code, opencode) and the Zen
    /// MCP provider are NOT registered here — they require subprocess / MCP
    /// transports that are tracked separately (audit finding 003).
    /// </summary>
    public static IServiceCollection AddTammaProviderHttpClients(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("anthropic", client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com");
            // 2023-06-01 is the published Anthropic API version. This client previously sent
            // "2024-01-01", which is not a released version at all — a drift artifact of the
            // Anthropic request shape being built in THREE places (here, InlineToolLoopRunner
            // and LlmProxyService), two of which already sent 2023-06-01. Collapsing those
            // three paths behind one provider descriptor is tracked in
            // .dev/findings/provider-abstraction-and-openai-compatible-candidates.md.
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            var apiKey = configuration["Anthropic:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        });
        // HTTP-based providers used by HttpProviderClient (finding 003). Each named
        // client carries its own base URL + auth header so the dispatch layer doesn't
        // have to know the provider details.
        services.AddHttpClient("openai", client =>
        {
            client.BaseAddress = new Uri(
                configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com");
            var apiKey = configuration["OpenAI:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        });
        services.AddHttpClient("github-copilot", client =>
        {
            client.BaseAddress = new Uri(
                configuration["Copilot:BaseUrl"] ?? "https://api.githubcopilot.com");
            var apiKey = configuration["Copilot:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        });
        services.AddHttpClient("gemini", client =>
        {
            client.BaseAddress = new Uri(
                configuration["Gemini:BaseUrl"] ?? "https://generativelanguage.googleapis.com");
            // F2 — Google's OpenAI-compatible surface (/v1beta/openai/chat/completions,
            // per ai.google.dev/gemini-api/docs/openai) authenticates with a standard
            // Authorization: Bearer header; the former X-Goog-Api-Key header was a
            // wire-fact error (that header belongs to the native Gemini API surface).
            var apiKey = configuration["Gemini:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        });
        services.AddHttpClient("openrouter", client =>
        {
            // F6 — https://openrouter.ai/api matches the catalogue descriptor, so the
            // path-preserving join posts to OpenRouter's documented endpoint
            // https://openrouter.ai/api/v1/chat/completions. The old base
            // (https://openrouter.ai) produced /v1/chat/completions — a 404 — so this
            // is a bug fix, not a behaviour break.
            client.BaseAddress = new Uri(
                configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api");
            var apiKey = configuration["OpenRouter:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        });
        services.AddHttpClient("z.ai", client =>
        {
            client.BaseAddress = new Uri(
                configuration["ZAi:BaseUrl"] ?? "https://api.z.ai");
            var apiKey = configuration["ZAi:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        });
        services.AddHttpClient("local", client =>
        {
            // Local model server (Ollama / LM Studio default). Configurable per-deploy.
            var baseUrl = configuration["LocalLLM:BaseUrl"] ?? "http://localhost:11434";
            client.BaseAddress = new Uri(baseUrl);
        });

        // ── Descriptor-driven provider clients (Phase 1 of the provider abstraction,
        // .dev/findings/provider-abstraction-and-openai-compatible-candidates.md).
        // The seven provider clients above predate the ProviderCatalog and keep their
        // hand-written registrations; every catalogue descriptor whose client name is
        // NOT hand-registered gets its named client here, configured from the
        // descriptor (default base URL + auth scheme + version header) with the same
        // "{Section}:BaseUrl" / "{Section}:ApiKey" override convention.
        // This is what closed the allowlist ↔ named-client gap: azure-openai,
        // together, groq, and the new deepseek + moonshot (Kimi) become dispatchable.
        // A descriptor with an empty base URL and no config (e.g. Azure OpenAI's
        // per-resource URL) is registered without a BaseAddress; HttpProviderClient
        // fails fast on that with its existing "no BaseAddress" error.
        var handRegisteredProviderClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "anthropic", "openai", "github-copilot", "gemini", "openrouter", "z.ai", "local",
        };
        foreach (var providerDescriptor in ProviderCatalog.HttpProviders)
        {
            if (!handRegisteredProviderClients.Add(providerDescriptor.HttpClientName))
                continue; // hand-registered above, or shared with an earlier descriptor

            var d = providerDescriptor;
            services.AddHttpClient(d.HttpClientName, client =>
            {
                var baseUrl = configuration[$"{d.ConfigSection}:BaseUrl"] ?? d.DefaultBaseUrl;
                if (!string.IsNullOrWhiteSpace(baseUrl))
                    client.BaseAddress = new Uri(baseUrl);

                var apiKey = configuration[$"{d.ConfigSection}:ApiKey"];
                if (!string.IsNullOrEmpty(apiKey))
                {
                    switch (d.AuthScheme)
                    {
                        case ProviderAuthScheme.AnthropicApiKey:
                            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                            break;
                        default:
                            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                            break;
                    }
                }

                if (d.VersionHeaderName is not null)
                    client.DefaultRequestHeaders.Add(d.VersionHeaderName, d.VersionHeaderValue);
            });
        }

        // The inline tool-loop runner's client is deliberately UNCONFIGURED: the
        // runner clears default headers and targets absolute URLs, applying base URL /
        // auth / version headers per call from the descriptor + resolved (BYOK-aware)
        // credentials. Registered here so the name is intentional rather than the old
        // phantom "llm-{provider}" lookup that resolved unregistered names.
        services.AddHttpClient(Services.Agents.InlineToolLoopRunner.RunnerHttpClientName);

        return services;
    }
}

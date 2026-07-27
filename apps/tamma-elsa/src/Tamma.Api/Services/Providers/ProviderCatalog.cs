using System.Collections.Frozen;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Phase 1 of the provider abstraction — the platform-owned provider
/// catalogue, seeded IN CODE
/// (.dev/findings/provider-abstraction-and-openai-compatible-candidates.md).
/// One entry per provider replaces the four disagreeing surfaces the finding
/// documented (allowlist / named-client registrations / provider→client map /
/// duplicated dialect branches). Phase 2 moves these rows to a
/// platform-owner-managed table; the shape and the closed
/// <see cref="ProviderWireDialect"/> stay.
///
/// <para>Keyset contract (enforced by
/// <c>ProviderCatalogTests.Allowlist_And_Catalog_Are_In_Exact_Keyset_Agreement</c>):
/// <c>ProviderAllowlist.DefaultProviders</c> == HTTP descriptor keys ∪
/// allow-listed non-HTTP keys, exactly. The two surfaces can no longer drift.</para>
/// </summary>
public static class ProviderCatalog
{
    /// <summary>Dialect-default chat endpoint paths (used when a descriptor
    /// carries no <see cref="ProviderDescriptor.ChatEndpointPath"/>).</summary>
    public const string AnthropicMessagesPath = "/v1/messages";
    public const string OpenAiChatCompletionsPath = "/v1/chat/completions";

    /// <summary>All HTTP provider descriptors, one per allow-listed HTTP key.</summary>
    public static IReadOnlyList<ProviderDescriptor> HttpProviders { get; } = new[]
    {
        new ProviderDescriptor
        {
            Key = "anthropic",
            DisplayName = "Anthropic Claude",
            Dialect = ProviderWireDialect.Anthropic,
            DefaultBaseUrl = "https://api.anthropic.com",
            AuthScheme = ProviderAuthScheme.AnthropicApiKey,
            DefaultModel = "claude-sonnet-4-20250514",
            HttpClientName = "anthropic",
            ConfigSection = "Anthropic",
            // Per-descriptor DATA: 2023-06-01 is the published Anthropic API
            // version (the finding's drift defect — one of three copies sent
            // 2024-01-01, which was never released). A Bedrock descriptor
            // would carry bedrock-2023-05-31 here instead.
            VersionHeaderName = "anthropic-version",
            VersionHeaderValue = "2023-06-01",
            Aliases = new[] { "anthropic-claude" },
        },
        new ProviderDescriptor
        {
            Key = "openai",
            DisplayName = "OpenAI",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://api.openai.com",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "gpt-4o",
            HttpClientName = "openai",
            ConfigSection = "OpenAI",
        },
        new ProviderDescriptor
        {
            Key = "openrouter",
            DisplayName = "OpenRouter",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://openrouter.ai/api",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "anthropic/claude-sonnet-4-20250514",
            HttpClientName = "openrouter",
            ConfigSection = "OpenRouter",
        },
        new ProviderDescriptor
        {
            Key = "github-copilot",
            DisplayName = "GitHub Copilot",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://api.githubcopilot.com",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "", // legacy behaviour: caller must specify
            HttpClientName = "github-copilot",
            ConfigSection = "Copilot",
        },
        new ProviderDescriptor
        {
            Key = "gemini",
            DisplayName = "Google Gemini",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://generativelanguage.googleapis.com",
            AuthScheme = ProviderAuthScheme.GoogleApiKey,
            DefaultModel = "",
            HttpClientName = "gemini",
            ConfigSection = "Gemini",
        },
        new ProviderDescriptor
        {
            // "google" and "gemini" are BOTH allow-listed keys today, so both
            // get a descriptor (same wire identity, same named client).
            Key = "google",
            DisplayName = "Google Gemini",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://generativelanguage.googleapis.com",
            AuthScheme = ProviderAuthScheme.GoogleApiKey,
            DefaultModel = "",
            HttpClientName = "gemini",
            ConfigSection = "Gemini",
        },
        new ProviderDescriptor
        {
            // Product decision 2026-07-25: z-ai IS Zhipu GLM (Z.ai is Zhipu's
            // international brand). The key stays "z-ai" — renaming would be a
            // wire-string change on persisted config. Finishing its wiring
            // (allowlist key ↔ named client, real chat path) is part of the
            // documented seven-unreachable-providers fix.
            Key = "z-ai",
            DisplayName = "Z.ai (Zhipu GLM)",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://api.z.ai",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "glm-5.2", // z.ai/model-api flagship (verified 2026-07)
            HttpClientName = "z.ai",
            ConfigSection = "ZAi",
            // Z.ai's chat endpoint is /api/paas/v4/chat/completions (docs.z.ai)
            // — not the OpenAI-default /v1/chat/completions.
            ChatEndpointPath = "/api/paas/v4/chat/completions",
            Aliases = new[] { "z.ai", "zai" },
        },
        new ProviderDescriptor
        {
            Key = "local-llm",
            DisplayName = "Local LLM server",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "http://localhost:11434",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "local",
            ConfigSection = "LocalLLM",
            Aliases = new[] { "local" },
        },
        new ProviderDescriptor
        {
            Key = "ollama",
            DisplayName = "Ollama",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "http://localhost:11434",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "local",
            ConfigSection = "LocalLLM",
        },
        new ProviderDescriptor
        {
            Key = "lmstudio",
            DisplayName = "LM Studio",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "http://localhost:1234",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "local",
            ConfigSection = "LocalLLM",
        },
        new ProviderDescriptor
        {
            Key = "azure-openai",
            DisplayName = "Azure OpenAI",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "", // per-resource URL — must be configured
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "azure-openai",
            ConfigSection = "AzureOpenAI",
        },
        new ProviderDescriptor
        {
            Key = "together",
            DisplayName = "Together AI",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://api.together.xyz",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "together",
            ConfigSection = "Together",
        },
        new ProviderDescriptor
        {
            Key = "groq",
            DisplayName = "Groq",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            // Groq serves the OpenAI shape under /openai
            // (https://api.groq.com/openai/v1/chat/completions).
            DefaultBaseUrl = "https://api.groq.com/openai",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "groq",
            ConfigSection = "Groq",
        },
        new ProviderDescriptor
        {
            // New candidate (finding, product owner 2026-07-25). Verified from
            // api-docs.deepseek.com: base https://api.deepseek.com, OpenAI
            // shape (both /chat/completions and /v1/chat/completions are
            // served; the /v1 is unrelated to model versioning), Bearer auth.
            Key = "deepseek",
            DisplayName = "DeepSeek",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://api.deepseek.com",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "deepseek-chat",
            HttpClientName = "deepseek",
            ConfigSection = "DeepSeek",
        },
        new ProviderDescriptor
        {
            // New candidate (finding, product owner 2026-07-25). Verified from
            // platform.moonshot.ai / platform.kimi.ai: OpenAI-compatible at
            // https://api.moonshot.ai/v1/chat/completions, Bearer auth.
            Key = "moonshot",
            DisplayName = "Moonshot AI (Kimi)",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://api.moonshot.ai",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "kimi-k3", // platform.kimi.ai quickstart model (verified 2026-07)
            HttpClientName = "moonshot",
            ConfigSection = "Moonshot",
            Aliases = new[] { "kimi" },
        },
    };

    /// <summary>Providers that require a non-HTTP transport. The
    /// <c>Allowlisted = false</c> entries are defensive rejection keys only
    /// (never selectable), preserved from the legacy
    /// <c>HttpProviderClient.NonHttpProviders</c> set.</summary>
    public static IReadOnlyList<NonHttpProviderDescriptor> NonHttpProviders { get; } = new[]
    {
        new NonHttpProviderDescriptor
        {
            Key = "opencode",
            DisplayName = "OpenCode CLI agent",
            Transport = NonHttpProviderTransport.Cli,
            Allowlisted = true,
            Aliases = new[] { "opencode-cli" },
        },
        new NonHttpProviderDescriptor
        {
            Key = "zen-mcp",
            DisplayName = "Zen MCP server",
            Transport = NonHttpProviderTransport.Mcp,
            Allowlisted = true,
            Aliases = new[] { "zen" },
        },
        new NonHttpProviderDescriptor
        {
            Key = "claude-code",
            DisplayName = "Claude Code CLI agent",
            Transport = NonHttpProviderTransport.Cli,
            Allowlisted = false,
            Aliases = new[] { "claude-code-cli" },
        },
    };

    private static readonly FrozenDictionary<string, ProviderDescriptor> HttpByKey =
        BuildLookup(HttpProviders, d => d.Key, d => d.Aliases);

    private static readonly FrozenDictionary<string, NonHttpProviderDescriptor> NonHttpByKey =
        BuildLookup(NonHttpProviders, d => d.Key, d => d.Aliases);

    private static FrozenDictionary<string, T> BuildLookup<T>(
        IReadOnlyList<T> items, Func<T, string> key, Func<T, IReadOnlyList<string>> aliases)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            map.Add(key(item), item);
            foreach (var alias in aliases(item))
            {
                map.Add(alias, item);
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolve an HTTP provider descriptor by key or alias
    /// (case-insensitive). Null when the key is unknown or non-HTTP.</summary>
    public static ProviderDescriptor? Resolve(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && HttpByKey.TryGetValue(provider.Trim(), out var d)
            ? d
            : null;

    /// <summary>Resolve a non-HTTP classification by key or alias.</summary>
    public static NonHttpProviderDescriptor? ResolveNonHttp(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && NonHttpByKey.TryGetValue(provider.Trim(), out var d)
            ? d
            : null;

    /// <summary>Wire dialect for a provider key. Unknown keys fall back to
    /// <see cref="ProviderWireDialect.OpenAiCompatible"/> — byte-identical to
    /// the legacy behaviour of the three collapsed
    /// <c>StartsWith("anthropic")</c>/<c>Equals("anthropic")</c> branches,
    /// whose else-arm was the OpenAI shape.</summary>
    public static ProviderWireDialect ResolveDialect(string? provider) =>
        Resolve(provider)?.Dialect ?? ProviderWireDialect.OpenAiCompatible;

    /// <summary>Chat endpoint path for a descriptor (dialect default when the
    /// descriptor carries no override). The null-descriptor overload keeps the
    /// legacy default for providers only known from configuration.</summary>
    public static string ChatPath(ProviderDescriptor? descriptor, ProviderWireDialect dialect) =>
        descriptor?.ChatEndpointPath
        ?? (dialect == ProviderWireDialect.Anthropic
            ? AnthropicMessagesPath
            : OpenAiChatCompletionsPath);

    /// <summary>Chat endpoint path for a known descriptor.</summary>
    public static string ChatPath(ProviderDescriptor descriptor) =>
        ChatPath(descriptor, descriptor.Dialect);
}

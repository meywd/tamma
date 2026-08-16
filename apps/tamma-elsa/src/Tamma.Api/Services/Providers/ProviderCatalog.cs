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

    /// <summary>Story 46-0 — the dialect-generic models-list path. Both wire
    /// dialects serve their list at <c>/v1/models</c> (Anthropic's own models
    /// endpoint IS <c>/v1/models</c>, so the distinction is theoretical today —
    /// stated here rather than left to be re-derived). Used for
    /// config-overridden (proxy) base URLs by <see cref="ModelsPathForBase"/>,
    /// mirroring the F3 chat-path rule.</summary>
    public const string DefaultModelsPath = "/v1/models";

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
            // 46-1 AC7 defaults refresh (2026-07-27): was the dated snapshot
            // claude-sonnet-4-20250514 — Anthropic's docs list Claude Sonnet 4
            // as DEPRECATED (migration guide: retires 2026-06-15, i.e. already
            // past; replacement guidance points at the current Sonnet line).
            // claude-sonnet-4-5 is the documented ACTIVE dash-formed alias
            // (full id claude-sonnet-4-5-20250929). Anthropic API ids are
            // dash-formed — "claude-sonnet-4.5" is the display name, not an id.
            DefaultModel = "claude-sonnet-4-5",
            HttpClientName = "anthropic",
            ConfigSection = "Anthropic",
            // Per-descriptor DATA: 2023-06-01 is the published Anthropic API
            // version (the finding's drift defect — one of three copies sent
            // 2024-01-01, which was never released). A Bedrock descriptor
            // would carry bedrock-2023-05-31 here instead.
            VersionHeaderName = "anthropic-version",
            VersionHeaderValue = "2023-06-01",
            // 46-0: verified LIVE from this sandbox 2026-07-27 — unauthenticated
            // GET https://api.anthropic.com/v1/models → 401 "x-api-key header
            // is required" (route + auth scheme confirmed). Envelope:
            // {data:[{id, display_name, …}], has_more}.
            ModelsEndpointPath = "/v1/models",
            Aliases = new[] { "anthropic-claude" },
        },
        new ProviderDescriptor
        {
            Key = "openai",
            DisplayName = "OpenAI",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://api.openai.com",
            AuthScheme = ProviderAuthScheme.BearerToken,
            // 46-1 AC7: gpt-4o verified against platform.openai.com docs — a
            // current, accepted model id. Unchanged.
            DefaultModel = "gpt-4o",
            HttpClientName = "openai",
            ConfigSection = "OpenAI",
            // 46-0: docs-verified (platform.openai.com/docs/api-reference/models;
            // host unreachable from this sandbox). OpenAI list envelope
            // {object:"list", data:[{id, owned_by, created}]}.
            ModelsEndpointPath = "/v1/models",
        },
        new ProviderDescriptor
        {
            Key = "openrouter",
            DisplayName = "OpenRouter",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://openrouter.ai/api",
            AuthScheme = ProviderAuthScheme.BearerToken,
            // 46-1 AC7 defaults refresh (2026-07-27): was the dated snapshot
            // slug anthropic/claude-sonnet-4-20250514 — OpenRouter's marketplace
            // slugs are UNDATED and DOT-formed (anthropic/claude-sonnet-4.5,
            // unlike Anthropic's own dash-formed API ids — do not "fix" the dot
            // here). Docs-verified via openrouter.ai model naming conventions;
            // the public /api/v1/models list was unreachable from this sandbox
            // (proxy 403), so this is a docs-level refresh, re-checkable in one
            // click once 46-2's picker is live against the real list.
            DefaultModel = "anthropic/claude-sonnet-4.5",
            HttpClientName = "openrouter",
            ConfigSection = "OpenRouter",
            // 46-0: docs-verified (openrouter.ai/docs — the models API is
            // PUBLIC, works with or without a key; display name in `name`).
            // Base /api is preserved by CombineUrl → …/api/v1/models.
            ModelsEndpointPath = "/v1/models",
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
            // 46-0 (epic D4): api.githubcopilot.com requires a Copilot token
            // exchange, not a plain API key — deliberately unlistable; the UIs
            // fall back to free-text model entry.
            ModelsEndpointPath = null,
        },
        new ProviderDescriptor
        {
            Key = "gemini",
            DisplayName = "Google Gemini",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://generativelanguage.googleapis.com",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "gemini",
            ConfigSection = "Gemini",
            // Google's OpenAI-compatible surface lives under /v1beta/openai
            // with standard Bearer auth (ai.google.dev/gemini-api/docs/openai;
            // verified empirically 2026-07-27: POST /v1beta/openai/chat/completions
            // with a Bearer header answers "Please pass a valid API key" while
            // /v1/chat/completions is a plain 404).
            ChatEndpointPath = "/v1beta/openai/chat/completions",
            // 46-0: verified LIVE from this sandbox 2026-07-27 — GET
            // /v1beta/openai/models with a bad Bearer → 400 "Please pass a
            // valid API key" (route + auth scheme confirmed); no auth → 404.
            // Ids come back as models/gemini-… — kept VERBATIM (they are what
            // the chat endpoint accepts on this surface).
            ModelsEndpointPath = "/v1beta/openai/models",
        },
        new ProviderDescriptor
        {
            // "google" and "gemini" are BOTH allow-listed keys today, so both
            // get a descriptor (same wire identity, same named client).
            Key = "google",
            DisplayName = "Google Gemini",
            Dialect = ProviderWireDialect.OpenAiCompatible,
            DefaultBaseUrl = "https://generativelanguage.googleapis.com",
            AuthScheme = ProviderAuthScheme.BearerToken,
            DefaultModel = "",
            HttpClientName = "gemini",
            ConfigSection = "Gemini",
            // Same wire identity as the "gemini" descriptor above.
            ChatEndpointPath = "/v1beta/openai/chat/completions",
            ModelsEndpointPath = "/v1beta/openai/models",
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
            // 46-0 Z.ai re-check (story AC1): re-attempted 2026-07-27 at
            // implementation time — docs.z.ai is still unreachable from this
            // sandbox (proxy 403) and no models-list route has surfaced in
            // search. Stays null per epic D4: the UIs fall back to free-text
            // model entry for z-ai; if the product owner's console shows a
            // list route, filling this in is a one-line data change.
            ModelsEndpointPath = null,
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
            // 46-0: Ollama's OpenAI-compat layer and LM Studio both serve
            // /v1/models with no auth (docs.ollama.com/api/openai-compatibility;
            // key-optional for listing — see ProviderModelCatalogService).
            ModelsEndpointPath = "/v1/models",
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
            ModelsEndpointPath = "/v1/models",
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
            ModelsEndpointPath = "/v1/models",
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
            // 46-0 (epic D4): Azure's listing needs an api-version query and
            // returns DEPLOYMENTS — a different resource model. Deliberately
            // unlistable; free-text model entry in the UIs.
            ModelsEndpointPath = null,
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
            // 46-0: docs-verified (docs.together.ai/reference/models). NOTE:
            // Together answers a BARE JSON ARRAY [{id, display_name, …}], not
            // the {data:[…]} envelope — the parser owns both shapes.
            ModelsEndpointPath = "/v1/models",
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
            // 46-0: docs-verified (console.groq.com/docs/api-reference). The
            // /openai base segment is preserved by CombineUrl, landing on the
            // documented https://api.groq.com/openai/v1/models.
            ModelsEndpointPath = "/v1/models",
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
            // 46-1 AC7: deepseek-chat verified against api-docs.deepseek.com —
            // still the documented general-purpose model id. Unchanged.
            DefaultModel = "deepseek-chat",
            HttpClientName = "deepseek",
            ConfigSection = "DeepSeek",
            // 46-0: docs-verified (api-docs.deepseek.com/api/list-models).
            // NOTE: NO /v1 prefix — matches DeepSeek's chat-path convention
            // documented above (both /chat/completions and /v1/chat/completions
            // are served for chat, but the models list is documented at /models).
            ModelsEndpointPath = "/models",
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
            // 46-1 AC7: kimi-k3 re-checked 2026-07 against platform.kimi.ai
            // quickstart — current. Unchanged.
            DefaultModel = "kimi-k3",
            HttpClientName = "moonshot",
            ConfigSection = "Moonshot",
            // 46-0: docs-verified (platform.kimi.ai/docs/api/list-models).
            // OpenAI list envelope + extra capability fields (ignored).
            ModelsEndpointPath = "/v1/models",
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
        new NonHttpProviderDescriptor
        {
            // 2026-08-13 (Epic 31 P5 follow-up) — the deterministic in-process
            // TEST provider that unblocks the engine-driven autonomous E2E.
            // Allowlisted=false ON PURPOSE: like the claude-code family it is
            // never selectable by default — enabling it requires the explicit
            // Llm:EnableScriptedProvider flag (ScriptedProviderPosture), which
            // additionally REFUSES to register on any SaaS/production-shaped
            // host. Catalogued (rather than left unknown) so the keyset
            // contract stays total: every provider key the platform ships is
            // either an HTTP descriptor or an explicit non-HTTP classification.
            Key = "scripted",
            DisplayName = "Scripted test provider (in-process, opt-in)",
            Transport = NonHttpProviderTransport.InProcess,
            Allowlisted = false,
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

    /// <summary>
    /// Chat endpoint path honouring the config-override rule: a descriptor's
    /// <see cref="ProviderDescriptor.ChatEndpointPath"/> describes the
    /// provider's OWN endpoint at its OWN <see cref="ProviderDescriptor.DefaultBaseUrl"/>.
    /// When <paramref name="effectiveBaseUrl"/> is an explicit configuration
    /// override (≠ the descriptor default), the pre-refactor proxy semantics
    /// apply instead: the dialect-default path (<c>/v1/messages</c> or
    /// <c>/v1/chat/completions</c>) — a gemini/z-ai deployment routed through
    /// an OpenAI-compatible proxy gets <c>{base}/v1/chat/completions</c>, not
    /// the descriptor's provider-specific path grafted onto the proxy URL.
    /// </summary>
    public static string ChatPathForBase(
        ProviderDescriptor? descriptor, ProviderWireDialect dialect, string? effectiveBaseUrl) =>
        descriptor is not null && IsDefaultBaseUrl(descriptor, effectiveBaseUrl)
            ? ChatPath(descriptor)
            : ChatPath(null, dialect);

    /// <summary>
    /// Story 46-0 — models-list endpoint path honouring the F3 config-override
    /// rule, mirroring <see cref="ChatPathForBase"/>: a descriptor's
    /// <see cref="ProviderDescriptor.ModelsEndpointPath"/> describes the
    /// provider's OWN endpoint at its OWN default base URL. When
    /// <paramref name="effectiveBaseUrl"/> is an explicit configuration
    /// override (≠ the descriptor default — i.e. an OpenAI-compatible proxy),
    /// the dialect-generic <see cref="DefaultModelsPath"/> applies instead —
    /// the provider-specific path (gemini's <c>/v1beta/openai/models</c>,
    /// deepseek's un-prefixed <c>/models</c>) must not be grafted onto a proxy.
    /// Both dialects get <c>/v1/models</c> on an override: Anthropic's own
    /// models path IS <c>/v1/models</c>, so the per-dialect distinction is
    /// theoretical today (stated here per the 46-0 plan rather than left to be
    /// re-derived). Returns null when the descriptor is null or the provider's
    /// models cannot be listed (<c>ModelsEndpointPath == null</c>) — listing
    /// support is a property of the PROVIDER, not of the base URL.
    /// </summary>
    public static string? ModelsPathForBase(
        ProviderDescriptor? descriptor, string? effectiveBaseUrl)
    {
        if (descriptor?.ModelsEndpointPath is null)
        {
            return null;
        }

        return IsDefaultBaseUrl(descriptor, effectiveBaseUrl)
            ? descriptor.ModelsEndpointPath
            : DefaultModelsPath;
    }

    /// <summary>Whether <paramref name="baseUrl"/> is the descriptor's own
    /// default base URL (slash- and case-insensitive), i.e. NOT a
    /// configuration override.</summary>
    public static bool IsDefaultBaseUrl(ProviderDescriptor descriptor, string? baseUrl) =>
        !string.IsNullOrWhiteSpace(descriptor.DefaultBaseUrl)
        && !string.IsNullOrWhiteSpace(baseUrl)
        && string.Equals(
            baseUrl.TrimEnd('/'),
            descriptor.DefaultBaseUrl.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Join a base URL and an endpoint path PRESERVING the base URL's own path
    /// segments. This is the single URL-composition helper for every LLM
    /// egress path: .NET's <c>Uri(baseAddress, relative)</c> composition
    /// DISCARDS base path segments for root-relative paths (groq's
    /// <c>https://api.groq.com/openai</c> + <c>/v1/chat/completions</c> would
    /// become <c>https://api.groq.com/v1/chat/completions</c> — a 404), so
    /// callers must build absolute request URIs through this method instead of
    /// posting relative paths against <c>HttpClient.BaseAddress</c>.
    /// Hosts without a base path stay byte-identical
    /// (<c>https://api.openai.com</c> + <c>/v1/chat/completions</c> →
    /// <c>https://api.openai.com/v1/chat/completions</c>).
    /// </summary>
    public static string CombineUrl(string baseUrl, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var trimmedBase = baseUrl.TrimEnd('/');
        return string.IsNullOrEmpty(path)
            ? trimmedBase
            : trimmedBase + "/" + path.TrimStart('/');
    }
}

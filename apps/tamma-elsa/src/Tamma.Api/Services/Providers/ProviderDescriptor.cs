namespace Tamma.Api.Services.Providers;

/// <summary>
/// Phase 1 of the provider abstraction
/// (.dev/findings/provider-abstraction-and-openai-compatible-candidates.md):
/// the wire dialect a provider speaks, as a CLOSED enum. Adding a member here
/// means writing a new request builder + response parser — it is deliberately
/// not extensible by configuration. Everything else about a provider (base
/// URL, auth scheme, default model, version header) is per-descriptor DATA on
/// <see cref="ProviderDescriptor"/>.
/// </summary>
public enum ProviderWireDialect
{
    /// <summary>Anthropic Messages API (<c>/v1/messages</c>, content blocks,
    /// <c>tool_use</c>/<c>tool_result</c>, top-level <c>system</c>).</summary>
    Anthropic,

    /// <summary>OpenAI Chat Completions shape (<c>/v1/chat/completions</c>,
    /// <c>choices[].message</c>, <c>tools[].function</c>). Spoken by OpenAI,
    /// OpenRouter, DeepSeek, Moonshot/Kimi, Z.ai (GLM), Together, Groq,
    /// Ollama / LM Studio, and Azure OpenAI's v1 surface.</summary>
    OpenAiCompatible,
}

/// <summary>
/// How the API key is presented on the wire. The key VALUE always comes from
/// configuration / the BYOK credential resolver — never from the descriptor.
/// </summary>
public enum ProviderAuthScheme
{
    /// <summary>Anthropic-style <c>x-api-key</c> header (paired with the
    /// descriptor's version header, e.g. <c>anthropic-version</c>).</summary>
    AnthropicApiKey,

    /// <summary>Standard <c>Authorization: Bearer &lt;key&gt;</c> header.</summary>
    BearerToken,

    /// <summary>Google-style <c>X-Goog-Api-Key</c> header.</summary>
    GoogleApiKey,
}

/// <summary>Transport of a provider that is NOT served over plain HTTP.</summary>
public enum NonHttpProviderTransport
{
    /// <summary>CLI agent driven over a subprocess (claude-code, opencode).</summary>
    Cli,

    /// <summary>Model Context Protocol server (zen-mcp).</summary>
    Mcp,
}

/// <summary>
/// Everything an HTTP LLM provider actually differs by, in one record. Seeded
/// in code by <see cref="ProviderCatalog"/> (Phase 1); becomes a
/// platform-owner-managed table in Phase 2 — the dialect stays a closed enum
/// either way (an admin adds an <em>instance of a dialect the code already
/// implements</em>, never a dialect).
/// </summary>
public sealed record ProviderDescriptor
{
    /// <summary>Canonical provider key (matches <c>ProviderAllowlist</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable name. Used to document key equivalences —
    /// e.g. <c>z-ai</c> IS Zhipu GLM (product decision 2026-07-25).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Wire dialect — request builder + response parser selection.
    /// Replaces the three duplicated <c>provider.StartsWith("anthropic")</c>
    /// branches.</summary>
    public required ProviderWireDialect Dialect { get; init; }

    /// <summary>Default base URL when no <c>{ConfigSection}:BaseUrl</c> /
    /// <c>LlmProviders:{key}:BaseUrl</c> config overrides it. Empty string =
    /// no meaningful default exists (e.g. Azure OpenAI's per-resource URL) and
    /// the provider must be configured before it can be called.</summary>
    public required string DefaultBaseUrl { get; init; }

    /// <summary>How the API key is presented on the wire.</summary>
    public required ProviderAuthScheme AuthScheme { get; init; }

    /// <summary>Default model when the caller / config supplies none. Empty
    /// string = caller must always specify a model (preserves the legacy
    /// behaviour for providers that never had a default).</summary>
    public required string DefaultModel { get; init; }

    /// <summary>Named <see cref="HttpClient"/> serving this provider on the
    /// <see cref="HttpProviderClient"/> dispatch path. Several descriptors may
    /// share one client (ollama / lmstudio / local-llm → <c>local</c>).</summary>
    public required string HttpClientName { get; init; }

    /// <summary>Configuration section carrying <c>BaseUrl</c> / <c>ApiKey</c>
    /// overrides for the named client (e.g. <c>OpenAI</c>, <c>ZAi</c>).</summary>
    public required string ConfigSection { get; init; }

    /// <summary>Chat endpoint path relative to the base URL. Null → the
    /// dialect default (<c>/v1/messages</c> or <c>/v1/chat/completions</c>).
    /// Z.ai is the current outlier (<c>/api/paas/v4/chat/completions</c>).</summary>
    public string? ChatEndpointPath { get; init; }

    /// <summary>API version header name, where the provider requires one
    /// (e.g. <c>anthropic-version</c>). Per-descriptor DATA, not a dialect
    /// constant — an AWS Bedrock descriptor would carry
    /// <c>anthropic_version: bedrock-2023-05-31</c> while speaking the same
    /// Anthropic dialect.</summary>
    public string? VersionHeaderName { get; init; }

    /// <summary>API version header value (paired with
    /// <see cref="VersionHeaderName"/>).</summary>
    public string? VersionHeaderValue { get; init; }

    /// <summary>Alternate spellings that resolve to this descriptor
    /// (e.g. <c>anthropic-claude</c> → anthropic, <c>z.ai</c>/<c>zai</c> →
    /// z-ai, <c>kimi</c> → moonshot). Aliases are lookup keys only; they are
    /// not required to appear in the allowlist.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Explicit classification of an allow-listed (or defensively rejected)
/// provider key that CANNOT be served over HTTP — CLI-subprocess agents and
/// MCP servers. Kept in the catalogue so the allowlist ↔ catalogue keyset
/// agreement is total: every provider key is either an HTTP descriptor or an
/// explicit non-HTTP classification, never silently unreachable.
/// </summary>
public sealed record NonHttpProviderDescriptor
{
    /// <summary>Canonical provider key.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Required transport (CLI subprocess or MCP).</summary>
    public required NonHttpProviderTransport Transport { get; init; }

    /// <summary>Whether this key is expected in
    /// <c>ProviderAllowlist.DefaultProviders</c>. <c>false</c> marks purely
    /// defensive rejection keys (the <c>claude-code</c> family) that the HTTP
    /// dispatch layer refuses with a transport-specific error but that are not
    /// selectable providers.</summary>
    public required bool Allowlisted { get; init; }

    /// <summary>Alternate spellings that resolve to this entry.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

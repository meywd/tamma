namespace Tamma.Api.Services.Security;

/// <summary>
/// Story 32-4 — the auth model a provider authenticates with. Drives SaaS
/// eligibility (only <see cref="ApiKey"/> providers are reachable in SaaS).
/// Mirrors the <c>Provider.AuthModel</c> string field (<c>api-key</c> |
/// <c>cli-token</c>) introduced by sibling story 34-11 (design §4.2).
/// </summary>
public enum ProviderAuthModel
{
    /// <summary>
    /// Cloud / local LLM API authenticated by an API key
    /// (<c>ILLMProvider</c> / <c>IAIProvider</c>, <c>type:'llm-api'</c>).
    /// SaaS-eligible — the key resolves from the principal's secret source
    /// (Story 32-3) and the call runs server-side inside the call-LLM
    /// endpoint.
    /// </summary>
    ApiKey,

    /// <summary>
    /// Headless CLI / token-based harness agent (<c>ICLIAgentProvider</c>,
    /// <c>type:'cli-agent'</c> — e.g. <c>claude-code</c>, <c>opencode</c>,
    /// <c>zen-mcp</c>). Spawns a local process and needs a host shell + local
    /// credential — NOT reachable in SaaS; single-user / self-hosted only
    /// (managed-LLM-execution deep-dive §1).
    /// </summary>
    CliToken,
}

/// <summary>
/// Story 32-4 — the single eligibility read seam the SaaS provider gate
/// consults. Returns the <see cref="ProviderAuthModel"/> for a provider key,
/// or <c>null</c> when the provider is unknown (which drives the fail-closed
/// DENY in SaaS — never a permissive allow, per
/// <c>feedback_resolution_no_empty_fallback</c>).
///
/// <para>The interim implementation (<see cref="StaticProviderAuthLookup"/>)
/// is keyed off <c>ProviderAllowlist.DefaultProviders</c>. Once 34-11's
/// <c>Provider</c> entity is the canonical source, an
/// <c>EntityProviderAuthLookup</c> backs this seam — swapping is a single DI
/// registration line; the <see cref="ISaaSProviderGate"/> contract is
/// unchanged.</para>
/// </summary>
public interface IProviderAuthLookup
{
    /// <summary>
    /// Resolve the <see cref="ProviderAuthModel"/> for a provider name.
    /// Case-insensitive and trimmed. Returns <c>null</c> for an unknown /
    /// blank provider (drives the SaaS fail-closed deny).
    /// </summary>
    Task<ProviderAuthModel?> AuthModelAsync(string? providerName, CancellationToken ct = default);
}

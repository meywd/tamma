using Tamma.Activities.Security;

namespace Tamma.Api.Services.Security;

/// <summary>
/// Story 32-4 — the interim (pre-34-11) <see cref="IProviderAuthLookup"/>,
/// keyed off the existing <c>ProviderAllowlist.DefaultProviders</c> set (the
/// single source of known providers — this class does NOT re-list providers).
///
/// <para>Classification: the harness providers (<c>ICLIAgentProvider</c>:
/// <c>claude-code</c>, <c>opencode</c>, <c>zen-mcp</c>) ⇒
/// <see cref="ProviderAuthModel.CliToken"/>; the complement within
/// <c>DefaultProviders</c> ⇒ <see cref="ProviderAuthModel.ApiKey"/>; an unknown
/// name ⇒ <c>null</c> (fail-closed in SaaS). Matching is case-insensitive and
/// trimmed.</para>
///
/// <para>Once 34-11 is the canonical source, an <c>EntityProviderAuthLookup</c>
/// reading <c>Provider.AuthModel</c> replaces this via a single DI registration
/// line — the <see cref="IProviderAuthLookup"/> / gate contracts are unchanged.</para>
/// </summary>
public sealed class StaticProviderAuthLookup : IProviderAuthLookup
{
    /// <summary>
    /// The harness (<c>ICLIAgentProvider</c>) providers. Verified against the
    /// TS <c>BUILTIN_PROVIDER_NAMES</c> / <c>agent-provider-factory.ts</c>
    /// CLI-agent registrations and the <c>ProviderPricingService.AuthModels</c>
    /// map (which classifies only <c>claude-code</c> as <c>cli-token</c>). The
    /// <c>ProviderAllowlist</c> additionally carries <c>opencode</c> and
    /// <c>zen-mcp</c>, which are also CLI-agent harnesses. Everything else in
    /// <c>DefaultProviders</c> is an api-key LLM API.
    /// </summary>
    private static readonly HashSet<string> CliTokenProviders =
        new(StringComparer.OrdinalIgnoreCase) { "claude-code", "opencode", "zen-mcp" };

    public Task<ProviderAuthModel?> AuthModelAsync(
        string? providerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return Task.FromResult<ProviderAuthModel?>(null);

        var name = providerName.Trim();

        // A known CLI harness provider classifies as cli-token. We check this
        // set FIRST because ProviderAllowlist.DefaultProviders omits the
        // flagship `claude-code` harness (it carries `opencode`/`zen-mcp` only),
        // so relying on the allowlist alone would mis-classify `claude-code` as
        // unknown — exactly the silent-mis-classification this seam must avoid.
        if (CliTokenProviders.Contains(name))
            return Task.FromResult<ProviderAuthModel?>(ProviderAuthModel.CliToken);

        // Otherwise it's api-key only if it's a known allowlisted provider;
        // anything else is unknown ⇒ null ⇒ fail-closed deny in SaaS.
        if (!ProviderAllowlist.IsAllowedDefault(name))
            return Task.FromResult<ProviderAuthModel?>(null);

        return Task.FromResult<ProviderAuthModel?>(ProviderAuthModel.ApiKey);
    }
}

using Tamma.Activities.Security;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// 2026-08-13 — the pure provider-chain resolution behind
/// <c>LlmCallWorkflow.ResolveChain</c>, extracted so it is unit-testable
/// without an Elsa execution context.
///
/// <para><b>Precedence</b> (first non-empty wins): caller-supplied chain →
/// DB agent-config chain → <c>Llm:DefaultProviderChain</c> configuration →
/// the hardcoded default (<c>anthropic, openai, openrouter</c>).</para>
///
/// <para><b>Allowlist.</b> The chain is filtered through the DI-configured
/// <see cref="ProviderAllowlist"/> (defaults + <c>Security:ProviderAllowlist:
/// AdditionalProviders</c>) — previously this filter ran against the STATIC
/// default instance, which silently ignored the AdditionalProviders config the
/// rejection message itself told operators to use. The config-registered
/// allowlist is what makes the opt-in "scripted" provider selectable in the
/// engine-driven E2E (and any self-hosted custom provider selectable at all).
/// An all-rejected chain still fails loud, naming the config key.</para>
/// </summary>
public static class LlmProviderChainHelper
{
    /// <summary>Config key for the deployment-default provider chain.</summary>
    public const string DefaultChainConfigKey = "Llm:DefaultProviderChain";

    /// <summary>The legacy hardcoded default chain (unchanged).</summary>
    public static readonly IReadOnlyList<string> HardcodedDefaultChain =
        new[] { "anthropic", "openai", "openrouter" };

    /// <summary>Resolve + filter the chain. Throws on an all-rejected chain.</summary>
    public static List<string> Resolve(
        IReadOnlyList<string>? callerChain,
        IReadOnlyList<string>? dbChain,
        IReadOnlyList<string>? configChain,
        ProviderAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);

        var chain =
            FirstNonEmpty(callerChain)
            ?? FirstNonEmpty(dbChain)
            ?? FirstNonEmpty(configChain)
            ?? HardcodedDefaultChain;

        var filtered = allowlist.FilterAllowed(chain);
        if (filtered.Count == 0)
        {
            // All providers rejected — fail with a clear error, never a silent fallback.
            throw new InvalidOperationException(
                $"All providers in chain were rejected by allowlist: [{string.Join(", ", chain)}]. " +
                "Configure allowed providers via Security:ProviderAllowlist:AdditionalProviders.");
        }

        return filtered;
    }

    private static IReadOnlyList<string>? FirstNonEmpty(IReadOnlyList<string>? chain)
    {
        if (chain is null)
        {
            return null;
        }

        var cleaned = chain
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
        return cleaned.Count > 0 ? cleaned : null;
    }
}

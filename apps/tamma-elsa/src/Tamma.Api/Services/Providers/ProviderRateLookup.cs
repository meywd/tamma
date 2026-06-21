using System.Collections.Frozen;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 34-11 — the SINGLE source of the provider-cost lookup algorithm,
/// shared by both <see cref="ProviderPricingService"/> (the frozen seed source /
/// boot fallback) and <see cref="DbProviderPricingService"/> (the DB-backed
/// impl). Holds the load-bearing quirks the frozen table carried, ported
/// VERBATIM so the two impls cannot drift (AC6 / the parity test pins this):
/// <list type="bullet">
///   <item>the <c>s_aliases</c> provider alias map (anthropic-claude/claude →
///     anthropic, gemini → google, github-copilot → openai, ollama/lmstudio →
///     local);</item>
///   <item><c>null</c>/<c>"default"</c> → the provider's first model;</item>
///   <item>exact match then loose prefix match (a request for
///     <c>claude-sonnet-4</c> matches a stored <c>claude-sonnet-4-20250514</c>);</item>
///   <item>unknown <c>(provider, model)</c> resolves to nothing — the caller
///     returns <c>0m</c> / <c>false</c>, never throws.</item>
/// </list>
/// </summary>
public static class ProviderRateLookup
{
    /// <summary>A per-token cost pair. USD per single token (not per 1M).</summary>
    public readonly record struct Rate(decimal InputPerToken, decimal OutputPerToken);

    /// <summary>
    /// Provider alias normalisation. Maps every accepted handle to the canonical
    /// provider key. Unrecognised keys pass through untouched so explicit table
    /// additions still resolve. (Ported verbatim from <c>ProviderPricingService.s_aliases</c>.)
    /// </summary>
    public static readonly FrozenDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = "anthropic",
            ["anthropic-claude"] = "anthropic",
            ["claude"] = "anthropic",
            ["claude-code"] = "claude-code",
            ["openai"] = "openai",
            ["github-copilot"] = "openai",      // Copilot is an OpenAI front-end
            ["gemini"] = "google",
            ["google"] = "google",
            ["openrouter"] = "openrouter",
            ["local"] = "local",
            ["ollama"] = "local",
            ["lmstudio"] = "local",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalise a (possibly aliased / mixed-case) provider key to its canonical
    /// form. Unknown keys pass through untouched. Used BOTH on the lookup path
    /// and on the write path (so <see cref="ProviderModelPrice.ProviderKey"/> is
    /// always stored canonical — AC6).
    /// </summary>
    public static string Canonicalize(string provider) =>
        provider is not null && Aliases.TryGetValue(provider, out var aliased)
            ? aliased
            : provider!;

    /// <summary>
    /// Resolve a rate from an arbitrary per-model table. The
    /// <paramref name="modelMap"/> is the resolved provider's models (key =
    /// model id, ordered the same way the table was built so the
    /// first-model / prefix rules are deterministic).
    /// </summary>
    /// <returns><c>true</c> + <paramref name="rate"/> on a hit; <c>false</c> otherwise.</returns>
    public static bool TryGetRate(
        string provider,
        string? model,
        IReadOnlyDictionary<string, Rate> modelMap,
        out Rate rate)
    {
        rate = default;
        if (modelMap is null || modelMap.Count == 0) return false;

        // Resolve "default" or null to the provider's first model.
        var lookupModel = string.IsNullOrWhiteSpace(model) || model == "default"
            ? modelMap.Keys.FirstOrDefault()
            : model;
        if (lookupModel is null) return false;

        if (modelMap.TryGetValue(lookupModel, out rate)) return true;

        // Loose match: if caller passed e.g. "claude-sonnet-4" and the table
        // has "claude-sonnet-4-20250514", match on the prefix. Picks the first
        // entry whose key starts with the requested model id.
        foreach (var (key, value) in modelMap)
        {
            if (key.StartsWith(lookupModel, StringComparison.OrdinalIgnoreCase))
            {
                rate = value;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Compute the USD cost for a single invocation given the resolved
    /// <paramref name="rate"/>. Negative token counts are clamped to zero — no
    /// upstream protocol legitimately reports negative usage but a malformed
    /// response must not flip the cost negative. (Ported verbatim.)
    /// </summary>
    public static decimal Cost(Rate rate, int inputTokens, int outputTokens)
    {
        var inTok = inputTokens < 0 ? 0 : inputTokens;
        var outTok = outputTokens < 0 ? 0 : outputTokens;
        return (rate.InputPerToken * inTok) + (rate.OutputPerToken * outTok);
    }
}

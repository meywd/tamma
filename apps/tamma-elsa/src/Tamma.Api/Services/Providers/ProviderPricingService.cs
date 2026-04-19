using System.Collections.Frozen;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Frozen-table implementation of <see cref="IProviderPricingService"/>.
///
/// <para>
/// Pricing data ported verbatim from
/// <c>packages/cost-monitor/src/pricing-config.ts</c> at commit
/// <c>9e9a57c~1</c>. Rates are quoted as USD-per-token (the TS
/// <c>inputPer1MTokens</c> values divided by 1,000,000).
/// </para>
///
/// <para>
/// Lookup is case-insensitive on both provider key and model id. Provider
/// keys are also normalised against a small alias map so callers using the
/// older <c>anthropic-claude</c> handle still resolve to the same Anthropic
/// rate sheet.
/// </para>
/// </summary>
public sealed class ProviderPricingService : IProviderPricingService
{
    private readonly record struct Rate(decimal InputPerToken, decimal OutputPerToken);

    /// <summary>
    /// Provider alias normalisation. Maps every accepted handle to the
    /// canonical provider key used in <see cref="s_pricing"/>. Unrecognised
    /// keys are passed through untouched so explicit additions to the table
    /// still resolve.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_aliases =
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

    /// <summary>Per-(provider, model) USD-per-token rates.</summary>
    private static readonly FrozenDictionary<string, FrozenDictionary<string, Rate>> s_pricing =
        BuildTable();

    public decimal Compute(string provider, string? model, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrWhiteSpace(provider)) return 0m;
        if (!TryGetRate(provider, model, out var rate)) return 0m;

        var inTok = inputTokens < 0 ? 0 : inputTokens;
        var outTok = outputTokens < 0 ? 0 : outputTokens;

        return (rate.InputPerToken * inTok) + (rate.OutputPerToken * outTok);
    }

    public bool IsKnown(string provider, string? model)
        => TryGetRate(provider, model, out _);

    private static bool TryGetRate(string provider, string? model, out Rate rate)
    {
        rate = default;
        var canonical = s_aliases.TryGetValue(provider, out var aliased) ? aliased : provider;
        if (!s_pricing.TryGetValue(canonical, out var modelMap)) return false;

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

    private static FrozenDictionary<string, FrozenDictionary<string, Rate>> BuildTable()
    {
        // Helper: USD-per-1M tokens → USD-per-token.
        static decimal Per(double per1M) => (decimal)(per1M / 1_000_000.0);

        var anthropic = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-4-20250514"] = new(Per(3.00), Per(15.00)),
            ["claude-opus-4-20250514"] = new(Per(15.00), Per(75.00)),
            ["claude-3-5-sonnet-20241022"] = new(Per(3.00), Per(15.00)),
            ["claude-3-5-haiku-20241022"] = new(Per(0.80), Per(4.00)),
            ["claude-3-opus-20240229"] = new(Per(15.00), Per(75.00)),
            ["claude-3-sonnet-20240229"] = new(Per(3.00), Per(15.00)),
            ["claude-3-haiku-20240307"] = new(Per(0.25), Per(1.25)),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        var openai = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o"] = new(Per(2.50), Per(10.00)),
            ["gpt-4o-mini"] = new(Per(0.15), Per(0.60)),
            ["gpt-4-turbo"] = new(Per(10.00), Per(30.00)),
            ["gpt-4"] = new(Per(30.00), Per(60.00)),
            ["gpt-3.5-turbo"] = new(Per(0.50), Per(1.50)),
            ["o1-preview"] = new(Per(15.00), Per(60.00)),
            ["o1-mini"] = new(Per(3.00), Per(12.00)),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        var google = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-1.5-pro"] = new(Per(1.25), Per(5.00)),
            ["gemini-1.5-flash"] = new(Per(0.075), Per(0.30)),
            ["gemini-2.0-flash"] = new(Per(0.10), Per(0.40)),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // OpenRouter shares model identifiers with vendor/model paths.
        var openrouter = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic/claude-3.5-sonnet"] = new(Per(3.00), Per(15.00)),
            ["anthropic/claude-3-opus"] = new(Per(15.00), Per(75.00)),
            ["anthropic/claude-3-haiku"] = new(Per(0.25), Per(1.25)),
            ["openai/gpt-4o"] = new(Per(2.50), Per(10.00)),
            ["openai/gpt-4o-mini"] = new(Per(0.15), Per(0.60)),
            ["meta-llama/llama-3.1-405b-instruct"] = new(Per(2.70), Per(2.70)),
            ["meta-llama/llama-3.1-70b-instruct"] = new(Per(0.52), Per(0.75)),
            ["meta-llama/llama-3.1-8b-instruct"] = new(Per(0.055), Per(0.055)),
            ["mistralai/mistral-large"] = new(Per(2.00), Per(6.00)),
            ["mistralai/mixtral-8x7b-instruct"] = new(Per(0.24), Per(0.24)),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // claude-code uses Anthropic pricing.
        var claudeCode = anthropic;

        // Local models bill at zero.
        var local = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = new(0m, 0m),
            ["default"] = new(0m, 0m),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, FrozenDictionary<string, Rate>>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = anthropic,
            ["openai"] = openai,
            ["google"] = google,
            ["openrouter"] = openrouter,
            ["claude-code"] = claudeCode,
            ["local"] = local,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}

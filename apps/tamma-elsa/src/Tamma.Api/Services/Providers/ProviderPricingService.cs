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
/// Story 34-11 — this class is RETAINED as the deterministic SEED SOURCE
/// (consumed by <c>ProviderPricingSeeder</c>) and the boot fallback when the
/// DB cost table is empty. The registered runtime impl is now
/// <see cref="DbProviderPricingService"/>; both share the
/// <see cref="ProviderRateLookup"/> alias-map + lookup algorithm so they
/// cannot drift (the parity test pins byte-identical <c>Compute</c> output).
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
    /// <summary>Per-(provider, model) USD-per-token rates. Exposed to the seeder (Story 34-11).</summary>
    public static readonly FrozenDictionary<string, FrozenDictionary<string, ProviderRateLookup.Rate>> Pricing =
        BuildTable();

    /// <summary>
    /// Story 34-11 — the AuthModel each seeded provider carries (feeds 32-4
    /// SaaS-eligibility). <c>claude-code</c> is the CLI harness (<c>cli-token</c>);
    /// every other provider authenticates with an API key.
    /// </summary>
    public static readonly FrozenDictionary<string, string> AuthModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = "api-key",
            ["openai"] = "api-key",
            ["google"] = "api-key",
            ["openrouter"] = "api-key",
            ["local"] = "api-key",
            ["claude-code"] = "cli-token",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Display names for the seeded providers (Story 34-11).</summary>
    public static readonly FrozenDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = "Anthropic",
            ["openai"] = "OpenAI",
            ["google"] = "Google",
            ["openrouter"] = "OpenRouter",
            ["local"] = "Local",
            ["claude-code"] = "Claude Code",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public decimal Compute(string provider, string? model, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrWhiteSpace(provider)) return 0m;
        if (!TryGetRate(provider, model, out var rate)) return 0m;
        return ProviderRateLookup.Cost(rate, inputTokens, outputTokens);
    }

    public bool IsKnown(string provider, string? model)
        => TryGetRate(provider, model, out _);

    private static bool TryGetRate(string provider, string? model, out ProviderRateLookup.Rate rate)
    {
        rate = default;
        var canonical = ProviderRateLookup.Canonicalize(provider);
        if (!Pricing.TryGetValue(canonical, out var modelMap)) return false;
        return ProviderRateLookup.TryGetRate(provider, model, modelMap, out rate);
    }

    private static FrozenDictionary<string, FrozenDictionary<string, ProviderRateLookup.Rate>> BuildTable()
    {
        // Helper: USD-per-1M tokens → USD-per-token.
        static ProviderRateLookup.Rate R(double in1M, double out1M) =>
            new((decimal)(in1M / 1_000_000.0), (decimal)(out1M / 1_000_000.0));

        var anthropic = new Dictionary<string, ProviderRateLookup.Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-4-20250514"] = R(3.00, 15.00),
            ["claude-opus-4-20250514"] = R(15.00, 75.00),
            ["claude-3-5-sonnet-20241022"] = R(3.00, 15.00),
            ["claude-3-5-haiku-20241022"] = R(0.80, 4.00),
            ["claude-3-opus-20240229"] = R(15.00, 75.00),
            ["claude-3-sonnet-20240229"] = R(3.00, 15.00),
            ["claude-3-haiku-20240307"] = R(0.25, 1.25),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        var openai = new Dictionary<string, ProviderRateLookup.Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o"] = R(2.50, 10.00),
            ["gpt-4o-mini"] = R(0.15, 0.60),
            ["gpt-4-turbo"] = R(10.00, 30.00),
            ["gpt-4"] = R(30.00, 60.00),
            ["gpt-3.5-turbo"] = R(0.50, 1.50),
            ["o1-preview"] = R(15.00, 60.00),
            ["o1-mini"] = R(3.00, 12.00),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        var google = new Dictionary<string, ProviderRateLookup.Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-1.5-pro"] = R(1.25, 5.00),
            ["gemini-1.5-flash"] = R(0.075, 0.30),
            ["gemini-2.0-flash"] = R(0.10, 0.40),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // OpenRouter shares model identifiers with vendor/model paths.
        var openrouter = new Dictionary<string, ProviderRateLookup.Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic/claude-3.5-sonnet"] = R(3.00, 15.00),
            ["anthropic/claude-3-opus"] = R(15.00, 75.00),
            ["anthropic/claude-3-haiku"] = R(0.25, 1.25),
            ["openai/gpt-4o"] = R(2.50, 10.00),
            ["openai/gpt-4o-mini"] = R(0.15, 0.60),
            ["meta-llama/llama-3.1-405b-instruct"] = R(2.70, 2.70),
            ["meta-llama/llama-3.1-70b-instruct"] = R(0.52, 0.75),
            ["meta-llama/llama-3.1-8b-instruct"] = R(0.055, 0.055),
            ["mistralai/mistral-large"] = R(2.00, 6.00),
            ["mistralai/mixtral-8x7b-instruct"] = R(0.24, 0.24),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // claude-code uses Anthropic pricing.
        var claudeCode = anthropic;

        // Local models bill at zero.
        var local = new Dictionary<string, ProviderRateLookup.Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = new(0m, 0m),
            ["default"] = new(0m, 0m),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, FrozenDictionary<string, ProviderRateLookup.Rate>>(StringComparer.OrdinalIgnoreCase)
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

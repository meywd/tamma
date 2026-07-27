using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamma.Activities.Security;

/// <summary>
/// Validates provider names against a known allowlist of supported LLM providers.
/// Prevents injection of malicious provider names that could redirect LLM calls.
/// Thread-safe and case-insensitive.
/// </summary>
public class ProviderAllowlist
{
    private readonly HashSet<string> _allowedProviders;
    private readonly ILogger<ProviderAllowlist>? _logger;

    /// <summary>
    /// Built-in known providers. Matches the providers supported by the platform.
    /// This set is kept in EXACT keyset agreement with the provider catalogue in
    /// <c>Tamma.Api.Services.Providers.ProviderCatalog</c> (HTTP descriptors +
    /// allow-listed non-HTTP classifications) — enforced by
    /// <c>ProviderCatalogTests</c> in Tamma.Activities.Tests, so the two
    /// surfaces can never drift apart again (they disagreed by seven entries
    /// before Phase 1 of the provider abstraction; see
    /// .dev/findings/provider-abstraction-and-openai-compatible-candidates.md).
    /// </summary>
    private static readonly HashSet<string> DefaultProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "anthropic",
        "openai",
        "openrouter",
        "google",
        "github-copilot",
        "local-llm",
        "opencode",
        "z-ai",           // Zhipu GLM — Z.ai is Zhipu's international brand
        "zen-mcp",
        "azure-openai",
        "gemini",
        "ollama",
        "lmstudio",
        "together",
        "groq",
        "deepseek",       // OpenAI-compatible candidate (finding, 2026-07-25)
        "moonshot"        // Moonshot AI / Kimi — OpenAI-compatible candidate
    };

    private static readonly ProviderAllowlist DefaultInstance = new();

    public ProviderAllowlist(
        IOptions<ProviderAllowlistOptions>? options = null,
        ILogger<ProviderAllowlist>? logger = null)
    {
        _logger = logger;
        _allowedProviders = new HashSet<string>(DefaultProviders, StringComparer.OrdinalIgnoreCase);

        var additionalCount = 0;
        if (options?.Value.AdditionalProviders != null)
        {
            foreach (var provider in options.Value.AdditionalProviders)
            {
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    _allowedProviders.Add(provider.Trim());
                    additionalCount++;
                }
            }
        }

        _logger?.LogInformation(
            "Allowlist configuration loaded: DefaultProviderCount={DefaultProviderCount}, AdditionalProviderCount={AdditionalProviderCount}, TotalProviders={TotalProviders}",
            DefaultProviders.Count, additionalCount, _allowedProviders.Count);
    }

    /// <summary>
    /// Check if a provider name is in the allowlist.
    /// Case-insensitive comparison.
    /// </summary>
    /// <param name="providerName">Provider name to check.</param>
    /// <returns>true if the provider is allowed; false otherwise.</returns>
    public bool IsAllowed(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return false;

        var allowed = _allowedProviders.Contains(providerName.Trim());
        if (allowed)
        {
            _logger?.LogDebug("Provider accepted by allowlist: ProviderName={ProviderName}", providerName);
        }
        else
        {
            _logger?.LogWarning("Provider rejected by allowlist: ProviderName={ProviderName}", providerName);
        }

        return allowed;
    }

    /// <summary>
    /// Filter a list of provider names, returning only those in the allowlist.
    /// Preserves original order.
    /// </summary>
    public List<string> FilterAllowed(IEnumerable<string> providerNames)
    {
        var result = new List<string>();
        foreach (var name in providerNames)
        {
            if (IsAllowed(name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    /// <summary>
    /// Get all allowed provider names (for diagnostics).
    /// </summary>
    public IReadOnlySet<string> GetAllowedProviders() => _allowedProviders;

    /// <summary>
    /// Static convenience method for contexts without DI.
    /// Uses default allowlist (no additional providers from config).
    /// </summary>
    public static bool IsAllowedDefault(string? providerName)
    {
        return DefaultInstance.IsAllowed(providerName);
    }

    /// <summary>
    /// Static convenience method: filter a chain using the default allowlist.
    /// </summary>
    public static List<string> FilterAllowedDefault(IEnumerable<string> providerNames)
    {
        return DefaultInstance.FilterAllowed(providerNames);
    }
}

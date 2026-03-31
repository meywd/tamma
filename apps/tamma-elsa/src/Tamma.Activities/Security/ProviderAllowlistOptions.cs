namespace Tamma.Activities.Security;

/// <summary>
/// Configuration options for the provider name allowlist.
/// Bound from "Security:ProviderAllowlist" config section.
/// </summary>
public class ProviderAllowlistOptions
{
    /// <summary>
    /// Additional provider names to allow beyond the built-in defaults.
    /// For self-hosted or custom LLM providers.
    /// Example config: Security:ProviderAllowlist:AdditionalProviders:0 = "my-custom-llm"
    /// </summary>
    public List<string> AdditionalProviders { get; set; } = new();
}

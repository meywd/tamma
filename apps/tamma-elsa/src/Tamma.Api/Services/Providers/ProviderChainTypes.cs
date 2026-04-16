namespace Tamma.Api.Services.Providers;

/// <summary>A single provider in a resolution chain.</summary>
/// <param name="Provider">Provider family identifier (e.g. <c>anthropic</c>, <c>openai</c>).</param>
/// <param name="Model">Optional model name (e.g. <c>claude-sonnet-4</c>). Combined with <see cref="Provider"/> to form the health key.</param>
/// <param name="Priority">Lower priority = earlier in the chain. Stable ordering.</param>
public sealed record ProviderHandle(string Provider, string? Model = null, int Priority = 0)
{
    /// <summary>Canonical health-tracker key: <c>provider[:model]</c>.</summary>
    public string Key => string.IsNullOrEmpty(Model) ? Provider : $"{Provider}:{Model}";
}

/// <summary>Why a provider was included or excluded from a resolved chain.</summary>
public enum ChainReason
{
    /// <summary>Provider is healthy (Closed state) — preferred.</summary>
    Healthy,

    /// <summary>Circuit is HalfOpen — included as last-resort probe candidate.</summary>
    HalfOpenProbeCandidate,

    /// <summary>Circuit is Open — excluded.</summary>
    CircuitOpen,

    /// <summary>Provider has no recorded state — treated as Healthy.</summary>
    Unknown,
}

/// <summary>Represents a single provider entry in the resolved chain with its reason.</summary>
public sealed record ChainEntry(ProviderHandle Provider, ChainReason Reason);

/// <summary>Result of calling <see cref="IProviderChainResolver.ResolveAsync"/>.</summary>
public sealed record ChainResolveResult(
    IReadOnlyList<ChainEntry> Ordered,
    IReadOnlyList<ChainEntry> Skipped,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    /// <summary>True when at least one provider is available to try.</summary>
    public bool HasCandidates => Ordered.Count > 0;
}

/// <summary>Orchestrates provider selection given tenant config and current circuit state.</summary>
public interface IProviderChainResolver
{
    /// <summary>
    /// Resolve the provider chain for the given <paramref name="role"/> and <paramref name="action"/>.
    /// Pulls the primary + fallback list from <c>agent_configs.config</c> JSON for <paramref name="tenantId"/>,
    /// skips providers whose circuit is Open, and returns HalfOpen providers at the tail as probes.
    /// </summary>
    Task<ChainResolveResult> ResolveAsync(Guid? tenantId, string role, string action, CancellationToken ct = default);
}

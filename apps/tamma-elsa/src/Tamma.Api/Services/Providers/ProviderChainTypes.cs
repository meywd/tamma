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

/// <summary>
/// Represents a single provider entry in the resolved chain with its reason and
/// resolved per-entry status (health / circuit / budget). <see cref="Healthy"/>
/// is true iff the provider is in the Closed (or Unknown) state. <see cref="CircuitOpen"/>
/// is true iff the provider is currently Open. <see cref="BudgetAllowed"/> mirrors
/// the account budget at resolve time (Story 9-5). <see cref="Recommended"/> marks
/// the first entry that is both healthy and within budget.
/// </summary>
public sealed record ChainEntry(
    ProviderHandle Provider,
    ChainReason Reason,
    bool Healthy = true,
    bool CircuitOpen = false,
    DateTimeOffset? CircuitOpenUntil = null,
    bool BudgetAllowed = true,
    decimal BudgetSpent = 0m,
    bool Recommended = false);

/// <summary>Result of calling <see cref="IProviderChainResolver.ResolveAsync"/>.</summary>
/// <param name="Ordered">Providers ordered preferred-first, half-open at tail.</param>
/// <param name="Skipped">Providers excluded from the ordered list with reason.</param>
/// <param name="RecommendedProvider">
/// The first <see cref="Ordered"/> entry that is healthy and within budget, or
/// <c>null</c> when no entry meets both criteria.
/// </param>
/// <param name="AllExhausted">
/// True when every configured provider was either circuit-open or over budget
/// (i.e. <see cref="RecommendedProvider"/> is <c>null</c>).
/// </param>
public sealed record ChainResolveResult(
    IReadOnlyList<ChainEntry> Ordered,
    IReadOnlyList<ChainEntry> Skipped,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? RecommendedProvider = null,
    bool AllExhausted = false)
{
    /// <summary>True when at least one provider is available to try.</summary>
    public bool HasCandidates => Ordered.Count > 0;
}

/// <summary>
/// Optional additional context for <see cref="IProviderChainResolver.ResolveAsync"/>.
/// Story 9-5 — lets callers pin budget evaluation to a specific account
/// (defaults to the tenant) without leaking the JWT shape into this layer.
/// </summary>
/// <param name="AccountId">
/// Account whose budget should be checked. When <c>null</c> the resolver uses
/// the <c>tenantId</c> argument; when both are <c>null</c> no budget filtering
/// is applied (treated as unlimited).
/// </param>
public sealed record ChainResolveOptions(Guid? AccountId = null);

/// <summary>Orchestrates provider selection given tenant config and current circuit state.</summary>
public interface IProviderChainResolver
{
    /// <summary>
    /// Resolve the provider chain for the given <paramref name="role"/> and <paramref name="action"/>.
    /// Pulls the primary + fallback list from <c>agent_configs.config</c> JSON for <paramref name="tenantId"/>,
    /// skips providers whose circuit is Open, and returns HalfOpen providers at the tail as probes.
    /// </summary>
    Task<ChainResolveResult> ResolveAsync(Guid? tenantId, string role, string action, CancellationToken ct = default);

    /// <summary>
    /// Story 9-5 overload that additionally checks per-account budget via
    /// <see cref="Diagnostics.IDiagnosticsService.GetBudgetAsync"/> and
    /// computes the <c>recommendedProvider</c> / <c>allExhausted</c> flags.
    /// </summary>
    Task<ChainResolveResult> ResolveAsync(
        Guid? tenantId,
        string role,
        string action,
        ChainResolveOptions options,
        CancellationToken ct = default);
}

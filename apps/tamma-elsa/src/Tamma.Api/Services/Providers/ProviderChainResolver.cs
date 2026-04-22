using System.Text.Json;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Resolves the ordered list of <see cref="ProviderHandle"/>s to try for a
/// given tenant, role, and action. Reads the primary + fallback chain out of
/// <c>agent_configs.config</c> (JSONB) and consults
/// <see cref="ICircuitBreakerService"/> to exclude Open providers and append
/// HalfOpen providers at the tail as probe candidates.
///
/// <para>
/// Expected JSON shape:
/// <code>
/// {
///   "chains": {
///     "default": [{"provider":"anthropic","model":"claude-sonnet-4"}, ...],
///     "developer": {
///       "code_generation": [{"provider":"openai","model":"gpt-4o"}, ...]
///     }
///   }
/// }
/// </code>
/// Lookup order: <c>chains[role][action]</c> → <c>chains[role]["default"]</c> →
/// <c>chains["default"]</c>.
/// </para>
///
/// <para>
/// Story 9-5 adds per-account budget filtering via the optional
/// <see cref="IDiagnosticsService"/> dependency. When an account is over
/// budget every entry is marked <c>BudgetAllowed=false</c> and
/// <c>RecommendedProvider</c> falls through to <c>null</c>, signalling
/// callers to fail closed.
/// </para>
/// </summary>
public sealed class ProviderChainResolver : IProviderChainResolver
{
    private readonly IAgentConfigRepository _configRepo;
    private readonly ICircuitBreakerService _breaker;
    private readonly IDiagnosticsService? _diagnostics;

    public ProviderChainResolver(IAgentConfigRepository configRepo, ICircuitBreakerService breaker)
        : this(configRepo, breaker, diagnostics: null)
    {
    }

    public ProviderChainResolver(
        IAgentConfigRepository configRepo,
        ICircuitBreakerService breaker,
        IDiagnosticsService? diagnostics)
    {
        _configRepo = configRepo ?? throw new ArgumentNullException(nameof(configRepo));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _diagnostics = diagnostics;
    }

    public Task<ChainResolveResult> ResolveAsync(
        Guid? tenantId,
        string role,
        string action,
        CancellationToken ct = default) =>
        ResolveAsync(tenantId, role, action, new ChainResolveOptions(), ct);

    public async Task<ChainResolveResult> ResolveAsync(
        Guid? tenantId,
        string role,
        string action,
        ChainResolveOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("Role must not be empty.", nameof(role));
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Action must not be empty.", nameof(action));

        var configured = await LoadChainAsync(tenantId, role, action);
        if (configured.Count == 0)
        {
            return new ChainResolveResult(
                Array.Empty<ChainEntry>(),
                Array.Empty<ChainEntry>(),
                ErrorCode: "EMPTY_PROVIDER_CHAIN",
                ErrorMessage: $"No provider chain configured for role='{role}' action='{action}'.",
                RecommendedProvider: null,
                AllExhausted: true);
        }

        // ── Account budget snapshot (single call, account-level) ─────────────
        // Story 9-5: budget is per-account, not per-provider. Resolve once and
        // apply to every entry. AccountId override > tenantId > skip entirely.
        var budgetAccount = options.AccountId ?? tenantId;
        BudgetStatus? budget = null;
        if (budgetAccount.HasValue && _diagnostics is not null)
        {
            try
            {
                budget = await _diagnostics.GetBudgetAsync(budgetAccount.Value, ct);
            }
            catch
            {
                // Fail-open on budget service errors — do not strand the
                // entire chain on a transient diagnostics outage. Per-entry
                // BudgetAllowed will default to true.
                budget = null;
            }
        }

        var budgetAllowed = budget is null || !budget.IsOverBudget;
        var budgetSpent = budget?.Spent ?? 0m;

        var healthyOrUnknown = new List<ChainEntry>();
        var halfOpenTail = new List<ChainEntry>();
        var skipped = new List<ChainEntry>();

        foreach (var handle in configured)
        {
            var status = await _breaker.GetStateAsync(handle.Key, tenantId, ct);
            switch (status.State)
            {
                case CircuitBreakerState.Closed:
                    // No existing row ≡ Unknown; existing row with Closed state ≡ Healthy.
                    // Distinguish by presence of any recorded activity (success or failure).
                    var reason = (status.LastSuccess is null && status.LastFailure is null)
                        ? ChainReason.Unknown
                        : ChainReason.Healthy;
                    healthyOrUnknown.Add(new ChainEntry(
                        handle,
                        reason,
                        Healthy: true,
                        CircuitOpen: false,
                        CircuitOpenUntil: null,
                        BudgetAllowed: budgetAllowed,
                        BudgetSpent: budgetSpent,
                        Recommended: false));
                    break;
                case CircuitBreakerState.HalfOpen:
                    halfOpenTail.Add(new ChainEntry(
                        handle,
                        ChainReason.HalfOpenProbeCandidate,
                        Healthy: false,
                        CircuitOpen: false,
                        CircuitOpenUntil: status.CircuitOpenUntil,
                        BudgetAllowed: budgetAllowed,
                        BudgetSpent: budgetSpent,
                        Recommended: false));
                    break;
                case CircuitBreakerState.Open:
                    skipped.Add(new ChainEntry(
                        handle,
                        ChainReason.CircuitOpen,
                        Healthy: false,
                        CircuitOpen: true,
                        CircuitOpenUntil: status.CircuitOpenUntil,
                        BudgetAllowed: budgetAllowed,
                        BudgetSpent: budgetSpent,
                        Recommended: false));
                    break;
            }
        }

        // Healthy / Unknown providers come first (stable input order); HalfOpen probes
        // appear at the tail so a Closed provider is always preferred over a probe.
        var ordered = new List<ChainEntry>(healthyOrUnknown.Count + halfOpenTail.Count);
        ordered.AddRange(healthyOrUnknown);
        ordered.AddRange(halfOpenTail);

        if (ordered.Count == 0)
        {
            return new ChainResolveResult(
                Array.Empty<ChainEntry>(),
                skipped,
                ErrorCode: "NO_AVAILABLE_PROVIDER",
                ErrorMessage: "All providers in the chain are circuit-open.",
                RecommendedProvider: null,
                AllExhausted: true);
        }

        // ── Recommendation pass ──────────────────────────────────────────────
        // Recommended = first ordered entry that is healthy AND within budget.
        // We mark exactly one entry; downstream callers can short-circuit on
        // RecommendedProvider when present. Half-open entries qualify because
        // they are still selectable probes (the CB layer gates concurrency).
        string? recommendedProvider = null;
        var orderedWithRecommendation = new List<ChainEntry>(ordered.Count);
        foreach (var entry in ordered)
        {
            // Treat Healthy as: not Open. Half-open is a permitted probe.
            var entryHealthy = entry.Reason != ChainReason.CircuitOpen;
            var isRecommended =
                recommendedProvider is null && entryHealthy && entry.BudgetAllowed;
            if (isRecommended)
            {
                recommendedProvider = entry.Provider.Provider;
            }
            orderedWithRecommendation.Add(entry with { Recommended = isRecommended });
        }

        return new ChainResolveResult(
            orderedWithRecommendation,
            skipped,
            ErrorCode: null,
            ErrorMessage: null,
            RecommendedProvider: recommendedProvider,
            AllExhausted: recommendedProvider is null);
    }

    // ── config parsing ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ProviderHandle>> LoadChainAsync(
        Guid? tenantId, string role, string action)
    {
        var configJson = tenantId.HasValue
            ? (await _configRepo.ResolveAsync(tenantId.Value)).Config.Config
            : (await _configRepo.GetAsync(null))?.Config ?? "{}";

        if (string.IsNullOrWhiteSpace(configJson) || configJson == "{}")
            return Array.Empty<ProviderHandle>();

        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;

        // Preferred (C#) shape: chains[role][action] → chains[role][default] →
        // chains[role] (as array) → chains[default].
        if (root.TryGetProperty("chains", out var chains) &&
            chains.ValueKind == JsonValueKind.Object)
        {
            if (chains.TryGetProperty(role, out var roleNode) && roleNode.ValueKind == JsonValueKind.Object)
            {
                if (roleNode.TryGetProperty(action, out var roleActionArr) &&
                    roleActionArr.ValueKind == JsonValueKind.Array)
                {
                    return ParseHandles(roleActionArr);
                }
                if (roleNode.TryGetProperty("default", out var roleDefault) &&
                    roleDefault.ValueKind == JsonValueKind.Array)
                {
                    return ParseHandles(roleDefault);
                }
            }
            if (chains.TryGetProperty(role, out var roleArrFallback) &&
                roleArrFallback.ValueKind == JsonValueKind.Array)
            {
                return ParseHandles(roleArrFallback);
            }
            if (chains.TryGetProperty("default", out var defaultNode) &&
                defaultNode.ValueKind == JsonValueKind.Array)
            {
                return ParseHandles(defaultNode);
            }
        }

        // Legacy TS (Story 9-5 / 9-8) shape — finding 011. Old rows persist
        // chains under roles.<role>.providerChain with defaults.providerChain.
        // Try canonical role first, then alias.
        var legacyRole = role;
        if (TryReadLegacy(root, legacyRole, out var legacyChain))
        {
            return legacyChain;
        }
        // Walk legacy aliases mapping to the requested canonical role.
        foreach (var (legacy, canonical) in Agents.RolePhaseMap.LegacyRoleAliases)
        {
            if (!string.Equals(canonical, role, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryReadLegacy(root, legacy, out var aliasedChain))
            {
                return aliasedChain;
            }
        }

        // Final TS fallback: defaults.providerChain.
        if (root.TryGetProperty("defaults", out var defaultsNode) &&
            defaultsNode.ValueKind == JsonValueKind.Object &&
            defaultsNode.TryGetProperty("providerChain", out var defChainNode) &&
            defChainNode.ValueKind == JsonValueKind.Array)
        {
            return ParseHandles(defChainNode);
        }

        return Array.Empty<ProviderHandle>();
    }

    /// <summary>
    /// Read the TS-shape <c>roles.{role}.providerChain</c> array. Returns
    /// false (no chain found) instead of an empty list so callers can
    /// continue cascading.
    /// </summary>
    private static bool TryReadLegacy(
        JsonElement root, string role, out IReadOnlyList<ProviderHandle> chain)
    {
        chain = Array.Empty<ProviderHandle>();
        if (!root.TryGetProperty("roles", out var roles) ||
            roles.ValueKind != JsonValueKind.Object) return false;
        if (!roles.TryGetProperty(role, out var roleNode) ||
            roleNode.ValueKind != JsonValueKind.Object) return false;
        if (!roleNode.TryGetProperty("providerChain", out var pcNode) ||
            pcNode.ValueKind != JsonValueKind.Array) return false;

        chain = ParseHandles(pcNode);
        return chain.Count > 0;
    }

    private static IReadOnlyList<ProviderHandle> ParseHandles(JsonElement arr)
    {
        var result = new List<ProviderHandle>();
        int priority = 0;
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("provider", out var provEl) ||
                provEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var provider = provEl.GetString();
            if (string.IsNullOrWhiteSpace(provider)) continue;

            string? model = null;
            if (entry.TryGetProperty("model", out var modelEl) &&
                modelEl.ValueKind == JsonValueKind.String)
            {
                model = modelEl.GetString();
            }

            result.Add(new ProviderHandle(provider!, model, priority++));
        }
        return result;
    }
}

using System.Text.Json;
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
/// </summary>
public sealed class ProviderChainResolver : IProviderChainResolver
{
    private readonly IAgentConfigRepository _configRepo;
    private readonly ICircuitBreakerService _breaker;

    public ProviderChainResolver(IAgentConfigRepository configRepo, ICircuitBreakerService breaker)
    {
        _configRepo = configRepo ?? throw new ArgumentNullException(nameof(configRepo));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
    }

    public async Task<ChainResolveResult> ResolveAsync(
        Guid? tenantId,
        string role,
        string action,
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
                ErrorMessage: $"No provider chain configured for role='{role}' action='{action}'.");
        }

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
                    healthyOrUnknown.Add(new ChainEntry(handle, reason));
                    break;
                case CircuitBreakerState.HalfOpen:
                    halfOpenTail.Add(new ChainEntry(handle, ChainReason.HalfOpenProbeCandidate));
                    break;
                case CircuitBreakerState.Open:
                    skipped.Add(new ChainEntry(handle, ChainReason.CircuitOpen));
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
                ErrorMessage: "All providers in the chain are circuit-open.");
        }

        return new ChainResolveResult(ordered, skipped);
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
        if (!doc.RootElement.TryGetProperty("chains", out var chains) ||
            chains.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ProviderHandle>();
        }

        // 1. chains[role][action]
        if (chains.TryGetProperty(role, out var roleNode) && roleNode.ValueKind == JsonValueKind.Object)
        {
            if (roleNode.TryGetProperty(action, out var roleActionArr) &&
                roleActionArr.ValueKind == JsonValueKind.Array)
            {
                return ParseHandles(roleActionArr);
            }
            // 2. chains[role]["default"]
            if (roleNode.TryGetProperty("default", out var roleDefault) &&
                roleDefault.ValueKind == JsonValueKind.Array)
            {
                return ParseHandles(roleDefault);
            }
        }

        // 3. chains["default"] — when the role key is itself an array it is also treated as default.
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

        return Array.Empty<ProviderHandle>();
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

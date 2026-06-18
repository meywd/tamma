using Tamma.Core;

namespace Tamma.Core.Enums;

/// <summary>
/// Story 34-1 — the snake_case string contract for
/// <see cref="EntitlementMetricKey"/>. EF persists the metric key via a
/// <c>HasConversion</c> value converter built on these methods so the DB
/// column stores <c>text</c> (e.g. <c>llm_tokens</c>), never the unstable
/// numeric ordinal. Metering / pricing / enforcement all key off the same
/// string so a quota key can never drift between layers.
/// </summary>
public static class EntitlementMetricKeyExtensions
{
    /// <summary>
    /// Canonical snake_case wire/DB form per member. The single source of
    /// truth for the persisted string. Kept in sync with
    /// <see cref="EntitlementMetricKey"/> (enum-tests assert one entry per
    /// member with no duplicates).
    /// </summary>
    private static readonly IReadOnlyDictionary<EntitlementMetricKey, string> s_toString =
        new Dictionary<EntitlementMetricKey, string>
        {
            [EntitlementMetricKey.Agents] = "agents",
            [EntitlementMetricKey.WorkflowRuns] = "workflow_runs",
            [EntitlementMetricKey.LlmTokens] = "llm_tokens",
            [EntitlementMetricKey.Seats] = "seats",
            [EntitlementMetricKey.Repos] = "repos",
            [EntitlementMetricKey.RagStorageMb] = "rag_storage_mb",
            [EntitlementMetricKey.BenchmarkRetentionDays] = "benchmark_retention_days",
        };

    private static readonly IReadOnlyDictionary<string, EntitlementMetricKey> s_fromString =
        s_toString.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>
    /// The complete set of canonical snake_case strings — exposed for tests
    /// and any consumer that needs to enumerate every valid metric key.
    /// </summary>
    public static IReadOnlyCollection<string> AllMetricStrings => s_fromString.Keys.ToArray();

    /// <summary>Map a metric key to its canonical snake_case persisted form.</summary>
    public static string ToMetricString(this EntitlementMetricKey key)
    {
        if (s_toString.TryGetValue(key, out var s))
        {
            return s;
        }

        // An enum member with no mapping is a programming error — fail loud.
        throw new TammaError(
            "PLAN.METRIC_KEY.UNMAPPED",
            $"EntitlementMetricKey '{key}' has no snake_case mapping.",
            new Dictionary<string, object?> { ["metricKey"] = key.ToString() },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// Parse a snake_case string back to its <see cref="EntitlementMetricKey"/>.
    /// Throws <see cref="TammaError"/> (<c>PLAN.METRIC_KEY.UNKNOWN</c>) on an
    /// unmapped string — never silently coerces to a default member.
    /// </summary>
    public static EntitlementMetricKey Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (s_fromString.TryGetValue(value, out var key))
        {
            return key;
        }

        throw new TammaError(
            "PLAN.METRIC_KEY.UNKNOWN",
            $"Unknown entitlement metric key '{value}'.",
            new Dictionary<string, object?> { ["value"] = value },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}

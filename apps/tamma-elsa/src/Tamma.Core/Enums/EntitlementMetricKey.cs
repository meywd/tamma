namespace Tamma.Core.Enums;

/// <summary>
/// Story 34-1 — closed set of meterable/limitable quota dimensions. Shared by
/// <c>PlanEntitlement</c> (limits), <c>PlanPrice</c> metered components
/// (pricing), usage metering (Epic 35), and enforcement (later Epic 34
/// stories) so a quota key is identical across every layer. Persisted as the
/// snake_case string (see <see cref="EntitlementMetricKeyExtensions"/>), never
/// the numeric ordinal — ordinals are not a stable wire/DB contract.
///
/// <para>Adding a member here is a breaking change: every member MUST have a
/// matching snake_case mapping in <see cref="EntitlementMetricKeyExtensions"/>
/// (the enum-tests assert the map has exactly one entry per member, so a new
/// member without a mapping fails the suite).</para>
/// </summary>
public enum EntitlementMetricKey
{
    /// <summary>Number of agent identities a tenant may own.</summary>
    Agents,

    /// <summary>Workflow run executions (per period).</summary>
    WorkflowRuns,

    /// <summary>LLM tokens consumed (per period).</summary>
    LlmTokens,

    /// <summary>Seats / member users.</summary>
    Seats,

    /// <summary>Connected repositories.</summary>
    Repos,

    /// <summary>RAG document storage in megabytes.</summary>
    RagStorageMb,

    /// <summary>Benchmark/leaderboard result retention in days.</summary>
    BenchmarkRetentionDays,
}

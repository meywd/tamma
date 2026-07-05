using System.Text.Json.Serialization;

namespace Tamma.Activities.Decomposition.Models;

/// <summary>
/// Story 2.14 — the canonical complexity buckets for a single decomposed sub-task
/// (Story 2.14 AC1/AC4 — sizing is driven off scope/complexity). Kept as a small closed set so
/// a drifting LLM label (<c>"trivial"</c>, <c>"Medium."</c>) is normalised onto a known bucket
/// rather than leaking an arbitrary string downstream to Stories 2.15 (#138 dependency mapping)
/// and 2.16 (#139 sequencing). An unrecognised / empty label normalises to
/// <see cref="Medium"/> — a neutral middle, never silently dropped.
/// </summary>
public static class SubtaskComplexities
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    private static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        Low, Medium, High,
    };

    /// <summary>
    /// Normalise a raw LLM complexity label onto the canonical set: trimmed + lower-cased, with
    /// a couple of common synonyms folded in. Anything unrecognised → <see cref="Medium"/>.
    /// Pure; exposed for unit testing.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Medium;

        var c = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (Canonical.Contains(c)) return c;

        return c switch
        {
            "trivial" or "simple" or "easy" or "xs" or "s" => Low,
            "moderate" or "m" => Medium,
            "complex" or "hard" or "large" or "xl" or "l" => High,
            _ => Medium,
        };
    }
}

/// <summary>
/// Story 2.14 — one implementable sub-task produced by decomposing a complex issue. This is the
/// FOUNDATION shape that Story 2.15 (#138 dependency mapping) and Story 2.16 (#139 sequencing)
/// build on, so it is designed to be consumed as a dependency graph node:
/// <list type="bullet">
///   <item><see cref="Id"/> — a stable identifier the LLM assigns (e.g. <c>ST-1</c>). Load-bearing:
///     <see cref="DependsOn"/> references sub-tasks by this id, so a sub-task with no id cannot be
///     wired into the graph and is dropped by the parser.</item>
///   <item><see cref="Title"/> / <see cref="Description"/> — what the sub-task is.</item>
///   <item><see cref="AcceptanceCriteria"/> — the definition of done (Story 2.14 AC2/AC4).</item>
///   <item><see cref="EstimateHours"/> — rough effort in hours (Story 2.14 AC4 — sub-tasks sized
///     ~2-8h); a soft guide, not enforced by the parser.</item>
///   <item><see cref="Complexity"/> — one of the <see cref="SubtaskComplexities"/> buckets.</item>
///   <item><see cref="DependsOn"/> — the ids of prerequisite sub-tasks (Story 2.14 AC3). The
///     parser prunes these to ids that exist in the same decomposition and removes self-references,
///     so #138 receives a clean edge set (it owns cycle detection / graph analysis).</item>
/// </list>
/// The position of a sub-task in <see cref="IssueDecomposition.Subtasks"/> is the initial suggested
/// order; #139 refines it into a topological sequence using <see cref="DependsOn"/>.
/// </summary>
public sealed class Subtask
{
    /// <summary>Stable sub-task id (load-bearing — <see cref="DependsOn"/> references it).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Short title / headline for the sub-task.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>What the sub-task does — the implementable slice of the parent issue.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The definition of done for this sub-task (Story 2.14 AC2/AC4).</summary>
    [JsonPropertyName("acceptanceCriteria")]
    public string AcceptanceCriteria { get; set; } = string.Empty;

    /// <summary>Rough effort estimate in hours (Story 2.14 AC4 — ~2-8h target); a soft guide.</summary>
    [JsonPropertyName("estimateHours")]
    public decimal EstimateHours { get; set; }

    /// <summary>Complexity bucket — one of the <see cref="SubtaskComplexities"/> values.</summary>
    [JsonPropertyName("complexity")]
    public string Complexity { get; set; } = SubtaskComplexities.Medium;

    /// <summary>Ids of prerequisite sub-tasks (Story 2.14 AC3). Pruned to existing ids by the parser.</summary>
    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = new();
}

/// <summary>
/// Story 2.14 — the structured decomposition of a complex issue into an ordered set of smaller,
/// implementable <see cref="Subtask"/>s plus an overview <see cref="Summary"/> explaining the
/// breakdown (Story 2.14 AC5 — the decomposition must preserve the original issue's intent and
/// business value; the summary is where that rationale is recorded and is load-bearing).
///
/// <para>Serialised into the <c>issue-decomposition</c> workflow's <c>decompositionJson</c> output
/// and carried onto the <c>DECOMPOSITION.COMPLETED</c> DCB event so the breakdown is fully auditable
/// and feeds the Epic-32 learning loop (Story 2.14 AC8). The parser
/// (<see cref="DecompositionParsing.ParseDecomposition"/>) fails closed on a missing summary or zero
/// usable sub-tasks, so a fabricated / empty decomposition is never acted on.</para>
///
/// <para>This shape is the input contract for Story 2.15 (#138 dependency mapping — consumes
/// <see cref="Subtask.Id"/> + <see cref="Subtask.DependsOn"/> as graph edges) and Story 2.16
/// (#139 sequencing — topologically orders the sub-tasks). Keep it stable.</para>
/// </summary>
public sealed class IssueDecomposition
{
    /// <summary>Overview of the breakdown / how it preserves the parent intent — load-bearing (fail-closed if empty).</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>The ordered sub-tasks; position is the initial suggested order (#139 refines it).</summary>
    [JsonPropertyName("subtasks")]
    public List<Subtask> Subtasks { get; set; } = new();
}

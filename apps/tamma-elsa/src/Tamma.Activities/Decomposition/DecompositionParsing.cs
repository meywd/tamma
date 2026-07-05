using System.Text.Json;
using Tamma.Activities.Decomposition.Models;

namespace Tamma.Activities.Decomposition;

/// <summary>
/// Story 2.14 — pure, context-free parser that recovers the structured
/// <see cref="IssueDecomposition"/> from a mediated <c>llm-call</c> decomposition response. Kept
/// side-effect-free (no Elsa context) so the fail-closed behaviour is unit-testable without a live
/// LLM. Mirrors the JSON-slice approach in <c>ResearchParsing</c> / <c>AmbiguityParsing</c> /
/// <c>ClarifyParsing</c>.
///
/// <para>The parser is <b>fail-closed</b>: an empty response, a response with no JSON object, a
/// decomposition missing its load-bearing <c>summary</c> (the intent-preservation rationale,
/// AC5), or a decomposition with no usable sub-tasks all yield <c>null</c>. The workflow routes a
/// <c>null</c> parse to its <c>DECOMPOSITION.FAILED</c> error terminal — it NEVER fabricates a
/// sub-task, a summary, or a dependency the downstream (Stories 2.15/2.16) would act on.</para>
///
/// <para>Sub-task ids are load-bearing (dependencies reference them), so a sub-task with no
/// <c>id</c>, or with neither a <c>title</c> nor a <c>description</c>, is dropped as an empty shell
/// (item-level fail-closed) rather than admitted blank. Duplicate ids keep the first occurrence.
/// After the surviving sub-task set is known, each sub-task's <c>dependsOn</c> is pruned to ids
/// that actually exist in the set (dangling references removed) with self-references stripped, so
/// #138 receives a clean edge set. Cycle detection / topological ordering is deliberately NOT done
/// here — that is the scope of Stories 2.15 (#138) and 2.16 (#139).</para>
/// </summary>
public static class DecompositionParsing
{
    /// <summary>
    /// Extract the structured decomposition from an <c>llm-call</c> text response. Expects a JSON
    /// object of the shape
    /// <c>{"summary":"...","subtasks":[{"id":"ST-1","title":"...","description":"...",
    /// "acceptanceCriteria":"...","estimateHours":4,"complexity":"medium","dependsOn":["ST-2"]}]}</c>.
    ///
    /// <para>The <c>summary</c> and at least one valid sub-task are load-bearing: the summary
    /// records how the breakdown preserves the parent intent (AC5) and a decomposition with no
    /// sub-tasks broke nothing down. Complexity labels are normalised onto the canonical buckets
    /// (<see cref="SubtaskComplexities"/>); estimate hours are clamped to be non-negative.</para>
    ///
    /// <para>Returns <c>null</c> (fail-closed) when the text is empty, carries no JSON object, has
    /// no non-empty <c>summary</c>, or contains no usable sub-task.</para>
    /// </summary>
    /// <param name="llmText">The raw LLM decomposition response.</param>
    public static IssueDecomposition? ParseDecomposition(string? llmText)
    {
        if (string.IsNullOrWhiteSpace(llmText))
            return null;

        var objStart = llmText.IndexOf('{');
        var objEnd = llmText.LastIndexOf('}');
        if (objStart < 0 || objEnd <= objStart)
            return null;

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(llmText[objStart..(objEnd + 1)]);

            var summary = ReadString(element, "summary");

            // Fail-closed: a decomposition with no overview rationale is not auditable/actionable
            // and cannot demonstrate intent preservation (AC5).
            if (string.IsNullOrWhiteSpace(summary))
                return null;

            var subtasks = ParseSubtasks(element);

            // Fail-closed: no sub-tasks → nothing was actually decomposed.
            if (subtasks.Count == 0)
                return null;

            PruneDependencies(subtasks);

            return new IssueDecomposition
            {
                Summary = summary,
                Subtasks = subtasks,
            };
        }
        catch
        {
            // Malformed JSON → fail closed.
            return null;
        }
    }

    /// <summary>
    /// Recover the sub-task list from the decomposition object. Each sub-task must carry a
    /// non-empty <c>id</c> (load-bearing — dependencies reference it) AND at least a title or a
    /// description; empty shells are dropped. Duplicate ids keep the first occurrence.
    /// </summary>
    private static List<Subtask> ParseSubtasks(JsonElement element)
    {
        if (!element.TryGetProperty("subtasks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<Subtask>();

        var subtasks = new List<Subtask>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "id").Trim();
            var title = ReadString(item, "title");
            var description = ReadString(item, "description");

            // A sub-task with no id cannot be referenced by dependencies; a sub-task with neither a
            // title nor a description is an empty shell. Either → drop rather than admit blank.
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description))
                continue;

            // Duplicate ids would make the dependency graph ambiguous — keep the first.
            if (!seenIds.Add(id))
                continue;

            subtasks.Add(new Subtask
            {
                Id = id,
                Title = title,
                Description = description,
                AcceptanceCriteria = ReadString(item, "acceptanceCriteria"),
                EstimateHours = Math.Max(0m, ReadNumber(item, "estimateHours")),
                Complexity = SubtaskComplexities.Normalize(ReadString(item, "complexity")),
                DependsOn = ReadStringList(item, "dependsOn"),
            });
        }

        return subtasks;
    }

    /// <summary>
    /// Prune every sub-task's <c>dependsOn</c> to the ids that actually exist in the surviving set,
    /// dropping self-references and duplicates. Keeps the edge set clean for #138 (which owns cycle
    /// detection and graph analysis) without ever fabricating an edge.
    /// </summary>
    private static void PruneDependencies(List<Subtask> subtasks)
    {
        var validIds = subtasks.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var subtask in subtasks)
        {
            var pruned = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dep in subtask.DependsOn)
            {
                var d = dep?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(d)) continue;
                if (string.Equals(d, subtask.Id, StringComparison.Ordinal)) continue; // no self-loop
                if (!validIds.Contains(d)) continue;                                   // no dangling ref
                if (!seen.Add(d)) continue;                                            // dedupe
                pruned.Add(d);
            }
            subtask.DependsOn = pruned;
        }
    }

    private static string ReadString(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static decimal ReadNumber(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? (decimal)v.GetDouble()
            : 0m;

    private static List<string> ReadStringList(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
    }
}

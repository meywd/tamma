using System.Text;
using System.Text.Json;
using Tamma.Activities.ADL;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Pure, fail-closed helpers for the <c>triage-item-cycle</c> workflow (no Elsa runtime
/// dependency). These are the levers behind the cycle build-out (completeness audit
/// 2026-06-22, <c>TriageItemCycle.md</c>):
///
/// <list type="bullet">
///   <item><description>#1 — <see cref="IsDecisionApplicable"/> is the decision-OK gate.
///     A missing / empty / <c>unparsed</c> / <c>llm-failed</c> / <c>skipped</c> PO
///     decision is NOT applicable: the cycle must skip label application and fail the
///     item, never label off a fabricated/empty decision (no-empty-fallback rule).</description></item>
///   <item><description>#5 — <see cref="BuildItemResult"/> renders the per-item outcome
///     the fire-and-forget parent needs (<c>{ itemKey, outcome, decisionStatus, error? }</c>)
///     so it reports <c>{ triaged, failed, skipped }</c> rather than a blanket success.</description></item>
///   <item><description>#7 — <see cref="DeriveItemKey"/> gives a deterministic key for
///     events/dedupe; <see cref="ValidateLabels"/> drops labels outside the canonical
///     vocabulary; <see cref="RenderComment"/> builds the AC5 markdown-table comment
///     <i>deterministically from the parsed decision</i>, not from raw LLM prose.</description></item>
/// </list>
/// </summary>
public static class TriageItemCycleHelper
{
    /// <summary>
    /// The canonical triage label vocabulary (the type/priority/complexity/automation
    /// grid) — superset mirror of <c>FetchUntriagedItemsActivity.TriageLabels</c>. A
    /// label the PO returned that is NOT in this set is dropped before applying (#7), so
    /// arbitrary LLM-invented labels never reach the issue.
    /// </summary>
    public static readonly IReadOnlySet<string> CanonicalLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // type
        "bug", "feature", "chore", "question", "security", "docs",
        // priority
        "priority-critical", "priority-high", "priority-medium", "priority-low",
        // automation
        "tamma-auto", "tamma-assist", "needs-human",
        // complexity
        "complexity-trivial", "complexity-simple", "complexity-medium",
        "complexity-complex", "complexity-epic",
        // lifecycle / triage outcome markers
        "tamma-processing", "tamma-completed", "tamma-error",
        "needs-human-review", "triage-failed", "triage-skipped",
    };

    /// <summary>The parsed PO decision the cycle reasons over.</summary>
    public sealed record CycleDecision(
        string Status,
        string Priority,
        string Type,
        string Complexity,
        string Automation,
        IReadOnlyList<string> Labels,
        string Comment);

    /// <summary>
    /// Derive a deterministic, replay-stable key for the item from its JSON. An issue →
    /// <c>repo#number</c>; an alert (no usable number) → <c>repo:source:title</c> (or
    /// <c>repo:source</c> when no title). Used for event tags and a future dedupe gate.
    /// Returns <c>repo:unknown</c> on wholly-unparseable input rather than throwing.
    /// </summary>
    public static string DeriveItemKey(string? repository, string? itemJson)
    {
        var repo = string.IsNullOrWhiteSpace(repository) ? "unknown-repo" : repository!.Trim();
        if (string.IsNullOrWhiteSpace(itemJson))
            return $"{repo}:unknown";

        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return $"{repo}:unknown";

            var number = 0;
            if (root.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number)
                n.TryGetInt32(out number);

            if (number > 0)
                return $"{repo}#{number}";

            var source = ReadString(root, "source");
            var title = ReadString(root, "title");
            if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(title))
                return $"{repo}:{source}:{title}";
            if (!string.IsNullOrWhiteSpace(source))
                return $"{repo}:{source}";
            if (!string.IsNullOrWhiteSpace(title))
                return $"{repo}:{title}";

            return $"{repo}:unknown";
        }
        catch
        {
            return $"{repo}:unknown";
        }
    }

    /// <summary>Read the item <c>source</c> field (issue / dependabot / codeql / ...)
    /// for the event tags; empty when absent / unparseable.</summary>
    public static string ReadItemSource(string? itemJson)
    {
        if (string.IsNullOrWhiteSpace(itemJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var src = ReadString(doc.RootElement, "source");
                if (!string.IsNullOrWhiteSpace(src)) return src;
                // Fall back to the type field (issue / security / dependency).
                return ReadString(doc.RootElement, "type");
            }
        }
        catch { /* unknown */ }
        return "";
    }

    /// <summary>
    /// Parse the PO <c>decisionJson</c> into the cycle's decision view. Unparseable /
    /// blank input yields a decision whose <see cref="CycleDecision.Status"/> is empty
    /// — which <see cref="IsDecisionApplicable"/> treats as NOT applicable (fail-closed).
    /// </summary>
    public static CycleDecision ParseDecision(string? decisionJson)
    {
        if (string.IsNullOrWhiteSpace(decisionJson))
            return Empty("");

        try
        {
            using var doc = JsonDocument.Parse(decisionJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Empty("");

            var status = ReadString(root, "status");
            var priority = ReadString(root, "priority");
            var type = ReadString(root, "type");
            var complexity = ReadString(root, "complexity");
            var automation = ReadString(root, "automation");
            var comment = ReadString(root, "comment");
            var labels = new List<string>();
            if (root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in l.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) labels.Add(s!);
                    }
                }
            }

            return new CycleDecision(status, priority, type, complexity, automation, labels, comment);
        }
        catch
        {
            return Empty("");
        }
    }

    private static CycleDecision Empty(string status)
        => new(status, "", "", "", "", Array.Empty<string>(), "");

    /// <summary>
    /// #1 — the decision-OK gate. A decision is applicable ONLY when the PO step
    /// reported the LLM call succeeded (<paramref name="callSucceeded"/>) AND the parsed
    /// decision status is <c>ok</c>. A faulted PO sub-workflow (no <c>callSucceeded</c>
    /// output), an <c>llm-failed</c> / <c>unparsed</c> / <c>skipped</c> status, or a
    /// missing/empty decision → NOT applicable: the cycle must fail the item and NOT
    /// apply labels off it. This is where the "never label from a fabricated/empty
    /// decision" rule is enforced at the cycle level.
    /// </summary>
    public static bool IsDecisionApplicable(bool callSucceeded, CycleDecision decision)
        => callSucceeded
           && string.Equals(decision.Status, TriagePoDecisionHelper.StatusOk, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Convenience overload reading the status straight off the decision JSON, for the
    /// workflow FlowDecision delegate. <paramref name="callSucceeded"/> is the PO step's
    /// <c>callSucceeded</c> output (false / absent → not applicable, fail-closed).
    /// </summary>
    public static bool IsDecisionApplicable(bool callSucceeded, string? decisionJson)
        => IsDecisionApplicable(callSucceeded, ParseDecision(decisionJson));

    /// <summary>
    /// #7 — validate the PO's labels against the canonical vocabulary, returning only
    /// the in-vocab ones. Out-of-vocab labels are dropped (never written to the issue);
    /// <paramref name="dropped"/> receives them so the caller can emit a
    /// <c>TRIAGE.LABELS.INVALID</c> warning. Order is preserved; duplicates collapsed.
    /// </summary>
    public static IReadOnlyList<string> ValidateLabels(IEnumerable<string>? labels, out IReadOnlyList<string> dropped)
    {
        var kept = new List<string>();
        var droppedList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (labels != null)
        {
            foreach (var raw in labels)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var label = raw.Trim();
                if (!CanonicalLabels.Contains(label))
                {
                    droppedList.Add(label);
                    continue;
                }
                if (seen.Add(label)) kept.Add(label);
            }
        }

        dropped = droppedList;
        return kept;
    }

    /// <summary>
    /// #7 — render the AC5 triage comment <b>deterministically from the parsed decision
    /// fields</b>, NOT from raw LLM prose. The PO's free-form <c>comment</c> is preserved
    /// below the table as the rationale (it is human-facing, not a classification), but
    /// the applied classification is the canonical, validated grid — so the comment can
    /// never disagree with the labels. Returns a stable markdown table a human can read.
    /// </summary>
    public static string RenderComment(CycleDecision decision)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Triage Decision");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Type | {Display(decision.Type, TriagePoDecisionHelper.DefaultType)} |");
        sb.AppendLine($"| Priority | {Display(decision.Priority, TriagePoDecisionHelper.DefaultPriority)} |");
        sb.AppendLine($"| Complexity | {Display(decision.Complexity, TriagePoDecisionHelper.DefaultComplexity)} |");
        sb.AppendLine($"| Automation | {Display(decision.Automation, TriagePoDecisionHelper.DefaultAutomation)} |");

        var rationale = decision.Comment?.Trim();
        if (!string.IsNullOrWhiteSpace(rationale))
        {
            sb.AppendLine();
            sb.AppendLine("### Notes");
            sb.AppendLine();
            sb.AppendLine(rationale);
        }

        sb.AppendLine();
        sb.Append("_Triaged automatically by Tamma._");
        return sb.ToString();
    }

    private static string Display(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();

    /// <summary>
    /// #5 — serialize the per-item outcome surfaced on the cycle's <c>itemResult</c>
    /// output: <c>{ itemKey, outcome, decisionStatus, error? }</c>. <paramref name="outcome"/>
    /// is one of <see cref="TriageCycleEvents.OutcomeTriaged"/> /
    /// <c>OutcomeSkipped</c> / <c>OutcomeFailed</c>.
    /// </summary>
    public static string BuildItemResult(string itemKey, string outcome, string? decisionStatus, string? error)
    {
        var dict = new Dictionary<string, object?>
        {
            ["itemKey"] = itemKey,
            ["outcome"] = outcome,
            ["decisionStatus"] = decisionStatus ?? "",
        };
        if (!string.IsNullOrWhiteSpace(error)) dict["error"] = error;
        return JsonSerializer.Serialize(dict);
    }

    private static string ReadString(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null => "",
            _ => v.GetRawText(),
        };
    }
}

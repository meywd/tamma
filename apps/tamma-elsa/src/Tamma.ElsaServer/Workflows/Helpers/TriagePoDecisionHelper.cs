using System.Text;
using System.Text.Json;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Pure, fail-closed decision-building for the <c>triage-po-decision</c> workflow
/// (no Elsa runtime dependency). This is the lever that fixes the headline
/// build-out bugs (completeness audit 2026-06-22, <c>TriagePODecision.md</c>):
///
/// <list type="bullet">
///   <item><description>#1 — a total <c>llm-call</c> failure must NOT be laundered
///     into a clean <c>needs-human</c> "No PO decision received." applied decision.
///     <see cref="BuildFailureDecision"/> emits an explicit <c>llm-failed</c> marker
///     labelled <c>triage-failed</c>/<c>needs-human</c> so the applied labels are
///     honest, never a fabricated <c>priority-normal/feature</c>.</description></item>
///   <item><description>#2 — "no parseable JSON" (model returned prose) is
///     distinguished from a real decision. Such output is marked
///     <c>status="unparsed"</c>, forced to <c>automation="needs-human"</c> with a
///     <c>needs-human-review</c> label — never presented as a clean classified
///     decision with default classifications.</description></item>
///   <item><description>#4 — <c>priority</c>/<c>type</c>/<c>complexity</c>/
///     <c>automation</c> are validated against the Story 26-1 allowed vocabulary;
///     an out-of-vocab value (e.g. <c>priority="P0"</c>, <c>automation="auto"</c>)
///     is clamped to the safe default AND flagged in the comment, never passed
///     straight to labels.</description></item>
///   <item><description>#7 — <see cref="BuildSkippedDecision"/> short-circuits an
///     empty input (<c>itemJson</c> blank/<c>{}</c>) without spending an LLM call.</description></item>
/// </list>
///
/// <para>Story 39-15 (D9): with the <c>triage-po-decision</c> workflow rebuilt as a
/// TriageDecision lifecycle binding, <see cref="BuildFailureDecision"/> /
/// <see cref="BuildSkippedDecision"/> / <see cref="SummarizeFailure"/> /
/// <see cref="IsUsableInput"/> survive as the binding/cycle's honest-fallback renderers;
/// <see cref="ParseDecision"/> stays as the fail-safe legacy-wire baseline the
/// cross-parser + round-trip pins reference. Deterministic parse independent of LLM
/// prose, fail-closed defaults that are loud rather than benign-looking.</para>
/// </summary>
public static class TriagePoDecisionHelper
{
    // ----------------------------------------------------------------
    // Decision status (carried on the decisionJson as `status`)
    // ----------------------------------------------------------------

    /// <summary>The model returned parseable JSON; fields validated/clamped.</summary>
    public const string StatusOk = "ok";

    /// <summary>The model returned prose / unparseable output — needs-human review.</summary>
    public const string StatusUnparsed = "unparsed";

    /// <summary>The <c>llm-call</c> reported failure — no decision produced.</summary>
    public const string StatusLlmFailed = "llm-failed";

    /// <summary>Empty input — the LLM call was short-circuited.</summary>
    public const string StatusSkipped = "skipped";

    // ----------------------------------------------------------------
    // Safe defaults (Story 26-1). normal == priority-medium (the documented
    // default); needs-human is the safe automation default.
    // ----------------------------------------------------------------

    public const string DefaultPriority = "normal";
    public const string DefaultType = "feature";
    public const string DefaultComplexity = "medium";
    public const string DefaultAutomation = "needs-human";

    // ----------------------------------------------------------------
    // Allowed vocabulary (Story 26-1). The build-out spec accepts the
    // urgent/critical and normal/medium synonyms; both map to the same label.
    // ----------------------------------------------------------------

    private static readonly HashSet<string> AllowedPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        "urgent", "critical", "high", "normal", "medium", "low",
    };

    private static readonly HashSet<string> AllowedType = new(StringComparer.OrdinalIgnoreCase)
    {
        "bug", "feature", "chore", "question", "security", "docs",
    };

    private static readonly HashSet<string> AllowedComplexity = new(StringComparer.OrdinalIgnoreCase)
    {
        "trivial", "simple", "medium", "complex", "epic",
    };

    private static readonly HashSet<string> AllowedAutomation = new(StringComparer.OrdinalIgnoreCase)
    {
        "tamma-auto", "tamma-assist", "needs-human",
    };

    /// <summary>
    /// The normalized PO decision. <see cref="Comment"/> may carry appended
    /// vocab-clamp notes (#4). <see cref="Labels"/> is a real list (serialized as a
    /// JSON array so the consumer's <c>List&lt;string&gt;</c> binds correctly).
    /// </summary>
    public sealed record PoDecision(
        string Status,
        string Priority,
        string Type,
        string Complexity,
        string Automation,
        IReadOnlyList<string> Labels,
        string Comment,
        string? Reasoning);

    /// <summary>
    /// Whether <paramref name="itemJson"/> represents usable input. Blank, the
    /// literal <c>"{}"</c>, or unparseable/empty-object JSON is NOT usable input
    /// (#7) — the caller should short-circuit with a SKIPPED marker rather than
    /// spending an LLM call.
    /// </summary>
    public static bool IsUsableInput(string? itemJson)
    {
        if (string.IsNullOrWhiteSpace(itemJson)) return false;
        var trimmed = itemJson.Trim();
        if (trimmed == "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
            foreach (var _ in doc.RootElement.EnumerateObject()) return true; // has at least one prop
            return false; // empty object
        }
        catch
        {
            // Unparseable item JSON is still "present" — let the LLM attempt it
            // rather than skip (the panel result may carry the real signal). Only
            // truly blank / {} input skips.
            return true;
        }
    }

    /// <summary>
    /// Build the decision from a successful <c>llm-call</c> response text. Carves
    /// the first <c>{</c>…last <c>}</c> JSON block (existing behaviour), validates
    /// each classification field against the allowed vocabulary (#4), populates
    /// <c>reasoning</c> (#5 — output only; the consumer extension is a follow-up),
    /// and — when no parseable JSON object is present — returns an
    /// <see cref="StatusUnparsed"/> decision (#2) rather than a clean classified
    /// one.
    /// </summary>
    public static PoDecision ParseDecision(string? llmResponse)
    {
        var output = llmResponse ?? "";

        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var candidate = output[jsonStart..(jsonEnd + 1)];
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                    return BuildFromJson(root);
            }
            catch
            {
                // Not valid JSON → fall through to the unparsed marker.
            }
        }

        // #2 — no parseable JSON. The model returned prose. Do NOT stamp default
        // classifications and present it as a clean decision: mark it unparsed,
        // force needs-human, add a needs-human-review label, keep the prose as the
        // comment so a human can read it.
        return new PoDecision(
            Status: StatusUnparsed,
            Priority: DefaultPriority,
            Type: DefaultType,
            Complexity: DefaultComplexity,
            Automation: DefaultAutomation,
            Labels: new List<string> { "needs-human-review" },
            Comment: string.IsNullOrWhiteSpace(output)
                ? "PO returned no parseable decision; requires human triage."
                : output,
            Reasoning: null);
    }

    private static PoDecision BuildFromJson(JsonElement root)
    {
        var notes = new StringBuilder();

        var priority = Clamp(root, "priority", AllowedPriority, DefaultPriority, notes);
        var type = Clamp(root, "type", AllowedType, DefaultType, notes);
        var complexity = Clamp(root, "complexity", AllowedComplexity, DefaultComplexity, notes);
        var automation = Clamp(root, "automation", AllowedAutomation, DefaultAutomation, notes);

        var labels = ReadLabels(root);
        var reasoning = ReadOptionalString(root, "reasoning");

        var comment = ReadOptionalString(root, "comment") ?? "";
        if (notes.Length > 0)
            comment = string.IsNullOrEmpty(comment) ? notes.ToString().TrimEnd() : $"{comment}\n\n{notes.ToString().TrimEnd()}";

        return new PoDecision(
            Status: StatusOk,
            Priority: priority,
            Type: type,
            Complexity: complexity,
            Automation: automation,
            Labels: labels,
            Comment: comment,
            Reasoning: reasoning);
    }

    /// <summary>
    /// Read a classification field and validate it against the allowed vocabulary.
    /// On a missing field, returns the default silently (absence is normal). On an
    /// OUT-OF-VOCAB value, returns the default AND appends a clamp note to
    /// <paramref name="notes"/> so the applied comment records "PO returned invalid
    /// <c>&lt;field&gt;=&lt;value&gt;</c>, defaulted to <c>&lt;default&gt;</c>" (#4) —
    /// never silently swallowing a bad value into a label.
    /// </summary>
    private static string Clamp(JsonElement root, string field, HashSet<string> allowed, string @default, StringBuilder notes)
    {
        var raw = ReadOptionalString(root, field);
        if (string.IsNullOrWhiteSpace(raw))
            return @default; // absent → default, no note (normal)

        if (allowed.Contains(raw.Trim()))
            return raw.Trim();

        notes.Append($"PO returned invalid {field}=\"{raw}\", defaulted to \"{@default}\".\n");
        return @default;
    }

    private static string? ReadOptionalString(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null => null,
            _ => v.GetRawText(),
        };
    }

    private static List<string> ReadLabels(JsonElement root)
    {
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
        return labels;
    }

    /// <summary>
    /// #1 — the explicit LLM-failure marker. NO classification is fabricated: the
    /// decision is labelled <c>triage-failed</c>/<c>needs-human</c> and forced to
    /// <c>automation="needs-human"</c> so a downstream apply (if it runs) applies an
    /// honest "this needs a human" set, never a clean <c>priority-normal/feature</c>.
    /// <paramref name="diagnostics"/> is a short, secret-free summary of why the
    /// call failed.
    /// </summary>
    public static PoDecision BuildFailureDecision(string? diagnostics)
    {
        var summary = string.IsNullOrWhiteSpace(diagnostics)
            ? "all providers failed"
            : diagnostics!.Trim();
        return new PoDecision(
            Status: StatusLlmFailed,
            Priority: DefaultPriority,
            Type: DefaultType,
            Complexity: DefaultComplexity,
            Automation: DefaultAutomation,
            Labels: new List<string> { "needs-human", "triage-failed" },
            Comment: $"Triage PO decision could not be produced (LLM call failed); requires human triage. ({summary})",
            Reasoning: null);
    }

    /// <summary>
    /// #7 — the empty-input skip marker. No LLM spend, no fabricated classification;
    /// a loud (skipped) decision a human can pick up.
    /// </summary>
    public static PoDecision BuildSkippedDecision()
        => new PoDecision(
            Status: StatusSkipped,
            Priority: DefaultPriority,
            Type: DefaultType,
            Complexity: DefaultComplexity,
            Automation: DefaultAutomation,
            Labels: new List<string> { "needs-human", "triage-skipped" },
            Comment: "Triage PO decision skipped — empty/missing item input; requires human triage.",
            Reasoning: null);

    /// <summary>
    /// Serialize a <see cref="PoDecision"/> into the <c>decisionJson</c> output
    /// contract the consumer (<c>ApplyTriageResultActivity</c> →
    /// <c>TriageDecision</c>) reads: <c>priority</c>/<c>type</c>/<c>complexity</c>/
    /// <c>automation</c>/<c>labels</c>/<c>comment</c>, additively carrying
    /// <c>status</c> and <c>reasoning</c>. <c>labels</c> is a real JSON array so the
    /// consumer's <c>List&lt;string&gt;</c> binds (the prior code emitted it as a
    /// JSON-string value, which the consumer could not read as labels).
    /// </summary>
    public static string Serialize(PoDecision decision)
    {
        var dict = new Dictionary<string, object?>
        {
            ["status"] = decision.Status,
            ["priority"] = decision.Priority,
            ["type"] = decision.Type,
            ["complexity"] = decision.Complexity,
            ["automation"] = decision.Automation,
            ["labels"] = decision.Labels,
            ["comment"] = decision.Comment,
        };
        if (!string.IsNullOrWhiteSpace(decision.Reasoning))
            dict["reasoning"] = decision.Reasoning;

        return JsonSerializer.Serialize(dict);
    }

    /// <summary>
    /// Best-effort parse of the triage item number out of <c>itemJson</c> for event
    /// tags. Delegates to <see cref="TriageBindingHelper.ParseItemNumber"/> (Story 39-15
    /// relocated the shared parse there when <c>TriagePanelAggregationHelper</c> was deleted)
    /// so every triage stage parses the item number identically.
    /// </summary>
    public static int ParseItemNumber(string? itemJson)
        => TriageBindingHelper.ParseItemNumber(itemJson);

    /// <summary>
    /// Summarize the <c>llm-call</c> failure diagnostics (the <c>workflowOutput</c>
    /// JSON) into a short, SECRET-FREE one-liner for the FAILED event + the failure
    /// decision comment. Reads only the structured <c>errorMessage</c> /
    /// diagnostic fields — never echoes prompt content or credentials. Returns a
    /// generic message on null/unparseable input (the event still emits).
    /// </summary>
    public static string SummarizeFailure(string? workflowOutputJson)
    {
        if (string.IsNullOrWhiteSpace(workflowOutputJson))
            return "all providers failed";
        try
        {
            using var doc = JsonDocument.Parse(workflowOutputJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("errorMessage", out var em) &&
                em.ValueKind == JsonValueKind.String)
            {
                var msg = em.GetString();
                if (!string.IsNullOrWhiteSpace(msg)) return msg!;
            }
        }
        catch
        {
            // fall through
        }
        return "all providers failed";
    }
}

using System.Text.Json;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-14 (Design Decisions D1/D3/D4) — the PURE, Elsa-free decision core of the
/// planning-family bindings: the <c>plan-generation</c> lifecycle binding and the
/// <c>plan-review</c> read-through shim. Mirrors the <see cref="DecompositionBindingHelper"/>
/// / <see cref="AssessmentBindingHelper"/> posture: every function is TOTAL and FAIL-CLOSED —
/// no function throws out of a routing lambda, an empty/unreadable input yields a
/// conservative result, never a fabricated success.
///
/// <para>The typed lifecycle-exit read is SHARED via 39-13's
/// <see cref="LifecycleBindingHelper.ReadLifecycleResult"/> (not duplicated here); this helper
/// carries only the plan-family-specific pieces: the consumed-decomposition carrier merge (D4,
/// the render-drop lesson), the behavior-preserving default acceptance rules (D3), the legacy
/// decision/discussion-log projections the shim needs, and the failure-detail wire.</para>
/// </summary>
public static class PlanBindingHelper
{
    /// <summary>
    /// The issue-identity anchor (D4): <c>"{repository}#{issueNumber}"</c> — the
    /// <c>TriageItemCycleHelper</c> <c>"{repo}#{number}"</c> precedent. Used when the caller
    /// passes no explicit <c>issueId</c> (SingleIssueCycle passes none).
    /// </summary>
    public static string DeriveIssueId(string? repository, int issueNumber)
        => $"{repository ?? string.Empty}#{issueNumber}";

    /// <summary>
    /// Merge the consumed <paramref name="decompositionJson"/> into the DECLARED
    /// <c>contextFindings</c> carrier ahead of <paramref name="poSummary"/> (D4). The
    /// decomposition is folded into a variable the shared Plan-family template ALREADY declares
    /// — NOT a new <c>decompositionJson</c> key, which the ~17-cell shared body does not declare
    /// and would silently drop at render (the <see cref="ValidationFeedbackHelper"/> render-drop
    /// lesson). An empty/whitespace decomposition (legacy runs with no accepted decomposition)
    /// returns <paramref name="poSummary"/> byte-identical, so the rendered prompt is unchanged.
    /// </summary>
    public static string MergeDecompositionIntoCarrier(string? poSummary, string? decompositionJson)
    {
        var summary = poSummary ?? string.Empty;
        if (string.IsNullOrWhiteSpace(decompositionJson))
            return summary;

        var block = "## Accepted decomposition\n" + decompositionJson!.Trim();
        return summary.Length == 0 ? block : block + "\n\n" + summary;
    }

    /// <summary>
    /// The behavior-preserving default acceptance rules for the <c>plan</c> type (D3), used ONLY
    /// when the <c>acceptanceRulesJson</c> input is empty (a stored per-type override always wins
    /// via the passthrough). Carries over today's effective budgets — PlanReview
    /// <c>maxRounds = 3</c> and PlanGeneration <c>maxRetries = 2</c> — over the 7-role plan/review
    /// panel: <c>MaxRevisionRounds = 3</c>, <c>MaxValidationRepairAttempts = 2</c>, panel majority.
    /// (39-5's generic defaults are rounds 2 / repair 2, so the mechanism swap does not silently
    /// change quality/cost — the story technical note.)
    /// </summary>
    public static string DefaultPlanRulesJson()
    {
        var rules = (AcceptanceDefaults.For(DocumentTypeKey.Plan) with
        {
            MaxRevisionRounds = 3,
            MaxValidationRepairAttempts = 2,
        }).Validate();
        return AcceptanceRulesJson.Serialize(rules);
    }

    /// <summary>
    /// The additive <c>decision</c> output for the <c>plan-generation</c> binding (D1): the typed
    /// lifecycle exit maps to the legacy verdict vocabulary — <c>accepted → "approved"</c>,
    /// anything else → <c>"needsHuman"</c> (the shim carries the typed outcome in
    /// <c>reviewNotes</c>).
    /// </summary>
    public static string MapDecisionForLegacyOutput(LifecycleBindingHelper.LifecycleExit exit)
        => MapDecisionForLegacyOutput(LifecycleBindingHelper.IsAccepted(exit));

    /// <summary>
    /// The <c>plan-review</c> shim's legacy <c>decision</c> (D1): an accepted plan present in the
    /// store maps to <c>"approved"</c>; no accepted plan (or an unreadable read) maps to
    /// <c>"needsHuman"</c>. Defer/split retire from the review surface (D2) — the shim never emits
    /// them.
    /// </summary>
    public static string MapDecisionForLegacyOutput(bool accepted)
        => accepted ? "approved" : "needsHuman";

    /// <summary>
    /// Reconstruct a legacy <c>discussionLog</c> projection from the accepted plan's lineage (D5 /
    /// AC6 round-projection half). The lineage carries the accepted <c>documentId</c>, its
    /// <c>revision</c>, and the round numbers; each round projects to one discussion-log entry, so
    /// the round count is reconstructable from the projection. An empty/unreadable lineage yields
    /// <c>"[]"</c> (fail-closed — never a throw).
    /// </summary>
    public static string BuildDiscussionLogProjection(string? lineageJson)
    {
        if (string.IsNullOrWhiteSpace(lineageJson))
            return "[]";

        try
        {
            using var doc = JsonDocument.Parse(lineageJson!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "[]";

            var documentId = root.TryGetProperty("documentId", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";

            var rounds = new List<int>();
            if (root.TryGetProperty("rounds", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in r.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                        rounds.Add(n);
            }

            // No explicit rounds but an accepted revision → one entry per revision.
            if (rounds.Count == 0 && root.TryGetProperty("revision", out var rev) &&
                rev.ValueKind == JsonValueKind.Number && rev.TryGetInt32(out var revN) && revN > 0)
            {
                for (var i = 1; i <= revN; i++) rounds.Add(i);
            }

            var entries = rounds
                .Select(round => (object)new Dictionary<string, object>
                {
                    ["round"] = round,
                    ["type"] = "lifecycle-review-round",
                    ["documentId"] = documentId,
                })
                .ToList();

            return JsonSerializer.Serialize(entries);
        }
        catch (JsonException)
        {
            return "[]";
        }
    }

    /// <summary>
    /// The failure detail for a non-accepted <c>plan-generation</c> exit — names the lifecycle
    /// status and the typed outcome wire so the compat <c>error</c> / <c>reviewNotes</c> outputs
    /// point at a typed escalation (<c>validation-exhausted</c> / <c>rounds-exhausted</c> /
    /// <c>review-undecidable</c>), never a dead terminal.
    /// </summary>
    public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit)
        => string.IsNullOrWhiteSpace(exit.Outcome)
            ? $"Plan lifecycle exited '{exit.Status}' without acceptance."
            : $"Plan lifecycle exited '{exit.Status}' with outcome '{exit.Outcome}'.";
}

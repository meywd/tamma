using System.Text.Json;
using Tamma.Activities.Debug;
using Tamma.Activities.Debug.Models;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-15 (D4) — the PURE, Elsa-free bridge the <c>debug-diagnosis</c> lifecycle
/// binding reads its accepted <see cref="Diagnosis"/> through so <c>DebuggingWorkflow</c>'s
/// fix/test/refine loop stays byte-untouched. The loop consumes a bare
/// <see cref="Hypothesis"/>[] JSON via <c>hypothesesJson</c> (sliced by
/// <c>SelectHypothesisActivity</c>), so this helper PROJECTS the typed
/// <see cref="Diagnosis.Hypotheses"/> onto that legacy shape — it does NOT emit
/// <see cref="Diagnosis.ToLegacyJson"/> (the snake_case wire the retired
/// <c>AIDiagnosisActivity.ParseDiagnosisResponse</c> read), which the loop never sees.
///
/// <para>Every function is TOTAL and FAIL-CLOSED — an unreadable / missing body yields the
/// empty-array projection (<c>"[]"</c>), never a fabricated hypothesis and never a throw out
/// of a routing lambda (the same posture as the sibling binding helpers).</para>
/// </summary>
public static class DiagnosisBindingHelper
{
    /// <summary>
    /// Project an accepted <see cref="Diagnosis"/> body (camelCase document JSON) onto the
    /// bare <see cref="Hypothesis"/>[] JSON the debug loop's <c>hypothesesJson</c> carrier
    /// holds. Each projected hypothesis is <see cref="HypothesisOutcome.Untried"/> so the
    /// loop's select/refine bookkeeping starts clean. Fail-closed <c>"[]"</c> on empty /
    /// unreadable / hypothesis-less input.
    /// </summary>
    public static string ToLegacyHypothesesJson(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
            return "[]";
        try
        {
            var diag = JsonSerializer.Deserialize<Diagnosis>(documentJson!, DocumentJson.Options);
            var hypotheses = diag?.Hypotheses;
            if (hypotheses is null || hypotheses.Count == 0)
                return "[]";

            var projected = hypotheses
                .Select(h => new Hypothesis
                {
                    Rank = h.Rank,
                    Description = h.Description ?? "",
                    Confidence = h.Confidence,
                    SuggestedFix = h.SuggestedFix ?? "",
                    AffectedFiles = (h.AffectedFiles ?? new List<string>()).ToList(),
                    Outcome = HypothesisOutcome.Untried,
                })
                .ToList();

            return JsonSerializer.Serialize(projected);
        }
        catch (JsonException)
        {
            return "[]";
        }
    }

    /// <summary>
    /// Whether the accepted diagnosis carries at least one usable hypothesis (the caller
    /// gate <c>DebuggingWorkflow</c> routes on to <c>DEBUG.DIAGNOSIS.SUCCESS</c> vs
    /// <c>.FAILED</c>). A projected <c>"[]"</c> — an empty / unreadable diagnosis — is NOT
    /// produced (fail-closed), preserving the retired activity's no-false-success contract.
    /// </summary>
    public static bool HasUsableHypotheses(string? legacyHypothesesJson)
        => !string.IsNullOrWhiteSpace(legacyHypothesesJson)
           && legacyHypothesesJson!.Trim() is var t
           && t.Length > 2
           && t != "[]";

    /// <summary>
    /// Map a non-accepted lifecycle exit onto the <c>DEBUG.DIAGNOSIS.FAILED</c> reason wire
    /// (<see cref="DebugEvents"/>): a <c>validation-exhausted</c> escalation maps onto the
    /// parse-failure equivalent (the model never produced a valid Diagnosis), any other
    /// non-accept exit onto the call-failed reason. Never an empty string — a failed
    /// diagnosis always carries a reason (no silent failure).
    /// </summary>
    public static string BuildFailureReason(LifecycleBindingHelper.LifecycleExit exit)
    {
        var outcome = exit.Outcome ?? "";
        return outcome == DocumentLifecycleOutcome.ValidationExhausted.ToWire()
            ? DebugEvents.ReasonDiagnosisParseFailure
            : DebugEvents.ReasonDiagnosisCallFailed;
    }
}

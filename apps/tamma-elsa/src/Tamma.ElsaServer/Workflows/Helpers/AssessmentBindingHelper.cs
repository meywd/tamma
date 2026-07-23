using System.Text.Json;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-13 (D4) — the PURE, Elsa-free decision core of the four assessment-family
/// lifecycle bindings (Research → <see cref="Findings"/>, Ambiguity →
/// <see cref="AmbiguityAssessment"/>, Clarify → <see cref="Clarification"/>, Design →
/// <see cref="Design"/>). Mirrors the <see cref="DecompositionBindingHelper"/> /
/// <see cref="LifecycleBindingHelper"/> posture: every function is TOTAL and FAIL-CLOSED —
/// an unreadable / missing payload yields conservative zeros, never a throw out of a routing
/// lambda. The typed-payload reads project the lifecycle's <c>documentJson</c> body back onto
/// the four bindings' legacy scalar outputs (finding count, score, question count, …).
/// </summary>
public static class AssessmentBindingHelper
{
    /// <summary>Read (findingCount, overallConfidence) from a <see cref="Findings"/> payload.
    /// Fail-closed zeros on any unreadable / empty / non-findings body.</summary>
    public static (int FindingCount, double Confidence) ReadFindings(string? documentJson)
    {
        var doc = TryDeserialize<Findings>(documentJson);
        if (doc is null) return (0, 0d);
        return (doc.Items?.Count ?? 0, (double)doc.OverallConfidence);
    }

    /// <summary>Read (score, ambiguityCount, confidence) from an <see cref="AmbiguityAssessment"/>
    /// payload. Fail-closed zeros on any unreadable body.</summary>
    public static (double Score, int AmbiguityCount, double Confidence) ReadAssessment(string? documentJson)
    {
        var doc = TryDeserialize<AmbiguityAssessment>(documentJson);
        if (doc is null) return (0d, 0, 0d);
        return ((double)doc.Score, doc.Ambiguities?.Count ?? 0, (double)doc.Confidence);
    }

    /// <summary>
    /// The effective ambiguity-escalation threshold (39-5). Reads the resolved
    /// <c>ambiguityEscalationThreshold</c> from the passthrough acceptance-rules json; an empty
    /// / unreadable json falls back to <see cref="AcceptanceDefaults.DefaultAmbiguityEscalationThreshold"/>
    /// (0.7). Compat-only — this feeds the binding's legacy <c>threshold</c> output; the
    /// routing decision lives in the lifecycle/policy machinery, never here.
    /// </summary>
    public static double EffectiveAmbiguityThreshold(string? acceptanceRulesJson)
    {
        if (string.IsNullOrWhiteSpace(acceptanceRulesJson))
            return AcceptanceDefaults.DefaultAmbiguityEscalationThreshold;
        try
        {
            return AcceptanceRulesJson.Deserialize(acceptanceRulesJson!).AmbiguityEscalationThreshold;
        }
        catch
        {
            return AcceptanceDefaults.DefaultAmbiguityEscalationThreshold;
        }
    }

    /// <summary>Whether the lifecycle exited on the typed <c>ambiguity-above-threshold</c> wire.</summary>
    public static bool IsAmbiguityOutcome(LifecycleBindingHelper.LifecycleExit exit)
        => LifecycleBindingHelper.IsAmbiguityOutcome(exit);

    /// <summary>Read (questionCount, resolved) from a <see cref="Clarification"/> payload,
    /// phase-aware: the questions phase carries the questions array; the resolution phase
    /// carries <c>resolved</c>. Fail-closed zeros / false on any unreadable body.</summary>
    public static (int QuestionCount, bool Resolved) ReadClarification(string? documentJson)
    {
        var doc = TryDeserialize<Clarification>(documentJson);
        if (doc is null) return (0, false);
        return (doc.Questions?.Count ?? 0, doc.Resolved);
    }

    /// <summary>Count the alternatives in a <see cref="Design"/> payload. Fail-closed 0 on any
    /// unreadable / non-design body.</summary>
    public static int CountAlternatives(string? documentJson)
    {
        var doc = TryDeserialize<Design>(documentJson);
        return doc?.Alternatives?.Count ?? 0;
    }

    /// <summary>The failure detail for a non-accepted exit — names the lifecycle status and the
    /// typed outcome wire so the LOUD family FAILED event points at a typed escalation, not a
    /// dead terminal.</summary>
    public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit)
        => string.IsNullOrWhiteSpace(exit.Outcome)
            ? $"Document lifecycle exited '{exit.Status}' without acceptance."
            : $"Document lifecycle exited '{exit.Status}' with outcome '{exit.Outcome}'.";

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json!, DocumentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

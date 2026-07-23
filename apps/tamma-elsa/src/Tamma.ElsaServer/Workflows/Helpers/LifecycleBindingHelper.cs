using System.Text.Json;
using Tamma.Core.Documents;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-13 (D4) — the PURE, Elsa-free shared core every <c>document-lifecycle</c>
/// binding reads its dispatch result through. Promoted out of 39-12's
/// <see cref="DecompositionBindingHelper"/> so the decomposition binding and the four
/// assessment-family bindings share ONE fail-closed reader. Every function is TOTAL and
/// FAIL-CLOSED — an unreadable / missing dispatch result yields a typed escalated exit
/// (never a silent success), and no function throws out of a routing lambda.
/// </summary>
public static class LifecycleBindingHelper
{
    /// <summary>
    /// The typed read of the <c>document-lifecycle</c> dispatch result. <see cref="Status"/>
    /// is the lifecycle status wire (<c>accepted</c> / <c>rejected</c> / <c>escalated</c>);
    /// <see cref="Outcome"/> is the typed escalation wire (non-null only on <c>escalated</c>);
    /// <see cref="DocumentJson"/> is the terminal revision's payload body (the 39-12 D4 /
    /// 39-13 D6a hook), <c>"{}"</c> when absent; <see cref="DecisionNotes"/> is the decider's
    /// notes (the 39-13 D6d hook), <c>""</c> when absent.
    /// </summary>
    public sealed record LifecycleExit(
        string Status, string? Outcome, string? DocumentId, string DocumentJson, string DecisionNotes);

    /// <summary>
    /// Fail-closed tolerant read of the <c>DispatchWorkflow</c> result dictionary (boxed /
    /// string / <see cref="JsonElement"/>). A null / missing / unreadable status yields an
    /// <c>escalated</c> exit carrying <c>validation-exhausted</c> — NEVER a silent success.
    /// </summary>
    public static LifecycleExit ReadLifecycleResult(IDictionary<string, object>? result)
    {
        if (result is null)
            return FailClosed();

        var status = ReadString(result, "status");
        if (string.IsNullOrWhiteSpace(status))
            return FailClosed();

        var outcome = ReadString(result, "outcome");
        var documentId = ReadString(result, "documentId");
        var documentJson = ReadString(result, "documentJson");
        var decisionNotes = ReadString(result, "decisionNotes");

        return new LifecycleExit(
            status!,
            string.IsNullOrWhiteSpace(outcome) ? null : outcome,
            string.IsNullOrWhiteSpace(documentId) ? null : documentId,
            string.IsNullOrWhiteSpace(documentJson) ? "{}" : documentJson!,
            decisionNotes ?? "");
    }

    /// <summary>Whether the lifecycle accepted the document (<c>status == accepted</c>).</summary>
    public static bool IsAccepted(LifecycleExit exit)
        => string.Equals(exit.Status, DocumentLifecycleResult.StatusAccepted, StringComparison.Ordinal);

    /// <summary>Whether the lifecycle exited on the typed <c>ambiguity-above-threshold</c> wire.</summary>
    public static bool IsAmbiguityOutcome(LifecycleExit exit)
        => string.Equals(exit.Outcome, DocumentLifecycleOutcome.AmbiguityAboveThreshold.ToWire(), StringComparison.Ordinal);

    private static LifecycleExit FailClosed()
        => new(
            DocumentLifecycleResult.StatusEscalated,
            DocumentLifecycleOutcome.ValidationExhausted.ToWire(),
            null,
            "{}",
            "");

    /// <summary>Tolerant string read of a dispatched-result value (boxed / string / JsonElement).</summary>
    public static string? ReadString(IDictionary<string, object> result, string key)
    {
        if (!result.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
            _ => value.ToString(),
        };
    }
}

using System.Text.Json;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-12 (Design Decisions D1/D2/D3) — the PURE, Elsa-free decision core of the
/// <c>issue-decomposition</c> lifecycle binding. It mirrors the
/// <see cref="TriagePoDecisionHelper"/> / <see cref="DocumentLifecycleHelper"/> posture:
/// every function is TOTAL and FAIL-CLOSED — an unreadable / missing dispatch result
/// yields a typed escalated exit (never a silent success), and no function throws out of
/// a routing lambda.
///
/// <para>The binding dispatches <c>document-lifecycle</c> and reads back its output
/// dictionary (<c>status</c> / <c>outcome</c> / <c>documentId</c> / the 39-12 D4
/// <c>documentJson</c> accepted-payload hook). The three reachable non-success wires the
/// lifecycle can hand back are <c>validation-exhausted</c>, <c>rounds-exhausted</c>,
/// <c>review-undecidable</c> (plus a first-class <c>rejected</c> status); this helper
/// maps the dictionary onto those and names them for the mirrored
/// <c>DECOMPOSITION.FAILED</c> event.</para>
/// </summary>
public static class DecompositionBindingHelper
{
    /// <summary>
    /// The typed read of the <c>document-lifecycle</c> dispatch result. <see cref="Status"/>
    /// is the lifecycle status wire (<c>accepted</c> / <c>rejected</c> / <c>escalated</c>);
    /// <see cref="Outcome"/> is the typed escalation wire (non-null only on
    /// <c>escalated</c>); <see cref="DocumentJson"/> is the accepted revision's payload body
    /// (the D4 hook), <c>"{}"</c> when absent.
    /// </summary>
    public sealed record LifecycleExit(string Status, string? Outcome, string? DocumentId, string DocumentJson);

    /// <summary>
    /// Fail-closed tolerant read of the <c>DispatchWorkflow</c> result dictionary (boxed /
    /// string / <see cref="JsonElement"/>). A null / missing / unreadable status yields an
    /// <c>escalated</c> exit carrying <c>validation-exhausted</c> — NEVER a silent success,
    /// so a lost lifecycle result can only escalate loud, never fabricate an accepted
    /// decomposition.
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

        return new LifecycleExit(
            status!,
            string.IsNullOrWhiteSpace(outcome) ? null : outcome,
            string.IsNullOrWhiteSpace(documentId) ? null : documentId,
            string.IsNullOrWhiteSpace(documentJson) ? "{}" : documentJson!);
    }

    /// <summary>Whether the lifecycle accepted the document (<c>status == accepted</c>).</summary>
    public static bool IsAccepted(LifecycleExit exit)
        => string.Equals(exit.Status, DocumentLifecycleResult.StatusAccepted, StringComparison.Ordinal);

    /// <summary>
    /// Count the subtasks in an accepted decomposition payload. Deserializes the payload
    /// through <see cref="DocumentJson.Options"/> into the typed <see cref="Decomposition"/>;
    /// returns 0 on any unreadable / empty / non-decomposition body (fail-closed — a bad
    /// body never inflates the audited count).
    /// </summary>
    public static int CountSubtasks(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
            return 0;
        try
        {
            var doc = JsonSerializer.Deserialize<Decomposition>(documentJson, DocumentJson.Options);
            return doc?.Subtasks?.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The <c>DECOMPOSITION.FAILED</c> detail for a non-accepted exit — names the lifecycle
    /// status and the typed outcome wire so the LOUD event points at a typed escalation
    /// (AC4's parenthetical), not a dead terminal.
    /// </summary>
    public static string BuildFailureDetail(LifecycleExit exit)
        => string.IsNullOrWhiteSpace(exit.Outcome)
            ? $"Issue decomposition lifecycle exited '{exit.Status}' without acceptance."
            : $"Issue decomposition lifecycle exited '{exit.Status}' with outcome '{exit.Outcome}'.";

    private static LifecycleExit FailClosed()
        => new(
            DocumentLifecycleResult.StatusEscalated,
            DocumentLifecycleOutcome.ValidationExhausted.ToWire(),
            null,
            "{}");

    /// <summary>Tolerant string read of a dispatched-result value (boxed / string / JsonElement).</summary>
    private static string? ReadString(IDictionary<string, object> result, string key)
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

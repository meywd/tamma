using System.Collections.Frozen;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// The data-driven document lifecycle state machine (Story 39-2 AC2/AC7). The
/// legal-transition map (Design Decision D4) is the single enforcement seam the
/// 39-6 lifecycle workflow consumes — written once, here.
///
/// <para>Legal map:</para>
/// <list type="bullet">
/// <item><c>Draft   → { Validated, Escalated }</c></item>
/// <item><c>Validated → { Reviewed, Escalated }</c></item>
/// <item><c>Reviewed → { Accepted, Rejected, Escalated }</c></item>
/// <item><c>Accepted / Rejected / Escalated → ∅</c> (terminal)</item>
/// </list>
/// <c>Escalated</c> is reachable from every non-terminal state (D4): the typed
/// unhandleable outcomes escalate with the full lineage attached rather than
/// rejecting. A revision mints a NEW envelope in the supersedes chain, so no
/// <c>Reviewed → Draft</c> rewind edge exists.
/// </summary>
public static class DocumentStateMachine
{
    private static readonly FrozenDictionary<DocumentState, FrozenSet<DocumentState>> s_legal =
        new Dictionary<DocumentState, FrozenSet<DocumentState>>
        {
            [DocumentState.Draft]     = FreezeSet(DocumentState.Validated, DocumentState.Escalated),
            [DocumentState.Validated] = FreezeSet(DocumentState.Reviewed, DocumentState.Escalated),
            [DocumentState.Reviewed]  = FreezeSet(DocumentState.Accepted, DocumentState.Rejected, DocumentState.Escalated),
            [DocumentState.Accepted]  = FrozenSet<DocumentState>.Empty,
            [DocumentState.Rejected]  = FrozenSet<DocumentState>.Empty,
            [DocumentState.Escalated] = FrozenSet<DocumentState>.Empty,
        }.ToFrozenDictionary();

    /// <summary>
    /// The legal-transition map, exposed read-only for the 39-6 lifecycle
    /// workflow to walk. Keyed by source state; the value is the set of legal
    /// destination states (empty for terminals).
    /// </summary>
    public static IReadOnlyDictionary<DocumentState, FrozenSet<DocumentState>> LegalTransitions => s_legal;

    /// <summary>
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a legal
    /// transition. Non-throwing.
    /// </summary>
    public static bool CanTransition(DocumentState from, DocumentState to) =>
        s_legal.TryGetValue(from, out var destinations) && destinations.Contains(to);

    /// <summary>
    /// Assert that <paramref name="from"/> → <paramref name="to"/> is legal.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.STATE.ILLEGAL_TRANSITION</c> — the message names BOTH
    /// state wire strings (AC7).
    /// </exception>
    public static void AssertTransition(DocumentState from, DocumentState to)
    {
        if (CanTransition(from, to)) return;

        var allowed = s_legal[from];
        var allowedText = allowed.Count == 0
            ? "none (terminal state)"
            : string.Join(", ", allowed.Select(s => $"'{s.ToWire()}'"));

        throw new TammaError(
            "DOCUMENT.STATE.ILLEGAL_TRANSITION",
            $"Illegal document state transition: '{from.ToWire()}' -> '{to.ToWire()}'. " +
            $"Legal transitions from '{from.ToWire()}': {allowedText}.",
            new Dictionary<string, object?>
            {
                ["from"] = from.ToWire(),
                ["to"] = to.ToWire(),
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// Whether <paramref name="state"/> is terminal (has no legal outbound
    /// transitions): <c>Accepted</c>, <c>Rejected</c>, or <c>Escalated</c>.
    /// </summary>
    public static bool IsTerminal(DocumentState state) =>
        s_legal.TryGetValue(state, out var destinations) && destinations.Count == 0;

    private static FrozenSet<DocumentState> FreezeSet(params DocumentState[] items) =>
        new HashSet<DocumentState>(items).ToFrozenSet();
}

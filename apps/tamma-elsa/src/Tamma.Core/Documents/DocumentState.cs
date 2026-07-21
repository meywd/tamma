using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// The document lifecycle states (Story 39-2 AC2). The legal transitions between
/// them live in <see cref="DocumentStateMachine"/>; this enum is only the
/// vocabulary. Each member carries its canonical wire string via <c>[Wire]</c>.
///
/// <para>
/// <c>Accepted</c>, <c>Rejected</c>, and <c>Escalated</c> are terminal — they
/// have no outbound transitions. A revision does not rewind a state; it mints a
/// new envelope in the <c>SupersedesDocumentId</c> chain (Design Decision D4).
/// </para>
/// </summary>
public enum DocumentState
{
    [Wire("draft")]     Draft,
    [Wire("validated")] Validated,
    [Wire("reviewed")]  Reviewed,
    [Wire("accepted")]  Accepted,
    [Wire("rejected")]  Rejected,
    [Wire("escalated")] Escalated,
}

public static class DocumentStateExtensions
{
    /// <summary>The canonical wire string for <paramref name="state"/>.</summary>
    public static string ToWire(this DocumentState state) => EnumWire<DocumentState>.ToWire(state);

    /// <summary>
    /// Resolves a wire string to a <see cref="DocumentState"/> (case-sensitive).
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.STATE.UNKNOWN</c> for null, empty, or unknown input.
    /// </exception>
    public static DocumentState Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) && EnumWire<DocumentState>.TryParse(input, out var state))
            return state;

        throw new TammaError(
            "DOCUMENT.STATE.UNKNOWN",
            $"Unknown document state: '{input}'. Valid states: {string.Join(", ", Enum.GetValues<DocumentState>().Select(s => s.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}

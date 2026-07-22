using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// How an escalation was ultimately dispositioned (Story 39-8 AC1 — the
/// <c>ESCALATION.RESOLVED</c> clause). A NEW <c>[Wire]</c> enum OWNED HERE by
/// 39-8 (a drift-tested closed set; 39-18's typed messages reuse it):
///
/// <list type="bullet">
/// <item><c>resolved</c> — the escalation was handled and the underlying decision
///   stands (the exception is closed on its merits).</item>
/// <item><c>overridden</c> — a human overrode the escalated outcome (e.g. forced
///   an accept/reject the autonomous path could not take).</item>
/// <item><c>abandoned</c> — the escalation was closed without a substantive
///   decision (the work was dropped / superseded).</item>
/// </list>
///
/// <para><see cref="ApprovalChannel"/> (<c>orchestrator | user | api</c>) is a
/// SEPARATE enum DEFINED BY 39-5 (<c>Tamma.Core/Documents/ApprovalChannel.cs</c>)
/// — it is referenced by 39-8, never redeclared here.</para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<EscalationDisposition>))]
public enum EscalationDisposition
{
    [Wire("resolved")]   Resolved,
    [Wire("overridden")] Overridden,
    [Wire("abandoned")]  Abandoned,
}

/// <summary><see cref="EscalationDisposition"/> wire helper.</summary>
public static class EscalationDispositionExtensions
{
    /// <summary>The canonical wire string for <paramref name="disposition"/>.</summary>
    public static string ToWire(this EscalationDisposition disposition) =>
        EnumWire<EscalationDisposition>.ToWire(disposition);

    /// <summary>
    /// Resolves a wire string to an <see cref="EscalationDisposition"/>.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>ESCALATION.DISPOSITION.UNKNOWN</c> for null, empty, or unknown input.
    /// </exception>
    public static EscalationDisposition Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) && EnumWire<EscalationDisposition>.TryParse(input, out var disposition))
            return disposition;

        throw new TammaError(
            "ESCALATION.DISPOSITION.UNKNOWN",
            $"Unknown escalation disposition: '{input}'. Valid dispositions: {string.Join(", ", Enum.GetValues<EscalationDisposition>().Select(d => d.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>Non-throwing wire lookup for an <see cref="EscalationDisposition"/>.</summary>
    public static bool TryParse(string? input, out EscalationDisposition disposition)
    {
        if (!string.IsNullOrWhiteSpace(input))
            return EnumWire<EscalationDisposition>.TryParse(input, out disposition);
        disposition = default;
        return false;
    }
}

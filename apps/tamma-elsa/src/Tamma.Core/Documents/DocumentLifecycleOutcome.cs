using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// The closed set of typed "unhandleable" lifecycle outcomes that force an
/// escalation rather than a bare failure (Design Decision D5; consumed by Story
/// 39-6). Each fires from a different lifecycle stage:
/// <list type="bullet">
/// <item><c>ValidationExhausted</c> — deterministic validation could not be
/// satisfied within the repair-ring budget (fires from Draft).</item>
/// <item><c>ReviewUndecidable</c> — the review panel could not reach a decision
/// (fires from Validated).</item>
/// <item><c>RoundsExhausted</c> — the review/revision rounds ran out (fires from
/// Reviewed).</item>
/// <item><c>AmbiguityAboveThreshold</c> — the input ambiguity score exceeded the
/// autonomy threshold.</item>
/// </list>
/// Shipped here in <c>Tamma.Core/Documents</c> so 39-6 has a compile-time target;
/// pinned by a drift test.
/// </summary>
public enum DocumentLifecycleOutcome
{
    [Wire("review-undecidable")]        ReviewUndecidable,
    [Wire("ambiguity-above-threshold")] AmbiguityAboveThreshold,
    [Wire("rounds-exhausted")]          RoundsExhausted,
    [Wire("validation-exhausted")]      ValidationExhausted,
}

public static class DocumentLifecycleOutcomeExtensions
{
    /// <summary>The canonical wire string for <paramref name="outcome"/>.</summary>
    public static string ToWire(this DocumentLifecycleOutcome outcome) =>
        EnumWire<DocumentLifecycleOutcome>.ToWire(outcome);

    /// <summary>
    /// Resolves a wire string to a <see cref="DocumentLifecycleOutcome"/>.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.OUTCOME.UNKNOWN</c> for null, empty, or unknown input.
    /// </exception>
    public static DocumentLifecycleOutcome Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) && EnumWire<DocumentLifecycleOutcome>.TryParse(input, out var outcome))
            return outcome;

        throw new TammaError(
            "DOCUMENT.OUTCOME.UNKNOWN",
            $"Unknown document lifecycle outcome: '{input}'. Valid outcomes: {string.Join(", ", Enum.GetValues<DocumentLifecycleOutcome>().Select(o => o.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}

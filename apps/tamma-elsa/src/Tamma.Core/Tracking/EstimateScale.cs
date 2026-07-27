using Tamma.Api.Services.Agents;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC13 — the closed estimate-scale vocabulary. Count-pinned at 5 by
/// <c>EstimateScaleTests</c>. This is <b>project configuration</b> (stored on
/// <c>Project</c> by 44-1); the work item stores a scale-free <c>Estimate</c>
/// (<c>decimal?</c>) — <b>not</b> <c>EstimateHours</c>. Naming the scale in the
/// column would make changing scale a migration and mixing scales across
/// projects impossible; and every scale Linear ships pointedly excludes hours,
/// because an hours-shaped estimate invites the reading that the number is a
/// commitment.
///
/// <para>Nothing reads <c>Estimate</c> in v1 (estimation/velocity/burndown are
/// Epic 36's); it is stored so the history exists when something does.</para>
/// </summary>
public enum EstimateScale
{
    [Wire("not_used")] NotUsed,
    [Wire("linear")] Linear,
    [Wire("fibonacci")] Fibonacci,
    [Wire("exponential")] Exponential,
    [Wire("t_shirt")] TShirt,
}

public static class EstimateScaleExtensions
{
    /// <summary>The canonical wire string for <paramref name="scale"/>.</summary>
    public static string ToWire(this EstimateScale scale) => EnumWire<EstimateScale>.ToWire(scale);

    /// <summary>Case-sensitive (ordinal) lookup of the member for <paramref name="wire"/>.</summary>
    public static bool TryParse(string wire, out EstimateScale scale) =>
        EnumWire<EstimateScale>.TryParse(wire, out scale);

    /// <summary>
    /// Resolve a wire string to an <see cref="EstimateScale"/> (case-sensitive, ordinal).
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>TRACKER.UNKNOWN_ESTIMATE_SCALE</c> for null, empty, or unknown input.
    /// </exception>
    public static EstimateScale Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) && EnumWire<EstimateScale>.TryParse(input, out var scale))
            return scale;

        throw new TammaError(
            "TRACKER.UNKNOWN_ESTIMATE_SCALE",
            $"Unknown estimate scale: '{input}'. Valid scales: " +
            $"{string.Join(", ", Enum.GetValues<EstimateScale>().Select(s => s.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// The single definition of estimate/scale coherence: an estimate is always
    /// optional, but a project whose scale is <see cref="EstimateScale.NotUsed"/>
    /// must not carry one — <c>(EstimateScale=not_used, Estimate=5)</c> is
    /// representable storage-wise and meaningless, the same defect class as
    /// <c>(Kind=Bug, Type=Feature)</c> that AC1 deletes
    /// (<c>.dev/findings/linear-comparison-against-story-44-0.md</c>, "Found
    /// while applying"). 44-2 enforces this rule at the API boundary; it lives
    /// here so it has exactly one implementation.
    /// </summary>
    public static bool AllowsEstimate(this EstimateScale scale, decimal? estimate) =>
        estimate is null || scale != EstimateScale.NotUsed;
}

using System.Text.Json.Serialization;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// A single deterministic-validation violation (Story 39-2 AC3). The interface
/// between deterministic validation and the 39-9 LLM repair turn / 39-6 review
/// notes: <see cref="Code"/> is a stable machine identifier (e.g.
/// <c>DANGLING_DEPENDS_ON</c>); <see cref="Message"/> is domain-phrased for the
/// model, never a bare schema path.
/// </summary>
public sealed record DocumentViolation(
    [property: JsonPropertyName("code")]    string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// The result of <see cref="IDocumentType.Validate"/> (Story 39-2 AC3): a valid
/// flag plus the ordered violation list (empty when valid).
/// </summary>
public sealed record DocumentValidationResult(
    [property: JsonPropertyName("isValid")]    bool IsValid,
    [property: JsonPropertyName("violations")] IReadOnlyList<DocumentViolation> Violations)
{
    private static readonly IReadOnlyList<DocumentViolation> s_none = Array.Empty<DocumentViolation>();

    /// <summary>A passing result with no violations.</summary>
    public static DocumentValidationResult Valid() => new(true, s_none);

    /// <summary>
    /// A failing result carrying at least one violation.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.VALIDATION.EMPTY_INVALID</c> if no violations are
    /// supplied — an invalid result without a reason is a programming error.
    /// </exception>
    public static DocumentValidationResult Invalid(params DocumentViolation[] violations)
    {
        if (violations is null || violations.Length == 0)
            throw new TammaError(
                "DOCUMENT.VALIDATION.EMPTY_INVALID",
                "An invalid DocumentValidationResult must carry at least one violation.",
                retryable: false,
                severity: TammaErrorSeverity.High);

        return new DocumentValidationResult(false, violations);
    }
}

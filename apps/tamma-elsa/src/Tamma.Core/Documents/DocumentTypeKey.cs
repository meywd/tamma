// NOTE: New Epic 39 code lands in the clean `Tamma.Core.Documents` namespace
// (Design Decision D1). The `[Wire]` attribute + `EnumWire<T>` bidirectional
// map live in the legacy `Tamma.Api.Services.Agents` namespace inside this same
// Tamma.Core assembly (see the relocation NOTE atop Agents/EnumWire.cs), so this
// file imports them explicitly rather than reimplementing the pattern.
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// The closed, compile-time vocabulary of Epic 39 document types (README's
/// 10-type table). Mirrors the <see cref="AgentRole"/> / <see cref="AgentAction"/>
/// pattern: each member carries its canonical kebab-case wire string via
/// <c>[Wire]</c> (Design Decision D2), and the set is count-pinned by a drift test.
///
/// <para>
/// This enum is the vocabulary; the <see cref="DocumentTypeRegistry"/> maps keys
/// to <see cref="IDocumentType"/> implementations (which arrive in 39-3/39-4).
/// </para>
/// </summary>
public enum DocumentTypeKey
{
    [Wire("findings")]             Findings,
    [Wire("ambiguity-assessment")] AmbiguityAssessment,
    [Wire("clarification")]        Clarification,
    [Wire("decomposition")]        Decomposition,
    [Wire("plan")]                 Plan,
    [Wire("design")]               Design,
    [Wire("review")]               Review,
    [Wire("triage-decision")]      TriageDecision,
    [Wire("diagnosis")]            Diagnosis,
    [Wire("test-spec")]            TestSpec,

    // Story 41-1b — the six Epic 41 document types (epic README's new-types
    // table). Registered atomically with their IDocumentType implementations
    // (the registry's Every_vocabulary_key_now_resolves_to_an_implementation
    // gate forbids a partial land).
    [Wire("acceptance-criteria")]  AcceptanceCriteria,
    [Wire("backlog-ordering")]     BacklogOrdering,
    [Wire("sprint-plan")]          SprintPlan,
    [Wire("test-plan")]            TestPlan,
    [Wire("threat-model")]         ThreatModel,
    [Wire("ux-spec")]              UxSpec,

    // Story 41-1c — the prose family's single type: kind + audience from closed
    // vocabularies, body deliberately unvalidated markdown ("prose stays prose").
    // Registered atomically with ProseDocumentType (same gate as the 41-1b six).
    [Wire("prose")]                Prose,
}

public static class DocumentTypeKeyExtensions
{
    /// <summary>The canonical wire string for <paramref name="key"/>.</summary>
    public static string ToWire(this DocumentTypeKey key) => EnumWire<DocumentTypeKey>.ToWire(key);

    /// <summary>
    /// Resolves a wire string to a <see cref="DocumentTypeKey"/>. Case-sensitive
    /// (ordinal) — non-canonical casing is rejected, not silently accepted.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.TYPE.UNKNOWN</c> if <paramref name="input"/> is null,
    /// empty, or not a canonical wire string.
    /// </exception>
    public static DocumentTypeKey Parse(string input)
    {
        if (TryParse(input, out var key)) return key;

        throw new TammaError(
            "DOCUMENT.TYPE.UNKNOWN",
            $"Unknown document type: '{input}'. Valid types: {string.Join(", ", Enum.GetValues<DocumentTypeKey>().Select(k => k.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// Non-throwing wire-string lookup. Returns <c>false</c> for null, empty, or
    /// unknown input.
    /// </summary>
    public static bool TryParse(string? input, out DocumentTypeKey key)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            key = default;
            return false;
        }
        return EnumWire<DocumentTypeKey>.TryParse(input, out key);
    }
}

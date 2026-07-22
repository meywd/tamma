namespace Tamma.Core.Documents;

/// <summary>
/// A named example payload for an <see cref="IDocumentType"/> (Story 39-2 AC3).
/// Each type ships ≥1 valid and ≥1 invalid example (enforced by the registry
/// drift test); examples feed contract rendering and are self-checked against the
/// type's own <see cref="IDocumentType.Validate"/>.
/// </summary>
/// <param name="Name">A stable, human-readable example name.</param>
/// <param name="IsValid">Whether the example is expected to pass <see cref="IDocumentType.Validate"/>.</param>
/// <param name="PayloadJson">The example payload JSON.</param>
/// <param name="ExpectedViolationCodes">
/// For an invalid example, the EXACT set of violation codes
/// <see cref="IDocumentType.Validate"/> must emit for this payload (Story 39-3
/// Design Decision D9 — additive over the 39-2 record). Empty for valid examples;
/// the registry drift loop asserts the emitted codes match this set exactly.
/// </param>
public sealed record DocumentExample(
    string Name,
    bool IsValid,
    string PayloadJson,
    IReadOnlyList<string> ExpectedViolationCodes)
{
    /// <summary>
    /// Convenience overload for examples that declare no expected codes (all valid
    /// examples, and pre-39-3 call sites) — defaults <see cref="ExpectedViolationCodes"/>
    /// to empty.
    /// </summary>
    public DocumentExample(string name, bool isValid, string payloadJson)
        : this(name, isValid, payloadJson, Array.Empty<string>())
    {
    }
}

using System.Text.Json;

namespace Tamma.Core.Documents;

/// <summary>
/// The contract every Epic 39 document type implements (Story 39-2 AC3;
/// implementations arrive in 39-3/39-4). Binds a <see cref="DocumentTypeKey"/>
/// wire string to executable validation, a deterministic prompt-contract
/// renderer, and self-checking examples.
/// </summary>
public interface IDocumentType
{
    /// <summary>
    /// The type's key — a <see cref="DocumentTypeKey"/> wire string (drift-tested
    /// against the vocabulary by the registry).
    /// </summary>
    string Key { get; }

    /// <summary>The payload schema version this type validates against.</summary>
    int SchemaVersion { get; }

    /// <summary>The CLR type the JSON payload deserializes to.</summary>
    Type PayloadClrType { get; }

    /// <summary>
    /// Deterministically validate <paramref name="payload"/>, returning
    /// domain-phrased violations (never bare schema paths) so 39-9's repair ring
    /// can feed them back to the model.
    /// </summary>
    DocumentValidationResult Validate(JsonElement payload);

    /// <summary>
    /// Render the prompt-contract block describing the required output shape. MUST
    /// be deterministic (stable ordering) — 39-16 diffs this output in CI.
    /// </summary>
    string RenderContract();

    /// <summary>
    /// The type's examples — ≥1 valid and ≥1 invalid (enforced by the registry
    /// drift test), used by contract rendering and tests.
    /// </summary>
    IReadOnlyList<DocumentExample> Examples { get; }
}

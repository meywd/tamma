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
    /// Story 39-15 (D3) — the cross-document validation seam. Validate
    /// <paramref name="payload"/> WITH an optional sibling-document context
    /// (<paramref name="validationContextJson"/>) so a type can check a binding
    /// that cannot be seen payload-only — e.g. a <c>test-spec</c> case referencing a
    /// task that does not exist in its consumed <c>plan</c>. Additive default
    /// interface member: every existing type is source-compatible and falls back to
    /// the context-free <see cref="Validate"/>. Only types that own a cross-document
    /// rule (currently <c>test-spec</c>) override it. The lifecycle forwards a
    /// non-empty context to this member from VALIDATE; an empty context is a no-op.
    /// </summary>
    DocumentValidationResult ValidateWithContext(JsonElement payload, string validationContextJson)
        => Validate(payload);

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

namespace Tamma.Core.Documents;

/// <summary>
/// A named example payload for an <see cref="IDocumentType"/> (Story 39-2 AC3).
/// Each type ships ≥1 valid and ≥1 invalid example (enforced by the registry
/// drift test); examples feed contract rendering and are self-checked against the
/// type's own <see cref="IDocumentType.Validate"/>.
/// </summary>
public sealed record DocumentExample(string Name, bool IsValid, string PayloadJson);

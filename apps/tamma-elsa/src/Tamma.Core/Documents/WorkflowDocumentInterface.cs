using System.Text.RegularExpressions;

namespace Tamma.Core.Documents;

/// <summary>
/// A static declaration of one producing workflow's document interface: which
/// document type keys it <see cref="Consumes"/> and the single key it
/// <see cref="Produces"/> (Story 39-2 AC4; Design Decision D6). Declarations are
/// keyed by <see cref="DocumentTypeKey"/> enum members, so a declaration can
/// never reference a type outside the compile-time vocabulary.
///
/// <para>
/// <see cref="Provisional"/> is <c>true</c> until each edge is reconciled against
/// the landed 39-1 workflow-io audit; the 39-1 PR (or a small follow-up) flips
/// the flags off. Until then the seed is derived from the README document-type
/// table mapped to real <c>DefinitionId</c>s observed in
/// <c>Tamma.ElsaServer/Workflows/</c>.
/// </para>
/// </summary>
public sealed record WorkflowDocumentInterface(
    string WorkflowDefinitionId,
    IReadOnlyList<DocumentTypeKey> Consumes,
    DocumentTypeKey? Produces,
    bool Provisional);

/// <summary>
/// Shared kebab-case validation for structural workflow-id / wire-token checks
/// (Design Decision D7). Kept internal to the Documents namespace.
/// </summary>
internal static partial class KebabCase
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();

    public static bool IsKebab(string? value) =>
        !string.IsNullOrEmpty(value) && Pattern().IsMatch(value);
}

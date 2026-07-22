using Tamma.Core;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Documents;

/// <summary>
/// The fail-loud, static, immutable document type registry (Story 39-2 AC4;
/// Design Decision D3). Mirrors the <c>SystemPrompts</c> / <c>RolePhaseMap</c>
/// facade shape: a pure <see cref="BuildIndex"/> core validates the registration
/// list at static init (a bad registration refuses to load), and
/// <see cref="Resolve(string)"/> never returns null or a silent default.
///
/// <para>
/// The key→<see cref="IDocumentType"/> map ships EMPTY at 39-2 (registered count
/// pinned at 0). Story 39-3 appends +4 implementations and 39-4 the remaining +6;
/// each bump is a conscious edit to <see cref="s_registrations"/> plus the count
/// pin in <c>DocumentTypeRegistryTests</c>.
/// </para>
/// </summary>
public static class DocumentTypeRegistry
{
    // The compile-time registration list.
    //   39-3 appends +4 IDocumentType implementations (bumps the count pin 0 -> 4)  <-- DONE
    //   39-4 appends the remaining +6 (bumps the count pin 4 -> 10)
    // and each shrinks the WorkflowInterfaceGraphTests PendingImplementations
    // ratchet accordingly.
    private static readonly IReadOnlyList<IDocumentType> s_registrations = new IDocumentType[]
    {
        new DecompositionDocumentType(),
        new FindingsDocumentType(),
        new AmbiguityAssessmentDocumentType(),
        new ClarificationDocumentType(),
    };

    private static readonly IReadOnlyDictionary<string, IDocumentType> s_index =
        BuildIndex(s_registrations);

    /// <summary>All registered document types (empty at 39-2).</summary>
    public static IReadOnlyList<IDocumentType> All => s_registrations;

    /// <summary>
    /// The static workflow interface declarations (Design Decision D6), seeded
    /// from the README document-type table mapped to real Elsa
    /// <c>DefinitionId</c>s. Every entry is <c>Provisional</c> until reconciled
    /// against the landed 39-1 audit. Keyed by <see cref="DocumentTypeKey"/>, so
    /// no declaration can reference a type outside the vocabulary.
    /// </summary>
    public static IReadOnlyList<WorkflowDocumentInterface> WorkflowInterfaces { get; } = BuildSeed();

    /// <summary>
    /// Resolve a document type by its wire-string key.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.TYPE.UNKNOWN</c> if <paramref name="key"/> is not a
    /// vocabulary wire string; code <c>DOCUMENT.TYPE.NOT_REGISTERED</c> if it is a
    /// valid key with no registered implementation yet.
    /// </exception>
    public static IDocumentType Resolve(string key)
    {
        // Unknown wire string -> UNKNOWN (throws DOCUMENT.TYPE.UNKNOWN).
        var typeKey = DocumentTypeKeyExtensions.Parse(key);
        return Resolve(typeKey);
    }

    /// <summary>
    /// Resolve a document type by its <see cref="DocumentTypeKey"/>.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.TYPE.NOT_REGISTERED</c> if no implementation is registered
    /// for <paramref name="key"/> yet (39-3/39-4 pending).
    /// </exception>
    public static IDocumentType Resolve(DocumentTypeKey key)
    {
        var wire = key.ToWire();
        if (s_index.TryGetValue(wire, out var type))
            return type;

        throw new TammaError(
            "DOCUMENT.TYPE.NOT_REGISTERED",
            $"Document type '{wire}' is a valid key but has no registered implementation yet " +
            "(implementations land in Story 39-3/39-4).",
            new Dictionary<string, object?> { ["key"] = wire },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// Pure core (PromptFileLoader.Build style): build the key→type index from a
    /// registration list, validating each type. Test-drivable with fakes.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.TYPE.KEY_NOT_IN_VOCABULARY</c> if a type's <c>Key</c> is
    /// not a <see cref="DocumentTypeKey"/> wire string; code
    /// <c>DOCUMENT.TYPE.DUPLICATE_KEY</c> if two types share a key.
    /// </exception>
    internal static IReadOnlyDictionary<string, IDocumentType> BuildIndex(IEnumerable<IDocumentType> types)
    {
        var index = new Dictionary<string, IDocumentType>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (!DocumentTypeKeyExtensions.TryParse(type.Key, out var parsed))
                throw new TammaError(
                    "DOCUMENT.TYPE.KEY_NOT_IN_VOCABULARY",
                    $"Document type key '{type.Key}' is not part of the DocumentTypeKey vocabulary.",
                    new Dictionary<string, object?> { ["key"] = type.Key },
                    retryable: false,
                    severity: TammaErrorSeverity.Critical);

            var wire = parsed.ToWire();
            if (!index.TryAdd(wire, type))
                throw new TammaError(
                    "DOCUMENT.TYPE.DUPLICATE_KEY",
                    $"Duplicate document type key '{wire}' — two IDocumentType implementations claim the same key.",
                    new Dictionary<string, object?> { ["key"] = wire },
                    retryable: false,
                    severity: TammaErrorSeverity.Critical);
        }
        return index;
    }

    /// <summary>
    /// The D6 provisional seed: README document-type table → real
    /// <c>DefinitionId</c>s observed in <c>Tamma.ElsaServer/Workflows/</c>. All
    /// entries <c>Provisional=true</c> pending the 39-1 audit. <c>Consumes</c> is
    /// left empty until the audit supplies the verified consumer edges.
    /// </summary>
    private static IReadOnlyList<WorkflowDocumentInterface> BuildSeed()
    {
        var empty = Array.Empty<DocumentTypeKey>();
        return new[]
        {
            new WorkflowDocumentInterface("research",           empty, DocumentTypeKey.Findings,            true),
            new WorkflowDocumentInterface("ambiguity-scoring",  empty, DocumentTypeKey.AmbiguityAssessment, true),
            new WorkflowDocumentInterface("clarifying-questions", empty, DocumentTypeKey.Clarification,      true),
            new WorkflowDocumentInterface("issue-decomposition", empty, DocumentTypeKey.Decomposition,      true),
            new WorkflowDocumentInterface("plan-generation",    empty, DocumentTypeKey.Plan,                true),
            new WorkflowDocumentInterface("task-creation",      empty, DocumentTypeKey.Plan,                true),
            new WorkflowDocumentInterface("design-proposal",    empty, DocumentTypeKey.Design,              true),
            new WorkflowDocumentInterface("plan-review",        empty, DocumentTypeKey.Review,              true),
            new WorkflowDocumentInterface("task-review",        empty, DocumentTypeKey.Review,              true),
            new WorkflowDocumentInterface("code-review",        empty, DocumentTypeKey.Review,              true),
            new WorkflowDocumentInterface("triage-po-decision", empty, DocumentTypeKey.TriageDecision,      true),
            new WorkflowDocumentInterface("blocker-diagnosis",  empty, DocumentTypeKey.Diagnosis,           true),
            new WorkflowDocumentInterface("debugging",          empty, DocumentTypeKey.Diagnosis,           true),
            new WorkflowDocumentInterface("test-case-creation", empty, DocumentTypeKey.TestSpec,            true),
        };
    }
}

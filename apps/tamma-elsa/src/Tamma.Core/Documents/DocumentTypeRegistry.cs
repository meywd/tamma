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
    //   39-4 appends the remaining +6 (bumps the count pin 4 -> 10)                  <-- DONE
    // and each shrinks the WorkflowInterfaceGraphTests PendingImplementations
    // ratchet accordingly.
    private static readonly IReadOnlyList<IDocumentType> s_registrations = new IDocumentType[]
    {
        new DecompositionDocumentType(),
        new FindingsDocumentType(),
        new AmbiguityAssessmentDocumentType(),
        new ClarificationDocumentType(),
        // 39-4 — Batch 2 (vocabulary complete at 10).
        new PlanDocumentType(),
        new DesignDocumentType(),
        new ReviewDocumentType(),
        new TriageDecisionDocumentType(),
        new DiagnosisDocumentType(),
        new TestSpecDocumentType(),
        // 41-1b — the six Epic 41 types (count pin 10 -> 16). No workflow edges
        // land here (D2): each of 41-2/41-3/41-6/41-13/41-19/41-27 declares its
        // own BuildSeed row when its producing workflow binds.
        new AcceptanceCriteriaDocumentType(),
        new BacklogOrderingDocumentType(),
        new SprintPlanDocumentType(),
        new TestPlanDocumentType(),
        new ThreatModelDocumentType(),
        new UxSpecDocumentType(),
        // 41-1c — prose (count pin 16 -> 17): one type for all ten prose kinds,
        // body unvalidated markdown. No workflow edge lands here — each of
        // 41-4/41-5/41-8/41-9/41-22/41-24/41-25/41-26 declares its own
        // BuildSeed row when its producing workflow binds.
        new ProseDocumentType(),
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
            // Story 39-13 (D9) — flipped non-provisional: each edge is now backed by a real
            // document-lifecycle binding (Research/Ambiguity/Clarify/Design), not a seed guess.
            new WorkflowDocumentInterface("research",           empty, DocumentTypeKey.Findings,            false),
            new WorkflowDocumentInterface("ambiguity-scoring",  empty, DocumentTypeKey.AmbiguityAssessment, false),
            new WorkflowDocumentInterface("clarifying-questions", empty, DocumentTypeKey.Clarification,      false),
            // Story 39-12 (D9) — flipped non-provisional: the edge is now backed by a real
            // document-lifecycle binding (IssueDecompositionWorkflow), not a seed guess.
            new WorkflowDocumentInterface("issue-decomposition", empty, DocumentTypeKey.Decomposition,      false),
            // Story 39-14 (D4) — the first two-link typed chain: plan-generation CONSUMES the
            // accepted decomposition and PRODUCES a plan via its document-lifecycle binding
            // (flipped non-provisional). plan-review is now a reader (consumes plan, produces
            // nothing) — the read-through shim.
            new WorkflowDocumentInterface("plan-generation",    new[] { DocumentTypeKey.Decomposition }, DocumentTypeKey.Plan, false),
            // Story 39-15 (D2) — task-creation CONSUMES the accepted (system) plan and PRODUCES a
            // task-breakdown plan via its document-lifecycle binding (flipped non-provisional).
            new WorkflowDocumentInterface("task-creation",      new[] { DocumentTypeKey.Plan }, DocumentTypeKey.Plan, false),
            new WorkflowDocumentInterface("design-proposal",    empty, DocumentTypeKey.Design,              false),
            new WorkflowDocumentInterface("plan-review",        new[] { DocumentTypeKey.Plan },          null,                 false),
            new WorkflowDocumentInterface("task-review",        empty, DocumentTypeKey.Review,              true),
            new WorkflowDocumentInterface("code-review",        empty, DocumentTypeKey.Review,              true),
            // Story 39-15 (D5) — triage-context-gathering PRODUCES a Findings document (the 39-13
            // Research recipe on the split (developer, triage-context-scan) cell); triage-po-decision
            // CONSUMES that Findings context and PRODUCES a reviewed TriageDecision (the 4-role panel
            // is now its lifecycle REVIEW stage). Both flipped non-provisional (real lifecycle bindings).
            new WorkflowDocumentInterface("triage-context-gathering", empty, DocumentTypeKey.Findings,   false),
            new WorkflowDocumentInterface("triage-po-decision", new[] { DocumentTypeKey.Findings }, DocumentTypeKey.TriageDecision, false),
            new WorkflowDocumentInterface("blocker-diagnosis",  empty, DocumentTypeKey.Diagnosis,           true),
            new WorkflowDocumentInterface("debugging",          empty, DocumentTypeKey.Diagnosis,           true),
            // Story 39-15 (D4) — the new debug-diagnosis binding PRODUCES a typed Diagnosis via its
            // document-lifecycle binding (DebuggingWorkflow's loop consumes it); flipped non-provisional.
            new WorkflowDocumentInterface("debug-diagnosis",    empty, DocumentTypeKey.Diagnosis,           false),
            // Story 39-15 (D3) — test-case-creation CONSUMES the task-breakdown plan and PRODUCES a
            // TestSpec (with the cross-document task-ID validation ring) via its lifecycle binding.
            new WorkflowDocumentInterface("test-case-creation", new[] { DocumentTypeKey.Plan }, DocumentTypeKey.TestSpec, false),
            // Story 41-2 — acceptance-criteria-authoring CONSUMES the accepted Clarification and
            // Findings (both optional, fail-closed reads) and PRODUCES the typed AcceptanceCriteria
            // that 41-15's acceptance verification and the merge gate read back. Real lifecycle
            // binding ⇒ non-provisional (epic-41 rule 1 clause (f): one edge per producing workflow).
            new WorkflowDocumentInterface("acceptance-criteria-authoring",
                new[] { DocumentTypeKey.Clarification, DocumentTypeKey.Findings },
                DocumentTypeKey.AcceptanceCriteria, false),
            // Story 41-9 — adr-authoring CONSUMES the accepted Design (41-10's output, which
            // design-proposal already produces today) and Findings, and PRODUCES prose with
            // kind=adr / audience=engineering. The REFERENCE prose-on-lifecycle edge: 41-4, 41-5,
            // 41-8, 41-22, 41-24, 41-25 and 41-26 each declare their own row in this shape.
            // (41-9 D8(i) wrote `empty` consumes; the row states the real consumed edges instead —
            // epic-41 rule 1 requires the binding to declare `consumes: [...]`, and the graph has
            // both fetch nodes.)
            new WorkflowDocumentInterface("adr-authoring",
                new[] { DocumentTypeKey.Design, DocumentTypeKey.Findings },
                DocumentTypeKey.Prose, false),
            // Story 41-3 — backlog-prioritization CONSUMES the accepted TriageDecision and
            // Findings of each candidate item (bounded per-item reads: the store has no set
            // query) and PRODUCES the typed BacklogOrdering that 41-6's sprint planning and
            // 41-4's roadmap read back. Both consumed edges are OPTIONAL, fail-closed reads —
            // a never-triaged backlog is still rankable from titles and summaries — but they
            // are real edges the graph has fetch nodes for, so the row declares them (the same
            // correction 41-9's row records). Real lifecycle binding ⇒ non-provisional.
            new WorkflowDocumentInterface("backlog-prioritization",
                new[] { DocumentTypeKey.TriageDecision, DocumentTypeKey.Findings },
                DocumentTypeKey.BacklogOrdering, false),
        };
    }
}

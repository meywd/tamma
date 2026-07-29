using System.Text.Json;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// <see cref="IDocumentType"/> for the unified <c>review</c> document (Story 39-4
/// AC2/AC3). Enforces the closed subject union (D3), per-issue severity/category/fix,
/// and the epic's FLAGSHIP executable domain rule (AC3): a payload whose
/// <c>decision</c> is <c>approve</c> while any issue is blocking-severity is
/// unrepresentable as a valid document — the state that caused the forked-verdict
/// bug class is rejected, with the violation naming the blocking issues so the 39-9
/// repair ring can feed it back.
/// </summary>
public sealed class ReviewDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape (missing required member / bad enum wire).</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>The subject <c>kind</c> is neither <c>document</c> nor <c>diff</c> (D3).</summary>
    public const string SubjectUnknownKind = "SUBJECT_UNKNOWN_KIND";

    /// <summary>The subject kind is known but its required members are missing/invalid (D3).</summary>
    public const string SubjectIncomplete = "SUBJECT_INCOMPLETE";

    /// <summary>The review has no summary — a decision with no summary is not auditable.</summary>
    public const string SummaryRequired = "SUMMARY_REQUIRED";

    /// <summary>An issue has no category (D10 — category stays a free non-empty string).</summary>
    public const string IssueMissingCategory = "ISSUE_MISSING_CATEGORY";

    /// <summary>An issue carries no concrete suggested fix (AC2) — legacy fix-less issues fail here (D4).</summary>
    public const string IssueMissingFix = "ISSUE_MISSING_FIX";

    /// <summary>AC3 FLAGSHIP: <c>decision=approve</c> while a blocking-severity issue exists.</summary>
    public const string ApproveWithBlockingIssues = "APPROVE_WITH_BLOCKING_ISSUES";

    /// <summary>Panel provenance present but empty or with duplicate ids (39-7 D7).</summary>
    public const string AggregatedFromInvalid = "AGGREGATED_FROM_INVALID";

    public string Key => DocumentTypeKey.Review.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Review);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        Review? doc;
        try
        {
            doc = payload.Deserialize<Review>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload,
                "The payload could not be parsed as a review (a missing subject/decision/summary/issues " +
                "member, or an out-of-vocabulary decision/severity wire, fails here)."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        ValidateSubject(doc.Subject, violations);

        if (string.IsNullOrWhiteSpace(doc.Summary))
            violations.Add(new DocumentViolation(
                SummaryRequired, "The review has no summary — a decision with no summary is not auditable."));

        var issues = doc.Issues ?? [];
        var index = 0;
        foreach (var issue in issues)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(issue.Description) ? $"#{index}" : $"'{issue.Description}'";

            if (string.IsNullOrWhiteSpace(issue.Category))
                violations.Add(new DocumentViolation(
                    IssueMissingCategory, $"Issue {label} has no category — every issue must state its category."));

            if (string.IsNullOrWhiteSpace(issue.SuggestedFix))
                violations.Add(new DocumentViolation(
                    IssueMissingFix,
                    $"Issue {label} carries no concrete suggested fix — a review issue must say how to resolve it."));
        }

        // AC3 flagship: an approving decision may not coexist with a blocking issue.
        if (doc.Decision == ReviewDecision.Approve)
        {
            var blocking = issues.Where(i => i.Severity.IsBlocking()).Select(i => i.Description).ToList();
            if (blocking.Count > 0)
                violations.Add(new DocumentViolation(
                    ApproveWithBlockingIssues,
                    "The review approves while blocking (critical) issues remain unresolved: " +
                    string.Join("; ", blocking) +
                    ". An approval cannot coexist with a blocking issue — resolve or downgrade them, or change the decision."));
        }

        if (doc.AggregatedFrom is { } aggregated)
        {
            if (aggregated.Count == 0 || aggregated.Distinct().Count() != aggregated.Count)
                violations.Add(new DocumentViolation(
                    AggregatedFromInvalid,
                    "aggregatedFrom, when present, must be a non-empty, duplicate-free list of the reviews " +
                    "this panel aggregate was built from."));
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    private static void ValidateSubject(ReviewSubject? subject, List<DocumentViolation> violations)
    {
        if (subject is null)
        {
            violations.Add(new DocumentViolation(SubjectIncomplete, "The review has no subject."));
            return;
        }

        switch (subject.Kind)
        {
            case "document":
                var hasValidType = !string.IsNullOrWhiteSpace(subject.DocumentType)
                                   && DocumentTypeKeyExtensions.TryParse(subject.DocumentType, out _);
                if (subject.DocumentId is null || !hasValidType)
                    violations.Add(new DocumentViolation(
                        SubjectIncomplete,
                        "A document-subject review must carry both a documentId and a documentType that is a " +
                        "valid document type key."));
                break;

            case "diff":
                if (string.IsNullOrWhiteSpace(subject.Repository) ||
                    (subject.PrNumber is null && string.IsNullOrWhiteSpace(subject.CommitSha)))
                    violations.Add(new DocumentViolation(
                        SubjectIncomplete,
                        "A diff-subject review must carry a repository and at least one of prNumber / commitSha."));
                break;

            default:
                violations.Add(new DocumentViolation(
                    SubjectUnknownKind,
                    $"Review subject kind '{subject.Kind}' is not one of document, diff."));
                break;
        }
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // All review-producing cells (the seven plan-review-family cells, task-review,
    // and code-review) are IntentionallyUnbound in ContractBindingTests, so this
    // renderer has no CI-pinned token conflict; 39-16 regenerates those cells from
    // here. The tokens below ("subject", "decision", "summary", "issues",
    // "severity", "category", "suggestedFix") are the unified reply contract.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "subject": { "kind": "document", "documentId": "<uuid>", "documentType": "plan" },
          "decision": "approve | request-changes | needs-discussion",
          "summary": "the overall verdict in one or two sentences",
          "issues": [
            {
              "severity": "critical | major | minor | suggestion",
              "category": "what kind of issue this is",
              "description": "what is wrong",
              "suggestedFix": "the concrete change that resolves it"
            }
          ]
        }
        Rules: a "diff" subject carries { "kind": "diff", "repository": "...", "prNumber": 12 }
        instead of documentId/documentType; every issue needs a "category" and a concrete
        "suggestedFix"; "decision" may NOT be "approve" while any issue has "severity":"critical"
        (a blocking issue is unresolved — request changes or downgrade it).
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-request-changes-with-blocking-issue",
            true,
            """
            {
              "subject": { "kind": "document", "documentId": "0192a8b0-1111-7abc-8def-000000000001", "documentType": "plan" },
              "decision": "request-changes",
              "summary": "The plan is sound but omits migration ordering.",
              "issues": [
                { "severity": "critical", "category": "correctness", "description": "Migration runs before the table exists", "suggestedFix": "Reorder task ST-2 before ST-1" }
              ]
            }
            """),
        new DocumentExample(
            "valid-code-review-diff-subject",
            true,
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 42 },
              "decision": "approve",
              "summary": "Clean implementation, only stylistic nits.",
              "issues": [
                { "severity": "suggestion", "category": "style", "description": "Prefer var here", "suggestedFix": "Use var for the local" }
              ]
            }
            """),
        new DocumentExample(
            "invalid-approve-with-blocking-issue",
            false,
            """
            {
              "subject": { "kind": "document", "documentId": "0192a8b0-2222-7abc-8def-000000000002", "documentType": "plan" },
              "decision": "approve",
              "summary": "Approving despite a blocker.",
              "issues": [
                { "severity": "critical", "category": "security", "description": "SQL injection in the query builder", "suggestedFix": "Parameterize the query" }
              ]
            }
            """,
            new[] { ApproveWithBlockingIssues }),
    };
}

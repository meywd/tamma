using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The closed criterion-form vocabulary (Story 41-1b, Design Decision D6): a
/// criterion is either a Given/When/Then behaviour statement or a checklist
/// line. Shipped as a <c>[Wire]</c> enum per the <c>AgentAction</c> pattern;
/// out-of-vocab values are violations, never silent clamps.
/// </summary>
public enum CriterionForm
{
    [Wire("given-when-then")] GivenWhenThen,
    [Wire("checklist")] Checklist,
}

/// <summary>
/// One acceptance criterion (Story 41-1b). Each criterion is independently
/// verifiable: a <see cref="Form"/> from the closed vocabulary, the fields that
/// form requires (given/when/then, or a checklist <see cref="Statement"/>), and
/// an explicit <see cref="Verifiable"/> attestation. The optional
/// <see cref="ScopeRef"/> names the decomposition subtask this criterion covers —
/// checked cross-document via <c>ValidateWithContext</c> (D5), inert payload-only.
/// </summary>
public sealed record AcceptanceCriterion
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("form")] public string Form { get; init; } = "";
    [JsonPropertyName("given")] public string? Given { get; init; }
    [JsonPropertyName("when")] public string? When { get; init; }
    [JsonPropertyName("then")] public string? Then { get; init; }
    [JsonPropertyName("statement")] public string? Statement { get; init; }
    [JsonPropertyName("verifiable")] public bool? Verifiable { get; init; }

    /// <summary>Optional reference to the planned scope (a decomposition subtask id) this criterion covers.</summary>
    [JsonPropertyName("scopeRef")] public string? ScopeRef { get; init; }
}

/// <summary>
/// The testable definition-of-done for one issue (Story 41-1b; epic README's
/// new-types table): the criteria consumed by 41-15's acceptance verification and
/// the merge gate. Not a <c>Clarification</c> (that resolves ambiguity) nor a
/// <c>Plan</c> (that maps files) — it is bound to an <see cref="IssueId"/> and
/// every criterion is independently verifiable.
/// </summary>
public sealed record AcceptanceCriteria
{
    [JsonPropertyName("issueId")] public string IssueId { get; init; } = "";
    [JsonPropertyName("criteria")] public IReadOnlyList<AcceptanceCriterion> Criteria { get; init; } = [];
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>acceptance-criteria</c> document
/// (Story 41-1b AC2). Enforces: bound to an issue; ≥1 criterion; unique non-empty
/// criterion ids; a closed <c>form</c> vocabulary with the form's required fields
/// present; and an explicit verifiability attestation per criterion. The
/// cross-document "no criterion references unimplemented scope" rule rides
/// <see cref="ValidateWithContext"/> (D5) and is inert without a consumed
/// decomposition.
/// </summary>
public sealed class AcceptanceCriteriaDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>The document is not bound to an issue — acceptance criteria define done for ONE issue.</summary>
    public const string IssueIdMissing = "ISSUE_ID_MISSING";

    /// <summary>No criteria — an empty definition-of-done defines nothing.</summary>
    public const string NoCriteria = "NO_CRITERIA";

    /// <summary>A criterion has no id — ids are what 41-15's verification and the merge gate reference.</summary>
    public const string CriterionIdMissing = "CRITERION_ID_MISSING";

    /// <summary>Two criteria share an id — references would be ambiguous.</summary>
    public const string CriterionIdDuplicated = "CRITERION_ID_DUPLICATED";

    /// <summary>A criterion's <c>form</c> is missing or outside the closed vocabulary.</summary>
    public const string CriterionFormOutOfVocabulary = "CRITERION_FORM_OUT_OF_VOCABULARY";

    /// <summary>A given-when-then criterion is missing one of given/when/then.</summary>
    public const string GwtIncomplete = "GWT_INCOMPLETE";

    /// <summary>A checklist criterion has no statement.</summary>
    public const string ChecklistItemEmpty = "CHECKLIST_ITEM_EMPTY";

    /// <summary>A criterion is not attested independently verifiable — aspirational criteria are rejected.</summary>
    public const string CriterionNotIndependentlyVerifiable = "CRITERION_NOT_INDEPENDENTLY_VERIFIABLE";

    /// <summary>
    /// Story 41-1b (D5) — the CROSS-DOCUMENT rule: a criterion's <c>scopeRef</c>
    /// names a subtask that does not exist in the consumed <c>decomposition</c>.
    /// Fires only through <see cref="ValidateWithContext"/>; the context-free
    /// <see cref="Validate"/> never emits it.
    /// </summary>
    public const string CriterionReferencesUnplannedScope = "CRITERION_REFERENCES_UNPLANNED_SCOPE";

    public string Key => DocumentTypeKey.AcceptanceCriteria.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(AcceptanceCriteria);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        AcceptanceCriteria? doc;
        try
        {
            doc = payload.Deserialize<AcceptanceCriteria>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as an acceptance-criteria document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (string.IsNullOrWhiteSpace(doc.IssueId))
            violations.Add(new DocumentViolation(
                IssueIdMissing, "The document names no issueId — acceptance criteria define done for one issue."));

        var criteria = doc.Criteria ?? [];
        if (criteria.Count == 0)
            violations.Add(new DocumentViolation(
                NoCriteria, "The document has no criteria — an empty definition-of-done defines nothing."));

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var reportedDupes = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var criterion in criteria)
        {
            index++;
            var id = criterion.Id?.Trim() ?? "";
            var label = id.Length == 0 ? $"#{index}" : $"'{id}'";

            if (id.Length == 0)
                violations.Add(new DocumentViolation(
                    CriterionIdMissing, $"Criterion {label} has no id — every criterion needs a referencable id."));
            else if (!seenIds.Add(id) && reportedDupes.Add(id))
                violations.Add(new DocumentViolation(
                    CriterionIdDuplicated, $"Criterion id '{id}' is used more than once — ids must be unique."));

            if (!EnumWire<CriterionForm>.TryParse(criterion.Form ?? "", out var form))
            {
                violations.Add(new DocumentViolation(
                    CriterionFormOutOfVocabulary,
                    $"Criterion {label} has form '{criterion.Form}' — it must be one of: given-when-then, checklist."));
            }
            else if (form == CriterionForm.GivenWhenThen)
            {
                if (string.IsNullOrWhiteSpace(criterion.Given) ||
                    string.IsNullOrWhiteSpace(criterion.When) ||
                    string.IsNullOrWhiteSpace(criterion.Then))
                    violations.Add(new DocumentViolation(
                        GwtIncomplete,
                        $"Criterion {label} is given-when-then but does not state all of given, when and then."));
            }
            else if (string.IsNullOrWhiteSpace(criterion.Statement))
            {
                violations.Add(new DocumentViolation(
                    ChecklistItemEmpty,
                    $"Criterion {label} is a checklist item with no statement — an empty line verifies nothing."));
            }

            if (criterion.Verifiable != true)
                violations.Add(new DocumentViolation(
                    CriterionNotIndependentlyVerifiable,
                    $"Criterion {label} is not attested independently verifiable — every criterion must be " +
                    "checkable on its own, not aspirational."));
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    /// <summary>
    /// Story 41-1b (D5) — cross-document validation: run the payload-only
    /// <see cref="Validate"/> AND, when a consumed <c>decomposition</c> context is
    /// supplied, reject any criterion whose <c>scopeRef</c> names a subtask absent
    /// from that decomposition (<see cref="CriterionReferencesUnplannedScope"/>).
    /// An empty / unreadable context degrades to payload-only validation, never a
    /// throw — the <c>TestSpec</c> precedent (39-15 D3).
    /// </summary>
    public DocumentValidationResult ValidateWithContext(JsonElement payload, string validationContextJson) =>
        DocumentPayloadGuard.Run(payload, p => ValidateWithContextCore(p, validationContextJson));

    private DocumentValidationResult ValidateWithContextCore(JsonElement payload, string validationContextJson)
    {
        var baseResult = Validate(payload);

        if (string.IsNullOrWhiteSpace(validationContextJson))
            return baseResult;

        var plannedIds = ReadDecompositionSubtaskIds(validationContextJson);
        if (plannedIds is null || plannedIds.Count == 0)
            return baseResult; // no readable decomposition context — the cross-document rule cannot fire.

        AcceptanceCriteria? doc;
        try
        {
            doc = payload.Deserialize<AcceptanceCriteria>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return baseResult; // malformed payload already reported by the base validate.
        }

        var extra = new List<DocumentViolation>();
        var index = 0;
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var criterion in doc?.Criteria ?? [])
        {
            index++;
            var scopeRef = criterion.ScopeRef?.Trim() ?? "";
            if (scopeRef.Length == 0)
                continue; // an unmapped criterion is legal payload-only; only a WRONG mapping is the violation.

            var label = string.IsNullOrWhiteSpace(criterion.Id) ? $"#{index}" : $"'{criterion.Id}'";
            if (!plannedIds.Contains(scopeRef) && reported.Add(scopeRef))
                extra.Add(new DocumentViolation(
                    CriterionReferencesUnplannedScope,
                    $"Criterion {label} references scope '{scopeRef}', which is not a subtask of the consumed " +
                    "decomposition — a criterion may not require unimplemented scope."));
        }

        if (extra.Count == 0)
            return baseResult;

        var merged = new List<DocumentViolation>(baseResult.Violations);
        merged.AddRange(extra);
        return DocumentValidationResult.Invalid(merged.ToArray());
    }

    /// <summary>
    /// Read the subtask-id set from a consumed <c>decomposition</c> body
    /// (<c>{ "subtasks": [ { "id": ... } ] }</c>). Fail-soft: an unreadable or
    /// empty body yields <c>null</c> so the caller degrades to payload-only
    /// validation.
    /// </summary>
    private static HashSet<string>? ReadDecompositionSubtaskIds(string decompositionJson)
    {
        Decomposition? decomposition;
        try
        {
            decomposition = JsonSerializer.Deserialize<Decomposition>(decompositionJson, DocumentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (decomposition?.Subtasks is null || decomposition.Subtasks.Count == 0)
            return null;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subtask in decomposition.Subtasks)
        {
            var id = subtask.Id?.Trim();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids.Count == 0 ? null : ids;
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // Producing cell (41-1b D4): (product_owner, define-acceptance-criteria).
    // The cell is NOT bound in ContractBindingTests yet (no compiled dispatch site
    // exists until 41-2 lands its workflow — the stale-Bindings guard forbids an
    // early entry); the intended tokens below are pinned Core-side by
    // RenderContractTokenTests so 41-2 binds against a stable contract.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "issueId": "the issue this definition-of-done is bound to",
          "criteria": [
            {
              "id": "AC-1",
              "form": "given-when-then | checklist",
              "given": "precondition (given-when-then form)",
              "when": "action (given-when-then form)",
              "then": "observable outcome (given-when-then form)",
              "statement": "the checklist line (checklist form)",
              "verifiable": true,
              "scopeRef": "optional decomposition subtask id this criterion covers"
            }
          ]
        }
        Rules: "issueId" is required; define at least one criterion; every criterion needs a
        unique "id", a "form" from the closed set, the fields its form requires (given/when/then,
        or a non-empty "statement"), and "verifiable": true — a criterion that cannot be
        independently verified is rejected, and a "scopeRef" may only name planned scope.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-gwt-and-checklist",
            true,
            """
            {
              "issueId": "issue-42",
              "criteria": [
                {
                  "id": "AC-1",
                  "form": "given-when-then",
                  "given": "a tenant over its rate limit",
                  "when": "another request arrives",
                  "then": "the API responds 429 with a Retry-After header",
                  "verifiable": true
                },
                {
                  "id": "AC-2",
                  "form": "checklist",
                  "statement": "the limiter's counters reset at the top of each window",
                  "verifiable": true
                }
              ]
            }
            """),
        new DocumentExample(
            "invalid-incomplete-gwt-and-unverifiable",
            false,
            """
            {
              "issueId": "issue-42",
              "criteria": [
                {
                  "id": "AC-1",
                  "form": "given-when-then",
                  "given": "a tenant over its rate limit",
                  "verifiable": true
                },
                {
                  "id": "AC-2",
                  "form": "checklist",
                  "statement": "the system feels fast",
                  "verifiable": false
                }
              ]
            }
            """,
            new[] { GwtIncomplete, CriterionNotIndependentlyVerifiable }),
    };
}

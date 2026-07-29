using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One user flow in a <see cref="UxSpec"/> (Story 41-1b): every flow states its
/// entry state, its success state, and at least one error state. The optional
/// <see cref="AcceptanceCriteriaRefs"/> map the flow to acceptance-criteria ids —
/// checked cross-document via <c>ValidateWithContext</c> (D5), inert payload-only.
/// </summary>
public sealed record UxFlow
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("entryState")] public string EntryState { get; init; } = "";
    [JsonPropertyName("successState")] public string SuccessState { get; init; } = "";
    [JsonPropertyName("errorStates")] public IReadOnlyList<string> ErrorStates { get; init; } = [];

    /// <summary>Optional acceptance-criteria ids this flow satisfies (cross-document, D5).</summary>
    [JsonPropertyName("acceptanceCriteriaRefs")] public IReadOnlyList<string> AcceptanceCriteriaRefs { get; init; } = [];
}

/// <summary>
/// One screen / step in a <see cref="UxSpec"/> (Story 41-1b): bound to a declared
/// flow, with the accessibility requirements listed per screen. A11y requirement
/// text is free non-empty strings (Design Decision D6 — deliberately NOT a closed
/// vocabulary).
/// </summary>
public sealed record UxScreen
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("flowRef")] public string FlowRef { get; init; } = "";
    [JsonPropertyName("a11yRequirements")] public IReadOnlyList<string> A11yRequirements { get; init; } = [];
}

/// <summary>
/// A UX specification (Story 41-1b; epic README's new-types table): a
/// <c>Design</c> weighs technical alternatives — a UX spec captures
/// FLOWS / STATES / ACCEPTANCE for an interface.
/// </summary>
public sealed record UxSpec
{
    [JsonPropertyName("flows")] public IReadOnlyList<UxFlow> Flows { get; init; } = [];
    [JsonPropertyName("screens")] public IReadOnlyList<UxScreen> Screens { get; init; } = [];
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>ux-spec</c> document (Story 41-1b AC2):
/// every flow has entry + success + ≥1 error state; every screen references a
/// declared flow and lists ≥1 accessibility requirement. The cross-document
/// "maps to acceptance criteria" rule rides <see cref="ValidateWithContext"/>
/// (D5) and is inert without a consumed acceptance-criteria document.
/// </summary>
public sealed class UxSpecDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>No flows — a UX spec with no flows specifies nothing.</summary>
    public const string NoFlows = "NO_FLOWS";

    /// <summary>A flow states no entry state.</summary>
    public const string FlowMissingEntryState = "FLOW_MISSING_ENTRY_STATE";

    /// <summary>A flow states no success state.</summary>
    public const string FlowMissingSuccessState = "FLOW_MISSING_SUCCESS_STATE";

    /// <summary>A flow states no error state — a flow that cannot fail is a flow that was not designed.</summary>
    public const string FlowMissingErrorState = "FLOW_MISSING_ERROR_STATE";

    /// <summary>A screen references no declared flow.</summary>
    public const string ScreenUnknownFlow = "SCREEN_UNKNOWN_FLOW";

    /// <summary>A screen lists no accessibility requirements.</summary>
    public const string ScreenMissingA11yRequirements = "SCREEN_MISSING_A11Y_REQUIREMENTS";

    /// <summary>
    /// Story 41-1b (D5) — the CROSS-DOCUMENT rule: a flow maps to no criterion of
    /// the consumed <c>acceptance-criteria</c> document. Fires only through
    /// <see cref="ValidateWithContext"/>; the context-free <see cref="Validate"/>
    /// never emits it.
    /// </summary>
    public const string FlowUnmappedToAcceptanceCriterion = "FLOW_UNMAPPED_TO_ACCEPTANCE_CRITERION";

    public string Key => DocumentTypeKey.UxSpec.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(UxSpec);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        UxSpec? doc;
        try
        {
            doc = payload.Deserialize<UxSpec>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a ux-spec document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        var flows = doc.Flows ?? [];
        if (flows.Count == 0)
            violations.Add(new DocumentViolation(
                NoFlows, "The spec has no flows — a UX spec with no flows specifies nothing."));

        var flowIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var flow in flows)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(flow.Id)
                ? (string.IsNullOrWhiteSpace(flow.Name) ? $"#{index}" : $"'{flow.Name}'")
                : $"'{flow.Id}'";

            var id = flow.Id?.Trim();
            if (!string.IsNullOrEmpty(id))
                flowIds.Add(id);

            if (string.IsNullOrWhiteSpace(flow.EntryState))
                violations.Add(new DocumentViolation(
                    FlowMissingEntryState, $"Flow {label} states no entry state — where does the user start?"));

            if (string.IsNullOrWhiteSpace(flow.SuccessState))
                violations.Add(new DocumentViolation(
                    FlowMissingSuccessState, $"Flow {label} states no success state — what does done look like?"));

            if ((flow.ErrorStates ?? []).All(string.IsNullOrWhiteSpace))
                violations.Add(new DocumentViolation(
                    FlowMissingErrorState,
                    $"Flow {label} states no error state — every flow must design at least one failure path."));
        }

        index = 0;
        foreach (var screen in doc.Screens ?? [])
        {
            index++;
            var label = string.IsNullOrWhiteSpace(screen.Id) ? $"#{index}" : $"'{screen.Id}'";

            var flowRef = screen.FlowRef?.Trim() ?? "";
            if (flowRef.Length == 0 || !flowIds.Contains(flowRef))
                violations.Add(new DocumentViolation(
                    ScreenUnknownFlow,
                    $"Screen {label} references flow '{flowRef}', which is not declared in flows — every screen " +
                    "belongs to a real flow."));

            if ((screen.A11yRequirements ?? []).All(string.IsNullOrWhiteSpace))
                violations.Add(new DocumentViolation(
                    ScreenMissingA11yRequirements,
                    $"Screen {label} lists no accessibility requirements — every screen/step must state them."));
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    /// <summary>
    /// Story 41-1b (D5) — cross-document validation: run the payload-only
    /// <see cref="Validate"/> AND, when a consumed <c>acceptance-criteria</c>
    /// context is supplied, reject any flow that maps to none of that document's
    /// criteria (<see cref="FlowUnmappedToAcceptanceCriterion"/> — either no refs
    /// at all, or only refs to criteria that do not exist). An empty / unreadable
    /// context degrades to payload-only validation, never a throw — the
    /// <c>TestSpec</c> precedent (39-15 D3).
    /// </summary>
    public DocumentValidationResult ValidateWithContext(JsonElement payload, string validationContextJson) =>
        DocumentPayloadGuard.Run(payload, p => ValidateWithContextCore(p, validationContextJson));

    private DocumentValidationResult ValidateWithContextCore(JsonElement payload, string validationContextJson)
    {
        var baseResult = Validate(payload);

        if (string.IsNullOrWhiteSpace(validationContextJson))
            return baseResult;

        var criterionIds = ReadAcceptanceCriterionIds(validationContextJson);
        if (criterionIds is null || criterionIds.Count == 0)
            return baseResult; // no readable acceptance-criteria context — the cross-document rule cannot fire.

        UxSpec? doc;
        try
        {
            doc = payload.Deserialize<UxSpec>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return baseResult; // malformed payload already reported by the base validate.
        }

        var extra = new List<DocumentViolation>();
        var index = 0;
        foreach (var flow in doc?.Flows ?? [])
        {
            index++;
            var label = string.IsNullOrWhiteSpace(flow.Id) ? $"#{index}" : $"'{flow.Id}'";

            var refs = (flow.AcceptanceCriteriaRefs ?? [])
                .Select(r => r?.Trim() ?? "")
                .Where(r => r.Length > 0)
                .ToList();

            if (!refs.Any(criterionIds.Contains))
                extra.Add(new DocumentViolation(
                    FlowUnmappedToAcceptanceCriterion,
                    $"Flow {label} maps to no criterion of the consumed acceptance-criteria document — every flow " +
                    "must satisfy at least one acceptance criterion."));
        }

        if (extra.Count == 0)
            return baseResult;

        var merged = new List<DocumentViolation>(baseResult.Violations);
        merged.AddRange(extra);
        return DocumentValidationResult.Invalid(merged.ToArray());
    }

    /// <summary>
    /// Read the criterion-id set from a consumed <c>acceptance-criteria</c> body
    /// (<c>{ "criteria": [ { "id": ... } ] }</c>). Fail-soft: an unreadable or
    /// empty body yields <c>null</c> so the caller degrades to payload-only
    /// validation.
    /// </summary>
    private static HashSet<string>? ReadAcceptanceCriterionIds(string acceptanceCriteriaJson)
    {
        AcceptanceCriteria? criteria;
        try
        {
            criteria = JsonSerializer.Deserialize<AcceptanceCriteria>(acceptanceCriteriaJson, DocumentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (criteria?.Criteria is null || criteria.Criteria.Count == 0)
            return null;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var criterion in criteria.Criteria)
        {
            var id = criterion.Id?.Trim();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids.Count == 0 ? null : ids;
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // Producing cell (41-1b D4): (ux_designer, author-ui-spec) — the role and its
    // prompt template are 41-1a scope (another lane); until they land, this cell
    // exists only as the documented intent here. The cell is NOT bound in
    // ContractBindingTests (no compiled dispatch site exists until 41-27 lands its
    // workflow — the stale-Bindings guard forbids an early entry); the intended
    // tokens below are pinned Core-side by RenderContractTokenTests.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "flows": [
            {
              "id": "F1",
              "name": "the user flow",
              "entryState": "where the user starts",
              "successState": "what done looks like",
              "errorStates": ["at least one designed failure path"],
              "acceptanceCriteriaRefs": ["AC-1"]
            }
          ],
          "screens": [
            {
              "id": "S1",
              "flowRef": "F1",
              "a11yRequirements": ["at least one accessibility requirement for this screen"]
            }
          ]
        }
        Rules: define at least one flow; every flow states an "entryState", a
        "successState", and at least one entry in "errorStates"; every screen references a
        declared flow via "flowRef" and lists at least one entry in "a11yRequirements";
        "acceptanceCriteriaRefs" map each flow to the acceptance criteria it satisfies.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-login-flow-spec",
            true,
            """
            {
              "flows": [
                {
                  "id": "F1",
                  "name": "sign in",
                  "entryState": "signed-out landing page",
                  "successState": "dashboard with the user's workspaces",
                  "errorStates": ["invalid credentials banner", "locked-account help screen"],
                  "acceptanceCriteriaRefs": ["AC-1"]
                }
              ],
              "screens": [
                {
                  "id": "S1",
                  "flowRef": "F1",
                  "a11yRequirements": ["all inputs labelled for screen readers", "error banner announced via aria-live"]
                }
              ]
            }
            """),
        new DocumentExample(
            "invalid-flow-without-error-state-and-orphan-screen",
            false,
            """
            {
              "flows": [
                {
                  "id": "F1",
                  "name": "sign in",
                  "entryState": "signed-out landing page",
                  "successState": "dashboard",
                  "errorStates": []
                }
              ],
              "screens": [
                {
                  "id": "S1",
                  "flowRef": "F9",
                  "a11yRequirements": ["labelled inputs"]
                }
              ]
            }
            """,
            new[] { FlowMissingErrorState, ScreenUnknownFlow }),
    };
}

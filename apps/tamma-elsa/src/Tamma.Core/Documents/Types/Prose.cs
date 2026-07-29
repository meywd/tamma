using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The closed audience vocabulary for prose documents (Story 41-1c, Design
/// Decision D3). Seeded from the actual Epic 41 consumers: <c>engineering</c>
/// (41-9 ADR, 41-22 postmortem), <c>developer</c> (41-24 changelog, 41-25 API
/// docs), <c>user</c> (41-24 release notes, 41-25 user docs), <c>ops</c> (41-26
/// runbook), <c>stakeholder</c> (41-4 roadmap, 41-5 stakeholder update),
/// <c>team</c> (41-8 retro narrative). Out-of-vocabulary values are violations
/// (<see cref="ProseDocumentType.AudienceOutOfVocabulary"/>), never a silent
/// normalisation or default.
/// </summary>
public enum ProseAudience
{
    [Wire("engineering")] Engineering,
    [Wire("developer")]   Developer,
    [Wire("user")]        User,
    [Wire("ops")]         Ops,
    [Wire("stakeholder")] Stakeholder,
    [Wire("team")]        Team,
}

/// <summary>
/// The closed kind vocabulary for prose documents (Story 41-1c, Design Decision
/// D3) — one member per prose family document. The kind names WHAT the prose is;
/// it never implies a validated body structure (per-kind shape guidance lives in
/// each producing cell's prompt file, D5).
/// </summary>
public enum ProseKind
{
    [Wire("adr")]             Adr,
    [Wire("postmortem")]      Postmortem,
    [Wire("release-notes")]   ReleaseNotes,
    [Wire("changelog")]       Changelog,
    [Wire("user-docs")]       UserDocs,
    [Wire("api-docs")]        ApiDocs,
    [Wire("runbook")]         Runbook,
    [Wire("roadmap")]         Roadmap,
    [Wire("status-update")]   StatusUpdate,
    [Wire("retro-narrative")] RetroNarrative,
}

/// <summary>Wire helpers for <see cref="ProseAudience"/> (the EnumWire pattern).</summary>
public static class ProseAudienceExtensions
{
    /// <summary>The canonical wire string for <paramref name="value"/>.</summary>
    public static string ToWire(this ProseAudience value) => EnumWire<ProseAudience>.ToWire(value);

    /// <summary>Case-sensitive (ordinal) lookup; false on null/unknown input.</summary>
    public static bool TryParse(string? input, out ProseAudience value)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            value = default;
            return false;
        }
        return EnumWire<ProseAudience>.TryParse(input, out value);
    }
}

/// <summary>Wire helpers for <see cref="ProseKind"/> (the EnumWire pattern).</summary>
public static class ProseKindExtensions
{
    /// <summary>The canonical wire string for <paramref name="value"/>.</summary>
    public static string ToWire(this ProseKind value) => EnumWire<ProseKind>.ToWire(value);

    /// <summary>Case-sensitive (ordinal) lookup; false on null/unknown input.</summary>
    public static bool TryParse(string? input, out ProseKind value)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            value = default;
            return false;
        }
        return EnumWire<ProseKind>.TryParse(input, out value);
    }
}

/// <summary>
/// A prose document (Story 41-1c): a <see cref="Kind"/> and an
/// <see cref="Audience"/> from the two closed vocabularies, a <see cref="Title"/>,
/// and a <see cref="Body"/> of free markdown. The body is DELIBERATELY
/// unvalidated — "prose stays prose" (epic-39 README principle, made code here).
/// </summary>
public sealed record Prose
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("audience")] public string Audience { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("body")] public string Body { get; init; } = "";
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>prose</c> document (Story 41-1c AC1/AC2/
/// AC4). Validation asserts ENVELOPE-LEVEL facts only: kind and audience present
/// and in vocabulary, a non-empty title, a non-whitespace body. There is NO
/// heading check, NO length check, NO structure check — AC2 pins both directions
/// (arbitrary markdown validates; an empty body does not), so adding a "helpful"
/// body rule here is the failure mode, not an improvement. Per-kind shape
/// conventions (ADR context/decision/consequences, postmortem timeline, runbook
/// steps…) are guidance in each producing cell's prompt file (D5), never rules.
/// </summary>
public sealed class ProseDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>No kind — a prose document must say what it is.</summary>
    public const string KindMissing = "PROSE_KIND_MISSING";

    /// <summary>The kind is not in the closed <see cref="ProseKind"/> vocabulary (AC4).</summary>
    public const string KindOutOfVocabulary = "PROSE_KIND_OUT_OF_VOCABULARY";

    /// <summary>No audience — a prose document must carry its audience tag (AC7's write guard, D8).</summary>
    public const string AudienceMissing = "PROSE_AUDIENCE_MISSING";

    /// <summary>The audience is not in the closed <see cref="ProseAudience"/> vocabulary (AC4).</summary>
    public const string AudienceOutOfVocabulary = "PROSE_AUDIENCE_OUT_OF_VOCABULARY";

    /// <summary>No title.</summary>
    public const string TitleMissing = "PROSE_TITLE_MISSING";

    /// <summary>The body is empty or whitespace-only (AC2's one and only body rule).</summary>
    public const string BodyEmpty = "PROSE_BODY_EMPTY";

    public string Key => DocumentTypeKey.Prose.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Prose);

    public DocumentValidationResult Validate(JsonElement payload)
    {
        Prose? doc;
        try
        {
            doc = payload.Deserialize<Prose>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a prose document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (string.IsNullOrWhiteSpace(doc.Kind))
            violations.Add(new DocumentViolation(
                KindMissing, "The prose document has no kind — say what it is (e.g. adr, postmortem, runbook)."));
        else if (!ProseKindExtensions.TryParse(doc.Kind, out _))
            violations.Add(new DocumentViolation(
                KindOutOfVocabulary,
                $"kind '{doc.Kind}' is not in the prose kind vocabulary " +
                $"({string.Join(", ", Enum.GetValues<ProseKind>().Select(k => k.ToWire()))})."));

        if (string.IsNullOrWhiteSpace(doc.Audience))
            violations.Add(new DocumentViolation(
                AudienceMissing, "The prose document has no audience — every prose document carries its audience tag."));
        else if (!ProseAudienceExtensions.TryParse(doc.Audience, out _))
            violations.Add(new DocumentViolation(
                AudienceOutOfVocabulary,
                $"audience '{doc.Audience}' is not in the prose audience vocabulary " +
                $"({string.Join(", ", Enum.GetValues<ProseAudience>().Select(a => a.ToWire()))})."));

        if (string.IsNullOrWhiteSpace(doc.Title))
            violations.Add(new DocumentViolation(
                TitleMissing, "The prose document has no title."));

        if (string.IsNullOrWhiteSpace(doc.Body))
            violations.Add(new DocumentViolation(
                BodyEmpty, "The prose body is empty or whitespace-only — a prose document must say something."));

        // NOTHING ELSE — the body is free markdown by design (AC2). Do not add
        // heading/length/structure checks here; that breaks eight downstream stories.

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // ONE contract for all ten kinds (41-1c D5): it renders the ENVELOPE contract
    // — the four payload keys and the two closed vocabularies — and states that
    // "body" is free markdown. Per-kind shape guidance (ADR sections, postmortem
    // timeline, runbook steps…) lives in each producing cell's Prompts/{role}/
    // {action}.md, added by the consuming story (41-4/41-5/41-8/41-9/41-22/
    // 41-24/41-25/41-26) — never as a validated schema.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "kind": "adr | postmortem | release-notes | changelog | user-docs | api-docs | runbook | roadmap | status-update | retro-narrative",
          "audience": "engineering | developer | user | ops | stakeholder | team",
          "title": "the document's title",
          "body": "the full document as free markdown — any structure you judge right for the kind and audience"
        }
        Rules: "kind" and "audience" must each be exactly one value from the closed sets above
        (lowercase, as written); "title" is required; "body" is required and must not be empty —
        but its CONTENT is unvalidated markdown: headings, ordering and structure are yours.
        Follow any shape guidance given elsewhere in this prompt as convention, not schema.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        // Valid: an ADR whose headings are in an unconventional order — proof the
        // body carries no structural rules (AC2's "no forced structure" direction).
        new DocumentExample(
            "valid-adr-headings-in-any-order",
            true,
            """
            {
              "kind": "adr",
              "audience": "engineering",
              "title": "ADR-007: Store prose as an unvalidated markdown body",
              "body": "## Consequences\nProse rides the document lifecycle unchanged.\n\n## Decision\nRegister a prose type whose body is free markdown.\n\n## Context\nEight stories need prose on the lifecycle with an audience tag."
            }
            """),
        new DocumentExample(
            "invalid-empty-body",
            false,
            """
            {
              "kind": "runbook",
              "audience": "ops",
              "title": "Restore the tenant pool",
              "body": "   \n\t  "
            }
            """,
            new[] { BodyEmpty }),
        new DocumentExample(
            "invalid-audience-out-of-vocabulary",
            false,
            """
            {
              "kind": "release-notes",
              "audience": "marketing",
              "title": "v2.0 release notes",
              "body": "## Highlights\n- Faster everything."
            }
            """,
            new[] { AudienceOutOfVocabulary }),
        new DocumentExample(
            "invalid-kind-out-of-vocabulary",
            false,
            """
            {
              "kind": "memo",
              "audience": "team",
              "title": "A quick memo",
              "body": "Just a note."
            }
            """,
            new[] { KindOutOfVocabulary }),
        new DocumentExample(
            "invalid-missing-title",
            false,
            """
            {
              "kind": "postmortem",
              "audience": "engineering",
              "title": "",
              "body": "## What happened\nThe queue stalled."
            }
            """,
            new[] { TitleMissing }),
        new DocumentExample(
            "invalid-missing-kind-and-audience",
            false,
            """
            {
              "title": "Untagged prose",
              "body": "Some words."
            }
            """,
            new[] { KindMissing, AudienceMissing }),
    };
}

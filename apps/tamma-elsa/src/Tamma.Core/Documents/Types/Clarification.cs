using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The two phases of a clarification document (Design Decision D3 — one flat
/// payload with a <c>phase</c> discriminator, NOT two types; the README's
/// "Questions → Resolution" single row). Shipped as a <c>[Wire]</c> enum.
/// </summary>
public enum ClarificationPhase
{
    [Wire("questions")] Questions,
    [Wire("resolution")] Resolution,
}

/// <summary>
/// One resolution in the resolution phase: the clarified <see cref="Requirement"/>
/// and the positional <see cref="QuestionId"/> (<c>Q-1</c>…<c>Q-n</c>) it resolves
/// (Design Decision D3). Question identity is positional and derived, never stored
/// on the question strings themselves.
/// </summary>
public sealed record QuestionResolution
{
    [JsonPropertyName("questionId")] public string QuestionId { get; init; } = "";
    [JsonPropertyName("requirement")] public string Requirement { get; init; } = "";
}

/// <summary>
/// One clarification document across both phases (Design Decision D3). The
/// questions phase carries <see cref="Questions"/> (an array of strings, so the
/// legacy <c>ClarifyParsing.ParseQuestions</c> still parses it); the resolution
/// phase adds root-level <see cref="ClarifiedRequirement"/> /
/// <see cref="RemainingAmbiguities"/> / <see cref="Resolved"/> (byte-compatible
/// with <c>ClarifyParsing.ParseClarification</c>) plus the additive
/// <see cref="Resolutions"/> array.
/// </summary>
public sealed record Clarification
{
    [JsonPropertyName("phase")] public string Phase { get; init; } = "";
    [JsonPropertyName("questions")] public IReadOnlyList<string> Questions { get; init; } = [];
    [JsonPropertyName("clarifiedRequirement")] public string? ClarifiedRequirement { get; init; }
    [JsonPropertyName("resolutions")] public IReadOnlyList<QuestionResolution> Resolutions { get; init; } = [];
    [JsonPropertyName("remainingAmbiguities")] public IReadOnlyList<string> RemainingAmbiguities { get; init; } = [];
    [JsonPropertyName("resolved")] public bool Resolved { get; init; }
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>clarification</c> document (Story 39-3
/// AC5). Two-phase: the questions phase requires ≥1 open-ended question; the
/// resolution phase requires a root-level clarified requirement and that every
/// resolution reference a known question.
///
/// <para><b>Open-endedness rule (D4):</b> the baseline floor is ≥1 non-empty
/// trimmed question. The deliberate tightening: a deterministic closed-question
/// detector — a question that STARTS with a yes/no auxiliary (is/are/do/does/did/
/// can/could/will/would/should/has/have/was/were) AND contains no interrogative
/// word (what/why/how/when/where/which/who/whom/whose) AND no "or"-alternative is
/// closed-form. <see cref="NoOpenQuestion"/> fires when ALL surviving questions
/// are closed-form. A question mark is NOT consulted. This aligns the validator
/// with what <c>product_owner/clarify-requirements.md</c> already instructs
/// ("open-ended (not yes/no)").</para>
/// </summary>
public sealed class ClarificationDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>The <c>phase</c> is not one of questions, resolution.</summary>
    public const string UnknownPhase = "UNKNOWN_PHASE";

    /// <summary>Questions phase: no open-ended question survives (zero questions, or all closed-form). D4.</summary>
    public const string NoOpenQuestion = "NO_OPEN_QUESTION";

    /// <summary>Questions phase: a question entry is empty / whitespace.</summary>
    public const string EmptyQuestion = "EMPTY_QUESTION";

    /// <summary>Resolution phase: the load-bearing root <c>clarifiedRequirement</c> is missing/empty.</summary>
    public const string MissingClarifiedRequirement = "MISSING_CLARIFIED_REQUIREMENT";

    /// <summary>Resolution phase: a resolution states no clarified requirement.</summary>
    public const string EmptyResolution = "EMPTY_RESOLUTION";

    /// <summary>Resolution phase: a resolution's questionId is outside Q-1…Q-n of the payload's questions.</summary>
    public const string UnknownQuestionRef = "UNKNOWN_QUESTION_REF";

    private static readonly IReadOnlySet<string> Auxiliaries = new HashSet<string>(StringComparer.Ordinal)
    {
        "is", "are", "do", "does", "did", "can", "could", "will", "would", "should", "has", "have", "was", "were",
    };

    private static readonly IReadOnlyList<string> Interrogatives = new[]
    {
        "what", "why", "how", "when", "where", "which", "who", "whom", "whose",
    };

    public string Key => DocumentTypeKey.Clarification.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Clarification);

    public DocumentValidationResult Validate(JsonElement payload)
    {
        Clarification? doc;
        try
        {
            doc = payload.Deserialize<Clarification>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a clarification document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        if (!EnumWire<ClarificationPhase>.TryParse(doc.Phase ?? "", out var phase))
            return DocumentValidationResult.Invalid(new DocumentViolation(
                UnknownPhase, $"phase is '{doc.Phase}', which is not one of questions, resolution."));

        var violations = new List<DocumentViolation>();
        var questions = doc.Questions ?? [];

        if (phase == ClarificationPhase.Questions)
        {
            if (questions.Any(q => string.IsNullOrWhiteSpace(q)))
                violations.Add(new DocumentViolation(
                    EmptyQuestion, "A question entry is empty — every question must be non-empty."));

            var surviving = questions.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
            if (surviving.Count == 0 || surviving.All(IsClosedForm))
                violations.Add(new DocumentViolation(
                    NoOpenQuestion,
                    "The questions phase needs at least one open-ended (not yes/no) question — none survive."));
        }
        else // Resolution
        {
            if (string.IsNullOrWhiteSpace(doc.ClarifiedRequirement))
                violations.Add(new DocumentViolation(
                    MissingClarifiedRequirement,
                    "The resolution phase has no clarifiedRequirement — the disambiguated requirement is load-bearing."));

            var questionCount = questions.Count;
            foreach (var resolution in doc.Resolutions ?? [])
            {
                if (string.IsNullOrWhiteSpace(resolution.Requirement))
                    violations.Add(new DocumentViolation(
                        EmptyResolution,
                        $"Resolution for '{resolution.QuestionId}' states no clarified requirement."));

                if (!IsKnownQuestionRef(resolution.QuestionId, questionCount))
                    violations.Add(new DocumentViolation(
                        UnknownQuestionRef,
                        $"Resolution references '{resolution.QuestionId}', which is not a question in this " +
                        $"document (expected Q-1…Q-{questionCount})."));
            }
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    private static bool IsClosedForm(string question)
    {
        var text = question.Trim().ToLowerInvariant();
        var firstWord = new string(text.TakeWhile(char.IsLetter).ToArray());
        if (!Auxiliaries.Contains(firstWord))
            return false;
        if (Interrogatives.Any(w => ContainsWord(text, w)))
            return false;
        if (text.Contains(" or ", StringComparison.Ordinal))
            return false;
        return true;
    }

    private static bool ContainsWord(string text, string word)
    {
        var idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = idx == 0 || !char.IsLetter(text[idx - 1]);
            var afterPos = idx + word.Length;
            var afterOk = afterPos >= text.Length || !char.IsLetter(text[afterPos]);
            if (beforeOk && afterOk)
                return true;
            idx = afterPos;
        }
        return false;
    }

    private static bool IsKnownQuestionRef(string? questionId, int questionCount)
    {
        if (string.IsNullOrWhiteSpace(questionId))
            return false;
        if (!questionId.StartsWith("Q-", StringComparison.Ordinal))
            return false;
        if (!int.TryParse(questionId.AsSpan(2), out var n))
            return false;
        return n >= 1 && n <= questionCount;
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // The tokens below are pinned by ContractBindingTests.Bindings for the two
    // clarify cells: (product_owner, clarify-requirements) → ParseQuestions pins the
    // phrase "JSON array"; (product_owner, incorporate-answers) → ParseClarification
    // pins "clarifiedRequirement", "remainingAmbiguities", "resolved".
    private const string Contract =
        """
        A clarification document has a "phase" of either "questions" or "resolution".

        QUESTIONS phase — return ONLY a JSON object with "phase": "questions" and a "questions"
        JSON array of open-ended (not yes/no) question strings:
        { "phase": "questions", "questions": ["What is the target platform?", "Which auth model is expected?"] }

        RESOLUTION phase — return ONLY a JSON object of this shape:
        {
          "phase": "resolution",
          "clarifiedRequirement": "the full disambiguated requirement text",
          "resolutions": [ { "questionId": "Q-1", "requirement": "the clarified statement" } ],
          "remainingAmbiguities": ["anything still unclear"],
          "resolved": true
        }
        Rules: the questions phase needs at least one open-ended question; the resolution phase
        needs a non-empty "clarifiedRequirement" and every "questionId" must reference a question
        (Q-1…Q-n) in this document.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-questions-phase",
            true,
            """
            { "phase": "questions", "questions": ["What is the target platform?", "Which auth model is expected?"] }
            """),
        new DocumentExample(
            "invalid-resolution-missing-requirement-and-bad-ref",
            false,
            """
            {
              "phase": "resolution",
              "questions": ["What is the target platform?"],
              "resolutions": [ { "questionId": "Q-9", "requirement": "web" } ]
            }
            """,
            new[] { MissingClarifiedRequirement, UnknownQuestionRef }),
    };
}

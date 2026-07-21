using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Ambiguity;
using Tamma.Activities.Clarify;
using Tamma.Activities.Core;
using Tamma.Activities.Decomposition;
using Tamma.Activities.Research;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using TypedAmbiguity = Tamma.Core.Documents.Types.AmbiguityAssessment;
using TypedClarification = Tamma.Core.Documents.Types.Clarification;
using TypedDecomposition = Tamma.Core.Documents.Types.Decomposition;
using TypedFindings = Tamma.Core.Documents.Types.Findings;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-3 AC7 — the 39-12/39-13 transition-window proof. For every fixture the
/// baseline <c>*ParsingTests.cs</c> parse successfully, this suite: slices the JSON
/// (the <see cref="JsonSlice"/> first-<c>{</c>/last-<c>}</c> idiom), deserializes into
/// the TYPED payload (<see cref="DocumentJson.Options"/>), runs <c>Validate</c> and
/// asserts either valid or EXACTLY the documented deliberate-tightening codes, then
/// RE-SERIALIZES the typed payload and asserts the OLD parser still returns non-null
/// with its key fields intact. Lives in <c>Tamma.Activities.Tests</c> (D7).
/// </summary>
[TestFixture]
public class RoundTripCompatibilityTests
{
    private static readonly DecompositionDocumentType DecompositionType = new();
    private static readonly FindingsDocumentType FindingsType = new();
    private static readonly AmbiguityAssessmentDocumentType AmbiguityType = new();
    private static readonly ClarificationDocumentType ClarificationType = new();

    private static T DeserializeTyped<T>(string fixture)
    {
        var sliced = JsonSlice.ExtractObject(fixture);
        sliced.Should().NotBeNull("the fixture must carry a JSON object to round-trip");
        return JsonSerializer.Deserialize<T>(sliced!, DocumentJson.Options)!;
    }

    private static void AssertValidateCodes(IDocumentType type, string json, params string[] expectedCodes)
    {
        using var doc = JsonDocument.Parse(json);
        var result = type.Validate(doc.RootElement);
        result.Violations.Select(v => v.Code).Distinct()
            .Should().BeEquivalentTo(expectedCodes,
                "the typed validator's verdict on this baseline fixture must match the documented set " +
                "(empty = valid; otherwise exactly the deliberate tightenings)");
        result.IsValid.Should().Be(expectedCodes.Length == 0);
    }

    // ── Decomposition fixtures ────────────────────────────────────────────────
    private const string ValidDecomposition =
        """
        {
          "summary": "Split the auth feature into a schema slice, an endpoint slice, and a UI slice.",
          "subtasks": [
            { "id": "ST-1", "title": "Add users table", "description": "schema + migration", "acceptanceCriteria": "applies cleanly", "estimateHours": 3, "complexity": "low", "dependsOn": [] },
            { "id": "ST-2", "title": "Login endpoint", "description": "POST /login", "acceptanceCriteria": "200 + token", "estimateHours": 6, "complexity": "medium", "dependsOn": ["ST-1"] },
            { "id": "ST-3", "title": "Login UI", "description": "form", "acceptanceCriteria": "user can log in", "estimateHours": 5, "complexity": "high", "dependsOn": ["ST-2"] }
          ]
        }
        """;

    private const string TemplateShapedDecomposition =
        """
        {
          "summary": "Deliver rate limiting incrementally: middleware first, then config.",
          "subtasks": [
            { "id": "ST-1", "title": "Token-bucket middleware", "description": "limiter keyed by tenant id", "acceptanceCriteria": "over the limit → 429", "estimateHours": 6, "complexity": "medium", "dependsOn": [] },
            { "id": "ST-2", "title": "Per-tenant config", "description": "read limit from config", "acceptanceCriteria": "configurable per tenant", "estimateHours": 4, "complexity": "low", "dependsOn": ["ST-1"] }
          ]
        }
        """;

    private const string MessyDecomposition =
        """{ "summary": "s", "subtasks": [ { "id": "A", "title": "t", "complexity": "Trivial.", "estimateHours": -4 } ] }""";

    private const string WithBadDepsDecomposition =
        """
        { "summary": "s", "subtasks": [
          { "id": "ST-1", "title": "a", "dependsOn": ["ST-1", "ST-99"] },
          { "id": "ST-2", "title": "b", "dependsOn": ["ST-1", "ST-1"] }
        ] }
        """;

    private const string WithShellsDecomposition =
        """
        { "summary": "s", "subtasks": [
          { "id": "", "title": "no id" },
          { "id": "ST-1", "title": "", "description": "" },
          { "id": "ST-2", "title": "real" },
          { "id": "ST-2", "title": "duplicate id kept as first" }
        ] }
        """;

    [Test]
    public void Decomposition_valid_fixtures_round_trip()
    {
        foreach (var fixture in new[] { ValidDecomposition, TemplateShapedDecomposition })
        {
            AssertValidateCodes(DecompositionType, fixture);

            var typed = DeserializeTyped<TypedDecomposition>(fixture);
            var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);

            var parsed = DecompositionParsing.ParseDecomposition(reserialized);
            parsed.Should().NotBeNull("the old parser must still parse the re-serialized typed payload");
            parsed!.Summary.Should().NotBeNullOrWhiteSpace();
            parsed.Subtasks.Should().NotBeEmpty();
        }
    }

    [Test]
    public void Decomposition_tightening_fixtures_round_trip()
    {
        AssertValidateCodes(DecompositionType, MessyDecomposition,
            DecompositionDocumentType.SizingOutOfRange, DecompositionDocumentType.UnknownComplexity);
        AssertValidateCodes(DecompositionType, WithBadDepsDecomposition,
            DecompositionDocumentType.SelfDependsOn, DecompositionDocumentType.DanglingDependsOn,
            DecompositionDocumentType.SizingOutOfRange);
        AssertValidateCodes(DecompositionType, WithShellsDecomposition,
            DecompositionDocumentType.TaskMissingId, DecompositionDocumentType.TaskEmptyShell,
            DecompositionDocumentType.DuplicateTaskId, DecompositionDocumentType.SizingOutOfRange);

        // The old parser still recovers a non-null decomposition from each re-serialized payload.
        foreach (var fixture in new[] { MessyDecomposition, WithBadDepsDecomposition, WithShellsDecomposition })
        {
            var typed = DeserializeTyped<TypedDecomposition>(fixture);
            var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);
            DecompositionParsing.ParseDecomposition(reserialized).Should().NotBeNull(
                "even a fixture that trips a documented tightening must remain parseable by the old parser");
        }
    }

    // ── Research (Findings) fixtures ──────────────────────────────────────────
    private const string ValidReport =
        """
        {
          "topic": "caching layer",
          "summary": "Redis is the incumbent cache; no per-tenant isolation exists yet.",
          "findings": [
            { "title": "Low relevance", "summary": "Minor note", "relevance": 0.2, "confidence": 0.9, "citations": ["a.cs"] },
            { "title": "High relevance", "summary": "Core finding", "relevance": 0.95, "confidence": 0.6, "citations": ["b.cs", "https://x"] },
            { "title": "Mid relevance", "summary": "Secondary", "relevance": 0.5, "confidence": 0.8 }
          ],
          "overallConfidence": 0.77
        }
        """;

    private const string TemplateShapedReport =
        """
        {
          "topic": "per-tenant rate limiting",
          "summary": "No rate limiter exists; a token-bucket keyed by tenant id is lowest-risk.",
          "findings": [
            { "title": "No existing limiter", "summary": "No rate-limiting middleware today.", "relevance": 0.95, "confidence": 0.9, "citations": ["src/Program.cs"] },
            { "title": "Tenant id on context", "summary": "Every request resolves a tenant id.", "relevance": 0.8, "confidence": 0.85, "citations": ["src/TenantContext.cs"] }
          ],
          "overallConfidence": 0.88
        }
        """;

    private const string NoOverallReport =
        """
        { "summary": "s", "findings": [
          { "summary": "a", "relevance": 0.5, "confidence": 0.4 },
          { "summary": "b", "relevance": 0.5, "confidence": 0.6 }
        ] }
        """;

    private const string NoTopicReport = """{ "summary": "s", "findings": [ { "summary": "a" } ] }""";

    private const string WithShellReport =
        """
        { "summary": "s", "findings": [
          { "title": "", "summary": "" },
          { "summary": "real finding" }
        ] }
        """;

    [Test]
    public void Findings_fixtures_round_trip()
    {
        // TemplateShapedReport cites every finding → valid; the others trip MISSING_EVIDENCE
        // (evidence-required tightening) and withShell also FINDING_EMPTY_SHELL.
        AssertValidateCodes(FindingsType, TemplateShapedReport);
        AssertValidateCodes(FindingsType, ValidReport, FindingsDocumentType.MissingEvidence);
        AssertValidateCodes(FindingsType, NoOverallReport, FindingsDocumentType.MissingEvidence);
        AssertValidateCodes(FindingsType, NoTopicReport, FindingsDocumentType.MissingEvidence);
        AssertValidateCodes(FindingsType, WithShellReport,
            FindingsDocumentType.FindingEmptyShell, FindingsDocumentType.MissingEvidence);

        foreach (var fixture in new[] { ValidReport, TemplateShapedReport, NoOverallReport, NoTopicReport, WithShellReport })
        {
            var typed = DeserializeTyped<TypedFindings>(fixture);
            var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);
            var parsed = ResearchParsing.ParseReport(reserialized, topic: "fallback");
            parsed.Should().NotBeNull("the old parser must still parse the re-serialized typed payload");
            parsed!.Summary.Should().NotBeNullOrWhiteSpace();
            parsed.Findings.Should().NotBeEmpty();
        }
    }

    // ── Ambiguity fixtures ────────────────────────────────────────────────────
    private const string ValidAssessment =
        """
        {
          "score": 0.72,
          "confidence": 0.8,
          "rationale": "Missing acceptance criteria and vague wording.",
          "ambiguities": [
            { "type": "missing", "description": "No acceptance criteria", "severity": "high", "recommendation": "Ask for measurable ACs" },
            { "type": "Vague.", "description": "\"fast\" is not quantified", "severity": "medium", "recommendation": "Define a latency target" }
          ]
        }
        """;

    private const string TemplateShapedAssessment =
        """
        {
          "score": 0.65,
          "confidence": 0.75,
          "rationale": "Omits the target platform and contradicts itself on auth.",
          "ambiguities": [
            { "type": "missing", "description": "Target platform is unstated.", "severity": "high", "recommendation": "Confirm the platform." },
            { "type": "contradictory", "description": "'no login' but 'per-user history'.", "severity": "high", "recommendation": "Resolve whether accounts are required." },
            { "type": "implicit", "description": "Assumes English-only.", "severity": "low", "recommendation": "Confirm i18n scope." }
          ]
        }
        """;

    private const string ClearAssessment =
        """{ "score": 0.05, "confidence": 0.9, "rationale": "Fully specified with clear ACs.", "ambiguities": [] }""";

    private const string ZeroAssessment = """{ "score": 0.0, "rationale": "Crystal clear." }""";
    private const string NoConfAssessment = """{ "score": 0.4, "rationale": "Some gaps." }""";

    [Test]
    public void Ambiguity_fixtures_round_trip()
    {
        // ValidAssessment carries "Vague." (a synonym the baseline folds) → the strict
        // typed validator reports UNKNOWN_AMBIGUITY_TYPE; the rest are clean.
        AssertValidateCodes(AmbiguityType, ValidAssessment, AmbiguityAssessmentDocumentType.UnknownAmbiguityType);
        AssertValidateCodes(AmbiguityType, TemplateShapedAssessment);
        AssertValidateCodes(AmbiguityType, ClearAssessment);
        AssertValidateCodes(AmbiguityType, ZeroAssessment);
        AssertValidateCodes(AmbiguityType, NoConfAssessment);

        foreach (var fixture in new[] { ValidAssessment, TemplateShapedAssessment, ClearAssessment, ZeroAssessment, NoConfAssessment })
        {
            var typed = DeserializeTyped<TypedAmbiguity>(fixture);
            var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);
            var parsed = AmbiguityParsing.ParseAssessment(reserialized);
            parsed.Should().NotBeNull("the old parser must still parse the re-serialized typed payload");
            parsed!.Rationale.Should().NotBeNullOrWhiteSpace();
        }
    }

    // ── Clarification fixtures (shaped per the two prompt templates) ──────────
    private const string QuestionsClarification =
        """
        {
          "phase": "questions",
          "questions": ["What is the target platform?", "Which auth model is expected?"]
        }
        """;

    private const string ResolutionClarification =
        """
        {
          "phase": "resolution",
          "clarifiedRequirement": "the full disambiguated requirement text",
          "questions": ["What is the target platform?"],
          "resolutions": [ { "questionId": "Q-1", "requirement": "web only" } ],
          "remainingAmbiguities": ["anything still unclear"],
          "resolved": true
        }
        """;

    [Test]
    public void Clarification_questions_phase_round_trips()
    {
        AssertValidateCodes(ClarificationType, QuestionsClarification);

        var typed = DeserializeTyped<TypedClarification>(QuestionsClarification);
        var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);

        var questions = ClarifyParsing.ParseQuestions(reserialized);
        questions.Should().HaveCount(2, "the old ParseQuestions must still recover the questions array");
        questions.Should().Contain("What is the target platform?");
    }

    [Test]
    public void Clarification_resolution_phase_round_trips()
    {
        AssertValidateCodes(ClarificationType, ResolutionClarification);

        var typed = DeserializeTyped<TypedClarification>(ResolutionClarification);
        var reserialized = JsonSerializer.Serialize(typed, DocumentJson.Options);

        var clarification = ClarifyParsing.ParseClarification(reserialized);
        clarification.Should().NotBeNull("the old ParseClarification must still parse the re-serialized payload");
        clarification!.ClarifiedRequirement.Should().Be("the full disambiguated requirement text");
        clarification.Resolved.Should().BeTrue();
    }
}

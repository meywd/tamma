using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b AC2 (AcceptanceCriteria) — one rejecting and one accepting
/// fixture per rule, each asserting the named violation code (never bare
/// "invalid"). The cross-document scope rule (D5) is covered in
/// <see cref="DocumentTypesCrossDocumentValidationTests"/>.
/// </summary>
[TestFixture]
public class AcceptanceCriteriaTypeTests
{
    private static readonly AcceptanceCriteriaDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    private const string ValidDoc =
        """
        {
          "issueId": "issue-42",
          "criteria": [
            { "id": "AC-1", "form": "given-when-then", "given": "g", "when": "w", "then": "t", "verifiable": true },
            { "id": "AC-2", "form": "checklist", "statement": "counters reset each window", "verifiable": true }
          ]
        }
        """;

    // ── accepting fixtures ──────────────────────────────────────────────────

    [Test]
    public void Valid_document_passes_every_rule()
    {
        var r = Validate(ValidDoc);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
        r.Violations.Should().BeEmpty();
    }

    [Test]
    public void Malformed_payload_is_reported()
    {
        Codes(Validate("""{ "criteria": "not-an-array" }"""))
            .Should().Contain(AcceptanceCriteriaDocumentType.MalformedPayload);
    }

    // ── ISSUE_ID_MISSING ────────────────────────────────────────────────────

    [Test]
    public void Missing_issue_id_is_reported()
    {
        var r = Validate(
            """
            { "issueId": "  ", "criteria": [ { "id": "AC-1", "form": "checklist", "statement": "s", "verifiable": true } ] }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.IssueIdMissing);
    }

    [Test]
    public void Present_issue_id_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(AcceptanceCriteriaDocumentType.IssueIdMissing);

    // ── NO_CRITERIA ─────────────────────────────────────────────────────────

    [Test]
    public void Empty_criteria_list_is_reported() =>
        Codes(Validate("""{ "issueId": "issue-42", "criteria": [] }"""))
            .Should().Contain(AcceptanceCriteriaDocumentType.NoCriteria);

    [Test]
    public void Non_empty_criteria_list_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(AcceptanceCriteriaDocumentType.NoCriteria);

    // ── CRITERION_ID_MISSING / CRITERION_ID_DUPLICATED ──────────────────────

    [Test]
    public void Criterion_without_id_is_reported()
    {
        var r = Validate(
            """
            { "issueId": "issue-42", "criteria": [ { "id": "", "form": "checklist", "statement": "s", "verifiable": true } ] }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.CriterionIdMissing);
    }

    [Test]
    public void Duplicate_criterion_ids_are_reported()
    {
        var r = Validate(
            """
            {
              "issueId": "issue-42",
              "criteria": [
                { "id": "AC-1", "form": "checklist", "statement": "a", "verifiable": true },
                { "id": "AC-1", "form": "checklist", "statement": "b", "verifiable": true }
              ]
            }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.CriterionIdDuplicated);
    }

    [Test]
    public void Unique_ids_are_accepted()
    {
        var codes = Codes(Validate(ValidDoc));
        codes.Should().NotContain(AcceptanceCriteriaDocumentType.CriterionIdMissing);
        codes.Should().NotContain(AcceptanceCriteriaDocumentType.CriterionIdDuplicated);
    }

    // ── CRITERION_FORM_OUT_OF_VOCABULARY ────────────────────────────────────

    [Test]
    public void Out_of_vocabulary_form_is_reported()
    {
        var r = Validate(
            """
            { "issueId": "issue-42", "criteria": [ { "id": "AC-1", "form": "user-story", "statement": "s", "verifiable": true } ] }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.CriterionFormOutOfVocabulary);
    }

    [Test]
    public void Both_closed_forms_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(AcceptanceCriteriaDocumentType.CriterionFormOutOfVocabulary);

    // ── GWT_INCOMPLETE ──────────────────────────────────────────────────────

    [Test]
    public void Gwt_criterion_missing_then_is_reported()
    {
        var r = Validate(
            """
            { "issueId": "issue-42", "criteria": [ { "id": "AC-1", "form": "given-when-then", "given": "g", "when": "w", "verifiable": true } ] }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.GwtIncomplete);
    }

    [Test]
    public void Complete_gwt_criterion_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(AcceptanceCriteriaDocumentType.GwtIncomplete);

    // ── CHECKLIST_ITEM_EMPTY ────────────────────────────────────────────────

    [Test]
    public void Checklist_criterion_without_statement_is_reported()
    {
        var r = Validate(
            """
            { "issueId": "issue-42", "criteria": [ { "id": "AC-1", "form": "checklist", "statement": "  ", "verifiable": true } ] }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.ChecklistItemEmpty);
    }

    [Test]
    public void Checklist_criterion_with_statement_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(AcceptanceCriteriaDocumentType.ChecklistItemEmpty);

    // ── CRITERION_NOT_INDEPENDENTLY_VERIFIABLE ──────────────────────────────

    [Test]
    public void Unverifiable_criterion_is_reported()
    {
        var r = Validate(
            """
            { "issueId": "issue-42", "criteria": [ { "id": "AC-1", "form": "checklist", "statement": "feels fast", "verifiable": false } ] }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.CriterionNotIndependentlyVerifiable);
    }

    [Test]
    public void Missing_verifiable_attestation_is_reported()
    {
        var r = Validate(
            """
            { "issueId": "issue-42", "criteria": [ { "id": "AC-1", "form": "checklist", "statement": "s" } ] }
            """);
        Codes(r).Should().Contain(AcceptanceCriteriaDocumentType.CriterionNotIndependentlyVerifiable);
    }

    [Test]
    public void Verifiable_criteria_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(AcceptanceCriteriaDocumentType.CriterionNotIndependentlyVerifiable);

    // ── Context-free Validate never emits the cross-document code ───────────

    [Test]
    public void Payload_only_validate_never_reports_unplanned_scope()
    {
        var r = Validate(
            """
            { "issueId": "issue-42", "criteria": [ { "id": "AC-1", "form": "checklist", "statement": "s", "verifiable": true, "scopeRef": "ST-99" } ] }
            """);
        Codes(r).Should().NotContain(AcceptanceCriteriaDocumentType.CriterionReferencesUnplannedScope);
        r.IsValid.Should().BeTrue();
    }

    // ── shared contract properties ──────────────────────────────────────────

    [Test]
    public void Contract_is_deterministic()
    {
        var first = Type.RenderContract();
        Type.RenderContract().Should().Be(first);
        first.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Typed_record_round_trips_through_document_json()
    {
        var doc = JsonSerializer.Deserialize<AcceptanceCriteria>(ValidDoc, DocumentJson.Options)!;
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options);
        var back = JsonSerializer.Deserialize<AcceptanceCriteria>(json, DocumentJson.Options)!;
        back.Should().BeEquivalentTo(doc);
        using var parsed = JsonDocument.Parse(json);
        Type.Validate(parsed.RootElement).IsValid.Should().BeTrue("the re-serialized shape must still validate");
    }
}

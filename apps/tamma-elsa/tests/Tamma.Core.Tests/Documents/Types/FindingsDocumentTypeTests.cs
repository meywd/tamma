using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-3 AC3 — domain rules for <see cref="FindingsDocumentType"/>: cited
/// evidence, [0,1] ranges rejected (not clamped), ranking rule, and the inherited
/// empty-findings-list baseline choice.
/// </summary>
[TestFixture]
public class FindingsDocumentTypeTests
{
    private static readonly FindingsDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    [Test]
    public void Valid_report_with_citations_passes()
    {
        var r = Validate(
            """
            {
              "topic": "t", "summary": "s",
              "findings": [ { "title": "F", "summary": "body", "relevance": 0.9, "confidence": 0.8, "citations": ["a.cs"] } ],
              "overallConfidence": 0.85
            }
            """);

        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Finding_without_citations_is_missing_evidence()
    {
        var r = Validate(
            """
            { "summary": "s", "findings": [ { "title": "F", "summary": "body", "relevance": 0.5, "confidence": 0.5, "citations": [] } ] }
            """);

        Codes(r).Should().Contain(FindingsDocumentType.MissingEvidence);
    }

    [Test]
    public void Out_of_range_relevance_and_confidence_are_rejected_not_clamped()
    {
        var r = Validate(
            """
            { "summary": "s", "findings": [ { "title": "F", "summary": "b", "relevance": 1.5, "confidence": -0.2, "citations": ["a.cs"] } ] }
            """);

        Codes(r).Should().Contain(FindingsDocumentType.RelevanceOutOfRange);
        Codes(r).Should().Contain(FindingsDocumentType.ConfidenceOutOfRange);
    }

    [Test]
    public void Out_of_range_overall_confidence_is_rejected()
    {
        var r = Validate(
            """
            { "summary": "s", "overallConfidence": 1.2,
              "findings": [ { "title": "F", "summary": "b", "relevance": 0.5, "confidence": 0.5, "citations": ["a.cs"] } ] }
            """);

        Codes(r).Should().Contain(FindingsDocumentType.ConfidenceOutOfRange);
    }

    [Test]
    public void Duplicate_explicit_ranks_are_reported()
    {
        var r = Validate(
            """
            { "summary": "s", "findings": [
              { "title": "A", "summary": "a", "relevance": 0.5, "confidence": 0.5, "citations": ["a.cs"], "rank": 1 },
              { "title": "B", "summary": "b", "relevance": 0.5, "confidence": 0.5, "citations": ["b.cs"], "rank": 1 }
            ] }
            """);

        Codes(r).Should().Contain(FindingsDocumentType.DuplicateRank);
    }

    [Test]
    public void Some_but_not_all_ranks_is_partial_ranks()
    {
        var r = Validate(
            """
            { "summary": "s", "findings": [
              { "title": "A", "summary": "a", "relevance": 0.5, "confidence": 0.5, "citations": ["a.cs"], "rank": 1 },
              { "title": "B", "summary": "b", "relevance": 0.5, "confidence": 0.5, "citations": ["b.cs"] }
            ] }
            """);

        Codes(r).Should().Contain(FindingsDocumentType.PartialRanks);
    }

    [Test]
    public void No_ranks_at_all_is_valid_order_is_rank()
    {
        var r = Validate(
            """
            { "summary": "s", "findings": [
              { "title": "A", "summary": "a", "relevance": 0.9, "confidence": 0.5, "citations": ["a.cs"] },
              { "title": "B", "summary": "b", "relevance": 0.5, "confidence": 0.5, "citations": ["b.cs"] }
            ] }
            """);

        Codes(r).Should().NotContain(FindingsDocumentType.PartialRanks);
        Codes(r).Should().NotContain(FindingsDocumentType.DuplicateRank);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Empty_findings_list_is_the_inherited_baseline_violation()
    {
        var r = Validate("""{ "summary": "s", "findings": [] }""");
        Codes(r).Should().Contain(FindingsDocumentType.EmptyFindings);
    }

    [Test]
    public void Missing_summary_is_reported()
    {
        var r = Validate(
            """{ "findings": [ { "title": "F", "summary": "b", "relevance": 0.5, "confidence": 0.5, "citations": ["a.cs"] } ] }""");

        Codes(r).Should().Contain(FindingsDocumentType.MissingSummary);
    }

    [Test]
    public void Shell_finding_is_reported()
    {
        var r = Validate(
            """{ "summary": "s", "findings": [ { "title": "", "summary": "", "relevance": 0.5, "confidence": 0.5, "citations": ["a.cs"] } ] }""");

        Codes(r).Should().Contain(FindingsDocumentType.FindingEmptyShell);
    }
}

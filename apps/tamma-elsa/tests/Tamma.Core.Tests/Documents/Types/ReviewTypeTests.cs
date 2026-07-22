using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC2/AC3 — the unified <see cref="ReviewDocumentType"/>: closed decision
/// and severity enums, the subject union (D3), and the FLAGSHIP
/// blocking-issue⇒not-approvable rule (AC3). Pure (no baseline parsers) per D8.
/// </summary>
[TestFixture]
public class ReviewTypeTests
{
    private static readonly ReviewDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    // ── enum pins ─────────────────────────────────────────────────────────────

    [Test]
    public void ReviewDecision_has_exactly_three_members_with_pinned_wires()
    {
        Enum.GetValues<ReviewDecision>().Should().HaveCount(3);
        EnumWire<ReviewDecision>.ToWire(ReviewDecision.Approve).Should().Be("approve");
        EnumWire<ReviewDecision>.ToWire(ReviewDecision.RequestChanges).Should().Be("request-changes");
        EnumWire<ReviewDecision>.ToWire(ReviewDecision.NeedsDiscussion).Should().Be("needs-discussion");
    }

    [Test]
    public void ReviewSeverity_has_exactly_four_members_with_pinned_wires()
    {
        Enum.GetValues<ReviewSeverity>().Should().HaveCount(4);
        EnumWire<ReviewSeverity>.ToWire(ReviewSeverity.Critical).Should().Be("critical");
        EnumWire<ReviewSeverity>.ToWire(ReviewSeverity.Major).Should().Be("major");
        EnumWire<ReviewSeverity>.ToWire(ReviewSeverity.Minor).Should().Be("minor");
        EnumWire<ReviewSeverity>.ToWire(ReviewSeverity.Suggestion).Should().Be("suggestion");
    }

    [Test]
    public void IsBlocking_is_true_only_for_critical()
    {
        ReviewSeverity.Critical.IsBlocking().Should().BeTrue();
        ReviewSeverity.Major.IsBlocking().Should().BeFalse();
        ReviewSeverity.Minor.IsBlocking().Should().BeFalse();
        ReviewSeverity.Suggestion.IsBlocking().Should().BeFalse();
    }

    // ── subject union (D3) ──────────────────────────────────────────────────────

    [Test]
    public void Document_subject_missing_document_type_is_incomplete()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "document", "documentId": "0192a8b0-1111-7abc-8def-000000000001" },
              "decision": "request-changes", "summary": "s", "issues": []
            }
            """);
        Codes(r).Should().Contain(ReviewDocumentType.SubjectIncomplete);
    }

    [Test]
    public void Document_subject_with_non_vocabulary_type_is_incomplete()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "document", "documentId": "0192a8b0-1111-7abc-8def-000000000001", "documentType": "not-a-type" },
              "decision": "request-changes", "summary": "s", "issues": []
            }
            """);
        Codes(r).Should().Contain(ReviewDocumentType.SubjectIncomplete);
    }

    [Test]
    public void Diff_subject_with_repo_and_pr_is_valid()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 7 },
              "decision": "request-changes", "summary": "s", "issues": []
            }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Diff_subject_without_pr_or_commit_is_incomplete()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma" },
              "decision": "request-changes", "summary": "s", "issues": []
            }
            """);
        Codes(r).Should().Contain(ReviewDocumentType.SubjectIncomplete);
    }

    [Test]
    public void Unknown_subject_kind_is_reported()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "branch", "repository": "meywd/tamma" },
              "decision": "request-changes", "summary": "s", "issues": []
            }
            """);
        Codes(r).Should().Contain(ReviewDocumentType.SubjectUnknownKind);
    }

    // ── AC3 flagship ────────────────────────────────────────────────────────────

    [Test]
    public void Approve_with_a_blocking_issue_is_rejected_and_names_the_issue()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "approve",
              "summary": "approving anyway",
              "issues": [ { "severity": "critical", "category": "security", "description": "SQL injection here", "suggestedFix": "parameterize" } ]
            }
            """);

        r.IsValid.Should().BeFalse();
        r.Violations.Should().Contain(v =>
            v.Code == ReviewDocumentType.ApproveWithBlockingIssues && v.Message.Contains("SQL injection here"));
    }

    [Test]
    public void Approve_with_only_non_blocking_issues_is_valid()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "approve",
              "summary": "fine with nits",
              "issues": [
                { "severity": "major", "category": "style", "description": "naming", "suggestedFix": "rename" },
                { "severity": "suggestion", "category": "style", "description": "prefer var", "suggestedFix": "use var" }
              ]
            }
            """);

        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void RequestChanges_with_a_blocking_issue_is_valid()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "request-changes",
              "summary": "must fix",
              "issues": [ { "severity": "critical", "category": "correctness", "description": "off-by-one", "suggestedFix": "fix the bound" } ]
            }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Issue_missing_category_and_fix_are_reported_per_issue()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "request-changes",
              "summary": "s",
              "issues": [ { "severity": "minor", "category": "", "description": "d", "suggestedFix": "" } ]
            }
            """);

        Codes(r).Should().Contain(ReviewDocumentType.IssueMissingCategory);
        Codes(r).Should().Contain(ReviewDocumentType.IssueMissingFix);
    }

    [Test]
    public void Missing_summary_is_reported()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "request-changes", "summary": "  ", "issues": []
            }
            """);
        Codes(r).Should().Contain(ReviewDocumentType.SummaryRequired);
    }

    [Test]
    public void Aggregated_from_with_duplicates_is_reported()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "request-changes", "summary": "s", "issues": [],
              "aggregatedFrom": ["0192a8b0-1111-7abc-8def-000000000001", "0192a8b0-1111-7abc-8def-000000000001"]
            }
            """);
        Codes(r).Should().Contain(ReviewDocumentType.AggregatedFromInvalid);
    }

    [Test]
    public void Aggregated_from_single_id_is_valid()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "request-changes", "summary": "s", "issues": [],
              "aggregatedFrom": ["0192a8b0-1111-7abc-8def-000000000001"]
            }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Bad_decision_wire_is_malformed_not_a_throw()
    {
        var r = Validate(
            """
            {
              "subject": { "kind": "diff", "repository": "meywd/tamma", "prNumber": 1 },
              "decision": "APPROVE", "summary": "s", "issues": []
            }
            """);
        // Canonical wire is lowercase-kebab; "APPROVE" is a LEGACY spelling, not a
        // canonical payload wire → deserialization fails → single MALFORMED_PAYLOAD.
        Codes(r).Should().Equal(new[] { ReviewDocumentType.MalformedPayload });
    }

    [Test]
    public void Contract_carries_every_unified_token_and_is_deterministic()
    {
        var contract = Type.RenderContract();
        foreach (var token in new[] { "\"subject\"", "\"decision\"", "\"summary\"", "\"issues\"", "\"severity\"", "\"category\"", "\"suggestedFix\"" })
            contract.Should().Contain(token);
        Type.RenderContract().Should().Be(contract);
    }
}

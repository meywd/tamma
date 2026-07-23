using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC2/AC8 — the legacy verdict ingest + decision-mapping table (D1/D4),
/// pure (no baseline parser; the legacy-verdict fold check lives in the
/// Activities.Tests cross-parser suite per D8 — its old-parser baseline was retired
/// in Story 39-14). Parse failure yields a TYPED error and NO document —
/// never a defaulted "concerns".
/// </summary>
[TestFixture]
public class ReviewLegacyCompatTests
{
    private static readonly ReviewSubject Subject = new() { Kind = "document", DocumentId = Guid.NewGuid(), DocumentType = "plan" };

    // ── D1 decision-mapping table ───────────────────────────────────────────────

    [TestCase("approve", ReviewDecision.Approve)]
    [TestCase("APPROVE", ReviewDecision.Approve)]
    [TestCase("REQUEST_CHANGES", ReviewDecision.RequestChanges)]
    [TestCase("NEEDS_DISCUSSION", ReviewDecision.NeedsDiscussion)]
    [TestCase("concerns", ReviewDecision.RequestChanges)]
    [TestCase("COMMENT", ReviewDecision.NeedsDiscussion)]
    public void String_verdict_maps_to_the_pinned_decision(string verdict, ReviewDecision expected)
    {
        var review = Review.FromLegacyVerdictJson($$"""{ "verdict": "{{verdict}}" }""", Subject);
        review.Decision.Should().Be(expected);
    }

    [Test]
    public void String_verdict_carries_comments_into_summary()
    {
        var review = Review.FromLegacyVerdictJson(
            """{ "verdict": "approve", "comments": "looks good to me" }""", Subject);
        review.Decision.Should().Be(ReviewDecision.Approve);
        review.Summary.Should().Be("looks good to me");
        review.Issues.Should().BeEmpty();
    }

    // ── object verdict ingest ───────────────────────────────────────────────────

    [Test]
    public void Object_verdict_maps_decision_summary_and_blocking_issues()
    {
        var review = Review.FromLegacyVerdictJson(
            """
            {
              "verdict": {
                "decision": "REQUEST_CHANGES",
                "summary": "Two blockers remain.",
                "blockingIssues": ["missing migration order", "no rollback plan"]
              }
            }
            """, Subject);

        review.Decision.Should().Be(ReviewDecision.RequestChanges);
        review.Summary.Should().Be("Two blockers remain.");
        review.Issues.Should().HaveCount(2);
        review.Issues.Should().OnlyContain(i => i.Severity == ReviewSeverity.Critical && i.Category == "blocking");
        review.Issues.Select(i => i.Description).Should().Contain("missing migration order");
    }

    [Test]
    public void Object_verdict_approve_maps_to_approve()
    {
        var review = Review.FromLegacyVerdictJson(
            """{ "verdict": { "decision": "APPROVE", "summary": "ok" } }""", Subject);
        review.Decision.Should().Be(ReviewDecision.Approve);
    }

    [Test]
    public void Legacy_ingested_blocking_issue_without_fix_fails_validate_deliberately()
    {
        // D4 — incomplete legacy content goes to repair, it is not laundered: the
        // blockingIssues[] ingest produces Critical issues with an empty suggestedFix,
        // which the validator rejects (ISSUE_MISSING_FIX).
        var review = Review.FromLegacyVerdictJson(
            """{ "verdict": { "decision": "REQUEST_CHANGES", "summary": "s", "blockingIssues": ["x"] } }""", Subject);

        var json = System.Text.Json.JsonSerializer.Serialize(review, Tamma.Core.Documents.DocumentJson.Options);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new ReviewDocumentType().Validate(doc.RootElement);
        result.Violations.Select(v => v.Code).Should().Contain(ReviewDocumentType.IssueMissingFix);
    }

    // ── parse failure → typed error, NO document ────────────────────────────────

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{}")]
    [TestCase("not json")]
    [TestCase("{ \"comments\": \"no verdict here\" }")]
    public void Parse_failure_throws_legacy_unparseable_never_a_default(string json)
    {
        var act = () => Review.FromLegacyVerdictJson(json, Subject);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.REVIEW.LEGACY_UNPARSEABLE");
    }

    [Test]
    public void Unknown_decision_spelling_throws_unknown_decision()
    {
        var act = () => Review.FromLegacyVerdictJson("""{ "verdict": "maybe-later" }""", Subject);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.REVIEW.UNKNOWN_DECISION");
    }

    [Test]
    public void Severity_parse_legacy_folds_style_info_and_blocker()
    {
        ReviewSeverityExtensions.ParseLegacy("style").Should().Be(ReviewSeverity.Suggestion);
        ReviewSeverityExtensions.ParseLegacy("info").Should().Be(ReviewSeverity.Suggestion);
        ReviewSeverityExtensions.ParseLegacy("blocker").Should().Be(ReviewSeverity.Critical);
        var act = () => ReviewSeverityExtensions.ParseLegacy("nit");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.REVIEW.UNKNOWN_SEVERITY");
    }
}

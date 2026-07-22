using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 — unit pins for <see cref="ReviewProducerHelper"/> (Design Decisions
/// D4/D5; covers AC1's mapping / no-laundering half).
/// </summary>
[TestFixture]
public class ReviewProducerHelperTests
{
    private static ReviewSubject DocSubject() => new()
    {
        Kind = "document",
        DocumentId = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001"),
        DocumentType = "plan",
    };

    [Test]
    public void MapReviewerReply_CanonicalReview_MapsAsIs()
    {
        var json =
            "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"plan\"}," +
            "\"decision\":\"request-changes\",\"summary\":\"needs work\",\"issues\":[" +
            "{\"severity\":\"major\",\"category\":\"design\",\"description\":\"no rollback\",\"suggestedFix\":\"add rollback\"}]}";

        var result = ReviewProducerHelper.MapReviewerReply(json, DocSubject());

        result.IsValid.Should().BeTrue();
        result.Payload!.Decision.Should().Be(ReviewDecision.RequestChanges);
        result.Payload!.Issues.Should().ContainSingle();
    }

    [Test]
    public void MapReviewerReply_LegacyCellShape_MapsSeverityCategoryFixIntact_AndValidates()
    {
        var json =
            "{\"issues\":[{\"task\":\"T1\",\"severity\":\"major\",\"category\":\"design\",\"issue\":\"No rollback\",\"recommendation\":\"add rollback\"}]," +
            "\"verdict\":{\"decision\":\"REQUEST_CHANGES\",\"summary\":\"needs work\",\"blockingIssues\":[]}}";

        var result = ReviewProducerHelper.MapReviewerReply(json, DocSubject());

        result.IsValid.Should().BeTrue();
        result.Payload!.Decision.Should().Be(ReviewDecision.RequestChanges);
        result.Payload!.Summary.Should().Be("needs work");
        var issue = result.Payload!.Issues.Single();
        issue.Severity.Should().Be(ReviewSeverity.Major);
        issue.Category.Should().Be("design");
        issue.SuggestedFix.Should().Be("add rollback");
        issue.Description.Should().Contain("No rollback");
    }

    [Test]
    public void MapReviewerReply_ObjectVerdictBlockingIssues_BecomeCriticalIssues()
    {
        // A blocking issue string becomes a Critical issue with no suggested fix —
        // observable because the validator then flags it as ISSUE_MISSING_FIX (proving
        // the blocking issue was materialised, not laundered away).
        var json = "{\"verdict\":{\"decision\":\"REQUEST_CHANGES\",\"summary\":\"blocked\",\"blockingIssues\":[\"SQL injection\"]}}";

        var result = ReviewProducerHelper.MapReviewerReply(json, DocSubject());

        result.Payload.Should().BeNull("a fix-less blocking issue is routed to repair, not laundered");
        result.Violations.Should().Contain(v => v.Code == ReviewDocumentType.IssueMissingFix);
    }

    [Test]
    public void MapReviewerReply_ApproveWithBlockingIssue_FailsFlagshipRule()
    {
        var json =
            "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"plan\"}," +
            "\"decision\":\"approve\",\"summary\":\"lgtm\",\"issues\":[" +
            "{\"severity\":\"critical\",\"category\":\"security\",\"description\":\"sqli\",\"suggestedFix\":\"parameterize\"}]}";

        var result = ReviewProducerHelper.MapReviewerReply(json, DocSubject());

        result.Payload.Should().BeNull();
        result.Violations.Should().Contain(v => v.Code == ReviewDocumentType.ApproveWithBlockingIssues);
    }

    [TestCase("not json at all")]
    [TestCase("")]
    [TestCase("{}")]
    [TestCase("   ")]
    public void MapReviewerReply_Garbage_YieldsViolationsNeverADefaultedReview(string reply)
    {
        var result = ReviewProducerHelper.MapReviewerReply(reply, DocSubject());

        result.Payload.Should().BeNull("garbage must never become a defaulted 'concerns' review");
        result.Violations.Should().NotBeEmpty();
    }

    [Test]
    public void BuildRepairVariables_WithoutViolations_IsBytePassthroughOfFeedbackVar()
    {
        var vars = "{\"workItemJson\":\"original\",\"planJson\":\"P\"}";
        var outJson = ReviewProducerHelper.BuildRepairVariables(
            vars, Array.Empty<DocumentViolation>(), "workItemJson", "CONTRACT");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(outJson)!;
        parsed["workItemJson"].Should().Be("original", "no violations → the feedback var is unchanged");
        parsed["planJson"].Should().Be("P");
    }

    [Test]
    public void BuildRepairVariables_WithViolations_AppendsOnlyIntoNamedVariable()
    {
        var vars = "{\"workItemJson\":\"original\",\"planJson\":\"P\"}";
        var violations = new[] { new DocumentViolation("X", "the summary is required") };

        var outJson = ReviewProducerHelper.BuildRepairVariables(vars, violations, "workItemJson", "CONTRACT");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(outJson)!;
        parsed["planJson"].Should().Be("P", "only the named feedback variable is touched");
        parsed["workItemJson"].Should().StartWith("original");
        parsed["workItemJson"].Should().Contain(ValidationFeedbackHelper.FeedbackHeader);
        parsed["workItemJson"].Should().Contain("the summary is required");
    }

    [Test]
    public void ShouldRepair_RespectsMaxAttempts()
    {
        var rules = AcceptanceDefaults.Rules with { MaxValidationRepairAttempts = 2 };
        ReviewProducerHelper.ShouldRepair(0, rules).Should().BeTrue();
        ReviewProducerHelper.ShouldRepair(1, rules).Should().BeTrue();
        ReviewProducerHelper.ShouldRepair(2, rules).Should().BeFalse();
    }
}

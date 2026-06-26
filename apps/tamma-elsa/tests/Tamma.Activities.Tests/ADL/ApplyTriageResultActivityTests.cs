using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriageItemCycle.md</c> #8) — the headline
/// regression: <see cref="ApplyTriageResultActivity"/> must <b>fail loud</b>. Its old
/// <c>RunAsync</c> swallowed every engine-callback failure (try/catch + return) and
/// never checked <c>response.IsSuccessStatusCode</c>, so a 4xx/5xx still emitted
/// <c>TRIAGE.APPLY.RESULT.COMPLETED</c> (a false success). The build-out checks the
/// status of each POST and THROWS on the first non-success (or a null item/decision),
/// so the activity base emits <c>TRIAGE.APPLY.RESULT.FAILED</c> and the cycle's
/// fail-the-item edge fires.
///
/// <para>Also covers #7 — the validated-label / rendered-comment overrides take
/// precedence over the decision JSON's own values.</para>
///
/// Tests the testable core (<see cref="ApplyTriageResultActivity.ApplyCoreAsync"/>) via
/// the <see cref="ITriageApplyClient"/> seam — the codebase pattern (no live HTTP).
/// </summary>
[TestFixture]
public class ApplyTriageResultActivityTests
{
    private const string IssueItem = """{"type":"issue","number":5,"title":"t","body":"b"}""";
    private const string AlertItem = """{"type":"security","number":0,"title":"CVE","body":"b"}""";
    private const string OkDecision = """{"status":"ok","labels":["bug"],"comment":"c"}""";

    private static Mock<ITriageApplyClient> AllOk()
    {
        var c = new Mock<ITriageApplyClient>();
        c.Setup(x => x.SetLabelsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageApplyResult.Ok());
        c.Setup(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageApplyResult.Ok());
        c.Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageApplyResult.Ok());
        return c;
    }

    [Test]
    public void JsonConstructor_DoesNotThrow()
    {
        FluentActions.Invoking(() => new ApplyTriageResultActivity()).Should().NotThrow();
    }

    // ================================================================
    // Happy path
    // ================================================================

    [Test]
    public async Task ApplyCore_Issue_AppliesLabelsThenComment()
    {
        var c = AllOk();

        await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", IssueItem, OkDecision, labelsOverride: null, commentOverride: null);

        c.Verify(x => x.SetLabelsAsync("o/r", 5, It.Is<IReadOnlyList<string>>(l => l.Contains("bug")), It.IsAny<CancellationToken>()), Times.Once);
        c.Verify(x => x.PostCommentAsync("o/r", 5, "c", It.IsAny<CancellationToken>()), Times.Once);
        c.Verify(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ApplyCore_Alert_CreatesIssue()
    {
        var c = AllOk();

        await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", AlertItem, OkDecision, labelsOverride: null, commentOverride: null);

        c.Verify(x => x.CreateIssueAsync("o/r", "CVE", It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ================================================================
    // #8 — fail loud (no silent swallow, no false success)
    // ================================================================

    [Test]
    public async Task ApplyCore_LabelsReturn403_Throws_NotSilentSuccess()
    {
        var c = AllOk();
        c.Setup(x => x.SetLabelsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageApplyResult.Fail(403));

        var act = async () => await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", IssueItem, OkDecision, null, null);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("issue-labels").And.Contain("403");
        // The comment must NOT be posted after a label failure (we threw first).
        c.Verify(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ApplyCore_CommentReturns500_Throws()
    {
        var c = AllOk();
        c.Setup(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageApplyResult.Fail(500));

        var act = async () => await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", IssueItem, OkDecision, null, null);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("issue-comment").And.Contain("500");
    }

    [Test]
    public async Task ApplyCore_CreateIssueReturns422_Throws()
    {
        var c = AllOk();
        c.Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriageApplyResult.Fail(422));

        var act = async () => await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", AlertItem, OkDecision, null, null);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task ApplyCore_ClientThrows_Propagates_NotSwallowed()
    {
        var c = AllOk();
        c.Setup(x => x.SetLabelsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        var act = async () => await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", IssueItem, OkDecision, null, null);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task ApplyCore_NullDecisionJson_Throws_NeverFalseSuccess()
    {
        var c = AllOk();
        var act = async () => await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", IssueItem, decisionJson: "not json", labelsOverride: null, commentOverride: null);

        await act.Should().ThrowAsync<Exception>(
            "a non-deserializable decision must not silently succeed");
    }

    // ================================================================
    // #7 — overrides take precedence
    // ================================================================

    [Test]
    public async Task ApplyCore_LabelsAndCommentOverride_TakePrecedenceOverDecisionJson()
    {
        var c = AllOk();

        await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", IssueItem, OkDecision,
            labelsOverride: new[] { "priority-high", "feature" },
            commentOverride: "## Triage Decision (rendered)");

        c.Verify(x => x.SetLabelsAsync("o/r", 5,
            It.Is<IReadOnlyList<string>>(l => l.Contains("priority-high") && l.Contains("feature") && !l.Contains("bug")),
            It.IsAny<CancellationToken>()), Times.Once);
        c.Verify(x => x.PostCommentAsync("o/r", 5, "## Triage Decision (rendered)", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ApplyCore_EmptyLabelOverride_FallsBackToDecisionLabels()
    {
        var c = AllOk();

        await ApplyTriageResultActivity.ApplyCoreAsync(
            c.Object, "o/r", IssueItem, OkDecision,
            labelsOverride: Array.Empty<string>(), commentOverride: null);

        c.Verify(x => x.SetLabelsAsync("o/r", 5,
            It.Is<IReadOnlyList<string>>(l => l.Contains("bug")), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ================================================================
    // EnsureSuccess — direct
    // ================================================================

    [Test]
    public void EnsureSuccess_OkResult_DoesNotThrow()
    {
        FluentActions.Invoking(() =>
            ApplyTriageResultActivity.EnsureSuccess(TriageApplyResult.Ok(), "issue-labels", 1))
            .Should().NotThrow();
    }

    [Test]
    public void EnsureSuccess_FailResult_ThrowsWithStatusAndEndpoint()
    {
        FluentActions.Invoking(() =>
            ApplyTriageResultActivity.EnsureSuccess(TriageApplyResult.Fail(404), "create-issue", 9))
            .Should().Throw<HttpRequestException>()
            .Which.Message.Should().Contain("create-issue").And.Contain("404");
    }
}

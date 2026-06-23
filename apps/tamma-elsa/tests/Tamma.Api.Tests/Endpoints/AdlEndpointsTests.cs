using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services;

namespace Tamma.Api.Tests.Endpoints;

/// <summary>
/// IMPORTANT-2 — the RBAC-gated merge-approval resume surface
/// (<c>POST /api/adl/merge-approval/resume</c>). Verifies the handler forwards a
/// well-formed decision to <see cref="IElsaWorkflowService.ResumeMergeApprovalAsync"/>,
/// validates input, and maps a "no gate waiting" engine response to 404.
/// </summary>
[TestFixture]
public class AdlEndpointsTests
{
    private Mock<IElsaWorkflowService> _elsa = null!;
    private readonly NullLoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    [SetUp]
    public void SetUp()
    {
        _elsa = new Mock<IElsaWorkflowService>();
    }

    [Test]
    public async Task ResumeMergeApproval_ValidMerge_ForwardsAndInjectsPayload_Returns200()
    {
        _elsa
            .Setup(s => s.ResumeMergeApprovalAsync(42, 7, "merge", "lgtm", "alice"))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: true, GateNotFound: false, WorkflowInstanceId: "wf-1"));

        var req = new AdlEndpoints.MergeApprovalDecisionRequest(42, 7, "merge", "lgtm", "alice");

        var result = await AdlEndpoints.ResumeMergeApproval(req, _elsa.Object, _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        // The decision + approver + feedback must be forwarded verbatim so the gate
        // can inject them as workflow input on resume.
        _elsa.Verify(s => s.ResumeMergeApprovalAsync(42, 7, "merge", "lgtm", "alice"), Times.Once);
    }

    [Test]
    public async Task ResumeMergeApproval_GateNotWaiting_Returns404()
    {
        _elsa
            .Setup(s => s.ResumeMergeApprovalAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.MergeApprovalDecisionRequest(1, 2, "reject", null, null);

        var result = await AdlEndpoints.ResumeMergeApproval(req, _elsa.Object, _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound,
            "a resume for a gate that is not suspended must 404, not 500");
    }

    [Test]
    public async Task ResumeMergeApproval_EmptyDecision_Returns400_NoForward()
    {
        var req = new AdlEndpoints.MergeApprovalDecisionRequest(1, 2, "   ", null, null);

        var result = await AdlEndpoints.ResumeMergeApproval(req, _elsa.Object, _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeMergeApprovalAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never, "an empty decision must be rejected up front, not escalated silently");
    }

    [Test]
    public async Task ResumeMergeApproval_NonPositiveIssueOrPr_Returns400()
    {
        var req = new AdlEndpoints.MergeApprovalDecisionRequest(0, 0, "merge", null, null);

        var result = await AdlEndpoints.ResumeMergeApproval(req, _elsa.Object, _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeMergeApprovalAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    /// <summary>Read the HTTP status code off any minimal-API result (typed
    /// results implement IStatusCodeHttpResult).</summary>
    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;
}

using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Entities;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Endpoints;

/// <summary>
/// The engine-side production-deploy approval resume seam
/// (<c>POST /elsa/api/adl/deploy-approval/resume</c>, completeness audit P0
/// item 3). Mirrors <see cref="MergeApprovalResumeEndpointTests"/>: it
///   - computes the SAME tenant+repo+SHA-scoped bookmark name as the gate activity;
///   - resolves only the matching (single) instance and resumes it;
///   - REFUSES with 409 on a multi-match (globally-unique name) rather than
///     resuming an arbitrary one;
///   - 404s when no bookmark matches (a cross-tenant/cross-SHA resume hits this).
/// </summary>
[TestFixture]
public class DeploymentApprovalResumeEndpointTests
{
    private Mock<IBookmarkStore> _bookmarks = null!;
    private Mock<IWorkflowRuntime> _runtime = null!;
    private Mock<IWorkflowClient> _client = null!;

    [SetUp]
    public void SetUp()
    {
        _bookmarks = new Mock<IBookmarkStore>();
        _runtime = new Mock<IWorkflowRuntime>();
        _client = new Mock<IWorkflowClient>();
        _runtime
            .Setup(r => r.CreateClientAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _client
            .Setup(c => c.RunInstanceAsync(It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunWorkflowInstanceResponse());
    }

    private static DeploymentApprovalResumeEndpoint.ResumeRequest Req(
        int issue, string decision, string? tenantId, string? repo, string? sha)
        => new(issue, decision, Feedback: null, Approver: null, TenantId: tenantId, Repository: repo, MergeSha: sha);

    private static StoredBookmark Bookmark(string name, string instanceId)
        => new() { Name = name, WorkflowInstanceId = instanceId, Id = Guid.NewGuid().ToString() };

    private void SetupFind(string expectedName, params StoredBookmark[] matches) =>
        _bookmarks
            .Setup(b => b.FindManyAsync(
                It.Is<BookmarkFilter>(f => f.Name == expectedName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.AsEnumerable());

    [Test]
    public async Task Resume_UsesTenantRepoShaScopedBookmarkName_AndResumesMatch()
    {
        var tenant = Guid.NewGuid().ToString();
        var expected = WaitForDeploymentApprovalActivity.BookmarkName(tenant, "octo/repo", 42, "deadbeef");
        SetupFind(expected, Bookmark(expected, "wf-instance-1"));

        var result = await DeploymentApprovalResumeEndpoint.Resume(
            Req(42, "approve", tenant, "octo/repo", "deadbeef"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r => (string)r.Input!["decision"] == "approve"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_CrossTenant_NoMatchingBookmark_Returns404_NeverResumes()
    {
        var callerTenant = Guid.NewGuid().ToString();
        var callerName = WaitForDeploymentApprovalActivity.BookmarkName(callerTenant, "victim/repo", 5, "sha");
        SetupFind(callerName /* zero matches */);

        var result = await DeploymentApprovalResumeEndpoint.Resume(
            Req(5, "approve", callerTenant, "victim/repo", "sha"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a cross-tenant miss must never resume any instance");
    }

    [Test]
    public async Task Resume_MoreThanOneMatch_Refuses409_DoesNotResumeArbitrary()
    {
        var tenant = Guid.NewGuid().ToString();
        var name = WaitForDeploymentApprovalActivity.BookmarkName(tenant, "octo/repo", 1, "sha");
        SetupFind(name, Bookmark(name, "wf-a"), Bookmark(name, "wf-b"));

        var result = await DeploymentApprovalResumeEndpoint.Resume(
            Req(1, "approve", tenant, "octo/repo", "sha"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "an ambiguous bookmark must refuse, not resume an arbitrary instance");
    }

    [Test]
    public async Task Resume_EmptyDecision_Returns400()
    {
        var result = await DeploymentApprovalResumeEndpoint.Resume(
            Req(1, "  ", Guid.NewGuid().ToString(), "octo/repo", "sha"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Resume_RejectDecision_ResumesWithRejectPayload()
    {
        var tenant = Guid.NewGuid().ToString();
        var expected = WaitForDeploymentApprovalActivity.BookmarkName(tenant, "octo/repo", 9, "sha");
        SetupFind(expected, Bookmark(expected, "wf-reject"));

        var result = await DeploymentApprovalResumeEndpoint.Resume(
            Req(9, "reject", tenant, "octo/repo", "sha"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r => (string)r.Input!["decision"] == "reject"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;
}

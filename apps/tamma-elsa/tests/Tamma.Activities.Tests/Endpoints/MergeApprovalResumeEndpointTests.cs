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
/// SECURITY C1/C2 — the engine-side merge-approval resume seam
/// (<c>POST /elsa/api/adl/merge-approval/resume</c>). Verifies it:
///   - computes the SAME tenant+repo-scoped bookmark name as the gate activity;
///   - resolves only the matching (single) instance and resumes it;
///   - REFUSES with 409 when more than one instance matches a (globally-unique)
///     name rather than resuming an arbitrary <c>bookmarks[0]</c> (C2);
///   - 404s when no bookmark matches (a cross-tenant/cross-repo resume hits this
///     because the name carries the caller's tenant+repo, C1).
/// </summary>
[TestFixture]
public class MergeApprovalResumeEndpointTests
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

    private static MergeApprovalResumeEndpoint.ResumeRequest Req(
        int issue, int pr, string decision, string? tenantId, string? repo)
        => new(issue, pr, decision, Feedback: null, Approver: null, TenantId: tenantId, Repository: repo);

    private static StoredBookmark Bookmark(string name, string instanceId)
        => new() { Name = name, WorkflowInstanceId = instanceId, Id = Guid.NewGuid().ToString() };

    private void SetupFind(string expectedName, params StoredBookmark[] matches) =>
        _bookmarks
            .Setup(b => b.FindManyAsync(
                It.Is<BookmarkFilter>(f => f.Name == expectedName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.AsEnumerable());

    [Test]
    public async Task Resume_UsesTenantRepoScopedBookmarkName_AndResumesMatch()
    {
        var tenant = Guid.NewGuid().ToString();
        var expected = WaitForMergeApprovalActivity.BookmarkName(tenant, "octo/repo", 42, 7);
        SetupFind(expected, Bookmark(expected, "wf-instance-1"));

        var result = await MergeApprovalResumeEndpoint.Resume(
            Req(42, 7, "merge", tenant, "octo/repo"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r => (string)r.Input!["decision"] == "merge"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_CrossTenant_NoMatchingBookmark_Returns404_NeverResumes()
    {
        // C1 — tenant B's gate is stored under tenant B's name. A caller scoped to
        // tenant A computes tenant A's name → no match → 404, and crucially never
        // calls RunInstanceAsync against the victim instance.
        var callerTenant = Guid.NewGuid().ToString();
        var callerName = WaitForMergeApprovalActivity.BookmarkName(callerTenant, "victim/repo", 5, 5);
        SetupFind(callerName /* zero matches */);

        var result = await MergeApprovalResumeEndpoint.Resume(
            Req(5, 5, "merge", callerTenant, "victim/repo"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a cross-tenant miss must never resume any instance");
    }

    [Test]
    public async Task Resume_MoreThanOneMatch_Refuses409_DoesNotResumeArbitrary()
    {
        // C2 — the name is globally unique; >1 match is an integrity violation.
        // The old code resumed bookmarks[0] (arbitrary). Now we refuse.
        var tenant = Guid.NewGuid().ToString();
        var name = WaitForMergeApprovalActivity.BookmarkName(tenant, "octo/repo", 1, 1);
        SetupFind(name, Bookmark(name, "wf-a"), Bookmark(name, "wf-b"));

        var result = await MergeApprovalResumeEndpoint.Resume(
            Req(1, 1, "merge", tenant, "octo/repo"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "an ambiguous bookmark must refuse, not resume an arbitrary instance");
    }

    [Test]
    public async Task Resume_EmptyDecision_Returns400()
    {
        var result = await MergeApprovalResumeEndpoint.Resume(
            Req(1, 1, "  ", Guid.NewGuid().ToString(), "octo/repo"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Resume_TwoTenantsSameIssuePr_TargetOnlyTheirOwnGate()
    {
        // C2 end-to-end: two tenants each waiting on issue #5 / PR #5 have DISTINCT
        // bookmark names, so a resume for tenant A resolves ONLY tenant A's
        // instance even though tenant B has the same issue/PR.
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();
        var nameA = WaitForMergeApprovalActivity.BookmarkName(tenantA, "octo/repo", 5, 5);
        var nameB = WaitForMergeApprovalActivity.BookmarkName(tenantB, "octo/repo", 5, 5);
        nameA.Should().NotBe(nameB, "two tenants on the same issue/PR must not collide");

        SetupFind(nameA, Bookmark(nameA, "wf-tenant-a"));

        var result = await MergeApprovalResumeEndpoint.Resume(
            Req(5, 5, "merge", tenantA, "octo/repo"),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _runtime.Verify(r => r.CreateClientAsync("wf-tenant-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;
}

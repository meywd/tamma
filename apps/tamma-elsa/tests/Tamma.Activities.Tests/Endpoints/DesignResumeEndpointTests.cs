using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Entities;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Endpoints;

/// <summary>
/// Story 3.7 — the engine-side design resume seam (<c>POST /elsa/api/adl/design/resume</c>).
/// Verifies it:
///   - computes the SAME tenant+session-scoped bookmark name as the suspend-side activity
///     (<c>document-decision-{tenant}-{session}</c>) and resumes the matching (single) instance
///     with the translated generic keys (<c>DecisionJson</c> accept/reject + <c>Feedback</c>);
///   - is IDOR-safe: a caller scoped to a DIFFERENT tenant computes a different name and 404s
///     (never resolves the victim's gate);
///   - REFUSES with 409 when more than one instance matches a (unique) name rather than
///     resuming an arbitrary <c>bookmarks[0]</c>;
///   - 404s when no bookmark matches (already decided / advanced / timed out);
///   - injects a reject DecisionJson on a reject decision;
///   - 400s a malformed request (empty session).
/// </summary>
[TestFixture]
public class DesignResumeEndpointTests
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

    private static DesignResumeEndpoint.ResumeRequest Req(
        Guid session, string? tenant, bool approved = true, string? feedback = "looks good") =>
        new(session, tenant, approved, feedback, Reviewer: "who@x.test");

    private static StoredBookmark Bookmark(string name, string instanceId)
        => new() { Name = name, WorkflowInstanceId = instanceId, Id = Guid.NewGuid().ToString() };

    private void SetupFind(string expectedName, params StoredBookmark[] matches) =>
        _bookmarks
            .Setup(b => b.FindManyAsync(
                It.Is<BookmarkFilter>(f => f.Name == expectedName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.AsEnumerable());

    private Task<IResult> Invoke(DesignResumeEndpoint.ResumeRequest req)
        => DesignResumeEndpoint.Resume(
            req, _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    [Test]
    public async Task Resume_ValidApprove_UsesCanonicalBookmarkName_InjectsApprovedAndFeedback_ResumesMatch()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var expected = LifecycleBookmarks.ForDecisionSession(tenant, session);
        SetupFind(expected, Bookmark(expected, "wf-d1"));

        var result = await Invoke(Req(session, tenant, approved: true, feedback: "ship it"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                ((string)r.Input!["DecisionJson"]).Contains("accept")
                && (string)r.Input!["Feedback"] == "ship it"
                && (string)r.Input!["Channel"] == "user"),
            It.IsAny<CancellationToken>()), Times.Once);
        _runtime.Verify(r => r.CreateClientAsync("wf-d1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_ValidReject_InjectsApprovedFalse()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var expected = LifecycleBookmarks.ForDecisionSession(tenant, session);
        SetupFind(expected, Bookmark(expected, "wf-d2"));

        var result = await Invoke(Req(session, tenant, approved: false, feedback: "revise the data model"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                ((string)r.Input!["DecisionJson"]).Contains("reject")
                && (string)r.Input!["Feedback"] == "revise the data model"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_CrossTenant_DifferentName_Returns404_NeverResumes()
    {
        // IDOR — the gate was armed under tenant A. A caller scoped to tenant B computes a
        // DIFFERENT bookmark name, so the store returns zero matches for B's name; the victim's
        // (A's) gate is never touched. We arm ONLY tenant A's name and resume as tenant B.
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();
        var nameA = LifecycleBookmarks.ForDecisionSession(tenantA, session);
        var nameB = LifecycleBookmarks.ForDecisionSession(tenantB, session);
        SetupFind(nameA, Bookmark(nameA, "wf-victim")); // A's gate is live
        SetupFind(nameB /* zero matches for B */);

        var result = await Invoke(Req(session, tenantB));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound,
            "a cross-tenant caller computes a different bookmark name and must 404, never resolving the victim's gate");
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_NoMatchingBookmark_Returns404_NeverResumes()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var name = LifecycleBookmarks.ForDecisionSession(tenant, session);
        SetupFind(name /* zero matches */);

        var result = await Invoke(Req(session, tenant));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a miss must never resume any instance");
    }

    [Test]
    public async Task Resume_MoreThanOneMatch_Refuses409_DoesNotResumeArbitrary()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var name = LifecycleBookmarks.ForDecisionSession(tenant, session);
        SetupFind(name, Bookmark(name, "wf-a"), Bookmark(name, "wf-b"));

        var result = await Invoke(Req(session, tenant));

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "an ambiguous bookmark must refuse, not resume an arbitrary instance");
    }

    [Test]
    public async Task Resume_EmptySession_Returns400()
    {
        var result = await Invoke(Req(Guid.Empty, Guid.NewGuid().ToString()));
        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }
}

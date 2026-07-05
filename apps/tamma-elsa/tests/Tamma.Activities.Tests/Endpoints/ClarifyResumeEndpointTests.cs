using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Entities;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Clarify;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Endpoints;

/// <summary>
/// Story 3.5 — the engine-side clarify resume seam (<c>POST /elsa/api/adl/clarify/resume</c>).
/// Verifies it:
///   - computes the SAME tenant+session-scoped bookmark name as the suspend-side activity
///     (<c>clarify-answers-{tenant}-{session}</c>) and resumes the matching (single) instance
///     with the EXACT input keys the callback reads (<c>Answered</c> + <c>Answers</c>);
///   - is IDOR-safe: a caller scoped to a DIFFERENT tenant computes a different name and 404s
///     (never resolves the victim's gate);
///   - REFUSES with 409 when more than one instance matches a (unique) name rather than
///     resuming an arbitrary <c>bookmarks[0]</c>;
///   - 404s when no bookmark matches (already answered / advanced / timed out);
///   - 400s a malformed request (empty session, empty answers).
/// </summary>
[TestFixture]
public class ClarifyResumeEndpointTests
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

    private static ClarifyResumeEndpoint.ResumeRequest Req(Guid session, string? tenant, string answers = "the answer") =>
        new(session, tenant, answers, Resolver: "who@x.test");

    private static StoredBookmark Bookmark(string name, string instanceId)
        => new() { Name = name, WorkflowInstanceId = instanceId, Id = Guid.NewGuid().ToString() };

    private void SetupFind(string expectedName, params StoredBookmark[] matches) =>
        _bookmarks
            .Setup(b => b.FindManyAsync(
                It.Is<BookmarkFilter>(f => f.Name == expectedName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.AsEnumerable());

    private Task<IResult> Invoke(ClarifyResumeEndpoint.ResumeRequest req)
        => ClarifyResumeEndpoint.Resume(
            req, _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    [Test]
    public async Task Resume_Valid_UsesCanonicalBookmarkName_InjectsAnsweredAndAnswers_ResumesMatch()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var expected = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenant, session);
        SetupFind(expected, Bookmark(expected, "wf-c1"));

        var result = await Invoke(Req(session, tenant, "we mean 30s and PostgreSQL"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                (bool)r.Input!["Answered"] == true
                && (string)r.Input!["Answers"] == "we mean 30s and PostgreSQL"),
            It.IsAny<CancellationToken>()), Times.Once);
        _runtime.Verify(r => r.CreateClientAsync("wf-c1", It.IsAny<CancellationToken>()), Times.Once);
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
        var nameA = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenantA, session);
        var nameB = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenantB, session);
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
        var name = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenant, session);
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
        var name = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenant, session);
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

    [TestCase("")]
    [TestCase("   ")]
    public async Task Resume_EmptyAnswers_Returns400(string answers)
    {
        var result = await Invoke(Req(Guid.NewGuid(), Guid.NewGuid().ToString(), answers));
        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }
}

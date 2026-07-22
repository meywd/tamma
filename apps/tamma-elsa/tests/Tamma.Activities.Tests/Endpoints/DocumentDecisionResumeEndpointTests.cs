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
/// Story 39-8 (AC5 engine half) — the generic engine-side document-decision resume seam
/// (<c>POST /elsa/api/documents/decision/resume</c>). Verifies it computes the SAME
/// tenant+session-scoped bookmark name as the suspend-side activity
/// (<c>document-decision-{tenant}-{session}</c>) and injects the EXACT D5 input keys; is
/// IDOR-safe (cross-tenant → 404, never resolves the victim's gate); REFUSES a duplicate
/// bookmark with 409 (never <c>bookmarks[0]</c>); 404s a miss; and 400s an empty session.
/// Copied from <c>DesignResumeEndpointTests</c>.
/// </summary>
[TestFixture]
public class DocumentDecisionResumeEndpointTests
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

    private static DocumentDecisionResumeEndpoint.ResumeRequest Req(
        Guid session, string? tenant, string decisionJson = "{\"kind\":\"accept\"}", string channel = "user") =>
        new(session, tenant, decisionJson, Feedback: "ok", DeciderId: "who@x.test",
            DeciderDisplay: "Who", Channel: channel, RulesReference: "system-default@1");

    private static StoredBookmark Bookmark(string name, string instanceId)
        => new() { Name = name, WorkflowInstanceId = instanceId, Id = Guid.NewGuid().ToString() };

    private void SetupFind(string expectedName, params StoredBookmark[] matches) =>
        _bookmarks
            .Setup(b => b.FindManyAsync(
                It.Is<BookmarkFilter>(f => f.Name == expectedName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.AsEnumerable());

    private Task<IResult> Invoke(DocumentDecisionResumeEndpoint.ResumeRequest req)
        => DocumentDecisionResumeEndpoint.Resume(
            req, _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    [Test]
    public async Task Resume_Valid_UsesCanonicalName_InjectsExactD5Keys_ResumesMatch()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var expected = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenant, session);
        SetupFind(expected, Bookmark(expected, "wf-1"));

        var result = await Invoke(Req(session, tenant));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                (string)r.Input!["DecisionJson"] == "{\"kind\":\"accept\"}"
                && (string)r.Input!["Feedback"] == "ok"
                && (string)r.Input!["DeciderId"] == "who@x.test"
                && (string)r.Input!["DeciderDisplay"] == "Who"
                && (string)r.Input!["Channel"] == "user"
                && (string)r.Input!["RulesReference"] == "system-default@1"),
            It.IsAny<CancellationToken>()), Times.Once);
        _runtime.Verify(r => r.CreateClientAsync("wf-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_CrossTenant_DifferentName_Returns404_NeverResumes()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();
        var nameA = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenantA, session);
        var nameB = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenantB, session);
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
        var name = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenant, session);
        SetupFind(name /* zero matches */);

        var result = await Invoke(Req(session, tenant));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_MoreThanOneMatch_Refuses409_DoesNotResumeArbitrary()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var name = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenant, session);
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

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
/// Story 39-13 (D3) — the generic engine-side input resume seam
/// (<c>POST /elsa/api/documents/input/resume</c>). Verifies it computes the canonical
/// <c>document-input-{tenant}-{session}</c> bookmark, injects <c>{Received, InputJson}</c>,
/// is IDOR-safe (cross-tenant 404), refuses collisions (409), 404s a miss, and 400s an empty
/// session / empty input. Mirrors <c>DocumentDecisionResumeEndpointTests</c>.
/// </summary>
[TestFixture]
public class DocumentInputResumeEndpointTests
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
        _runtime.Setup(r => r.CreateClientAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _client.Setup(c => c.RunInstanceAsync(It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunWorkflowInstanceResponse());
    }

    private static DocumentInputResumeEndpoint.ResumeRequest Req(Guid session, string? tenant, string input = "answer") =>
        new(session, tenant, input, Respondent: "who@x.test");

    private static StoredBookmark Bookmark(string name, string instanceId)
        => new() { Name = name, WorkflowInstanceId = instanceId, Id = Guid.NewGuid().ToString() };

    private void SetupFind(string expectedName, params StoredBookmark[] matches) =>
        _bookmarks.Setup(b => b.FindManyAsync(
            It.Is<BookmarkFilter>(f => f.Name == expectedName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.AsEnumerable());

    private Task<IResult> Invoke(DocumentInputResumeEndpoint.ResumeRequest req)
        => DocumentInputResumeEndpoint.Resume(
            req, _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

    private static int? StatusCodeOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    [Test]
    public async Task Resume_Valid_UsesCanonicalName_InjectsReceivedAndInput_ResumesMatch()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var expected = WaitForDocumentInputActivity.InputBookmarkName(tenant, session);
        SetupFind(expected, Bookmark(expected, "wf-i1"));

        var result = await Invoke(Req(session, tenant, "we mean 30s"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                (bool)r.Input!["Received"] == true && (string)r.Input!["InputJson"] == "we mean 30s"),
            It.IsAny<CancellationToken>()), Times.Once);
        _runtime.Verify(r => r.CreateClientAsync("wf-i1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_CrossTenant_DifferentName_Returns404_NeverResumes()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();
        var nameA = WaitForDocumentInputActivity.InputBookmarkName(tenantA, session);
        var nameB = WaitForDocumentInputActivity.InputBookmarkName(tenantB, session);
        SetupFind(nameA, Bookmark(nameA, "wf-victim"));
        SetupFind(nameB);

        var result = await Invoke(Req(session, tenantB));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_NoMatchingBookmark_Returns404()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        SetupFind(WaitForDocumentInputActivity.InputBookmarkName(tenant, session));

        StatusCodeOf(await Invoke(Req(session, tenant))).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Resume_MoreThanOneMatch_Refuses409()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var name = WaitForDocumentInputActivity.InputBookmarkName(tenant, session);
        SetupFind(name, Bookmark(name, "wf-a"), Bookmark(name, "wf-b"));

        StatusCodeOf(await Invoke(Req(session, tenant))).Should().Be(StatusCodes.Status409Conflict);
        _client.Verify(c => c.RunInstanceAsync(It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_EmptySession_Returns400()
        => StatusCodeOf(await Invoke(Req(Guid.Empty, Guid.NewGuid().ToString()))).Should().Be(StatusCodes.Status400BadRequest);

    [TestCase("")]
    [TestCase("   ")]
    public async Task Resume_EmptyInput_Returns400(string input)
        => StatusCodeOf(await Invoke(Req(Guid.NewGuid(), Guid.NewGuid().ToString(), input))).Should().Be(StatusCodes.Status400BadRequest);
}

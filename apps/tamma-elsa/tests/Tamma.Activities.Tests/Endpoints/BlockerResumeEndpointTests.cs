using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Entities;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Blocker;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Endpoints;

/// <summary>
/// Follow-up #15 — the engine-side blocker resume seam
/// (<c>POST /elsa/api/adl/blocker/resume</c>). Verifies it:
///   - computes the SAME session-scoped bookmark name as the suspend-side activity
///     (progress <c>blocker-progress-{session}-{level}</c> / escalation
///     <c>blocker-escalation-{session}</c>) and resumes the matching (single) instance
///     with the EXACT input keys the callback reads;
///   - REFUSES with 409 when more than one instance matches a (unique) name rather than
///     resuming an arbitrary <c>bookmarks[0]</c>;
///   - 404s when no bookmark matches (already advanced / resolved / timed out);
///   - 400s a malformed request (empty session, unknown kind, missing progress level).
/// </summary>
[TestFixture]
public class BlockerResumeEndpointTests
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

    private static BlockerResumeEndpoint.ResumeRequest Progress(
        Guid session, string? level, string? progressType = null, string? details = null)
        => new(session, "progress", level, Resolved: false, progressType, details, SeniorResponse: null, Resolver: "who@x.test");

    private static BlockerResumeEndpoint.ResumeRequest Escalation(
        Guid session, bool resolved, string? seniorResponse = null)
        => new(session, "escalation", Level: null, resolved, ProgressType: null, Details: null, seniorResponse, Resolver: "who@x.test");

    private static StoredBookmark Bookmark(string name, string instanceId)
        => new() { Name = name, WorkflowInstanceId = instanceId, Id = Guid.NewGuid().ToString() };

    private void SetupFind(string expectedName, params StoredBookmark[] matches) =>
        _bookmarks
            .Setup(b => b.FindManyAsync(
                It.Is<BookmarkFilter>(f => f.Name == expectedName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.AsEnumerable());

    private Task<IResult> Invoke(BlockerResumeEndpoint.ResumeRequest req)
        => BlockerResumeEndpoint.Resume(
            req, _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

    [Test]
    public async Task Resume_Progress_UsesProgressBookmarkName_InjectsProgressDetected_ResumesMatch()
    {
        var session = Guid.NewGuid();
        var expected = DetectProgressActivity.ProgressBookmarkName(session, "Guidance");
        SetupFind(expected, Bookmark(expected, "wf-p1"));

        var result = await Invoke(Progress(session, "Guidance", "commit", "pushed a fix"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                (bool)r.Input!["ProgressDetected"] == true
                && (string)r.Input!["ProgressType"] == "commit"
                && (string)r.Input!["Details"] == "pushed a fix"),
            It.IsAny<CancellationToken>()), Times.Once);
        _runtime.Verify(r => r.CreateClientAsync("wf-p1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_Progress_CanonicalisesLevelCasing_ToWorkflowSegment()
    {
        // A lower-case "hint" on the wire must resolve the SAME bookmark the workflow armed
        // with the canonical "Hint" segment.
        var session = Guid.NewGuid();
        var expected = DetectProgressActivity.ProgressBookmarkName(session, "Hint");
        SetupFind(expected, Bookmark(expected, "wf-hint"));

        var result = await Invoke(Progress(session, "hint"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _runtime.Verify(r => r.CreateClientAsync("wf-hint", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_Escalation_UsesEscalationBookmarkName_InjectsResolvedAndSeniorResponse()
    {
        var session = Guid.NewGuid();
        var expected = EscalateToSeniorActivity.EscalationBookmarkName(session);
        SetupFind(expected, Bookmark(expected, "wf-e1"));

        var result = await Invoke(Escalation(session, resolved: true, seniorResponse: "fixed the config"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                (bool)r.Input!["Resolved"] == true
                && (string)r.Input!["SeniorResponse"] == "fixed the config"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_NoMatchingBookmark_Returns404_NeverResumes()
    {
        var session = Guid.NewGuid();
        var name = EscalateToSeniorActivity.EscalationBookmarkName(session);
        SetupFind(name /* zero matches */);

        var result = await Invoke(Escalation(session, resolved: true));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a miss must never resume any instance");
    }

    [Test]
    public async Task Resume_MoreThanOneMatch_Refuses409_DoesNotResumeArbitrary()
    {
        // The name is unique per session (+ level); >1 match is an integrity violation.
        var session = Guid.NewGuid();
        var name = DetectProgressActivity.ProgressBookmarkName(session, "Assistance");
        SetupFind(name, Bookmark(name, "wf-a"), Bookmark(name, "wf-b"));

        var result = await Invoke(Progress(session, "Assistance"));

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "an ambiguous bookmark must refuse, not resume an arbitrary instance");
    }

    [Test]
    public async Task Resume_EmptySession_Returns400()
    {
        var result = await Invoke(Escalation(Guid.Empty, resolved: true));
        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [TestCase("resolve")]
    [TestCase("")]
    public async Task Resume_UnknownKind_Returns400(string kind)
    {
        var req = new BlockerResumeEndpoint.ResumeRequest(
            Guid.NewGuid(), kind, "Hint", Resolved: true,
            ProgressType: null, Details: null, SeniorResponse: null, Resolver: null);

        var result = await Invoke(req);
        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [TestCase(null)]
    [TestCase("nope")]
    public async Task Resume_ProgressMissingOrBadLevel_Returns400(string? level)
    {
        var result = await Invoke(Progress(Guid.NewGuid(), level));
        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public void CanonicalLevel_MapsCaseInsensitively_ElseNull()
    {
        BlockerResumeEndpoint.CanonicalLevel("hint").Should().Be("Hint");
        BlockerResumeEndpoint.CanonicalLevel(" Guidance ").Should().Be("Guidance");
        BlockerResumeEndpoint.CanonicalLevel("ASSISTANCE").Should().Be("Assistance");
        BlockerResumeEndpoint.CanonicalLevel("escalation").Should().BeNull();
        BlockerResumeEndpoint.CanonicalLevel(null).Should().BeNull();
    }

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;
}

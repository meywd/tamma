using System.Text.Json;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Entities;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Testing;
using Tamma.Activities.Testing.Models;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Endpoints;

/// <summary>
/// Epic 31 P3 Milestone 2 (DG-5) — the engine-side half of the CI completion
/// poller (<c>GET /elsa/api/ci/waits</c> + <c>POST /elsa/api/ci/waits/resume</c>).
/// Verifies:
///   - listing surfaces every suspended CI wait under the common stimulus name
///     with its payload (run id, repository, tenant) and EXCLUDES unpollable
///     waits (no repository — pre-P3 bookmarks — or an "unknown" run id);
///   - resume targets the exact bookmark id and injects the result as workflow
///     input (the keys <c>WaitForCIResultsActivity.OnResumeAsync</c> reads);
///   - the DOUBLE-ADVANCE GUARD: a burned/unknown bookmark id answers 404 and
///     NEVER runs any instance (the timeout race is a benign no-op).
/// </summary>
[TestFixture]
public class CiWaitEndpointsTests
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
        // Default: the atomic claim succeeds (this caller deleted the row).
        _bookmarks
            .Setup(b => b.DeleteAsync(It.IsAny<BookmarkFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);
    }

    private static StoredBookmark Bookmark(string id, string instanceId, object? payload) => new()
    {
        Id = id,
        Name = WaitForCIResultsActivity.CiWaitStimulusName,
        WorkflowInstanceId = instanceId,
        Payload = payload,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private void StoreHasWaits(params StoredBookmark[] bookmarks) => _bookmarks
        .Setup(b => b.FindManyAsync(
            It.Is<BookmarkFilter>(f => f.Name == WaitForCIResultsActivity.CiWaitStimulusName),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(bookmarks.AsEnumerable());

    private void StoreHasBookmarkById(string id, params StoredBookmark[] matches) => _bookmarks
        .Setup(b => b.FindManyAsync(
            It.Is<BookmarkFilter>(f => f.BookmarkId == id),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(matches.AsEnumerable());

    private static async Task<JsonElement> BodyOf(IResult result)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<
            Microsoft.Extensions.Logging.ILoggerFactory>(services, NullLoggerFactory.Instance);
        var ctx = new DefaultHttpContext
        {
            RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                .BuildServiceProvider(services),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    // ================================================================
    // ListWaits
    // ================================================================

    [Test]
    public async Task ListWaits_SurfacesPollableWaits_WithPayloadFields()
    {
        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        StoreHasWaits(Bookmark("bm-1", "wf-1",
            new CIResultBookmarkPayload(session, "42", "acme/widgets", tenant)));

        var body = await BodyOf(await CiWaitEndpoints.ListWaits(_bookmarks.Object, CancellationToken.None));

        var wait = body.GetProperty("waits").EnumerateArray().Single();
        wait.GetProperty("bookmarkId").GetString().Should().Be("bm-1");
        wait.GetProperty("workflowInstanceId").GetString().Should().Be("wf-1");
        wait.GetProperty("runId").GetString().Should().Be("42");
        wait.GetProperty("repository").GetString().Should().Be("acme/widgets");
        wait.GetProperty("tenantId").GetString().Should().Be(tenant);
    }

    [Test]
    public async Task ListWaits_ExcludesUnpollableWaits_PreP3AndUnknownRunIds()
    {
        var session = Guid.NewGuid();
        StoreHasWaits(
            // Pre-P3 bookmark: no repository on the payload.
            Bookmark("bm-old", "wf-old", new CIResultBookmarkPayload(session, "7")),
            // A trigger failure left runId "unknown" — nothing to poll.
            Bookmark("bm-unknown", "wf-u", new CIResultBookmarkPayload(session, "unknown", "acme/widgets")),
            Bookmark("bm-good", "wf-g", new CIResultBookmarkPayload(session, "42", "acme/widgets")));

        var body = await BodyOf(await CiWaitEndpoints.ListWaits(_bookmarks.Object, CancellationToken.None));

        body.GetProperty("waits").EnumerateArray()
            .Select(w => w.GetProperty("bookmarkId").GetString())
            .Should().ContainSingle(
                "unpollable waits keep their timeout SLA; the poller never sees them")
            .Which.Should().Be("bm-good");
    }

    [Test]
    public async Task ListWaits_ToleratesJsonShapedPayloads()
    {
        // The EF-backed store materializes payloads as JSON shapes, not live
        // CLR instances — DeserializePayload must normalize both.
        var session = Guid.NewGuid();
        var jsonPayload = JsonSerializer.SerializeToElement(
            new CIResultBookmarkPayload(session, "42", "acme/widgets", null));
        StoreHasWaits(Bookmark("bm-json", "wf-1", jsonPayload));

        var body = await BodyOf(await CiWaitEndpoints.ListWaits(_bookmarks.Object, CancellationToken.None));

        body.GetProperty("waits").EnumerateArray().Single()
            .GetProperty("runId").GetString().Should().Be("42");
    }

    // ================================================================
    // Resume — exact bookmark, result injected as workflow input
    // ================================================================

    [Test]
    public async Task Resume_RunsTheOwningInstance_WithStatusAndBuildPassedInput()
    {
        StoreHasBookmarkById("bm-1", Bookmark("bm-1", "wf-1",
            new CIResultBookmarkPayload(Guid.NewGuid(), "42", "acme/widgets")));

        var result = await CiWaitEndpoints.Resume(
            new CiWaitEndpoints.ResumeRequest("bm-1", "42", "success", BuildPassed: true),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _runtime.Verify(r => r.CreateClientAsync("wf-1", It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                r.BookmarkId == "bm-1"
                && (string)r.Input!["Status"] == "success"
                && (bool)r.Input!["BuildPassed"]),
            It.IsAny<CancellationToken>()), Times.Once);
        // The row is claimed (deleted) exactly once, BEFORE the run.
        _bookmarks.Verify(b => b.DeleteAsync(
            It.Is<BookmarkFilter>(f => f.BookmarkId == "bm-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ================================================================
    // Concurrent-resume serialization (Epic 31 review, F-critical):
    // the bookmark row is claimed ATOMICALLY before running, so of N
    // concurrent callers exactly one executes the continuation.
    // ================================================================

    [Test]
    public async Task Resume_TwoConcurrentResumes_OnlyOneRunsTheInstance()
    {
        // Both callers pass the FindMany check (the row is still visible for
        // the whole in-flight burst — that is the race), but the atomic
        // delete-claim admits exactly one: the store answers 1 for the first
        // DELETE and 0 for the loser.
        StoreHasBookmarkById("bm-1", Bookmark("bm-1", "wf-1",
            new CIResultBookmarkPayload(Guid.NewGuid(), "42", "acme/widgets")));
        var claims = 0;
        _bookmarks
            .Setup(b => b.DeleteAsync(
                It.Is<BookmarkFilter>(f => f.BookmarkId == "bm-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref claims) == 1 ? 1L : 0L);

        var request = new CiWaitEndpoints.ResumeRequest("bm-1", "42", "success", BuildPassed: true);
        var results = await Task.WhenAll(
            Task.Run(() => CiWaitEndpoints.Resume(
                request, _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None)),
            Task.Run(() => CiWaitEndpoints.Resume(
                request, _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None)));

        results.Select(StatusCodeOf).Should().BeEquivalentTo(
            new int?[] { StatusCodes.Status200OK, StatusCodes.Status404NotFound },
            "exactly one caller wins the claim; the loser is a benign no-op");
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Once,
            "the continuation must execute exactly once — never in parallel");
    }

    [Test]
    public async Task Resume_LostClaim_Returns404_NeverRunsAnInstance()
    {
        // The row was visible at FindMany time but a concurrent caller claimed
        // it before this one — mid-burst duplicate ⇒ benign 404, no run.
        StoreHasBookmarkById("bm-1", Bookmark("bm-1", "wf-1",
            new CIResultBookmarkPayload(Guid.NewGuid(), "42", "acme/widgets")));
        _bookmarks
            .Setup(b => b.DeleteAsync(It.IsAny<BookmarkFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);

        var result = await CiWaitEndpoints.Resume(
            new CiWaitEndpoints.ResumeRequest("bm-1", "42", "success", BuildPassed: true),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_RunFailure_RestoresTheClaimedRow_AndPropagates()
    {
        // A claim whose run fails must put the row back — otherwise the wait
        // disappears from the poller and only the 30m timeout can end it.
        var bookmark = Bookmark("bm-1", "wf-1",
            new CIResultBookmarkPayload(Guid.NewGuid(), "42", "acme/widgets"));
        StoreHasBookmarkById("bm-1", bookmark);
        _client
            .Setup(c => c.RunInstanceAsync(It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("engine burst failed"));

        var act = () => CiWaitEndpoints.Resume(
            new CiWaitEndpoints.ResumeRequest("bm-1", "42", "success", BuildPassed: true),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _bookmarks.Verify(b => b.SaveAsync(bookmark, It.IsAny<CancellationToken>()), Times.Once,
            "a failed run must restore the claimed row so the wait stays discoverable");
    }

    [Test]
    public async Task Resume_BurnedBookmark_Returns404_NeverRunsAnInstance()
    {
        // THE double-advance guard: the timeout edge already completed the
        // activity and Elsa burned its bookmarks — a late resume must be a
        // benign 404, never a second advance of the workflow.
        StoreHasBookmarkById("bm-gone" /* zero matches */);

        var result = await CiWaitEndpoints.Resume(
            new CiWaitEndpoints.ResumeRequest("bm-gone", "42", "success", BuildPassed: true),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a burned bookmark must never advance any instance");
    }

    [Test]
    public async Task Resume_MissingBookmarkId_Returns400()
    {
        var result = await CiWaitEndpoints.Resume(
            new CiWaitEndpoints.ResumeRequest("", "42", "success", BuildPassed: true),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ================================================================
    // The wait activity registers under the common stimulus name with the
    // repo-carrying payload (the discovery contract the poller relies on).
    // ================================================================

    [Test]
    public void Payload_CarriesRepositoryAndTenant_ForThePoller()
    {
        var session = Guid.NewGuid();
        var payload = new CIResultBookmarkPayload(session, "42", "acme/widgets", "t-1");

        payload.BookmarkId.Should().Be($"ci-result-{session}-42");
        payload.Repository.Should().Be("acme/widgets");
        payload.TenantId.Should().Be("t-1");
    }

    [Test]
    public void PreP3Payload_DeserializesWithEmptyRepository_RolloutSafe()
    {
        var legacy = JsonSerializer.Deserialize<CIResultBookmarkPayload>(
            "{\"SessionId\":\"" + Guid.NewGuid() + "\",\"RunId\":\"7\",\"BookmarkId\":\"ci-result-x-7\"}");

        legacy!.Repository.Should().BeEmpty(
            "in-flight pre-P3 bookmarks must deserialize (and be skipped by the poller), not throw");
    }
}

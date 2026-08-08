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
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Endpoints;

/// <summary>
/// Epic 31 P4 M2 (DG-6) — the engine-side merged-PR resume seam.
///
/// <para><b>Red-first claim.</b> Before this milestone the
/// <c>pr-merged-{n}</c> bookmark had ZERO resumers — the endpoint under test
/// did not exist and every merged PR ended its cycle through the 12h TimedOut
/// SLA, for every platform including GitHub.
/// <see cref="Resume_QualifiedBookmark_RunsTheInstance_WithMergeSha"/> is the
/// engine half of the plan's acceptance ("replaying a recorded merged-PR
/// webhook against a suspended cycle resumes WaitForPRMerged on the Merged
/// edge with mergeSha"); the handler half lives in
/// <c>Tamma.Api.Tests.Webhooks.PrMergedWebhookHandlerTests</c>.</para>
/// </summary>
[TestFixture]
public class PrMergedResumeEndpointTests
{
    private const string Tenant = "6a5ee5c1-8f5a-4d3a-9b6e-000000000001";
    private const string Repo = "acme/widgets";
    private const int Pr = 55;

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
        // Default: every name lookup finds nothing; tests override per name.
        _bookmarks
            .Setup(b => b.FindManyAsync(It.IsAny<BookmarkFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<StoredBookmark>());
    }

    private static StoredBookmark Bookmark(string id, string name, string instanceId) => new()
    {
        Id = id,
        Name = name,
        WorkflowInstanceId = instanceId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private void StoreHas(string name, params StoredBookmark[] matches) => _bookmarks
        .Setup(b => b.FindManyAsync(
            It.Is<BookmarkFilter>(f => f.Name == name),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(matches.AsEnumerable());

    private Task<IResult> Call(
        int pr = Pr, string? sha = "abc123", string? tenant = Tenant, string? repo = Repo) =>
        PrMergedResumeEndpoint.Resume(
            new PrMergedResumeEndpoint.ResumeRequest(pr, sha, tenant, repo),
            _bookmarks.Object, _runtime.Object, NullLoggerFactory.Instance, CancellationToken.None);

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    // ================================================================
    // Bookmark naming — the rollout-safe transition contract.
    // ================================================================

    [Test]
    public void BookmarkName_FoldsTenantAndRepoIn_TheMergeApprovalConvention()
    {
        WaitForPRMergedActivity.BookmarkName(Tenant, Repo, Pr)
            .Should().Be(
                $"pr-merged-{WaitForMergeApprovalActivity.NormalizeSegment(Tenant)}-acme_widgets-{Pr}",
                "the qualified name must normalize segments exactly like the merge-approval gate");
        WaitForMergeApprovalActivity.NormalizeSegment(Tenant)
            .Should().NotContain("-", "GUID dashes normalize to '_' so the '-' delimiter stays unambiguous");
        WaitForPRMergedActivity.BookmarkName(null, null, Pr)
            .Should().Be($"pr-merged-none-none-{Pr}",
                "single-user mode folds the stable 'none' placeholders");
        WaitForPRMergedActivity.LegacyBookmarkName(Pr).Should().Be($"pr-merged-{Pr}");
    }

    // ================================================================
    // Resume — qualified name is the designed path.
    // ================================================================

    [Test]
    public async Task Resume_QualifiedBookmark_RunsTheInstance_WithMergeSha()
    {
        var qualified = WaitForPRMergedActivity.BookmarkName(Tenant, Repo, Pr);
        StoreHas(qualified, Bookmark("bm-1", qualified, "wf-1"));

        var result = await Call();

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _runtime.Verify(r => r.CreateClientAsync("wf-1", It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                r.BookmarkId == "bm-1"
                && (string)r.Input!["mergeSha"] == "abc123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_LegacyBookmark_TransitionFallback_StillResumes()
    {
        // An instance suspended BEFORE the P4 deploy holds the unqualified
        // pr-merged-{n} name. The resumer must match it too (rollout-safe).
        var legacy = WaitForPRMergedActivity.LegacyBookmarkName(Pr);
        StoreHas(legacy, Bookmark("bm-old", legacy, "wf-old"));

        var result = await Call();

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r => r.BookmarkId == "bm-old"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resume_QualifiedWins_LegacyNotConsulted_WhenQualifiedMatches()
    {
        var qualified = WaitForPRMergedActivity.BookmarkName(Tenant, Repo, Pr);
        var legacy = WaitForPRMergedActivity.LegacyBookmarkName(Pr);
        StoreHas(qualified, Bookmark("bm-new", qualified, "wf-new"));
        StoreHas(legacy, Bookmark("bm-old", legacy, "wf-old"));

        await Call();

        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r => r.BookmarkId == "bm-new"),
            It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r => r.BookmarkId == "bm-old"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_CrossTenantName_NeverMatchesAnotherTenantsWait()
    {
        // Tenant B's wait is suspended under B's qualified name; a caller
        // scoped to tenant A computes A's name and must 404, never act.
        var tenantB = Guid.NewGuid().ToString();
        var bName = WaitForPRMergedActivity.BookmarkName(tenantB, Repo, Pr);
        StoreHas(bName, Bookmark("bm-b", bName, "wf-b"));

        var result = await Call(tenant: Tenant);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ================================================================
    // Idempotency + integrity guards.
    // ================================================================

    [Test]
    public async Task Resume_BurnedBookmark_Returns404_NeverRunsAnInstance()
    {
        // The 12h SLA edge fired first (or a duplicate delivery raced): both
        // names resolve nothing → benign 404, never a double-advance.
        var result = await Call();

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_AmbiguousLegacyName_Refuses409_NeverPicksArbitrarily()
    {
        // The legacy name is only unique per PR number — two live instances on
        // the same number is exactly the integrity violation the qualified
        // name exists to prevent. REFUSE (the merge-approval C2 rule).
        var legacy = WaitForPRMergedActivity.LegacyBookmarkName(Pr);
        StoreHas(legacy,
            Bookmark("bm-1", legacy, "wf-1"),
            Bookmark("bm-2", legacy, "wf-2"));

        var result = await Call();

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _client.Verify(c => c.RunInstanceAsync(
            It.IsAny<RunWorkflowInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Resume_NonPositivePrNumber_Returns400()
    {
        var result = await Call(pr: 0);
        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Resume_NoMergeSha_StillResumes_WithEmptyInput()
    {
        // Gitea/GitLab payloads may omit the merge SHA; the Merged edge still
        // fires (MergeSha output stays null, matching the activity contract).
        var qualified = WaitForPRMergedActivity.BookmarkName(Tenant, Repo, Pr);
        StoreHas(qualified, Bookmark("bm-1", qualified, "wf-1"));

        var result = await Call(sha: null);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _client.Verify(c => c.RunInstanceAsync(
            It.Is<RunWorkflowInstanceRequest>(r =>
                r.BookmarkId == "bm-1" && !r.Input!.ContainsKey("mergeSha")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

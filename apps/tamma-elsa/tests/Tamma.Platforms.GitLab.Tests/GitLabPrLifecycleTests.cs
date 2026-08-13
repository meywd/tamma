using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab.Tests.Support;

namespace Tamma.Platforms.GitLab.Tests;

/// <summary>
/// Epic 31 P6 M1 — the six Story 31-13 PR lifecycle verbs, made REAL on
/// the GitLab driver. Per-verb happy path + the load-bearing platform
/// facts pinned by tests (each verified against the GitLab API docs /
/// doc-source history on 2026-08-09):
///
/// <list type="bullet">
///   <item>close / reopen ride the update-MR <c>state_event</c> field.</item>
///   <item>reviewers ride <c>reviewer_ids</c> on the update-MR API —
///         introduced 13.8 (gitlab-org/gitlab!51186) but honored by the
///         update endpoint only from 13.9 (#299846/#320780), hence the
///         13.9 family floor. Username→id resolution happens IN the
///         driver via <c>GET /users?username=</c>; an unresolvable name
///         answers the typed <c>reviewer_unresolvable</c> refusal that
///         mediation's DG-3 alternative step consumes.</item>
///   <item>labels ride <c>add_labels</c>/<c>remove_labels</c>
///         (comma-separated NAMES; GitLab auto-creates missing project
///         labels on add — GitHub parity for the loop's tamma-* labels).</item>
///   <item>draft is the TITLE PREFIX (<c>Draft:</c> since 13.2; legacy
///         WIP readable until its 14.8 removal) — the update-MR API has
///         no draft field, so SetDraft is an idempotent title edit.</item>
///   <item>below the 13.9 floor (or when the version probe failed) every
///         verb answers typed <c>capability_unsupported</c> WITHOUT
///         touching the network — agreeing with
///         <c>GitLabPlatformDriver.ComputeCapabilities</c>.</item>
/// </list>
/// </summary>
[TestFixture]
public class GitLabPrLifecycleTests
{
    private static (GitLabPlatformClient Client, FakeHttpMessageHandler Handler) BuildLive() =>
        TestFactory.BuildClient(detectedVersion: TestFactory.LifecycleCapableVersion);

    private static string MrJson(
        string state = "opened", string title = "feat: thing", bool draft = false) =>
        $$"""
        {"id":100,"iid":5,"title":"{{title}}","description":"b",
          "source_branch":"feat/x","target_branch":"main","state":"{{state}}",
          "draft":{{(draft ? "true" : "false")}},"work_in_progress":{{(draft ? "true" : "false")}},
          "web_url":"https://gitlab.example.com/octo/repo/-/merge_requests/5",
          "author":{"username":"bot","id":7},
          "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z",
          "sha":"headsha000"}
        """;

    // ───────────── Close / Reopen (state_event) ─────────────

    [Test]
    public async Task ClosePullRequestAsync_PutsStateEventClose()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MrJson(state: "closed")));

        var result = await client.ClosePullRequestAsync("octo", "repo", "5");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Closed);
        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        put.RequestUri.ToString().Should().Contain("projects/octo%2Frepo/merge_requests/5");
        put.Body.Should().Contain("\"state_event\":\"close\"");
    }

    [Test]
    public async Task ReopenPullRequestAsync_PutsStateEventReopen()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MrJson()));

        var result = await client.ReopenPullRequestAsync("octo", "repo", "5");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Open);
        handler.Requests.Single(r => r.Method == HttpMethod.Put)
            .Body.Should().Contain("\"state_event\":\"reopen\"");
    }

    [Test]
    public async Task ClosePullRequestAsync_MapsNotFound()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(
                HttpStatusCode.NotFound, """{"message":"404 Not Found"}"""));

        var result = await client.ClosePullRequestAsync("octo", "repo", "5");

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.NotFound>();
    }

    // ───────────── RequestReviewers (username→id resolver) ─────────────

    [Test]
    public async Task RequestReviewersAsync_ResolvesUsernames_ThenPutsReviewerIds()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "users?username=alice",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """[{"id":42,"username":"alice","name":"Alice"}]"""));
        handler.AddRoute(HttpMethod.Get, "users?username=bob",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """[{"id":77,"username":"bob","name":"Bob"}]"""));
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MrJson()));

        var result = await client.RequestReviewersAsync(
            new RequestReviewersRequest("octo", "repo", "5", ["alice", "bob"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Body.Should().Contain("\"reviewer_ids\":[42,77]");
    }

    [Test]
    public async Task RequestReviewersAsync_ExactMatchOnly_IgnoresPartialMatches()
    {
        // GET /users?username= is documented as an exact-match filter, but the
        // driver still guards against a fuzzy-answering proxy: only a row whose
        // username equals the query (ordinal-ignore-case) resolves.
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "users?username=ali",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """[{"id":42,"username":"alice","name":"Alice"}]"""));

        var result = await client.RequestReviewersAsync(
            new RequestReviewersRequest("octo", "repo", "5", ["ali"]));

        var err = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>().Which;
        err.Code.Should().Be("reviewer_unresolvable");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Test]
    public async Task RequestReviewersAsync_UnresolvableUsername_AnswersTypedRefusal()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "users?username=ghost",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]"));

        var result = await client.RequestReviewersAsync(
            new RequestReviewersRequest("octo", "repo", "5", ["ghost"]));

        var err = result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>().Which;
        err.Code.Should().Be("reviewer_unresolvable");
        err.Hint.Should().Contain("ghost");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put,
            "an unresolvable reviewer must not half-apply the request");
    }

    [Test]
    public async Task RequestReviewersAsync_LookupRejection_SurfacesPlatformError()
    {
        // A 401 on the resolver is NOT reviewer_unresolvable — it is the
        // platform rejecting the lookup and must map through the error mapper
        // (DG-3 §4.5 exact-code discipline: only a truly-unknown username may
        // classify as unresolvable).
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "users?username=alice",
            _ => FakeHttpMessageHandler.Json(
                HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}"""));

        var result = await client.RequestReviewersAsync(
            new RequestReviewersRequest("octo", "repo", "5", ["alice"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.AuthExpired>();
    }

    // ───────────── Labels (add_labels / remove_labels) ─────────────

    [Test]
    public async Task AddPullRequestLabelsAsync_PutsCommaSeparatedAddLabels()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MrJson()));

        var result = await client.AddPullRequestLabelsAsync(
            new AddPullRequestLabelsRequest("octo", "repo", "5", ["tamma-adl", "needs-review"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        handler.Requests.Single(r => r.Method == HttpMethod.Put)
            .Body.Should().Contain("\"add_labels\":\"tamma-adl,needs-review\"");
    }

    [Test]
    public async Task AddPullRequestLabelsAsync_RejectsCommaBearingLabelName()
    {
        var (client, handler) = BuildLive();

        var result = await client.AddPullRequestLabelsAsync(
            new AddPullRequestLabelsRequest("octo", "repo", "5", ["a,b"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("label_name_unsupported");
        handler.Requests.Should().BeEmpty(
            "a name the wire format would silently split must be rejected before HTTP");
    }

    [Test]
    public async Task RemovePullRequestLabelAsync_PutsRemoveLabels()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MrJson()));

        var result = await client.RemovePullRequestLabelAsync("octo", "repo", "5", "needs-review");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        handler.Requests.Single(r => r.Method == HttpMethod.Put)
            .Body.Should().Contain("\"remove_labels\":\"needs-review\"");
    }

    // ───────────── SetDraft (title-prefix toggle) ─────────────

    [Test]
    public async Task SetDraftAsync_EntersDraft_ByPrefixingTitle()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MrJson()));
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                MrJson(title: "Draft: feat: thing", draft: true)));

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: true));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeTrue();
        handler.Requests.Single(r => r.Method == HttpMethod.Put)
            .Body.Should().Contain("\"title\":\"Draft: feat: thing\"");
    }

    [Test]
    public async Task SetDraftAsync_MarksReady_ByStrippingPrefix_IncludingLegacyWip()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                MrJson(title: "Draft: WIP: feat: thing", draft: true)));
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MrJson()));

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: false));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeFalse();
        handler.Requests.Single(r => r.Method == HttpMethod.Put)
            .Body.Should().Contain("\"title\":\"feat: thing\"",
                "stacked draft prefixes (incl. legacy WIP forms) are stripped in one pass");
    }

    [Test]
    public async Task SetDraftAsync_IsIdempotent_NoPutWhenAlreadyInRequestedState()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                MrJson(title: "Draft: feat: thing", draft: true)));

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: true));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeTrue();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put,
            "already-draft = success without an edit");
    }

    [Test]
    public async Task SetDraftAsync_InfersDraftFromTitle_WhenBooleansAbsent()
    {
        // Payloads from older instances / thin proxies can omit the draft
        // booleans — the title prefix alone must be enough to detect the
        // current state (and stay idempotent).
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                {"id":100,"iid":5,"title":"[WIP] feat: thing","state":"opened",
                  "source_branch":"feat/x","target_branch":"main",
                  "author":{"username":"bot","id":7},
                  "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
                """));

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: true));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeTrue("the [WIP] prefix marks the draft");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    // ── Epic 31 review (F-medium) — the booleans-vs-prefix conflict. On
    //    GitLab ≥14.8 a "WIP:" title is ordinary text: the server's explicit
    //    draft:false makes the MR READY, and SetDraft(true) must actually
    //    draft it (PUT "Draft: " onto the title), not no-op on stale prefix
    //    inference while reporting IsDraft=true. ──

    [Test]
    public async Task SetDraftAsync_WipTitledReadyMr_BooleansWin_AndItGetsDrafted()
    {
        var (client, handler) = BuildLive();
        handler.AddRoute(HttpMethod.Get, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                MrJson(title: "WIP: refactor auth", draft: false)));
        handler.AddRoute(HttpMethod.Put, "/merge_requests/5",
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                MrJson(title: "Draft: WIP: refactor auth", draft: true)));

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: true));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeTrue();
        handler.Requests.Single(r => r.Method == HttpMethod.Put)
            .Body.Should().Contain("\"title\":\"Draft: WIP: refactor auth\"",
                "only a current-generation Draft prefix actually drafts the MR on ≥14.8 — "
                + "the legacy WIP prefix must neither satisfy the idempotency check nor "
                + "suppress the write");
    }

    // ───────────── The version gate (both directions) ─────────────

    [Test]
    public async Task AllSixVerbs_BelowFloorOrUnknownVersion_AnswerTypedRefusal_WithoutNetwork()
    {
        foreach (var version in new Version?[] { null, new(13, 8) })
        {
            var (client, handler) = TestFactory.BuildClient(detectedVersion: version);

            var results = new[]
            {
                await client.ClosePullRequestAsync("o", "r", "1"),
                await client.ReopenPullRequestAsync("o", "r", "1"),
                await client.RequestReviewersAsync(new RequestReviewersRequest("o", "r", "1", ["a"])),
                await client.AddPullRequestLabelsAsync(new AddPullRequestLabelsRequest("o", "r", "1", ["l"])),
                await client.RemovePullRequestLabelAsync("o", "r", "1", "l"),
                await client.SetDraftAsync(new SetPullRequestDraftRequest("o", "r", "1", true)),
            };

            foreach (var result in results)
            {
                result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
                    .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
                    .Which.Code.Should().Be("capability_unsupported",
                        $"version={version?.ToString() ?? "unknown"} is below the 13.9 floor");
            }
            handler.Requests.Should().BeEmpty(
                "the refusal must not touch the network (capability contract)");
        }
    }
}

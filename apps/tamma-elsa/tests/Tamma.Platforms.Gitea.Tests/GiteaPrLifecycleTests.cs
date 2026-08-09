using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Epic 31 P5 M1 — the six Story 31-13 PR lifecycle verbs, made REAL on
/// the Gitea driver. Per-verb happy path + the load-bearing platform
/// facts pinned by tests:
///
/// <list type="bullet">
///   <item>close / reopen ride the edit-PR <c>state</c> field (PATCH).</item>
///   <item>reviewers ride <c>POST /pulls/{n}/requested_reviewers</c>
///         (Gitea 1.14+; the version floor for the whole family).</item>
///   <item>labels ride the ISSUE side of the PR and take label IDs —
///         names are resolved (and created best-effort) first.</item>
///   <item>draft is the WIP TITLE PREFIX: no Gitea release has a draft
///         field on Create/EditPullRequestOption (verified against
///         structs/pull.go v1.19..v1.24 + main), and the response-side
///         <c>draft</c> boolean (1.22+) is computed from the prefix. So
///         SetDraft toggles the title, and the mapper infers draft from
///         the prefix for pre-1.22 instances.</item>
///   <item>below the 1.14 floor (or when the version probe failed) every
///         verb answers typed <c>capability_unsupported</c> WITHOUT
///         touching the network — agreeing with
///         <c>GiteaPlatformDriver.ComputeCapabilities</c>.</item>
/// </list>
/// </summary>
[TestFixture]
public class GiteaPrLifecycleTests
{
    private const string PrJson =
        """
        {"number":5,"title":"feat: thing","body":"b","state":"open",
          "merged":false,"draft":false,"html_url":"https://x",
          "user":{"login":"bot"},
          "head":{"ref":"feat/x"},"base":{"ref":"main"},
          "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
        """;

    // ───────────── Close / Reopen (PATCH state) ─────────────

    [Test]
    public async Task ClosePullRequestAsync_PatchesStateClosed()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Patch,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.Created,
            """
            {"number":5,"title":"feat: thing","body":"b","state":"closed",
              "merged":false,"draft":false,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/x"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.ClosePullRequestAsync("octo", "repo", "5");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Closed);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        patch.Body.Should().Contain("\"state\":\"closed\"");
    }

    [Test]
    public async Task ReopenPullRequestAsync_PatchesStateOpen()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Patch,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.Created, PrJson);

        var result = await client.ReopenPullRequestAsync("octo", "repo", "5");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.State.Should().Be(PullRequestState.Open);
        handler.Requests.Single(r => r.Method == HttpMethod.Patch)
            .Body.Should().Contain("\"state\":\"open\"");
    }

    [Test]
    public async Task ClosePullRequestAsync_MapsNotFound()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Patch,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var result = await client.ClosePullRequestAsync("octo", "repo", "5");

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.NotFound>();
    }

    // ───────────── RequestReviewers ─────────────

    [Test]
    public async Task RequestReviewersAsync_PostsReviewers_ThenRefetchesPR()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        // Gitea answers 201 with a []PullReview body — NOT the PR.
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5/requested_reviewers",
            HttpStatusCode.Created, """[{"id":11,"state":"REQUEST_REVIEW"}]""");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK, PrJson);

        var result = await client.RequestReviewersAsync(
            new RequestReviewersRequest("octo", "repo", "5", ["alice", "bob"],
                TeamReviewers: ["backend"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.Number.Should().Be("5");
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Body.Should().Contain("\"reviewers\":[\"alice\",\"bob\"]")
            .And.Contain("\"team_reviewers\":[\"backend\"]");
    }

    [Test]
    public async Task RequestReviewersAsync_MapsUnresolvableReviewer()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        // Gitea 422s when a requested reviewer is not a collaborator.
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5/requested_reviewers",
            (HttpStatusCode)422, """{"message":"user ghost is not a collaborator"}""");

        var result = await client.RequestReviewersAsync(
            new RequestReviewersRequest("octo", "repo", "5", ["ghost"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>();
    }

    // ───────────── Labels (issue-side, id-resolved) ─────────────

    [Test]
    public async Task AddPullRequestLabelsAsync_ResolvesIds_PostsIssueSide_RefetchesPR()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/labels",
            HttpStatusCode.OK, """[{"id":3,"name":"bug"},{"id":9,"name":"infra"}]""");
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/issues/5/labels",
            HttpStatusCode.OK, """[{"id":3,"name":"bug"}]""");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK, PrJson);

        var result = await client.AddPullRequestLabelsAsync(
            new AddPullRequestLabelsRequest("octo", "repo", "5", ["bug"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var post = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Url.Contains("/issues/5/labels"));
        post.Body.Should().Contain("\"labels\":[3]");
    }

    [Test]
    public async Task AddPullRequestLabelsAsync_CreatesMissingLabels_BestEffort()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/labels",
            HttpStatusCode.OK, "[]");
        // The missing label is created (GitHub's issues-labels endpoint
        // auto-creates; parity keeps the loop's tamma-* labels working on a
        // fresh repo), then the add posts its id.
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/labels",
            HttpStatusCode.Created, """{"id":77,"name":"tamma-processing"}""");
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/octo/repo/issues/5/labels",
            HttpStatusCode.OK, """[{"id":77,"name":"tamma-processing"}]""");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK, PrJson);

        var result = await client.AddPullRequestLabelsAsync(
            new AddPullRequestLabelsRequest("octo", "repo", "5", ["tamma-processing"]));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        var createLabel = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Url.EndsWith("/repos/octo/repo/labels"));
        createLabel.Body.Should().Contain("\"name\":\"tamma-processing\"");
        handler.Requests.Single(r =>
                r.Method == HttpMethod.Post && r.Url.Contains("/issues/5/labels"))
            .Body.Should().Contain("\"labels\":[77]");
    }

    [Test]
    public async Task RemovePullRequestLabelAsync_DeletesById_RefetchesPR()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/labels",
            HttpStatusCode.OK, """[{"id":3,"name":"bug"}]""");
        handler.EnqueueJson(HttpMethod.Delete,
            "https://gitea.example.com/api/v1/repos/octo/repo/issues/5/labels/3",
            HttpStatusCode.NoContent, "");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK, PrJson);

        var result = await client.RemovePullRequestLabelAsync("octo", "repo", "5", "bug");

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        handler.Requests.Should().Contain(r =>
            r.Method == HttpMethod.Delete && r.Url.EndsWith("/issues/5/labels/3"));
    }

    [Test]
    public async Task RemovePullRequestLabelAsync_IsIdempotent_WhenLabelUnknown()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/labels",
            HttpStatusCode.OK, "[]");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK, PrJson);

        var result = await client.RemovePullRequestLabelAsync("octo", "repo", "5", "ghost");

        // Unknown label = already absent = success; no DELETE issued.
        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    // ───────────── SetDraft (WIP title prefix) ─────────────

    [Test]
    public async Task SetDraftAsync_False_StripsWipPrefix()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK,
            """
            {"number":5,"title":"WIP: feat: thing","body":"b","state":"open",
              "merged":false,"draft":true,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/x"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);
        handler.EnqueueJson(HttpMethod.Patch,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.Created,
            """
            {"number":5,"title":"feat: thing","body":"b","state":"open",
              "merged":false,"draft":false,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/x"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: false));

        var pr = result.Should().BeOfType<PlatformResult<PullRequest>.Ok>().Subject.Value;
        pr.IsDraft.Should().BeFalse();
        handler.Requests.Single(r => r.Method == HttpMethod.Patch)
            .Body.Should().Contain("\"title\":\"feat: thing\"");
    }

    [Test]
    public async Task SetDraftAsync_True_AddsWipPrefix()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK, PrJson);
        handler.EnqueueJson(HttpMethod.Patch,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.Created,
            """
            {"number":5,"title":"WIP: feat: thing","body":"b","state":"open",
              "merged":false,"draft":true,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/x"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: true));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeTrue();
        handler.Requests.Single(r => r.Method == HttpMethod.Patch)
            .Body.Should().Contain("\"title\":\"WIP: feat: thing\"");
    }

    [Test]
    public async Task SetDraftAsync_IsIdempotent_WhenAlreadyInRequestedState()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK, PrJson); // draft:false, no WIP prefix

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: false));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeFalse();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    [Test]
    public async Task SetDraftAsync_False_InfersDraftFromTitle_OnPre122Response()
    {
        // A 1.21 instance has NO draft boolean in the response — the WIP
        // title prefix is the only signal. The un-draft must still detect
        // draft state and strip the prefix. (1.21 ≥ the 1.14 lifecycle
        // floor, so the verb itself is live.)
        var (client, _, handler, _) = GiteaTestFixtures.Build(
            detectedVersion: new Version(1, 21));
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.OK,
            """
            {"number":5,"title":"[WIP] feat: thing","body":"b","state":"open",
              "merged":false,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/x"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);
        handler.EnqueueJson(HttpMethod.Patch,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/5",
            HttpStatusCode.Created,
            """
            {"number":5,"title":"feat: thing","body":"b","state":"open",
              "merged":false,"html_url":"https://x",
              "user":{"login":"bot"},
              "head":{"ref":"feat/x"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("octo", "repo", "5", Draft: false));

        result.Should().BeOfType<PlatformResult<PullRequest>.Ok>()
            .Which.Value.IsDraft.Should().BeFalse();
        handler.Requests.Single(r => r.Method == HttpMethod.Patch)
            .Body.Should().Contain("\"title\":\"feat: thing\"");
    }

    // ───────────── Version gate (the honest-detection arm) ─────────────

    [Test]
    public async Task LifecycleVerbs_AnswerTypedRefusal_WithoutNetwork_BelowFloor()
    {
        // 1.13 < the 1.14 requested_reviewers floor.
        var (client, _, handler, _) = GiteaTestFixtures.Build(
            detectedVersion: new Version(1, 13));

        var answers = new[]
        {
            await client.ClosePullRequestAsync("o", "r", "1"),
            await client.ReopenPullRequestAsync("o", "r", "1"),
            await client.RequestReviewersAsync(new RequestReviewersRequest("o", "r", "1", ["a"])),
            await client.AddPullRequestLabelsAsync(new AddPullRequestLabelsRequest("o", "r", "1", ["l"])),
            await client.RemovePullRequestLabelAsync("o", "r", "1", "l"),
            await client.SetDraftAsync(new SetPullRequestDraftRequest("o", "r", "1", true)),
        };

        foreach (var answer in answers)
        {
            answer.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
                .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
                .Which.Code.Should().Be("capability_unsupported");
        }
        handler.Requests.Should().BeEmpty("an unadvertised capability must refuse without HTTP");
    }

    [Test]
    public async Task LifecycleVerbs_AnswerTypedRefusal_WhenVersionUnknown()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build(useDefaultVersion: false);

        var result = await client.SetDraftAsync(
            new SetPullRequestDraftRequest("o", "r", "1", false));

        result.Should().BeOfType<PlatformResult<PullRequest>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("capability_unsupported");
        handler.Requests.Should().BeEmpty();
    }

    // ───────────── Capability detection agreement ─────────────

    [Test]
    public void ComputeCapabilities_FlipsPrLifecycle_ExactlyAtTheFloor()
    {
        GiteaPlatformDriver.ComputeCapabilities(new Version(1, 22))
            .Should().Contain(PlatformCapability.PrLifecycle);
        GiteaPlatformDriver.ComputeCapabilities(new Version(1, 14))
            .Should().Contain(PlatformCapability.PrLifecycle);
        GiteaPlatformDriver.ComputeCapabilities(new Version(1, 13))
            .Should().NotContain(PlatformCapability.PrLifecycle);
        GiteaPlatformDriver.ComputeCapabilities(null)
            .Should().NotContain(PlatformCapability.PrLifecycle);
    }

    [Test]
    public void ForgejoComputeCapabilities_MirrorsGiteaFloor_IncludingModernForgejoVersions()
    {
        ForgejoPlatformDriver.ComputeCapabilities(new Version(1, 22))
            .Should().Contain(PlatformCapability.PrLifecycle);
        // Forgejo v7+ reports its own major (7.x, 12.x) — well above the floor.
        ForgejoPlatformDriver.ComputeCapabilities(new Version(12, 0))
            .Should().Contain(PlatformCapability.PrLifecycle);
        ForgejoPlatformDriver.ComputeCapabilities(null)
            .Should().NotContain(PlatformCapability.PrLifecycle);
    }

    // ───────────── WIP helpers ─────────────

    [TestCase("WIP: feat", true)]
    [TestCase("wip: feat", true)]
    [TestCase("[WIP] feat", true)]
    [TestCase("[wip] feat", true)]
    [TestCase("feat: wip handling", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void HasWipPrefix_MatchesGiteaDefaults(string? title, bool expected) =>
        GiteaPlatformClient.HasWipPrefix(title).Should().Be(expected);

    [TestCase("feat", "WIP: feat")]
    [TestCase("WIP: feat", "WIP: feat")]
    [TestCase("[WIP] feat", "[WIP] feat")]
    public void AddWipPrefix_IsIdempotent(string title, string expected) =>
        GiteaPlatformClient.AddWipPrefix(title).Should().Be(expected);

    [TestCase("WIP: feat", "feat")]
    [TestCase("WIP:feat", "feat")]
    [TestCase("[WIP] feat", "feat")]
    [TestCase("feat", "feat")]
    public void StripWipPrefix_RemovesLeadingPrefixOnly(string title, string expected) =>
        GiteaPlatformClient.StripWipPrefix(title).Should().Be(expected);

    // ───────────── Merge read-backs (P5 M1 correctness fix) ─────────────

    [Test]
    public async Task GetPullRequestAsync_MapsMergeCommitSha_AndPositiveMergeableOnly()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/7",
            HttpStatusCode.OK,
            """
            {"number":7,"title":"x","body":null,"state":"closed","merged":true,
              "merge_commit_sha":"abc123","mergeable":false,
              "draft":false,"html_url":"https://x","user":{"login":"a"},
              "head":{"ref":"feat"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var pr = (await client.GetPullRequestAsync("octo", "repo", "7"))
            .Should().BeOfType<PlatformResult<PullRequest>.Ok>().Subject.Value;

        pr.MergeCommitSha.Should().Be("abc123");
        // Gitea's mergeable=false is ambiguous (still checking OR conflict) —
        // it must NOT surface as a confirmed conflict.
        pr.Mergeable.Should().BeNull();
    }

    [Test]
    public async Task GetPullRequestAsync_SurfacesPositiveMergeable()
    {
        var (client, _, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/octo/repo/pulls/8",
            HttpStatusCode.OK,
            """
            {"number":8,"title":"x","body":null,"state":"open","merged":false,
              "mergeable":true,
              "draft":false,"html_url":"https://x","user":{"login":"a"},
              "head":{"ref":"feat"},"base":{"ref":"main"},
              "created_at":"2026-04-21T00:00:00Z","updated_at":"2026-04-21T00:00:00Z"}
            """);

        var pr = (await client.GetPullRequestAsync("octo", "repo", "8"))
            .Should().BeOfType<PlatformResult<PullRequest>.Ok>().Subject.Value;

        pr.Mergeable.Should().BeTrue();
    }
}

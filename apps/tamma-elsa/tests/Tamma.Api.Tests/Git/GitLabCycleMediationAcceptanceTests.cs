using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Git;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab;

namespace Tamma.Api.Tests.Git;

/// <summary>
/// Epic 31 P6 M2 — the MEDIATION-LEVEL acceptance for GitLab (the plan's
/// default P6 depth; full-stack GitLab E2E stays deferred per the §7
/// owner-decision default). The P5 Gitea E2E's git-surface scenario is
/// repeated here against a REAL <c>GitLabPlatformDriver</c> — built by the
/// production factory (version probe included) over a stateful in-memory
/// GitLab API v4 — driven through the REAL <see cref="GitMediationService"/>
/// in the single-issue cycle's order:
///
/// <para><b>branch → draft MR → line-anchored review comment on a
/// MULTI-COMMIT MR (diff_refs) → labels → un-draft → merge requesting
/// rebase (DG-4 fallback → squash) with degraded issue-close
/// (capability_unsupported) + branch delete.</b></para>
///
/// <para>What this pins beyond the driver unit tests: the mediation cores
/// and the GitLab driver agree end to end — the anchored comment is NOT
/// downgraded (diff_refs anchoring works, DG-2's alternative step stays
/// un-taken), the DG-4 fallback consumes exactly GitLab's typed
/// <c>merge_method_unsupported</c> rebase refusal, the merge activity's
/// fail-loud-on-missing-SHA gate passes on a squash merge
/// (<c>squash_commit_sha</c> mapped), and the degraded issue-close leaves
/// the merge SUCCESSFUL with warnings — never FAILED-by-capability.</para>
/// </summary>
[TestFixture]
public class GitLabCycleMediationAcceptanceTests
{
    private const string Repo = "group/widgets";
    private const string Branch = "tamma/issue-7-fix";
    private FakeGitLabServer _gitlab = null!;
    private GitMediationService _sut = null!;
    private RecordingEventRepository _events = null!;
    private ServiceProvider _services = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _gitlab = new FakeGitLabServer();
        _events = new RecordingEventRepository();

        // The REAL driver, built by the REAL registered factory — the GET
        // /version probe rides the fake server, so capability detection is
        // the production code path, not a hand-rolled capability set.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitLabPlatform();
        services.AddHttpClient("tamma-gitlab")
            .ConfigurePrimaryHttpMessageHandler(() => _gitlab);
        _services = services.BuildServiceProvider();

        var factory = _services.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitLab);
        var driver = await factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: _tenant,
                Kind: PlatformKind.GitLab,
                BaseUrl: "http://gitlab.fake",
                InstallationExternalId: null),
            "glpat-mediation-acceptance");

        driver.Capabilities.Should().Contain(PlatformCapability.PrLifecycle,
            "the fake reports 16.11.1-ee, above the 13.9 floor — precondition for the scenario");

        var authorizer = new Mock<IGitRepoAuthorizer>();
        authorizer
            .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitRepoAuthorization.Allow());
        var resolver = new Mock<IPlatformResolver>();
        resolver
            .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediationDriverResolution(driver, MediationCredentialSource.TenantInstallation));

        _sut = new GitMediationService(
            authorizer.Object, resolver.Object, _events,
            NullLogger<GitMediationService>.Instance);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_services is not null) await _services.DisposeAsync();
        _gitlab?.Dispose();
    }

    [Test, Order(1)]
    public async Task Step1_CreateBranch_FromMain()
    {
        var result = await _sut.CreateBranchAsync(_tenant, Repo, new Tamma.Api.Services.Git.CreateBranchRequest
        {
            BranchName = Branch,
            BaseRef = "main",
            IssueNumber = 7,
            CorrelationId = "p6-acceptance",
        });

        result.Success.Should().BeTrue(result.FailureReason);
        result.BranchRef.Should().Contain(Branch);
        _gitlab.Branches.Should().ContainKey(Branch);

        // The fake now advances the branch a second commit — the MR the next
        // step opens is a MULTI-COMMIT MR whose head differs from base AND
        // from the first commit (the diff_refs shape the old driver 400'd on).
        _gitlab.AdvanceBranch(Branch, "head-commit-2");
    }

    [Test, Order(2)]
    public async Task Step2_OpenDraftMr_WithLabelsAndResolvedReviewer()
    {
        var result = await _sut.CreatePullRequestAsync(_tenant, Repo, new CreatePrRequest
        {
            Title = "[ADL] #7: fix the widget",
            Body = "Fixes #7",
            HeadRef = Branch,
            BaseRef = "main",
            IsDraft = true,
            Labels = ["tamma-adl"],
            Reviewers = ["rev-bot"],
            CorrelationId = "p6-acceptance",
        });

        result.Success.Should().BeTrue(result.FailureReason);
        result.PrNumber.Should().Be(1);
        result.IsDraft.Should().BeTrue("the MR opened as a Draft: -titled draft");
        result.ReviewersSkipped.Should().BeNull(
            "rev-bot resolves through the in-driver username→id lookup (DG-3 not taken)");

        _gitlab.Mr!.Title.Should().StartWith("Draft: ", "GitLab drafts are the title prefix");
        _gitlab.Mr.Labels.Should().Contain("tamma-adl");
        _gitlab.Mr.ReviewerIds.Should().Equal(9L);
    }

    [Test, Order(3)]
    public async Task Step3_LineAnchoredReviewComment_OnTheMultiCommitMr_UsesDiffRefs()
    {
        var result = await _sut.ReviewCommentOnPullRequestAsync(_tenant, Repo, 1, new PrReviewCommentRequest
        {
            Body = "consider a guard clause here",
            Path = "src/widget.cs",
            Line = 10,
            CommitId = null, // mediation resolves the branch head; the driver replaces it with diff_refs
            CorrelationId = "p6-acceptance",
        });

        result.Success.Should().BeTrue(result.FailureReason);
        result.ReviewCommentDowngraded.Should().BeNull(
            "diff_refs anchoring works on the 2-commit MR — DG-2's downgrade step is NOT taken");
        result.CommentId.Should().Be(42);

        var position = _gitlab.CapturedDiscussionPosition!.Value;
        position.GetProperty("base_sha").GetString().Should().Be(FakeGitLabServer.BaseSha);
        position.GetProperty("start_sha").GetString().Should().Be(FakeGitLabServer.StartSha);
        position.GetProperty("head_sha").GetString().Should().Be("head-commit-2",
            "the position anchors on the MR's REAL diff_refs, not one caller SHA three times");
    }

    [Test, Order(4)]
    public async Task Step4_LabelRoundTrip_AddThenRemove()
    {
        var result = await _sut.UpdatePullRequestLabelsAsync(_tenant, Repo, 1, new PrLabelsRequest
        {
            AddLabels = ["needs-review"],
            RemoveLabels = ["tamma-adl"],
            CorrelationId = "p6-acceptance",
        });

        result.Success.Should().BeTrue(result.FailureReason);
        _gitlab.Mr!.Labels.Should().Contain("needs-review");
        _gitlab.Mr.Labels.Should().NotContain("tamma-adl");
    }

    [Test, Order(5)]
    public async Task Step5_UnDraft_StripsTheTitlePrefix()
    {
        var result = await _sut.SetPullRequestDraftAsync(_tenant, Repo, 1, new PrDraftRequest
        {
            Draft = false,
            CorrelationId = "p6-acceptance",
        });

        result.Success.Should().BeTrue(result.FailureReason);
        result.IsDraft.Should().BeFalse();
        _gitlab.Mr!.Title.Should().Be("[ADL] #7: fix the widget",
            "marking ready is the Draft: prefix strip");
    }

    [Test, Order(6)]
    public async Task Step6_Merge_RebaseRequested_FallsBackToSquash_DegradesIssueClose()
    {
        var result = await _sut.MergePullRequestAsync(_tenant, Repo, 1, new MergePrRequest
        {
            MergeStrategy = "rebase",
            IssueNumber = 7,
            BranchName = Branch,
            AutoDeleteBranch = true,
            CloseAssociatedIssue = true,
            CorrelationId = "p6-acceptance",
        });

        result.Success.Should().BeTrue(result.FailureReason);
        result.Merged.Should().BeTrue();
        result.MergeSha.Should().Be(FakeGitLabServer.SquashSha,
            "the squash merge's squash_commit_sha satisfies the fail-loud-on-missing-SHA gate");
        result.AppliedMergeStrategy.Should().Be("squash",
            "DG-4 consumed GitLab's typed merge_method_unsupported rebase refusal and fell back");
        result.Outcome.Should().Be("MergedWithWarnings",
            "the degraded issue-close (capability_unsupported on GitLab) is a warning, never a failure");
        result.IssueClosed.Should().BeFalse();
        result.BranchDeleted.Should().BeTrue();

        _gitlab.Mr!.State.Should().Be("merged");
        _gitlab.MergeWasSquash.Should().BeTrue();
        _gitlab.Branches.Should().NotContainKey(Branch, "post-merge cleanup deleted the branch");
    }

    [Test, Order(7)]
    public void Step7_AuditTrail_CarriesTheCycleAndTheFallback()
    {
        var types = _events.Appended.Select(e => e.Type).ToList();
        types.Should().Contain(GitEventTypes.BranchCreatedSuccess);
        types.Should().Contain(GitEventTypes.PrOpenedSuccess);
        types.Should().Contain(GitEventTypes.PrReviewCommentedSuccess);
        types.Should().Contain(GitEventTypes.PrLabelsUpdatedSuccess);
        types.Should().Contain(GitEventTypes.PrDraftSetSuccess);
        types.Should().Contain(GitEventTypes.PrMergedSuccess);
        types.Should().Contain(GitEventTypes.PrMergeMethodFallback,
            "§4.4 — every trip through an alternative step is on the audit record");
        types.Should().NotContain(GitEventTypes.PrReviewCommentDowngraded,
            "the anchored comment succeeded — no DG-2 downgrade event may exist");
    }

    // ====================================================================
    // A stateful in-memory GitLab API v4 — just enough surface for the
    // cycle's git verbs, faithful to the wire shapes the driver sends.
    // ====================================================================

    private sealed class FakeGitLabServer : HttpMessageHandler
    {
        public const string BaseSha = "base-sha-000";
        public const string StartSha = "start-sha-111";
        public const string SquashSha = "squash-sha-999";

        public ConcurrentDictionary<string, string> Branches { get; } = new()
        {
            ["main"] = BaseSha,
        };

        public MrState? Mr { get; private set; }
        public JsonElement? CapturedDiscussionPosition { get; private set; }
        public bool MergeWasSquash { get; private set; }

        public sealed class MrState
        {
            public long Iid { get; init; } = 1;
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
            public string SourceBranch { get; init; } = "";
            public string TargetBranch { get; init; } = "";
            public string State { get; set; } = "opened";
            public List<string> Labels { get; } = new();
            public List<long> ReviewerIds { get; } = new();
            public string? SquashCommitSha { get; set; }
        }

        public void AdvanceBranch(string name, string newSha) => Branches[name] = newSha;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; // /api/v4/...
            var query = request.RequestUri.Query;
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // ── version probe ──
            if (path == "/api/v4/version")
                return Json(HttpStatusCode.OK, """{"version":"16.11.1-ee","revision":"abc"}""");

            // ── users lookup (reviewer resolver) ──
            if (path == "/api/v4/users" && query.Contains("username=rev-bot", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """[{"id":9,"username":"rev-bot","name":"Reviewer Bot"}]""");
            if (path == "/api/v4/users")
                return Json(HttpStatusCode.OK, "[]");

            // ── branches ──
            const string branchesPrefix = "/api/v4/projects/group%2Fwidgets/repository/branches";
            if (path.StartsWith(branchesPrefix, StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    var name = Uri.UnescapeDataString(
                        System.Web.HttpUtility.ParseQueryString(query).Get("branch") ?? "");
                    var refSha = System.Web.HttpUtility.ParseQueryString(query).Get("ref") ?? "";
                    var sha = Branches.TryGetValue(refSha, out var fromBranch) ? fromBranch : refSha;
                    Branches[name] = sha;
                    return Json(HttpStatusCode.Created, BranchJson(name, sha));
                }

                var branchName = Uri.UnescapeDataString(path[(branchesPrefix.Length)..].TrimStart('/'));
                if (request.Method == HttpMethod.Get)
                {
                    return Branches.TryGetValue(branchName, out var sha)
                        ? Json(HttpStatusCode.OK, BranchJson(branchName, sha))
                        : Json(HttpStatusCode.NotFound, """{"message":"404 Branch Not Found"}""");
                }
                if (request.Method == HttpMethod.Delete)
                {
                    Branches.TryRemove(branchName, out _);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }

            // ── merge requests ──
            const string mrPrefix = "/api/v4/projects/group%2Fwidgets/merge_requests";
            if (path == mrPrefix && request.Method == HttpMethod.Get)
            {
                // List (idempotency lookup). One open MR max in this fake.
                return Mr is { State: "opened" }
                    ? Json(HttpStatusCode.OK, $"[{MrJson()}]")
                    : Json(HttpStatusCode.OK, "[]");
            }
            if (path == mrPrefix && request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                Mr = new MrState
                {
                    Title = doc.RootElement.GetProperty("title").GetString() ?? "",
                    Description = doc.RootElement.TryGetProperty("description", out var d)
                        ? d.GetString() ?? "" : "",
                    SourceBranch = doc.RootElement.GetProperty("source_branch").GetString() ?? "",
                    TargetBranch = doc.RootElement.GetProperty("target_branch").GetString() ?? "",
                };
                return Json(HttpStatusCode.Created, MrJson());
            }
            if (path == $"{mrPrefix}/1" && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, MrJson());
            if (path == $"{mrPrefix}/1" && request.Method == HttpMethod.Put)
            {
                ApplyMrUpdate(body);
                return Json(HttpStatusCode.OK, MrJson());
            }
            if (path == $"{mrPrefix}/1/discussions" && request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                CapturedDiscussionPosition = doc.RootElement.GetProperty("position").Clone();
                return Json(HttpStatusCode.Created,
                    """{"id":"d-1","notes":[{"id":42,"body":"posted","author":{"username":"bot"},"created_at":"2026-08-09T00:00:00Z"}]}""");
            }
            if (path == $"{mrPrefix}/1/merge" && request.Method == HttpMethod.Put)
            {
                using var doc = JsonDocument.Parse(body);
                MergeWasSquash = doc.RootElement.TryGetProperty("squash", out var s) && s.GetBoolean();
                Mr!.State = "merged";
                Mr.SquashCommitSha = MergeWasSquash ? SquashSha : "merge-sha-888";
                return Json(HttpStatusCode.OK, MrJson());
            }

            return Json(HttpStatusCode.NotImplemented,
                $$"""{"message":"fake gitlab has no route for {{request.Method}} {{path}}{{query}}"}""");
        }

        private void ApplyMrUpdate(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t)) Mr!.Title = t.GetString() ?? "";
            if (root.TryGetProperty("state_event", out var se))
            {
                Mr!.State = se.GetString() switch
                {
                    "close" => "closed",
                    "reopen" => "opened",
                    _ => Mr.State,
                };
            }
            if (root.TryGetProperty("add_labels", out var add))
            {
                foreach (var label in (add.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Mr!.Labels.Contains(label)) Mr.Labels.Add(label);
                }
            }
            if (root.TryGetProperty("remove_labels", out var remove))
            {
                foreach (var label in (remove.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    Mr!.Labels.Remove(label);
                }
            }
            if (root.TryGetProperty("reviewer_ids", out var ids))
            {
                Mr!.ReviewerIds.Clear();
                foreach (var id in ids.EnumerateArray()) Mr.ReviewerIds.Add(id.GetInt64());
            }
        }

        private string MrJson()
        {
            var mr = Mr!;
            var headSha = Branches.TryGetValue(mr.SourceBranch, out var s) ? s : "head-unknown";
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = 100,
                ["iid"] = mr.Iid,
                ["title"] = mr.Title,
                ["description"] = mr.Description,
                ["source_branch"] = mr.SourceBranch,
                ["target_branch"] = mr.TargetBranch,
                ["state"] = mr.State,
                ["labels"] = mr.Labels,
                ["merge_status"] = "can_be_merged",
                ["detailed_merge_status"] = "mergeable",
                ["merge_commit_sha"] = null,
                ["squash_commit_sha"] = mr.SquashCommitSha,
                ["web_url"] = $"http://gitlab.fake/group/widgets/-/merge_requests/{mr.Iid}",
                ["author"] = new Dictionary<string, object?> { ["username"] = "bot", ["id"] = 7 },
                ["created_at"] = "2026-08-09T00:00:00Z",
                ["updated_at"] = "2026-08-09T00:00:00Z",
                ["sha"] = headSha,
                ["diff_refs"] = new Dictionary<string, object?>
                {
                    ["base_sha"] = BaseSha,
                    ["start_sha"] = StartSha,
                    ["head_sha"] = headSha,
                },
            });
        }

        private static string BranchJson(string name, string sha) =>
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["protected"] = false,
                ["commit"] = new Dictionary<string, object?> { ["id"] = sha },
            });

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}

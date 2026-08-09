using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Platforms.IntegrationTests.Fixtures;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Epic 31 P5 M3 — the full-stack Gitea acceptance vehicle: real Gitea +
/// real Postgres (containers) + the REAL production Tamma.Api binary as a
/// host process in single-user mode, with ZERO GitHub configuration
/// anywhere in the deployment (see <see cref="GiteaFullStackFixture"/> for
/// the topology and the scrubbed-env guarantee).
///
/// <para><b>What this suite proves.</b> The single-issue cycle's entire
/// GIT SURFACE — exactly the governed <c>/api/v1/git/*</c> calls the
/// engine's thin activities make, in the cycle's order — completes against
/// Gitea end to end: capability probe → branch → DRAFT PR (WIP-prefix
/// draft) → line-anchored review comment → labels → un-draft →
/// squash-merge, with the PR observably MERGED in Gitea, the terminal
/// GIT.* DCB events in the control-plane store, the degraded-not-failed
/// guarantee on a capability Gitea lacks (issue lifecycle), and the P4
/// startup webhook registration leaving a live hook whose merged-PR
/// delivery crosses the container gateway back into the receiver.</para>
///
/// <para><b>What it does NOT yet prove</b> (recorded honestly, see the
/// explicitly-Ignored headline test at the bottom): the cycle driven by
/// the Tamma.ElsaServer engine process itself. The engine's LLM steps
/// (plan/review/tasks) run through <c>POST /api/v1/llm/call</c>, and the
/// codebase ships NO fake/echo LLM provider — a scripted no-LLM executor
/// exists only for the AGENT plane (LocalExecutor's process seam). Until
/// an LLM stub lands, a fully autonomous no-network-LLM cycle cannot
/// execute anywhere, including nightly CI.</para>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("Gitea")]
[Category("GiteaE2E")]
[Category("Nightly")]
public class GiteaFullStackE2ETests
{
    private GiteaFullStackFixture _stack = null!;
    private HttpClient _api = null!;
    private HttpClient _gitea = null!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Owner => _stack.Gitea.OwnerLogin;
    private string Repo => _stack.Gitea.RepoName;

    // Cross-test state — the cycle surface test is one linear scenario.
    private static int _prNumber;
    private static string _branch = string.Empty;
    private static string _correlation = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        DockerAvailability.RequireOrSkip();

        _stack = new GiteaFullStackFixture();
        await _stack.StartAsync(CancellationToken.None);
        if (!_stack.IsReady)
        {
            TestContext.Error.WriteLine(
                $"GiteaFullStackFixture did not reach ready state: {_stack.NotReadyReason}\n"
                + $"API log tail:\n{_stack.ApiLogTail()}");
        }
        else
        {
            _api = new HttpClient { BaseAddress = new Uri(_stack.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(60) };
            _gitea = new HttpClient { BaseAddress = new Uri(_stack.Gitea.BaseUrl) };
            _gitea.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("token", _stack.Gitea.BotToken);
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _api?.Dispose();
        _gitea?.Dispose();
        if (_stack is not null) await _stack.DisposeAsync();
    }

    private void RequireReady()
    {
        if (_stack.IsReady) return;
        if (DockerAvailability.RequireDocker)
        {
            Assert.Fail(
                $"PLATFORMS_REQUIRE_DOCKER=true but the full-stack fixture failed: {_stack.NotReadyReason}");
        }
        Assert.Inconclusive(
            $"GiteaFullStackFixture not ready ({_stack.NotReadyReason}) — see OneTimeSetUp output.");
    }

    // ================================================================
    // 1 — the deployment is Gitea-shaped, capability-honest, GitHub-free
    // ================================================================

    [Test, Order(1), Timeout(300_000)]
    public async Task CapabilityProbe_ReportsGitea_WithPrLifecycle()
    {
        RequireReady();

        using var resp = await _api.GetAsync($"/api/v1/git/{Owner}/{Repo}/capabilities");
        var body = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue($"capability probe answered: {body}");

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(body);
        doc.RootElement.GetProperty("platformKind").GetString().Should().Be("gitea",
            "the config-tier activation names Gitea and nothing else");
        var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetString()).ToList();
        caps.Should().Contain("PrLifecycle",
            "the fixture Gitea (1.21) is above the 1.14 lifecycle floor");
        caps.Should().Contain("PrFileReview");
    }

    [Test, Order(2), Timeout(300_000)]
    public async Task ZeroGitHubConfiguration_AnywhereInTheFixture()
    {
        RequireReady();

        // (a) the API child process received no GitHub-shaped env keys —
        //     the fixture scrubbed whatever the parent carried.
        // (b) the driver plane persisted nothing GitHub: no tenant platform
        //     installations at all (config-tier is in-memory by design) and
        //     the only github_installations row is the fixture's repo-GRANT
        //     row (AppId=0/InstallationId=0 — the guard registry, which is
        //     GitHub-named but carries no GitHub semantics here; recorded as
        //     an honest naming gap in the P5 report).
        await using var db = _stack.CreateDbContext();

        (await db.TenantPlatformInstallations.CountAsync()).Should().Be(0,
            "single-user config-tier activation persists no installation rows");

        var installs = await db.GitHubInstallations.ToListAsync();
        installs.Should().HaveCount(1);
        installs[0].AppId.Should().Be(0, "no GitHub App exists in this deployment");
        installs[0].InstallationId.Should().Be(0);
        installs[0].AccountLogin.Should().Be(Owner);
    }

    // ================================================================
    // 3 — the cycle's git surface, in the cycle's order, against Gitea
    // ================================================================

    [Test, Order(3), Timeout(300_000)]
    public async Task CycleGitSurface_SeededIssue_To_MergedPr_OnGitea()
    {
        RequireReady();

        _correlation = $"e2e-{Guid.NewGuid():N}";

        // ── seed one issue in Gitea (the work item) ──
        var issue = await PostGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/issues",
            new { title = "E2E: implement the thing", body = "seeded by GiteaFullStackE2ETests" });
        var issueNumber = issue.GetProperty("number").GetInt64();

        // ── cycle step: create the work branch (governed mediation) ──
        _branch = $"tamma/issue-{issueNumber}-{Guid.NewGuid():N}"[..30];
        var branchResult = await ApiPostAsync($"/api/v1/git/{Owner}/{Repo}/branches", new
        {
            branchName = _branch,
            baseRef = _stack.Gitea.DefaultBranch,
            issueNumber = (int)issueNumber,
            correlationId = _correlation,
        });
        branchResult.GetProperty("success").GetBoolean().Should().BeTrue(branchResult.ToString());

        // ── the agent's commit (raw git plane — outside mediation, as deployed) ──
        var file = $"src/e2e-{issueNumber}.txt";
        await PostGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/contents/{file}", new
        {
            branch = _branch,
            content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("e2e change\n")),
            message = $"feat: e2e change for #{issueNumber}",
        });

        // ── cycle step: open the DRAFT PR ──
        var prResult = await ApiPostAsync($"/api/v1/git/{Owner}/{Repo}/pull-requests", new
        {
            title = $"[ADL] #{issueNumber}: implement the thing",
            body = $"E2E PR for issue {issueNumber}",
            headRef = _branch,
            baseRef = _stack.Gitea.DefaultBranch,
            isDraft = true,
            correlationId = _correlation,
        });
        prResult.GetProperty("success").GetBoolean().Should().BeTrue(prResult.ToString());
        prResult.GetProperty("isDraft").GetBoolean().Should().BeTrue(
            "Gitea drafts ride the WIP title prefix — the driver must express draft for real");
        _prNumber = prResult.GetProperty("prNumber").GetInt32();
        _prNumber.Should().BeGreaterThan(0);

        // Confirm the platform's own view: the PR is a draft (WIP title).
        var giteaPr = await GetGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/pulls/{_prNumber}");
        giteaPr.GetProperty("title").GetString().Should().StartWith("WIP:");

        // ── cycle step: line-anchored review comment (DG-2's happy path —
        //    Gitea CAN anchor, so no downgrade fires) ──
        var reviewComment = await ApiPostAsync(
            $"/api/v1/git/{Owner}/{Repo}/pull-requests/{_prNumber}/review-comments", new
            {
                body = "e2e: anchored review note",
                path = file,
                line = 1,
                correlationId = _correlation,
            });
        reviewComment.GetProperty("success").GetBoolean().Should().BeTrue(reviewComment.ToString());

        // ── cycle step: PR labels (auto-created on the fresh repo) ──
        var labels = await ApiPutAsync(
            $"/api/v1/git/{Owner}/{Repo}/pull-requests/{_prNumber}/labels", new
            {
                addLabels = new[] { "tamma-processing" },
                correlationId = _correlation,
            });
        labels.GetProperty("success").GetBoolean().Should().BeTrue(labels.ToString());

        // ── cycle step: mark ready for review (the DG-1 edge, supported here) ──
        var draft = await ApiPutAsync(
            $"/api/v1/git/{Owner}/{Repo}/pull-requests/{_prNumber}/draft", new
            {
                draft = false,
                correlationId = _correlation,
            });
        draft.GetProperty("success").GetBoolean().Should().BeTrue(draft.ToString());
        draft.GetProperty("isDraft").GetBoolean().Should().BeFalse();

        // ── cycle step: MERGE (squash). closeAssociatedIssue=true on purpose:
        //    Gitea's driver does not carry IssueLifecycle, so the close is the
        //    DEGRADED-NOT-FAILED path — the merge STANDS with a warning
        //    (MergedWithWarnings), never a failure-by-capability.
        //    Gitea computes mergeability ASYNC after the un-draft title edit;
        //    a too-early merge answers 405 "Please try again later"
        //    (NOT_MERGEABLE on the wire) — retry until the checker settles
        //    (the deployed cycle reaches the merge minutes later via the
        //    human gate, so this race is a harness artifact, not a product
        //    path). ──
        JsonElement merge = default;
        var mergeDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            merge = await ApiPutJsonAsync(HttpMethod.Put,
                $"/api/v1/git/{Owner}/{Repo}/pull-requests/{_prNumber}/merge", new
                {
                    mergeStrategy = "squash",
                    issueNumber = (int)issueNumber,
                    branchName = _branch,
                    autoDeleteBranch = false,
                    closeAssociatedIssue = true,
                    correlationId = _correlation,
                });
            if (merge.GetProperty("success").GetBoolean()) break;
            var failureCode = merge.GetProperty("failureCode").GetString();
            if (failureCode != "NOT_MERGEABLE" || DateTimeOffset.UtcNow >= mergeDeadline)
            {
                break; // a real failure (or the checker never settled) — assert loud below
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        merge.GetProperty("success").GetBoolean().Should().BeTrue(merge.ToString());
        merge.GetProperty("merged").GetBoolean().Should().BeTrue();
        merge.GetProperty("mergeSha").GetString().Should().NotBeNullOrEmpty(
            "the P5 M1 merge_commit_sha read-back must surface the real SHA");
        merge.GetProperty("outcome").GetString().Should().Be("MergedWithWarnings",
            "issue-close is capability-unsupported on Gitea — the cycle DEGRADES (merge stands, "
            + "warning recorded), it never fails by capability");

        // ── the platform's own verdict: the PR is MERGED in Gitea ──
        var merged = await GetGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/pulls/{_prNumber}");
        merged.GetProperty("merged").GetBoolean().Should().BeTrue(
            "the epic's headline: the PR must be really merged on the Gitea side");
    }

    // ================================================================
    // 4 — audit trail: the terminal GIT.* DCB events are in the store
    // ================================================================

    [Test, Order(4), Timeout(300_000)]
    public async Task DcbAuditTrail_CarriesEveryTerminalGitEvent()
    {
        RequireReady();
        _prNumber.Should().BeGreaterThan(0, "runs after the cycle-surface test");

        // Single-user (null-tenant) GIT.* events land on platform_events
        // (Story 28-1 PR D moved domain_events off the control plane;
        // EventRepository routes null-tenant appends to IPlatformEventRepository).
        // Tags is jsonb — LIKE doesn't push down, so filter client-side over
        // the recent GIT.* rows.
        await using var db = _stack.CreateDbContext();
        var rows = await db.PlatformEvents.AsNoTracking()
            .Where(e => e.Type.StartsWith("GIT."))
            .OrderByDescending(e => e.CreatedAt)
            .Take(500)
            .Select(e => new { e.Type, e.Tags })
            .ToListAsync();
        var types = rows
            .Where(r => r.Tags.Contains(_correlation, StringComparison.Ordinal))
            .Select(r => r.Type)
            .ToList();

        types.Should().Contain("GIT.BRANCH_CREATED.SUCCESS");
        types.Should().Contain("GIT.PR_OPENED.SUCCESS");
        types.Should().Contain("GIT.PR_REVIEW_COMMENTED.SUCCESS");
        types.Should().Contain("GIT.PR_LABELS_UPDATED.SUCCESS");
        types.Should().Contain("GIT.PR_DRAFT_SET.SUCCESS");
        types.Should().Contain("GIT.PR_MERGED.SUCCESS");
    }

    // ================================================================
    // 5 — degraded, never failed: unsupported capability answers the
    //     typed, branchable refusal (the §4 wire surface)
    // ================================================================

    [Test, Order(5), Timeout(300_000)]
    public async Task UnsupportedCapability_AnswersTypedBranchableFailure_Never5xx()
    {
        RequireReady();

        // Issue-label update rides IssueLifecycle — not carried by the Gitea
        // driver. The mediation answer must be HTTP 200, success=false,
        // failureCode=capability_unsupported (exact code): the workflow's
        // check step / safety net branches on it; a 5xx would fail the cycle.
        using var resp = await _api.PatchAsync($"/api/v1/git/{Owner}/{Repo}/issues/1",
            JsonContent.Create(new
            {
                addLabels = new[] { "tamma-processing" },
                correlationId = $"e2e-degraded-{Guid.NewGuid():N}",
            }, options: Json));
        var body = await resp.Content.ReadAsStringAsync();

        ((int)resp.StatusCode).Should().Be(200, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse(body);
        doc.RootElement.GetProperty("failureCode").GetString().Should().Be("capability_unsupported",
            "§4.5 — the exact typed code is the branchable degradation surface");
    }

    // ================================================================
    // 6 — webhooks: startup registration left a live hook; the merge
    //     delivery crossed the container gateway into the receiver
    // ================================================================

    [Test, Order(6), Timeout(300_000)]
    public async Task WebhookRegistration_LeftLiveHook_AndMergeDeliveryReachedReceiver()
    {
        RequireReady();
        _prNumber.Should().BeGreaterThan(0, "runs after the cycle-surface test");

        // (a) the P4 startup validator registered a hook pointing at the
        //     container-resolvable callback URL.
        var hooks = await GetGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/hooks");
        var hookUrls = hooks.EnumerateArray()
            .Select(h => h.GetProperty("config").TryGetProperty("url", out var u) ? u.GetString() : null)
            .Where(u => u is not null)
            .ToList();
        hookUrls.Should().Contain(u => u!.StartsWith(_stack.PublicBaseUrl),
            $"startup webhook registration must leave a live hook on the repo "
            + $"(hooks found: [{string.Join(", ", hookUrls)}]; api log tail:\n{_stack.ApiLogTail(1500)})");

        // (b) the merge in the cycle-surface test made Gitea deliver at least
        //     one event through host.docker.internal into the 31-7 receiver —
        //     visible as a persisted platform delivery row.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var count = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = _stack.CreateDbContext();
            count = await db.PlatformWebhookDeliveries.CountAsync(d => d.PlatformKind == "gitea");
            if (count > 0) break;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        count.Should().BeGreaterThan(0,
            "the merged-PR webhook must cross the container gateway and be verified + persisted "
            + $"by the receiver (api log tail:\n{_stack.ApiLogTail(1500)})");
    }

    // ================================================================
    // 7 — the engine-driven headline (recorded gap)
    // ================================================================

    [Test, Order(7)]
    public void FullEngineCycle_SingleIssue_CompletesOnGitea()
    {
        Assert.Ignore(
            "Deferred pending an LLM stub: the single-issue-cycle workflow's plan/review/task "
            + "steps run through POST /api/v1/llm/call and the codebase ships no fake/echo LLM "
            + "provider — a scripted no-LLM seam exists only for the AGENT plane "
            + "(LocalExecutor's IProcessRunner/CLI protocol). Every git-plane leg the engine "
            + "would drive is proven above through the SAME governed routes the engine's "
            + "activities call (CycleGitSurface_SeededIssue_To_MergedPr_OnGitea + the webhook "
            + "receiver leg); the remaining delta is the Elsa host + LLM stub, tracked for the "
            + "P5 follow-up in docs/stories/epic-31/EXECUTION-PLAN.md §3 P5.");
    }

    // ── wire helpers ─────────────────────────────────────────────────

    private async Task<JsonElement> ApiPostAsync(string path, object body)
    {
        using var resp = await _api.PostAsJsonAsync(path, body, Json);
        var text = await resp.Content.ReadAsStringAsync();
        ((int)resp.StatusCode).Should().Be(200, $"POST {path} → {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> ApiPutAsync(string path, object body)
        => await ApiPutJsonAsync(HttpMethod.Put, path, body);

    private async Task<JsonElement> ApiPutJsonAsync(HttpMethod method, string path, object body)
    {
        using var req = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        using var resp = await _api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        ((int)resp.StatusCode).Should().Be(200, $"{method} {path} → {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> PostGiteaAsync(string path, object body)
    {
        using var resp = await _gitea.PostAsJsonAsync(path, body, Json);
        var text = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue($"POST {path} → {(int)resp.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> GetGiteaAsync(string path)
    {
        using var resp = await _gitea.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue($"GET {path} → {(int)resp.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}

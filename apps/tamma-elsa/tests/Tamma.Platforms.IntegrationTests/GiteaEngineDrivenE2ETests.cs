using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Platforms.IntegrationTests.Fixtures;

namespace Tamma.Platforms.IntegrationTests;

/// <summary>
/// Epic 31 P5 follow-up (2026-08-13) — the ENGINE-DRIVEN autonomous headline:
/// the previously-Ignored <c>FullEngineCycle_SingleIssue_CompletesOnGitea</c>,
/// now REAL. The <see cref="EngineFullStackFixture"/> joins the REAL
/// Tamma.ElsaServer engine binary to the P5 topology (Gitea + 2× Postgres +
/// the real Tamma.Api, zero GitHub configuration), configured with the
/// scripted LLM provider (no network LLM) and the scripted agent executor
/// (LocalExecutor's process seam). One issue is seeded in Gitea; the ACTUAL
/// AdlOrchestrator → SingleIssueCycle workflows drive it.
///
/// <para><b>What the harness plays, and why that is honest.</b> Four seams
/// are, BY DESIGN, external actors the deployed product also waits on — the
/// test drives each through its shipped public seam, never a shortcut:</para>
/// <list type="bullet">
///   <item><b>the document decider</b> (39-8): every document lifecycle
///   suspends on the accept gate until the orchestrator agent / a human
///   decides. No autonomous decider service ships in this deployment, so the
///   test accepts each request via the API's own
///   <c>POST /api/documents/decisions/{sessionId}/resume</c> — discovering
///   sessions from the cycle's own APPROVAL.REQUESTED audit events.</item>
///   <item><b>the CI system</b>: with <c>Testing:UseMock=true</c> TriggerCI
///   succeeds with a synthetic run id; the test resumes the suspended wait
///   through the engine's DG-5 seam (<c>/elsa/api/ci/waits</c>) with a green
///   result — the same seam the production CI completion poller drives.</item>
///   <item><b>the merge approver</b> (FR-19 human gate): the test posts the
///   <c>merge</c> decision on the engine's merge-approval resume seam once
///   the PR is un-drafted and mergeable — the gate is a HUMAN wait by design;
///   autonomy here would be a product change, not a test fidelity gap.</item>
///   <item><b>the production-deploy approver</b> (43-9 Seam E): at the
///   default autonomy dial the deploy-control effect gate routes the PROD
///   deploy to a human; the test approves it through the shipped
///   <c>/elsa/api/adl/deploy-approval/resume</c> seam after the merge —
///   same rationale as the merge approver.</item>
/// </list>
///
/// <para><b>What is then proven end-to-end with no human and no network
/// LLM:</b> work selected off the seeded label → context scans + PO summary →
/// plan produced/validated/panel-reviewed/accepted (typed documents through
/// the REAL validators) → tasks + test-spec accepted → branch + zero-diff
/// DRAFT PR in Gitea → scripted agent commits real code per task → CI-stub
/// leg green through the DG-5 seam → code review → un-draft → merge decision
/// → REAL squash-merge in Gitea → Gitea's merged-PR webhook crosses the
/// container gateway into the API receiver and resumes the cycle's
/// WaitForPRMerged bookmark → deployment pipeline (scripted deploys) →
/// CYCLE.COMPLETED. Zero GitHub configuration anywhere.</para>
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("Platforms")]
[Category("Gitea")]
[Category("GiteaE2E")]
[Category("Nightly")]
public class GiteaEngineDrivenE2ETests
{
    private EngineFullStackFixture _stack = null!;
    private HttpClient _api = null!;
    private HttpClient _gitea = null!;
    private HttpClient _engine = null!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Owner => _stack.Gitea.OwnerLogin;
    private string Repo => _stack.Gitea.RepoName;
    private string RepoSlug => $"{Owner}/{Repo}";

    /// <summary>The work-selection label the orchestrator is configured with.</summary>
    private const string AutoLabel = "tamma-auto";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        DockerAvailability.RequireOrSkip();

        _stack = new EngineFullStackFixture();
        await _stack.StartAsync(CancellationToken.None);
        if (!_stack.IsReady)
        {
            TestContext.Error.WriteLine(
                $"EngineFullStackFixture did not reach ready state: {_stack.NotReadyReason}\n"
                + $"API log tail:\n{_stack.ApiLogTail()}\n"
                + $"Engine log tail:\n{_stack.EngineLogTail()}");
            return;
        }

        _api = new HttpClient { BaseAddress = new Uri(_stack.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        _gitea = new HttpClient { BaseAddress = new Uri(_stack.Gitea.BaseUrl) };
        _gitea.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("token", _stack.Gitea.BotToken);
        _engine = new HttpClient { BaseAddress = new Uri(_stack.EngineBaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        _engine.DefaultRequestHeaders.Add(
            "Authorization", $"ApiKey {EngineFullStackFixture.ElsaAdminApiKey}");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _api?.Dispose();
        _gitea?.Dispose();
        _engine?.Dispose();
        if (_stack is not null) await _stack.DisposeAsync();
    }

    private void RequireReady()
    {
        if (_stack.IsReady) return;
        if (DockerAvailability.RequireDocker)
        {
            Assert.Fail(
                $"PLATFORMS_REQUIRE_DOCKER=true but the engine full-stack fixture failed: {_stack.NotReadyReason}");
        }
        Assert.Inconclusive(
            $"EngineFullStackFixture not ready ({_stack.NotReadyReason}) — see OneTimeSetUp output.");
    }

    // ================================================================
    // THE headline — the full engine-driven autonomous cycle
    // ================================================================

    [Test, Timeout(1_500_000)] // 25 min: container-warm topology + one full cycle
    public async Task FullEngineCycle_SingleIssue_CompletesOnGitea()
    {
        RequireReady();

        // ── 1. seed the work item: the auto label + one issue carrying it ──
        await PostGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/labels",
            new { name = AutoLabel, color = "#00aabb", description = "tamma work-selection label" });
        var labels = await GetGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/labels");
        var labelId = labels.EnumerateArray()
            .First(l => l.GetProperty("name").GetString() == AutoLabel)
            .GetProperty("id").GetInt64();

        var issue = await PostGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/issues", new
        {
            title = "E2E: implement the scripted feature",
            body = "Seeded by GiteaEngineDrivenE2ETests — the engine-driven autonomous headline.",
            labels = new[] { labelId },
        });
        var issueNumber = (int)issue.GetProperty("number").GetInt64();

        // ── 2. start the autonomous loop: dispatch the REAL adl-orchestrator ──
        // cooldownSeconds is set high ON PURPOSE: the loop's self-restart would
        // otherwise re-select the same still-open issue (Gitea cannot close
        // issues — IssueLifecycle is capability-degraded), and this test wants
        // exactly ONE deterministic cycle in its window.
        var dispatch = await _engine.PostAsJsonAsync(
            "/elsa/api/workflow-definitions/adl-orchestrator/dispatch", new
            {
                input = new Dictionary<string, object>
                {
                    ["repository"] = RepoSlug,
                    ["issueLabels"] = new[] { AutoLabel },
                    ["botAssignee"] = _stack.Gitea.OwnerLogin,
                    ["baseBranch"] = _stack.Gitea.DefaultBranch,
                    ["configJson"] = """{"cooldownSeconds": 3600, "maxIssuesPerRun": 1}""",
                },
            }, Json);
        var dispatchBody = await dispatch.Content.ReadAsStringAsync();
        dispatch.IsSuccessStatusCode.Should().BeTrue(
            $"adl-orchestrator dispatch answered {(int)dispatch.StatusCode}: {dispatchBody}\n"
            + $"engine log tail:\n{_stack.EngineLogTail(1500)}");

        // ── 3. the cycle must actually start (work selected + dispatched) ──
        await WaitForEventAsync("CYCLE.STARTED", TimeSpan.FromMinutes(4),
            "the orchestrator must select the seeded issue and dispatch the single-issue cycle");

        // ── 4. drive the three BY-DESIGN external seams until the PR merges ──
        var merged = await PumpUntilMergedAsync(issueNumber, TimeSpan.FromMinutes(14));
        merged.Should().BeTrue(
            "the cycle must reach a REAL merged PR in Gitea. "
            + $"api log tail:\n{_stack.ApiLogTail(2000)}\nengine log tail:\n{_stack.EngineLogTail(3000)}");

        // ── 5. the platform's own verdict ──
        var prNumber = await FindCyclePrNumberAsync(state: "closed");
        prNumber.Should().NotBeNull("the cycle's PR must exist in Gitea");
        var pr = await GetGiteaAsync($"/api/v1/repos/{Owner}/{Repo}/pulls/{prNumber}");
        pr.GetProperty("merged").GetBoolean().Should().BeTrue("the epic's headline: really merged on the Gitea side");
        pr.GetProperty("title").GetString().Should().NotStartWith("WIP:",
            "the cycle must have un-drafted the PR before the merge gate");

        // ── 6. the scripted agent's commits are REAL code on the merged tree ──
        using (var files = await GetGiteaRawAsync(
                   $"/api/v1/repos/{Owner}/{Repo}/pulls/{prNumber}/files?limit=50"))
        {
            var paths = files.RootElement.EnumerateArray()
                .Select(f => f.GetProperty("filename").GetString())
                .ToList();
            paths.Should().Contain(p => p!.StartsWith("src/scripted/"),
                "the TDD loop's scripted agent executor must have committed real files to the branch "
                + $"(files: [{string.Join(", ", paths)}])");
        }

        // ── 7. the cycle runs to COMPLETION (post-merge deployment included) ──
        // The FOURTH by-design external actor (2026-08-13, run 43): the
        // deploy-control effect gate (43-9 Seam E) routes the PRODUCTION deploy
        // to a human at the default autonomy dial — approve it through the
        // shipped resume seam (/elsa/api/adl/deploy-approval/resume), exactly
        // like the merge approver seat, then the scripted prod deploy runs and
        // the cycle emits its terminal.
        // Gitea's PR JSON spells it `merge_commit_sha` (run 45: the misspelled
        // read produced a "none" sha segment and the resume 404'd forever).
        var mergeShaForDeploy = pr.TryGetProperty("merge_commit_sha", out var msha)
            ? msha.GetString()
            : pr.TryGetProperty("merged_commit_sha", out var msha2) ? msha2.GetString() : null;
        var completed = await ApproveProdDeployAndAwaitCompletionAsync(
            issueNumber, mergeShaForDeploy, TimeSpan.FromMinutes(5));
        completed.Should().BeTrue(
            "after the merge webhook resumes WaitForPRMerged and the harness approves the "
            + "production-deploy gate, the deployment pipeline (scripted qa/uat/prod deploys) "
            + "must finish and the cycle must emit CYCLE.COMPLETED. "
            + $"api log tail:\n{_stack.ApiLogTail(1500)}\nengine log tail:\n{_stack.EngineLogTail(2000)}");

        // ── 8. audit trail: every leg left its durable event ──
        var types = await EventTypesAsync();

        // The git surface, engine-driven this time.
        types.Should().Contain("GIT.BRANCH_CREATED.SUCCESS");
        types.Should().Contain("GIT.PR_OPENED.SUCCESS");
        types.Should().Contain("GIT.PR_DRAFT_SET.SUCCESS", "the cycle un-drafts before the merge gate");
        types.Should().Contain("GIT.PR_MERGED.SUCCESS");

        // The webhook→engine resume leg (P4/DG-6), now cycle-attached.
        types.Should().Contain("CYCLE.PR_MERGE_WAIT.RESUMED",
            "the merged-PR webhook must cross the container gateway, be verified by the receiver, "
            + "and resume the engine's WaitForPRMerged bookmark");

        // The document plane: typed documents were produced AND accepted.
        types.Should().Contain("DOCUMENT.ACCEPTED",
            "the plan/tasks/test-spec lifecycles must reach accepted terminals");
        types.Should().Contain("APPROVAL.REQUESTED");
        types.Should().Contain("APPROVAL.PROVIDED");

        // The LLM plane ran managed + scripted (no network LLM anywhere).
        types.Should().Contain("AGENT.RUN.SUCCESS", "the managed llm-call path served the cycle");
        var scriptedRun = await AnyEventWithTagAsync("AGENT.RUN.SUCCESS", "\"provider\":\"scripted\"");
        scriptedRun.Should().BeTrue("the runs must have been served by the scripted provider");

        // The CI leg (stub trigger + DG-5 resume): the gate evaluated and passed.
        types.Should().Contain("GATE.EVALUATED.SUCCESS");
        types.Should().Contain("GATE.PASSED.SUCCESS",
            "the green DG-5 resume (build passed, coverage forwarded) must pass the quality gate");
    }

    // ================================================================
    // the pump — plays the three BY-DESIGN external actors
    // ================================================================

    private async Task<bool> PumpUntilMergedAsync(int issueNumber, TimeSpan budget)
    {
        var handledSessions = new HashSet<string>();
        var mergeDecisionPosted = false;
        var deadline = DateTimeOffset.UtcNow.Add(budget);

        while (DateTimeOffset.UtcNow < deadline)
        {
            // (a) accept every pending document decision (the orchestrator seat).
            foreach (var sessionId in await PendingDecisionSessionsAsync())
            {
                if (!handledSessions.Add(sessionId)) continue;
                using var resp = await _api.PostAsJsonAsync(
                    $"/api/documents/decisions/{sessionId}/resume",
                    new { kind = "accept", feedback = "e2e harness: accepted" }, Json);
                // 404 = already decided / gate burned — benign; anything else is
                // surfaced via the final assertion timing out.
                TestContext.Out.WriteLine(
                    $"[pump] decision accept session={sessionId} -> {(int)resp.StatusCode}");
            }

            // (b) resume every suspended CI wait with a green result (the CI seat).
            foreach (var wait in await ListCiWaitsAsync())
            {
                using var resp = await _engine.PostAsJsonAsync("/elsa/api/ci/waits/resume", new
                {
                    bookmarkId = wait.BookmarkId,
                    runId = wait.RunId,
                    status = "Succeeded",
                    buildPassed = true,
                    totalTests = 5,
                    passedTests = 5,
                    failedTests = 0,
                    coveragePercentage = 100.0,
                    lintWarnings = 0,
                    lintErrors = 0,
                }, Json);
                TestContext.Out.WriteLine(
                    $"[pump] ci resume bookmark={wait.BookmarkId} run={wait.RunId} -> {(int)resp.StatusCode}");
            }

            // (c) the human merge decision (the approver seat) — once the PR is
            // un-drafted AND Gitea's async mergeability checker has settled.
            var openPr = await FindCyclePrAsync(state: "open");
            if (!mergeDecisionPosted && openPr is { } p
                && p.TryGetProperty("title", out var t) && !(t.GetString() ?? "").StartsWith("WIP:")
                && p.TryGetProperty("mergeable", out var m) && m.GetBoolean())
            {
                var prNumber = (int)p.GetProperty("number").GetInt64();
                using var resp = await _engine.PostAsJsonAsync("/elsa/api/adl/merge-approval/resume", new
                {
                    issueNumber,
                    prNumber,
                    decision = "merge",
                    feedback = "e2e harness: approved",
                    approver = "e2e-harness",
                    tenantId = (string?)null,
                    repository = RepoSlug,
                }, Json);
                TestContext.Out.WriteLine(
                    $"[pump] merge decision pr={prNumber} -> {(int)resp.StatusCode}");
                // 404 = the gate is not suspended yet — retry next tick.
                if (resp.StatusCode != HttpStatusCode.NotFound) mergeDecisionPosted = true;
            }

            // Terminal check: the PR is merged in Gitea.
            var mergedPr = await FindCyclePrAsync(state: "closed");
            if (mergedPr is { } mp
                && mp.TryGetProperty("merged", out var mrgd) && mrgd.GetBoolean())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return false;
    }

    /// <summary>
    /// The production-deploy approver seat (2026-08-13, run 43): poll for
    /// CYCLE.COMPLETED while approving the deployment pipeline's prod-approval
    /// gate through its shipped resume seam once it suspends (404 = not
    /// suspended yet — retry next tick, the merge-approval convention).
    /// </summary>
    private async Task<bool> ApproveProdDeployAndAwaitCompletionAsync(
        int issueNumber, string? mergeSha, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow.Add(budget);
        var approved = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await EventTypesAsync()).Contains("CYCLE.COMPLETED")) return true;

            if (!approved)
            {
                using var resp = await _engine.PostAsJsonAsync(
                    "/elsa/api/adl/deploy-approval/resume", new
                    {
                        issueNumber,
                        decision = "approve",
                        feedback = "e2e harness: production deploy approved",
                        approver = "e2e-harness",
                        tenantId = (string?)null,
                        repository = RepoSlug,
                        mergeSha,
                    }, Json);
                TestContext.Out.WriteLine(
                    $"[pump] deploy approval issue={issueNumber} sha={mergeSha} -> {(int)resp.StatusCode}");
                if (resp.StatusCode != HttpStatusCode.NotFound) approved = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        return false;
    }

    /// <summary>Session ids of APPROVAL.REQUESTED audit rows (the cycle's own
    /// durable trace of suspended accept gates).</summary>
    private async Task<IReadOnlyList<string>> PendingDecisionSessionsAsync()
    {
        var rows = (await AllDurableEventsAsync())
            .Where(e => e.Type == "APPROVAL.REQUESTED")
            .Select(e => e.Tags)
            .ToList();

        var sessions = new List<string>();
        foreach (var tags in rows)
        {
            try
            {
                using var doc = JsonDocument.Parse(tags);
                if (doc.RootElement.TryGetProperty("sessionId", out var s)
                    && s.GetString() is { Length: > 0 } sid)
                {
                    sessions.Add(sid);
                }
            }
            catch (JsonException) { /* malformed tags row — skip */ }
        }
        return sessions;
    }

    private sealed record CiWait(string BookmarkId, string RunId);

    private async Task<IReadOnlyList<CiWait>> ListCiWaitsAsync()
    {
        using var resp = await _engine.GetAsync("/elsa/api/ci/waits");
        if (!resp.IsSuccessStatusCode) return Array.Empty<CiWait>();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("waits", out var waits)) return Array.Empty<CiWait>();
        return waits.EnumerateArray()
            .Select(w => new CiWait(
                w.GetProperty("bookmarkId").GetString() ?? "",
                w.GetProperty("runId").GetString() ?? ""))
            .Where(w => w.BookmarkId.Length > 0)
            .ToList();
    }

    private async Task<JsonElement?> FindCyclePrAsync(string state)
    {
        using var resp = await _gitea.GetAsync(
            $"/api/v1/repos/{Owner}/{Repo}/pulls?state={state}&limit=20");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var pr in doc.RootElement.EnumerateArray())
        {
            var head = pr.GetProperty("head").GetProperty("ref").GetString() ?? "";
            var title = pr.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            // 2026-08-13 (run 44): the cycle DELETES the head branch after a
            // successful merge, and Gitea then rewrites the closed PR's
            // head.ref to "refs/pull/{n}/head" — the branch-prefix match alone
            // never sees the merged PR and the pump loops past its own
            // terminal. The cycle's PR title prefix is the stable identity.
            if (head.StartsWith("adl/", StringComparison.Ordinal)
                || title.StartsWith("[ADL]", StringComparison.Ordinal)
                || title.StartsWith("WIP: [ADL]", StringComparison.Ordinal))
            {
                return pr.Clone();
            }
        }
        return null;
    }

    private async Task<int?> FindCyclePrNumberAsync(string state)
    {
        var pr = await FindCyclePrAsync(state);
        return pr is { } p ? (int)p.GetProperty("number").GetInt64() : null;
    }

    private async Task WaitForEventAsync(string type, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await AllDurableEventsAsync()).Any(e => e.Type == type))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        Assert.Fail(
            $"Event '{type}' did not appear within {timeout.TotalSeconds}s — {because}.\n"
            + $"api log tail:\n{_stack.ApiLogTail(2000)}\nengine log tail:\n{_stack.EngineLogTail(3000)}");
    }

    private async Task<IReadOnlyList<string>> EventTypesAsync()
        => (await AllDurableEventsAsync()).Select(e => e.Type).Distinct().ToList();

    /// <summary>
    /// Tag matching must survive BOTH renderings the two read paths produce
    /// (2026-08-20). PlatformEvents rows come back through EF as the compact
    /// string the writer serialized ({"provider":"scripted"}), but the raw
    /// tenant-schema reads cast a JSONB column to text, and Postgres's canonical
    /// jsonb rendering inserts a space after every colon
    /// ({"provider": "scripted"}) — so a compact-form Contains can NEVER match a
    /// tenant-schema row. That was invisible while the agent-run events landed in
    /// platform_events; the moment they landed tenant-bound in the tenant schema,
    /// this assertion went red against runs that WERE served by the scripted
    /// provider (verified by reading the live rows). Whitespace is stripped from
    /// both sides before matching; the fragments used here never carry meaningful
    /// spaces.
    /// </summary>
    private async Task<bool> AnyEventWithTagAsync(string type, string tagFragment)
    {
        var needle = tagFragment.Replace(" ", "", StringComparison.Ordinal);
        return (await AllDurableEventsAsync())
            .Any(e => e.Type == type
                      && e.Tags.Replace(" ", "", StringComparison.Ordinal)
                          .Contains(needle, StringComparison.Ordinal));
    }

    /// <summary>
    /// ALL durable audit rows the cycle writes, across BOTH planes:
    /// the control-plane <c>platform_events</c> table AND every tenant schema's
    /// <c>t_&lt;hex&gt;.domain_events</c> on the same central database. The
    /// single-user service-plane binding (EnsurePersonalTenantMiddleware, item
    /// 12 of the engine-DI finding) means the engine's mediated calls carry the
    /// sole user's PERSONAL tenant — so the cycle's DCB events land in that
    /// tenant's schema, not the platform plane, exactly as they do on a
    /// deployed single-user instance.
    /// </summary>
    private async Task<IReadOnlyList<(string Type, string Tags)>> AllDurableEventsAsync()
    {
        var events = new List<(string Type, string Tags)>();

        await using var db = _stack.Stack.CreateDbContext();
        events.AddRange((await db.PlatformEvents.AsNoTracking()
                .OrderBy(e => e.CreatedAt)
                .Select(e => new { e.Type, e.Tags })
                .ToListAsync())
            .Select(e => (e.Type, e.Tags)));

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        var schemas = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT nspname FROM pg_namespace WHERE nspname LIKE 't#_%' ESCAPE '#'";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schemas.Add(reader.GetString(0));
            }
        }

        foreach (var schema in schemas)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                // schema name comes from pg_namespace (t_<hex> minted by the
                // provisioner) — not attacker-controlled; quoted for safety.
                cmd.CommandText =
                    $"SELECT \"Type\", \"Tags\"::text FROM \"{schema}\".domain_events ORDER BY \"CreatedAt\"";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // The schema exists but its tables are still MID-PROVISION
                // (CreateSchema landed, EfTenantDbMigrator has not) — the
                // poll simply catches it on the next tick.
            }
        }

        return events;
    }

    // ── wire helpers ─────────────────────────────────────────────────

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

    private async Task<JsonDocument> GetGiteaRawAsync(string path)
    {
        using var resp = await _gitea.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue($"GET {path} → {(int)resp.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }
}

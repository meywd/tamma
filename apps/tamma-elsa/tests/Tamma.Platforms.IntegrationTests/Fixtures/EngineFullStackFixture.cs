using System.Diagnostics;
using System.Net.Sockets;

namespace Tamma.Platforms.IntegrationTests.Fixtures;

/// <summary>
/// Epic 31 P5 follow-up (2026-08-13) — the ENGINE-DRIVEN full-stack topology:
/// everything <see cref="GiteaFullStackFixture"/> deploys (Gitea + 2× Postgres
/// containers + the REAL Tamma.Api host process, zero GitHub configuration)
/// PLUS the REAL Tamma.ElsaServer engine binary as a second host process, so
/// the ACTUAL AdlOrchestrator/SingleIssueCycle workflows can drive one seeded
/// issue end to end with no network LLM and no real agent CLI:
///
/// <list type="bullet">
///   <item><b>Scripted LLM provider</b> — <c>Llm:EnableScriptedProvider=true</c>
///   on BOTH hosts; the engine's chain is pinned to it via
///   <c>Llm:DefaultProviderChain=[scripted]</c>, and the API serves the
///   deterministic in-process responses (commit 1/3 of this change).</item>
///   <item><b>Scripted agent executor</b> — <c>Agent:Local</c> points the
///   LocalExecutor's process seam at a python3 script this fixture writes: it
///   reads the exec-request file, makes ONE real commit to the work branch via
///   the Gitea API (empirically required — Gitea opens a zero-diff draft PR
///   but refuses to merge a PR with no commits), and writes a success result
///   artifact. The agent PLANE is real; only the agent BRAIN is scripted.</item>
///   <item><b>CI stub</b> — <c>Testing:UseMock=true</c>: TriggerCI succeeds
///   with a synthetic run id and the suspended CI wait is resumed by the test
///   through the engine's own DG-5 seam (<c>/elsa/api/ci/waits</c>), i.e. the
///   harness plays the CI system, not the workflow.</item>
///   <item><b>Engine⇄API wiring</b> — the engine's LLM/git/issue callbacks
///   ride <c>Tamma:ApiUrl</c>/<c>Engine:CallbackUrl</c>; the API's webhook →
///   engine resume hop rides <c>Elsa:ServerUrl</c> + the Elsa admin API key
///   (Gitea's merged-PR webhook crosses the container gateway into the API
///   receiver and resumes the cycle's <c>WaitForPRMerged</c> bookmark on the
///   engine — the same P4 leg the surface suite proved, now engine-attached).</item>
/// </list>
///
/// <para>The engine gets its own database (created on the app-DB container)
/// for the Elsa stores. NOTE: <c>ConnectionStrings:ControlPlane</c> is
/// deliberately NOT set anywhere — it is a SaaS signal that would (correctly)
/// make the scripted provider refuse to register.</para>
/// </summary>
public sealed class EngineFullStackFixture : IAsyncDisposable
{
    /// <summary>The Elsa admin API key — UseAdminApiKey()'s AdminApiKeyProvider
    /// accepts only the all-zero GUID (see docker/.env.example).</summary>
    public const string ElsaAdminApiKey = "00000000-0000-0000-0000-000000000000";

    public GiteaFullStackFixture Stack { get; } = new();

    public GiteaContainerFixture Gitea => Stack.Gitea;
    public string ApiBaseUrl => Stack.ApiBaseUrl;

    /// <summary>Host-side base URL of the running Tamma.ElsaServer.</summary>
    public string EngineBaseUrl { get; private set; } = string.Empty;

    public bool IsReady { get; private set; }
    public string NotReadyReason { get; private set; } = "StartAsync has not run";

    private Process? _engine;
    private string _engineLogPath = string.Empty;
    private string _agentScriptPath = string.Empty;
    private string _agentWorkDir = string.Empty;

    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            // ── ports first: the API needs the engine URL (webhook→resume
            //    hop) and the engine needs the API URL (mediation planes) ──
            var enginePort = FreeTcpPort();
            EngineBaseUrl = $"http://localhost:{enginePort}";

            Stack.ExtraApiEnvironment["Llm__EnableScriptedProvider"] = "true";
            Stack.ExtraApiEnvironment["Elsa__ServerUrl"] = EngineBaseUrl;
            Stack.ExtraApiEnvironment["Elsa__ApiKey"] = ElsaAdminApiKey;

            // Single-user principal: the deployed instance has an owner user
            // from its setup flow; the fixture pins one explicitly so
            // SoleUserProvider does not fail every autonomy/document decision
            // with GOVERNANCE.PRINCIPAL.NO_SOLE_USER (observed run 10).
            Stack.ExtraApiEnvironment["Tamma__SingleUser__OwnerUserId"] =
                "aaaaaaaa-0000-0000-0000-00000000e2ee";

            // The API's per-IP limiter throttles its OWN engine (one
            // agent-config resolve per llm-call; a 7-role review panel blows
            // through ConfigRead=100/min and the engine churns retries into
            // 429s — observed run 21: 1452 rejects, 4× call amplification).
            // The E2E raises the limits the way a single-box deployment would.
            Stack.ExtraApiEnvironment["RateLimits__ConfigRead"] = "100000";
            Stack.ExtraApiEnvironment["RateLimits__ProviderExecute"] = "100000";

            await Stack.StartAsync(ct).ConfigureAwait(false);
            if (!Stack.IsReady)
            {
                NotReadyReason = $"API stack not ready: {Stack.NotReadyReason}";
                return;
            }

            // ── the single-user OWNER row (the deployed instance has one from
            //    its registration/setup flow). With the row + the pinned
            //    Tamma:SingleUser:OwnerUserId above, the API's single-user
            //    service-plane binding (EnsurePersonalTenantMiddleware) mints
            //    and binds the personal tenant on the engine's first mediated
            //    call — the ambient-tenant home every tenant-resident read
            //    (acceptance rules, document instances) resolves against. ──
            await using (var db = Stack.CreateDbContext())
            {
                db.Users.Add(new Tamma.Data.Entities.User
                {
                    Id = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000e2ee"),
                    Email = "owner@e2e.local",
                    DisplayName = "E2E Owner",
                    Role = "owner",
                    EmailVerified = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            // ── the engine SHARES the control-plane database (the deployed
            //    compose shape: elsa-server's DefaultConnection is the same
            //    "tamma" database the API migrates) — the Elsa stores migrate
            //    their own tables alongside, and the engine's CP-polling
            //    triggers (TenantCleanup/TenantDelete) find platform_events
            //    instead of erroring every tick against a bare Elsa DB. The
            //    API has already migrated this database (it started first). ──
            var engineDb = Stack.ControlPlaneDb.GetConnectionString();

            // ── the scripted agent executor script (the LocalExecutor CLI
            //    protocol's other side): one REAL commit per task session ──
            WriteAgentScript();

            // ── launch the real engine binary ──
            var engineDll = LocateDll("Tamma.ElsaServer");
            _engineLogPath = Path.Combine(Path.GetTempPath(), $"tamma-e2e-engine-{enginePort}.log");

            var psi = new ProcessStartInfo("dotnet", $"\"{engineDll}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(engineDll)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            // Zero GitHub configuration on the ENGINE too (AgentExecutorFactory
            // auto-detects GitHubActions off GitHub:AppId — scrub it away).
            foreach (var key in psi.Environment.Keys
                         .Where(k => k.StartsWith("GITHUB", StringComparison.OrdinalIgnoreCase)
                                     || k.StartsWith("GitHub__", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                psi.Environment.Remove(key);
            }

            // Production, DELIBERATELY (matching the deployed engine): under
            // Development, DI ValidateOnBuild refuses to boot the engine on a
            // pre-existing registration defect — HourlyAnalyticsRollupScheduler
            // (singleton hosted service) consumes the scoped IWorkflowDispatcher.
            // The deployed engine never sees it (no ValidateOnBuild outside
            // Development). Recorded in .dev/findings/ (engine-driven E2E
            // follow-up, 2026-08-13).
            //
            // Documents:ReEntryDisabled is deliberately NOT set: the engine now
            // defaults to HttpLifecycleReEntryService (latest-accepted read over
            // the API), which the plan-review shim REQUIRES — with the Null seam
            // the shim can never see the accepted plan and every cycle terminates
            // needs-human (run 29's root cause).
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            psi.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{enginePort}";
            psi.Environment["ConnectionStrings__DefaultConnection"] = engineDb;
            psi.Environment["Elsa__Identity__SigningKey"] =
                "sufficiently-long-secret-signing-key-for-the-e2e-engine";
            psi.Environment["Elsa__Server__BaseUrl"] = EngineBaseUrl;
            psi.Environment["OpenSearch__Enabled"] = "false";

            // Engine → API planes (LLM mediation, issues/git callbacks, events drain).
            psi.Environment["Tamma__ApiUrl"] = Stack.ApiBaseUrl;
            psi.Environment["Tamma__ApiToken"] = "e2e-engine-token"; // dev-permissive API auth
            psi.Environment["Engine__CallbackUrl"] = Stack.ApiBaseUrl;

            // Single-user / dev mode: no SaaS signal, deployment mode "dev"
            // (no prod-approval human gate in the deployment pipeline).
            psi.Environment["Tamma__Mode"] = "single-user";

            // The scripted LLM provider: enabled + selected as THE chain, and
            // ALLOW-LISTED. All three are required: enablement registers it,
            // the chain selects it, and the egress allowlist admits it — the
            // tool-loop rejects any provider outside the allowlist and falls
            // back to the platform default (which, in this fixture, is a real
            // vendor with no credentials, so every call returned empty).
            psi.Environment["Llm__EnableScriptedProvider"] = "true";
            psi.Environment["Llm__DefaultProviderChain__0"] = "scripted";
            psi.Environment["Security__ProviderAllowlist__AdditionalProviders__0"] = "scripted";

            // CI stub: TriggerCI succeeds with a synthetic run id; the test
            // resumes the suspended wait through /elsa/api/ci/waits.
            psi.Environment["Testing__UseMock"] = "true";

            // The scripted agent executor (LocalExecutor process seam).
            psi.Environment["Agent__Local__NodeExecutable"] = "python3";
            psi.Environment["Agent__Local__CliEntryPoint"] = _agentScriptPath;
            psi.Environment["Agent__Local__WorkingDirectory"] = _agentWorkDir;
            psi.Environment["Agent__Local__CleanupAfterRun"] = "false"; // keep artifacts for debugging

            _engine = new Process { StartInfo = psi };
            var log = new StreamWriter(_engineLogPath, append: false) { AutoFlush = true };
            _engine.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.WriteLine(e.Data); } };
            _engine.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.WriteLine("[err] " + e.Data); } };
            _engine.Start();
            _engine.BeginOutputReadLine();
            _engine.BeginErrorReadLine();

            await WaitForEngineHealthyAsync(TimeSpan.FromMinutes(4), ct).ConfigureAwait(false);

            // /health goes green BEFORE Elsa's workflow registry has
            // materialized the CLR workflow definitions — a dispatch in that
            // window is queued and then fails "Workflow graph not found"
            // (observed on the first run of this fixture). Wait until the
            // definition the test dispatches actually resolves.
            await WaitForWorkflowDefinitionAsync(
                "adl-orchestrator", TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);

            IsReady = true;
            NotReadyReason = string.Empty;
        }
        catch (Exception ex)
        {
            NotReadyReason = $"{ex.GetType().Name}: {ex.Message}";
            IsReady = false;
        }
    }

    public string EngineLogTail(int maxChars = 4000)
    {
        try
        {
            if (!File.Exists(_engineLogPath)) return "(no engine log)";
            var text = File.ReadAllText(_engineLogPath);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch (Exception ex)
        {
            return $"(engine log unreadable: {ex.Message})";
        }
    }

    public string ApiLogTail(int maxChars = 4000) => Stack.ApiLogTail(maxChars);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_engine is { HasExited: false })
            {
                _engine.Kill(entireProcessTree: true);
                _engine.WaitForExit(10_000);
            }
            _engine?.Dispose();
        }
        catch { /* best effort */ }

        await Stack.DisposeAsync().ConfigureAwait(false);
    }

    // ── internals ─────────────────────────────────────────────────────

    /// <summary>
    /// The scripted no-LLM agent: reads the LocalExecutor exec-request file,
    /// commits one deterministic file to the work branch via the Gitea
    /// contents API (unique path per session, so the two-task TDD loop makes
    /// two clean commits), and writes the success result artifact. Empirically
    /// load-bearing: Gitea opens a zero-diff draft PR but refuses to merge a
    /// PR whose branch carries no commits, and the engine-driven cycle opens
    /// its PR BEFORE the TDD loop runs.
    /// </summary>
    private void WriteAgentScript()
    {
        _agentWorkDir = Path.Combine(Path.GetTempPath(), $"tamma-e2e-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_agentWorkDir);
        _agentScriptPath = Path.Combine(_agentWorkDir, "scripted-agent.py");

        var script = $$"""
import base64, json, sys, time, urllib.request

GITEA = {{ToPyString(Gitea.BaseUrl)}}
TOKEN = {{ToPyString(Gitea.BotToken)}}

def main() -> int:
    args = sys.argv[1:]
    req_path = args[args.index("--request") + 1]
    out_path = args[args.index("--output") + 1]
    with open(req_path, encoding="utf-8") as f:
        req = json.load(f)

    repository = req["repository"]          # owner/repo
    branch = req["branch_name"]
    session = req.get("tamma_session_id") or f"s{int(time.time())}"

    file_path = f"src/scripted/{session}.txt"
    body = json.dumps({
        "branch": branch,
        "content": base64.b64encode(
            f"scripted agent change for {session}\n".encode()).decode(),
        "message": f"feat: scripted agent change ({session})",
    }).encode()

    http = urllib.request.Request(
        f"{GITEA}/api/v1/repos/{repository}/contents/{file_path}",
        data=body, method="POST",
        headers={"Content-Type": "application/json",
                 "Authorization": f"token {TOKEN}"})
    with urllib.request.urlopen(http, timeout=30) as resp:
        created = json.load(resp)
    sha = created.get("commit", {}).get("sha", "") or ""

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump({
            "success": True,
            "commit_sha": sha,
            "files_changed": [file_path],
            "tokens_used": 0,
            "duration_seconds": 1,
            "agent_provider": "scripted",
            "agent_version": "e2e-1",
            "agent_log_summary": f"scripted agent committed {file_path} to {branch}",
        }, f)
    return 0

if __name__ == "__main__":
    sys.exit(main())
""";
        File.WriteAllText(_agentScriptPath, script);
    }

    private static string ToPyString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private async Task WaitForEngineHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_engine is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"Tamma.ElsaServer exited during startup (code {_engine.ExitCode}). Log tail:\n{EngineLogTail()}");
            }
            try
            {
                using var resp = await http.GetAsync($"{EngineBaseUrl}/health", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
                last = new Exception($"/health answered HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Tamma.ElsaServer did not become healthy within {timeout.TotalSeconds}s. Log tail:\n{EngineLogTail()}",
            last);
    }

    private async Task WaitForWorkflowDefinitionAsync(
        string definitionId, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("Authorization", $"ApiKey {ElsaAdminApiKey}");
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var url = $"{EngineBaseUrl}/elsa/api/workflow-definitions/by-definition-id/{definitionId}";
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
                last = new Exception($"{url} answered HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Workflow definition '{definitionId}' did not materialize within {timeout.TotalSeconds}s. "
            + $"Engine log tail:\n{EngineLogTail()}", last);
    }

    private static int FreeTcpPort()
    {
        var listener = TcpListener.Create(0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Locate a built src project dll next to this repo checkout —
    /// same pattern as <see cref="GiteaFullStackFixture"/>.</summary>
    private static string LocateDll(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tamma.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "could not locate Tamma.sln above the test bin directory");
        }

        var configs = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? new[] { "Release", "Debug" }
            : new[] { "Debug", "Release" };
        foreach (var config in configs)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", project, "bin", config, "net8.0", $"{project}.dll");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            $"{project}.dll not found under {dir.FullName}/src/{project}/bin — build the solution first "
            + "(the E2E vehicle runs the real binaries)");
    }
}

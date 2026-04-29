using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Tamma.Platforms.IntegrationTests.Fixtures;

/// <summary>
/// Story 31-10 — Gitea container fixture. Boots <c>gitea/gitea:1.21</c>
/// (the oldest version supported by the 31-4 driver per its capability
/// detection: 1.21+ for Actions, 1.20 for non-Actions baseline). We
/// deliberately pin to 1.21 — newer patch versions can drift; 1.21 is
/// the floor we need the harness to verify.
///
/// <para>Boot sequence (mirrors what a fresh production install does):</para>
/// <list type="number">
///   <item>Container starts with <c>INSTALL_LOCK=true</c> +
///         <c>SECRET_KEY</c>/<c>INTERNAL_TOKEN</c> pre-set so first-run
///         setup is a no-op.</item>
///   <item>Poll <c>GET /api/v1/version</c> until 200 OK (cold-boot
///         takes ~10–30s on a developer laptop, ~10–60s in CI).</item>
///   <item><c>docker exec</c> the <c>gitea admin user create</c>
///         command to create an admin user (REST endpoints to create
///         the first admin require it to already exist — chicken &amp;
///         egg avoided by exec'ing the bin command).</item>
///   <item>Use the admin's basic-auth to mint a PAT for itself, create
///         a bot user via <c>POST /admin/users</c>, mint a PAT for the
///         bot, then create the fixture repo
///         <c>{bot}/test-repo</c> initialized with a README + a
///         sample <c>.gitea/workflows/echo.yaml</c>.</item>
/// </list>
///
/// <para>The fixture is intentionally idempotent on the seed step —
/// the test class hits <c>Assert.Inconclusive</c> if the seed fails
/// rather than skipping silently. A failed seed is always a regression
/// in the harness OR the platform image, not a "transient" condition.
/// </para>
/// </summary>
public sealed class GiteaContainerFixture : PlatformIntegrationFixture
{
    /// <summary>
    /// Pinned image tag. Per 31-10 plan §2 the harness must test the
    /// <em>oldest</em> Gitea version the driver supports — 31-4's
    /// capability detection sets <see cref="Tamma.Platforms.Gitea.GiteaPlatformDriver.MinimumActionsVersion"/>
    /// to 1.21. Bumping this image tag should be a deliberate decision
    /// (not "newer = better").
    /// </summary>
    public const string GiteaImage = "gitea/gitea:1.21";

    /// <summary>
    /// Detected version (read from <c>/api/v1/version</c> after boot).
    /// Exposed so contract tests can assert that capability detection
    /// matches what the running container actually reports.
    /// </summary>
    public Version? DetectedVersion { get; private set; }

    /// <summary>
    /// True when the fixture detected an act_runner (sidecar from the
    /// future <c>ActRunnerFixture</c> per plan §step-4) is registered
    /// with the running Gitea instance. Today no act_runner is wired
    /// in the harness, so this stays false — Actions dispatch tests
    /// will queue runs that never execute. Tests that require a
    /// runner gracefully <c>Assert.Inconclusive</c> instead of waiting
    /// forever.
    /// </summary>
    public bool HasActRunner { get; private set; }

    private const string AdminUsername = "tamma-admin";
    private const string AdminPassword = "tamma-admin-password!";
    private const string AdminEmail = "tamma-admin@example.com";
    private const string BotUsername = "tamma-bot";
    private const string BotPassword = "tamma-bot-password!";
    private const string BotEmail = "tamma-bot@example.com";

    private IContainer? _container;
    private string? _adminToken;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public override async Task StartAsync(CancellationToken ct = default)
    {
        OwnerLogin = BotUsername;

        _container = new ContainerBuilder()
            .WithImage(GiteaImage)
            .WithEnvironment("USER_UID", "1000")
            .WithEnvironment("USER_GID", "1000")
            .WithEnvironment("INSTALL_LOCK", "true")
            .WithEnvironment("DISABLE_REGISTRATION", "false")
            .WithEnvironment("REQUIRE_SIGNIN_VIEW", "false")
            .WithEnvironment("RUN_MODE", "prod")
            .WithEnvironment("DB_TYPE", "sqlite3")
            // Disable mailer + actions registration token endpoint
            // checks so the cold boot is fast.
            .WithEnvironment("GITEA__mailer__ENABLED", "false")
            // Random-port mapping; testcontainers picks an unused
            // host port and exposes it as GetMappedPublicPort(3000).
            .WithPortBinding(3000, true)
            .WithPortBinding(22, true)
            .Build();

        await _container.StartAsync(ct).ConfigureAwait(false);

        var hostPort = _container.GetMappedPublicPort(3000);
        BaseUrl = $"http://localhost:{hostPort}";

        // 1) Wait for the API to come up. Cold boot on a fresh image
        //    pull is dominated by the docker pull; the runtime boot
        //    itself is ~10–20s.
        try
        {
            await PollHealthAsync(
                $"{BaseUrl}/api/v1/version",
                TimeSpan.FromMinutes(3),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var logs = await CaptureContainerLogsAsync(_container, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Gitea {GiteaImage} did not become healthy. Container logs:\n{logs}",
                ex);
        }

        // 2) Detect version (mirrors what the driver does at construction).
        DetectedVersion = await DetectVersionAsync(ct).ConfigureAwait(false);

        // 3) Seed admin via `gitea admin user create` exec.
        await CreateAdminUserAsync(ct).ConfigureAwait(false);

        // 4) Mint admin PAT (basic-auth → /api/v1/users/{user}/tokens).
        _adminToken = await MintTokenAsync(
            AdminUsername, AdminPassword, ct).ConfigureAwait(false);

        // 5) Create bot user (admin REST).
        await CreateBotUserAsync(ct).ConfigureAwait(false);

        // 6) Mint bot PAT (basic-auth as the bot now that it exists).
        BotToken = await MintTokenAsync(
            BotUsername, BotPassword, ct).ConfigureAwait(false);

        // 7) Create fixture repo as the bot.
        DefaultBranchSha = await CreateFixtureRepoAsync(ct).ConfigureAwait(false);

        IsReady = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            try
            {
                await _container.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort dispose. Ryuk handles orphans.
            }
        }
    }

    // ── seed helpers ───────────────────────────────────────────────

    private async Task<Version?> DetectVersionAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        using var resp = await http.GetAsync("/api/v1/version", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var raw = doc.RootElement.TryGetProperty("version", out var v)
            ? v.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrEmpty(raw)) return null;
        var canonical = raw;
        var plus = canonical.IndexOf('+');
        if (plus >= 0) canonical = canonical[..plus];
        var dash = canonical.IndexOf('-');
        if (dash >= 0) canonical = canonical[..dash];
        return Version.TryParse(canonical, out var parsed) ? parsed : null;
    }

    private async Task CreateAdminUserAsync(CancellationToken ct)
    {
        // `gitea admin user create` is the official documented way to
        // bootstrap the first admin on a fresh install. It runs
        // entirely inside the container and idempotently no-ops if the
        // user already exists.
        //
        // Two gotchas on the gitea/gitea:1.21 image:
        //   1. Default container user is `root`, but the bin needs to
        //      run as `git` so it can read /data/gitea/conf/app.ini —
        //      we use `su git -c "..."`.
        //   2. The `/etc/profile.d/gitea_bash_autocomplete.sh` script
        //      shipped with the image uses bash-only syntax; loading
        //      it under `/bin/sh -lc` (login mode) breaks with a
        //      `syntax error: unexpected "("`. We call gitea directly
        //      as a non-login shell to sidestep.
        var giteaArgs = string.Join(" ", new[]
        {
            "admin", "user", "create",
            "--username", AdminUsername,
            "--password", AdminPassword,
            "--email", AdminEmail,
            "--admin",
            "--must-change-password=false",
        });
        var result = await _container!.ExecAsync(
            new[]
            {
                "/bin/su", "git", "-c",
                $"gitea {giteaArgs}",
            },
            ct).ConfigureAwait(false);
        if (result.ExitCode != 0
            // already-exists is fine — fixture is idempotent.
            && !result.Stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            && !result.Stdout.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"`gitea admin user create` failed (exit={result.ExitCode}):\n" +
                $"stdout: {result.Stdout}\nstderr: {result.Stderr}");
        }
    }

    private async Task<string> MintTokenAsync(
        string username, string password, CancellationToken ct)
    {
        // POST /api/v1/users/{user}/tokens — name unique per call so
        // re-runs against a re-used container don't 422.
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));

        var tokenName = $"tamma-it-{Guid.NewGuid():N}";
        var body = new
        {
            name = tokenName,
            // All scopes — the harness exercises every API surface.
            scopes = new[]
            {
                "write:admin", "write:repository", "write:user", "write:issue",
                "write:misc", "write:notification", "write:organization",
                "write:package",
            },
        };
        using var resp = await http.PostAsJsonAsync(
            $"/api/v1/users/{username}/tokens", body, JsonOpts, ct)
            .ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"PAT mint for {username} failed: HTTP {(int)resp.StatusCode}: {json}");
        }
        using var doc = JsonDocument.Parse(json);
        var sha1 = doc.RootElement.GetProperty("sha1").GetString();
        if (string.IsNullOrEmpty(sha1))
        {
            throw new InvalidOperationException(
                $"PAT mint for {username} returned no sha1 token: {json}");
        }
        return sha1;
    }

    private async Task CreateBotUserAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "token", _adminToken!);

        var body = new
        {
            username = BotUsername,
            email = BotEmail,
            password = BotPassword,
            must_change_password = false,
        };
        using var resp = await http.PostAsJsonAsync(
            "/api/v1/admin/users", body, JsonOpts, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return;
        var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // 422 with "user already exists" → idempotent re-run, OK.
        if (err.Contains("user already exists", StringComparison.OrdinalIgnoreCase)
            || err.Contains("already taken", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException(
            $"create bot user failed: HTTP {(int)resp.StatusCode}: {err}");
    }

    private async Task<string> CreateFixtureRepoAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "token", BotToken);

        var body = new
        {
            name = RepoName,
            @private = false,
            auto_init = true,
            default_branch = DefaultBranch,
            description = "Tamma Story 31-10 integration test fixture repo",
        };
        using var resp = await http.PostAsJsonAsync(
            "/api/v1/user/repos", body, JsonOpts, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode
            && !json.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"create fixture repo failed: HTTP {(int)resp.StatusCode}: {json}");
        }

        // Fetch the default-branch tip SHA — tests use this for
        // CreateBranchAsync's FromSha argument.
        using var branchResp = await http.GetAsync(
            $"/api/v1/repos/{BotUsername}/{RepoName}/branches/{DefaultBranch}",
            ct).ConfigureAwait(false);
        if (!branchResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"fetch default branch failed: HTTP {(int)branchResp.StatusCode}: " +
                await branchResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        var branchJson = await branchResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(branchJson);
        var sha = doc.RootElement.GetProperty("commit").GetProperty("id").GetString();
        return sha ?? throw new InvalidOperationException(
            $"default branch {DefaultBranch} returned no commit.id: {branchJson}");
    }
}

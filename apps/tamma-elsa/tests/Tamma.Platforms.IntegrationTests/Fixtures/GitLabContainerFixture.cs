using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Tamma.Platforms.IntegrationTests.Fixtures;

/// <summary>
/// Epic 31 P6 M2 — GitLab CE container fixture (the Story 31-10 stub made
/// real). Boots a pinned <c>gitlab/gitlab-ce</c>, seeds a root PAT + a
/// reviewer user via <c>gitlab-rails runner</c>, and creates the fixture
/// project over REST.
///
/// <para>Boot economics (why this is a NIGHTLY fixture): the image is
/// ~3&#160;GB and omnibus first-boot reconfigure takes 3–6 minutes even on a
/// beefy runner — the readiness poll allows 12. The omnibus config trims
/// what it can: single-process Puma, no Prometheus, no KAS, low Sidekiq
/// concurrency.</para>
///
/// <para>Seeding path notes:</para>
/// <list type="bullet">
///   <item>The root password rides <c>gitlab_rails['initial_root_password']</c>
///         (first-reconfigure only — fine for a throwaway container).</item>
///   <item>PATs cannot be minted over REST without a PAT (chicken &amp; egg) —
///         one <c>gitlab-rails runner</c> exec creates the root token with a
///         KNOWN value (<c>set_token</c>) plus the <c>tamma-reviewer</c> user
///         the reviewer-resolution tests target. A single exec, because each
///         Rails boot costs ~30–60&#160;s.</item>
///   <item><c>monitoring_whitelist 0.0.0.0/0</c> so the host-side readiness
///         poll on <c>/-/readiness</c> is not 403'd (requests arrive from the
///         docker proxy, not localhost).</item>
/// </list>
/// </summary>
public sealed class GitLabContainerFixture : PlatformIntegrationFixture
{
    /// <summary>
    /// Pinned image tag. 16.11 is comfortably above the driver's 13.9
    /// PR-lifecycle floor while still being a settled (EOL'd, immutable)
    /// series — bumping it should be a deliberate decision.
    /// </summary>
    public const string GitLabImage = "gitlab/gitlab-ce:16.11.10-ce.0";

    /// <summary>Detected version (from <c>GET /api/v4/version</c> after boot) —
    /// contract tests assert capability detection matches the running
    /// container.</summary>
    public Version? DetectedVersion { get; private set; }

    /// <summary>Username the reviewer-resolution tests request — seeded by the
    /// rails-runner exec.</summary>
    public const string ReviewerUsername = "tamma-reviewer";

    private const string RootUsername = "root";

    /// <summary>Must dodge GitLab's weak-password heuristic — a value
    /// containing the word "password" fails first-boot admin seeding with
    /// "commonly used combinations of words and letters".</summary>
    private const string RootPassword = "Xk7mQ92vTz4bN8pQ!";

    /// <summary>Known-value PAT set via <c>set_token</c> (20+ chars as 16.x
    /// requires).</summary>
    private const string RootTokenValue = "glpat-tamma-integration-p6x1";

    private IContainer? _container;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public override async Task StartAsync(CancellationToken ct = default)
    {
        OwnerLogin = RootUsername;

        // Valid omnibus keys verified against gitlab-ce 16.x attribute docs;
        // an UNKNOWN key would fail the first reconfigure outright, so only
        // long-standing ones are used.
        const string omnibusConfig =
            "external_url 'http://localhost'; " +
            $"gitlab_rails['initial_root_password'] = '{RootPassword}'; " +
            "gitlab_rails['monitoring_whitelist'] = ['0.0.0.0/0']; " +
            "prometheus_monitoring['enable'] = false; " +
            "puma['worker_processes'] = 0; " +
            "sidekiq['max_concurrency'] = 5; " +
            "gitlab_kas['enable'] = false";

        _container = new ContainerBuilder()
            .WithImage(GitLabImage)
            .WithEnvironment("GITLAB_OMNIBUS_CONFIG", omnibusConfig)
            .WithPortBinding(80, true)
            .Build();

        await _container.StartAsync(ct).ConfigureAwait(false);

        var hostPort = _container.GetMappedPublicPort(80);
        BaseUrl = $"http://localhost:{hostPort}";

        try
        {
            await PollHealthAsync(
                $"{BaseUrl}/-/readiness",
                TimeSpan.FromMinutes(12),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var logs = await CaptureContainerLogsAsync(_container, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"GitLab {GitLabImage} did not become healthy. Container logs:\n{logs}",
                ex);
        }

        await SeedTokenAndReviewerAsync(ct).ConfigureAwait(false);
        BotToken = RootTokenValue;

        DetectedVersion = await DetectVersionAsync(ct).ConfigureAwait(false);
        DefaultBranchSha = await CreateFixtureProjectAsync(ct).ConfigureAwait(false);

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

    private async Task SeedTokenAndReviewerAsync(CancellationToken ct)
    {
        // One exec = one Rails boot (~30–60 s). The script is idempotent so a
        // re-used container doesn't 500 the fixture.
        var script =
            "u = User.find_by_username('root'); " +
            "unless u.personal_access_tokens.find_by(name: 'tamma-it') then " +
            "t = u.personal_access_tokens.create!(scopes: ['api'], name: 'tamma-it', expires_at: 300.days.from_now); " +
            $"t.set_token('{RootTokenValue}'); t.save!; end; " +
            $"unless User.find_by_username('{ReviewerUsername}') then " +
            $"r = User.new(username: '{ReviewerUsername}', name: 'Tamma Reviewer', " +
            "email: 'tamma-reviewer@example.com', password: 'Wj3rF81kLm5xC7dY!', " +
            "password_confirmation: 'Wj3rF81kLm5xC7dY!'); " +
            "r.assign_personal_namespace(Organizations::Organization.default_organization) if r.respond_to?(:assign_personal_namespace); " +
            "r.skip_confirmation!; r.save!; end";

        var result = await _container!.ExecAsync(
            ["gitlab-rails", "runner", script], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gitlab-rails runner seed failed (exit={result.ExitCode}):\n" +
                $"stdout: {result.Stdout}\nstderr: {result.Stderr}");
        }
    }

    private async Task<Version?> DetectVersionAsync(CancellationToken ct)
    {
        using var http = CreateApiClient();
        using var resp = await http.GetAsync("/api/v4/version", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var raw = doc.RootElement.TryGetProperty("version", out var v)
            ? v.GetString() ?? string.Empty
            : string.Empty;
        var dash = raw.IndexOf('-');
        if (dash >= 0) raw = raw[..dash];
        return Version.TryParse(raw, out var parsed) ? parsed : null;
    }

    private async Task<string> CreateFixtureProjectAsync(CancellationToken ct)
    {
        using var http = CreateApiClient();

        var createBody = new
        {
            name = RepoName,
            initialize_with_readme = true,
            default_branch = DefaultBranch,
            visibility = "private",
            description = "Tamma Epic 31 P6 integration test fixture project",
        };
        using (var resp = await http.PostAsJsonAsync("/api/v4/projects", createBody, JsonOpts, ct)
                   .ConfigureAwait(false))
        {
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode
                && !json.Contains("has already been taken", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"create fixture project failed: HTTP {(int)resp.StatusCode}: {json}");
            }
        }

        // Seed a .gitlab-ci.yml so POST /pipeline has something to run —
        // pipeline CREATION works without any runner (jobs just stay pending),
        // which is exactly enough for dispatch/status/cancel coverage.
        var ciBody = new
        {
            branch = DefaultBranch,
            commit_message = "Add .gitlab-ci.yml for pipeline dispatch tests",
            actions = new[]
            {
                new
                {
                    action = "create",
                    file_path = ".gitlab-ci.yml",
                    content = "echo-job:\n  script:\n    - echo tamma\n",
                },
            },
        };
        using (var resp = await http.PostAsJsonAsync(
                   $"/api/v4/projects/{ProjectRef()}/repository/commits", ciBody, JsonOpts, ct)
                   .ConfigureAwait(false))
        {
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode
                && !json.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"seed .gitlab-ci.yml failed: HTTP {(int)resp.StatusCode}: {json}");
            }
        }

        using var branchResp = await http.GetAsync(
            $"/api/v4/projects/{ProjectRef()}/repository/branches/{DefaultBranch}", ct)
            .ConfigureAwait(false);
        var branchJson = await branchResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!branchResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"fetch default branch failed: HTTP {(int)branchResp.StatusCode}: {branchJson}");
        }
        using var doc = JsonDocument.Parse(branchJson);
        var sha = doc.RootElement.GetProperty("commit").GetProperty("id").GetString();
        return sha ?? throw new InvalidOperationException(
            $"default branch {DefaultBranch} returned no commit.id: {branchJson}");
    }

    /// <summary>
    /// Commit a file change on a branch through the commits API — the tests
    /// use this to shape a MULTI-COMMIT MR (two commits on the source branch)
    /// for the diff_refs review-comment leg.
    /// </summary>
    public async Task CommitFileAsync(
        string branch, string filePath, string content, string message,
        string? startBranch = null, bool update = false,
        CancellationToken ct = default)
    {
        using var http = CreateApiClient();
        var body = new Dictionary<string, object?>
        {
            ["branch"] = branch,
            ["commit_message"] = message,
            ["actions"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = update ? "update" : "create",
                    ["file_path"] = filePath,
                    ["content"] = content,
                },
            },
        };
        if (startBranch is not null) body["start_branch"] = startBranch;

        using var resp = await http.PostAsJsonAsync(
            $"/api/v4/projects/{ProjectRef()}/repository/commits", body, JsonOpts, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"commit to {branch} failed: HTTP {(int)resp.StatusCode}: {json}");
        }
    }

    private string ProjectRef() => Uri.EscapeDataString($"{OwnerLogin}/{RepoName}");

    private HttpClient CreateApiClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Remove("PRIVATE-TOKEN");
        http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", RootTokenValue);
        return http;
    }
}

using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Platforms.IntegrationTests.Fixtures;

/// <summary>
/// Epic 31 P5 M3 — the compose-style acceptance vehicle for the Gitea
/// end-to-end suite. One logical deployment with ZERO GitHub
/// configuration:
///
/// <list type="bullet">
///   <item><b>Gitea 1.21</b> (container, via <see cref="GiteaContainerFixture"/> —
///   admin/PAT/repo seeding reused) with <c>host.docker.internal</c>
///   mapped to the docker host gateway so its outbound WEBHOOKS can
///   reach the host-run API.</item>
///   <item><b>Postgres 17</b> ×2 (containers) — the control-plane store
///   and the tenant/app store, exactly the two connection strings the
///   production API wants.</item>
///   <item><b>Tamma.Api</b> — the REAL production binary launched as a
///   HOST process (<c>dotnet Tamma.Api.dll</c>) in single-user mode:
///   <c>Platform:</c> config-tier activation (kind=gitea + the fixture
///   bot's PAT), <c>Tamma:PublicBaseUrl</c> pointing back through the
///   container gateway, dev-permissive auth, and — the point — every
///   <c>GitHub__*</c> variable scrubbed from the child environment.
///   The app self-migrates at startup (its wipe-and-recreate default),
///   registers the Gitea webhook via the P4 startup validator, and
///   serves the governed mediation planes the engine's activities call.</item>
/// </list>
///
/// <para><b>Why host processes, not app containers.</b> The engine and
/// API are plain dotnet processes; putting them in containers would add
/// an in-test image build (multi-GB SDK layers) for zero fidelity gain —
/// the network-real legs this vehicle must prove are Api→Gitea (driver
/// HTTP) and Gitea→Api (webhook delivery through the container gateway),
/// and both are exercised exactly as deployed.</para>
///
/// <para><b>Repo-grant seeding.</b> The git-mediation cross-tenant guard
/// requires a repo-grant row (the <c>github_installations</c>/<c>*_repos</c>
/// registry — HONEST NOTE: the table is GitHub-named but its guard role
/// is platform-agnostic; a single-user Gitea deployment must seed it the
/// same way today). The fixture seeds the grant for the fixture repo
/// through the real EF model after the API has migrated.</para>
/// </summary>
public sealed class GiteaFullStackFixture : IAsyncDisposable
{
    public GiteaContainerFixture Gitea { get; } = new();

    public PostgreSqlContainer ControlPlaneDb { get; private set; } = null!;
    public PostgreSqlContainer AppDb { get; private set; } = null!;

    private Process? _api;
    private string _apiLogPath = string.Empty;

    /// <summary>Host-side base URL of the running Tamma.Api.</summary>
    public string ApiBaseUrl { get; private set; } = string.Empty;

    /// <summary>The callback URL Gitea's webhooks deliver to (container →
    /// host gateway → the API process).</summary>
    public string PublicBaseUrl { get; private set; } = string.Empty;

    /// <summary>Config-tier webhook secret (Webhooks:Secrets:gitea).</summary>
    public string WebhookSecret { get; } =
        Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    public bool IsReady { get; private set; }
    public string NotReadyReason { get; private set; } = "StartAsync has not run";

    /// <summary>The GitHub-shaped env keys scrubbed from the API child
    /// process — asserted by the zero-GitHub-configuration test.</summary>
    public IReadOnlyList<string> ScrubbedGitHubEnvKeys => _scrubbedGitHubKeys;
    private readonly List<string> _scrubbedGitHubKeys = new();

    /// <summary>
    /// Engine-driven E2E (2026-08-13) — extra environment applied to the API
    /// child process AFTER the built-ins (so an entry here can also override).
    /// The engine-full-stack fixture uses it to enable the scripted LLM
    /// provider and to point the API's "elsa" client at the launched engine.
    /// Populate BEFORE <see cref="StartAsync"/>.
    /// </summary>
    public IDictionary<string, string> ExtraApiEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            // ── 1. containers (parallel) ──────────────────────────────
            ControlPlaneDb = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("tamma_e2e_cp").WithUsername("tamma").WithPassword("tamma")
                .Build();
            AppDb = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("tamma_e2e_app").WithUsername("tamma").WithPassword("tamma")
                .Build();

            await Task.WhenAll(
                ControlPlaneDb.StartAsync(ct),
                AppDb.StartAsync(ct),
                Gitea.StartAsync(ct)).ConfigureAwait(false);

            // ── 1b. tenant-schema migrations on the app DB ────────────
            // 2026-08-13 (engine-driven E2E): the API's SYSTEM STORE
            // (SystemStoreDbContextFactory — conventions, document instances,
            // domain events, …) rides ConnectionStrings:TammaAppDb, but NOTHING
            // in API startup migrates the Tenant schema onto it — deployment
            // does that once, operator-side (AdminTenantMigrationEndpoints /
            // `dotnet ef database update`). Without this, ConventionStoreSeeder
            // fails at boot ("relation \"conventions\" does not exist") and
            // every /api/conventions/resolve 500s. The fixture plays the
            // operator: apply the Tenant migrations before the API boots so
            // its seeders find the tables.
            {
                var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
                    .UseNpgsql(AppDb.GetConnectionString(), npgsql =>
                        npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
                    .Options;
                await using var tenantDb = new TenantDbContext(tenantOptions);
                await tenantDb.Database.MigrateAsync(ct).ConfigureAwait(false);
            }

            // ── 2. launch the real Tamma.Api as a host process ────────
            var apiDll = LocateApiDll();
            var port = FreeTcpPort();
            ApiBaseUrl = $"http://localhost:{port}";
            PublicBaseUrl = $"http://host.docker.internal:{port}";
            _apiLogPath = Path.Combine(Path.GetTempPath(), $"tamma-e2e-api-{port}.log");

            var psi = new ProcessStartInfo("dotnet", $"\"{apiDll}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(apiDll)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            // Zero GitHub configuration — scrub anything GitHub-shaped the
            // parent environment might carry, and record what was scrubbed.
            foreach (var key in psi.Environment.Keys
                         .Where(k => k.StartsWith("GITHUB", StringComparison.OrdinalIgnoreCase)
                                     || k.StartsWith("GitHub__", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _scrubbedGitHubKeys.Add(key);
                psi.Environment.Remove(key);
            }

            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; // dev-permissive auth branch
            // 0.0.0.0 so the container-gateway leg (webhooks) can connect.
            psi.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{port}";
            psi.Environment["ConnectionStrings__DefaultConnection"] = ControlPlaneDb.GetConnectionString();
            psi.Environment["ConnectionStrings__TammaDb"] = ControlPlaneDb.GetConnectionString();
            psi.Environment["ConnectionStrings__TammaAppDb"] = AppDb.GetConnectionString();
            psi.Environment["OpenSearch__Enabled"] = "false";
            psi.Environment["TenantConnectionPool__MaxEntries"] = "8";
            psi.Environment.Remove("Jwt__Secret");

            // Single-user activation: the Platform: config tier (owner point 1)
            // — the ONLY platform this deployment knows is the fixture Gitea.
            psi.Environment["Platform__Kind"] = "gitea";
            psi.Environment["Platform__BaseUrl"] = Gitea.BaseUrl;
            psi.Environment["Platform__Credential"] = Gitea.BotToken;

            // P4 startup webhook registration: public callback through the
            // container gateway + the config-tier receiver secret.
            psi.Environment["Tamma__PublicBaseUrl"] = PublicBaseUrl;
            psi.Environment["Webhooks__RegisterOnStartup"] = "true";
            psi.Environment["Webhooks__Secrets__gitea"] = WebhookSecret;

            // Engine-driven E2E (2026-08-13) — caller-supplied extras, applied
            // LAST so they may extend or override the built-ins above.
            foreach (var (key, value) in ExtraApiEnvironment)
            {
                psi.Environment[key] = value;
            }

            _api = new Process { StartInfo = psi };
            var log = new StreamWriter(_apiLogPath, append: false) { AutoFlush = true };
            _api.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.WriteLine(e.Data); } };
            _api.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.WriteLine("[err] " + e.Data); } };
            _api.Start();
            _api.BeginOutputReadLine();
            _api.BeginErrorReadLine();

            // Startup = self-migration (wipe + recreate) + seeds + Kestrel.
            await WaitForApiHealthyAsync(TimeSpan.FromMinutes(4), ct).ConfigureAwait(false);

            // ── 3. seed the repo-grant row the mediation guard requires ──
            await SeedRepoGrantAsync(ct).ConfigureAwait(false);

            IsReady = true;
            NotReadyReason = string.Empty;
        }
        catch (Exception ex)
        {
            NotReadyReason = $"{ex.GetType().Name}: {ex.Message}";
            IsReady = false;
        }
    }

    /// <summary>A ControlPlaneDbContext over the CP container — for seed +
    /// DCB-event / webhook-delivery assertions through the real EF model.</summary>
    public ControlPlaneDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(ControlPlaneDb.GetConnectionString())
            .Options;
        return new ControlPlaneDbContext(options);
    }

    public string ApiLogTail(int maxChars = 4000)
    {
        try
        {
            if (!File.Exists(_apiLogPath)) return "(no api log)";
            var text = File.ReadAllText(_apiLogPath);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch (Exception ex)
        {
            return $"(api log unreadable: {ex.Message})";
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_api is { HasExited: false })
            {
                _api.Kill(entireProcessTree: true);
                _api.WaitForExit(10_000);
            }
            _api?.Dispose();
        }
        catch { /* best effort */ }

        await Gitea.DisposeAsync().ConfigureAwait(false);
        if (ControlPlaneDb is not null) await ControlPlaneDb.DisposeAsync().ConfigureAwait(false);
        if (AppDb is not null) await AppDb.DisposeAsync().ConfigureAwait(false);
    }

    // ── internals ─────────────────────────────────────────────────────

    private async Task WaitForApiHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_api is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"Tamma.Api exited during startup (code {_api.ExitCode}). Log tail:\n{ApiLogTail()}");
            }
            try
            {
                using var resp = await http.GetAsync($"{ApiBaseUrl}/health", ct).ConfigureAwait(false);
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
            $"Tamma.Api did not become healthy within {timeout.TotalSeconds}s. Log tail:\n{ApiLogTail()}",
            last);
    }

    private async Task SeedRepoGrantAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        var installationId = Guid.NewGuid();
        db.GitHubInstallations.Add(new Tamma.Data.Entities.GitHubInstallation
        {
            Id = installationId,
            InstallationId = 0,          // no GitHub App — grant-registry row only
            AccountLogin = Gitea.OwnerLogin,
            AccountType = "User",
            AppId = 0,
            Permissions = "{}",
            TenantId = null,             // single-user mode: null acting tenant
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.GitHubInstallationRepos.Add(new Tamma.Data.Entities.GitHubInstallationRepo
        {
            Id = Guid.NewGuid(),
            InstallationEntityId = installationId,
            RepoId = 1,
            Owner = Gitea.OwnerLogin,
            Name = Gitea.RepoName,
            RepoFullName = $"{Gitea.OwnerLogin}/{Gitea.RepoName}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static int FreeTcpPort()
    {
        var listener = TcpListener.Create(0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Locate the built Tamma.Api.dll next to this repo checkout —
    /// same configuration as the test build first, then the other one. The
    /// CI job builds the solution before running this suite.</summary>
    private static string LocateApiDll()
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
                dir.FullName, "src", "Tamma.Api", "bin", config, "net8.0", "Tamma.Api.dll");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            $"Tamma.Api.dll not found under {dir.FullName}/src/Tamma.Api/bin — build the solution first "
            + "(the E2E vehicle runs the real API binary)");
    }
}

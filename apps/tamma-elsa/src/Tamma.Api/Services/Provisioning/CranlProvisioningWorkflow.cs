using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// State-machine walker for Cranl provisioning. Lives separately from the
/// platform-queue handlers that drive it (the v2 Cranl provider enqueues
/// <c>provisioning.tenant</c>[<c>.deprovision</c>] tasks) so the
/// long-running flow can be invoked directly by the handler (and
/// unit-tested in isolation without the task-queue plumbing).
///
/// <para>Provisioning resumes from whichever state the tenant row is in,
/// so a worker that died mid-flow can be resumed by re-enqueueing the
/// task — each step checks "do I already have what this step produces?"
/// before issuing the API call.</para>
///
/// <para>Polling: <see cref="DatabasePollTimeout"/> bounds the db readiness
/// poll; <see cref="ApplicationPollTimeout"/> bounds the app readiness
/// poll. A timeout flips the row to <see cref="ProvisioningState.Failed"/>
/// with a descriptive detail so operators can investigate (or re-trigger
/// after Cranl-side issues are resolved).</para>
/// </summary>
public sealed class CranlProvisioningWorkflow
{
    public static readonly TimeSpan DatabasePollTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ApplicationPollTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ControlPlaneDbContext _db;
    private readonly ICranlApiClient _cranl;
    private readonly CranlOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IRuntimeSecretResolver? _secretResolver;
    private readonly ILogger<CranlProvisioningWorkflow> _logger;

    // Epic 30 Phase B — the pool-row admin envelope + KEK slot use the
    // AES-GCM connection-string protector the tenant_databases CRUD/seeder
    // use; the schema move re-points the tenant onto the newly-registered
    // Cranl pool row. (Task B3 dropped the standalone TenantSecretProtector
    // dependency: the encrypted DB URL is no longer persisted on the tenant
    // row — it lives only on the pool row's AdminConnectionStringEncrypted,
    // and the plaintext libpq URI is re-derived transiently by polling.)
    private readonly ITenantConnectionStringProtector _connProtector;
    private readonly ITenantMoveService _moveService;

    public CranlProvisioningWorkflow(
        ControlPlaneDbContext db,
        ICranlApiClient cranl,
        CranlOptions options,
        IConfiguration configuration,
        ILogger<CranlProvisioningWorkflow> logger,
        ITenantConnectionStringProtector connProtector,
        ITenantMoveService moveService,
        IRuntimeSecretResolver? secretResolver = null)
    {
        _db = db;
        _cranl = cranl;
        _options = options;
        _configuration = configuration;
        _secretResolver = secretResolver;
        _logger = logger;
        _connProtector = connProtector;
        _moveService = moveService;
    }

    /// <summary>Run the full provisioning flow for the tenant.</summary>
    public async Task ProvisionAsync(Guid tenantId, ProvisioningOptions options, CancellationToken ct)
    {
        var tenant = await LoadTenantAsync(tenantId, ct);
        var entry = _db.Entry(tenant);
        try
        {
            // B3: the Cranl walk/resume working-state lives in the
            // tenants.provider_resource_ids JSONB (via CranlResourceIds), not
            // the retired cranl_* columns. Each step reads back the id it may
            // have already minted so a re-reserved task resumes rather than
            // restarting. Stamp the region up front so the resource-ids map is
            // self-consistent regardless of which caller seeded it (the v2
            // provider stamps it at enqueue; the standalone walk relies on the
            // resolved ProvisioningOptions.Region).
            if (!string.IsNullOrEmpty(options.Region))
            {
                CranlResourceIds.Set(entry, CranlResourceIds.Region, options.Region);
            }

            // Step 1: project
            var projectId = CranlResourceIds.Get(entry, CranlResourceIds.ProjectId);
            if (string.IsNullOrEmpty(projectId))
            {
                var name = options.CustomName ?? BuildProjectName(tenantId);
                var project = await _cranl.CreateProjectAsync(name, _options.OrganizationId, ct);
                projectId = project.Id;
                CranlResourceIds.Set(entry, CranlResourceIds.ProjectId, projectId);
                await TransitionAsync(tenant,
                    ProvisioningState.DatabaseProvisioning,
                    "cranl_project_created", ct);
            }
            else
            {
                await TransitionAsync(tenant,
                    ProvisioningState.DatabaseProvisioning,
                    "resuming_existing_project", ct);
            }

            // Step 2: create db
            var databaseId = CranlResourceIds.Get(entry, CranlResourceIds.DatabaseId);
            if (string.IsNullOrEmpty(databaseId))
            {
                var dbName = "tamma-" + ShortenForName(tenantId);
                var dbReq = new CreateDatabaseRequest
                {
                    Name = dbName,
                    ProjectId = projectId!,
                    Type = "postgresql",
                    ServerId = options.Region
                };
                var created = await _cranl.CreateDatabaseAsync(dbReq, ct);
                databaseId = created.Id;
                CranlResourceIds.Set(entry, CranlResourceIds.DatabaseId, databaseId);
                await TransitionAsync(tenant,
                    ProvisioningState.DatabaseProvisioning,
                    "cranl_database_create_pending", ct);
            }

            // Step 3: poll db until running, capture the connection string
            // transiently. B3: the encrypted DB URL is no longer persisted on
            // the tenant row — the durable copy lives on the tenant_databases
            // pool row (admin envelope) minted just below, and the plaintext
            // libpq URI is re-derived here on every pass (polling a running
            // DB returns immediately, so a resumed walk still gets it).
            var databaseUrl = await PollDatabaseUntilRunningAsync(databaseId!, ct);
            if (databaseUrl is null)
            {
                await TransitionAsync(tenant,
                    ProvisioningState.Failed,
                    "database_did_not_report_connection_string", ct);
                return;
            }
            await TransitionAsync(tenant,
                ProvisioningState.DatabaseReady,
                "cranl_database_running", ct);

            // ── B2 (Epic 30 Phase B): register the ready Cranl hosting DB as
            //    a tenant_databases pool row and move the tenant's schema onto
            //    it — so a Cranl-backed tenant routes through its unified
            //    per-tenant EncryptedConnectionString envelope (the only DB
            //    route after Phase B Task B1). Runs on every DatabaseReady
            //    pass (fresh or resumed) and is idempotent. Fail-closed: a
            //    pool-row/move failure flips the row to Failed rather than
            //    deploying an app against a not-yet-moved tenant. The freshly
            //    polled libpq URI is passed in — B2 no longer decrypts a
            //    tenant column.
            try
            {
                await RegisterCranlDatabaseAndMoveAsync(tenant, databaseUrl, ct);
            }
            catch (OperationCanceledException)
            {
                // Shutdown/cancellation — resume from DatabaseReady on the
                // next reservation (mirrors the outer cancellation policy).
                throw;
            }
            catch (Exception ex)
            {
                // The full exception (with message + stack) goes to the trusted
                // log sink for diagnostics. The PERSISTED provisioning detail
                // gets a STRUCTURED short code (the exception type name) only —
                // a raw ex.Message from a conn-string/DB failure can echo the
                // admin connection string (CLAUDE.md: never persist/log secrets).
                _logger.LogError(ex,
                    "Cranl pool-row registration / schema move failed for tenant {TenantId}",
                    tenantId);
                await TransitionAsync(tenant,
                    ProvisioningState.Failed,
                    $"tenant_schema_move_failed:{ex.GetType().Name}", ct);
                return;
            }

            // Step 4: create application
            var appId = CranlResourceIds.Get(entry, CranlResourceIds.AppId);
            if (string.IsNullOrEmpty(appId))
            {
                var appName = "tamma-engine-" + ShortenForName(tenantId);
                var appReq = new CreateApplicationRequest
                {
                    Name = appName,
                    ProjectId = projectId!,
                    RepositoryId = _options.RepositoryId,
                    Branch = _options.DefaultBranch,
                    BuildType = _options.DefaultBuildType,
                    ServerId = options.Region,
                    BuildPath = _options.AppBuildPath
                };
                var app = await _cranl.CreateApplicationAsync(appReq, ct);
                appId = app.Id;
                CranlResourceIds.Set(entry, CranlResourceIds.AppId, appId);
                await TransitionAsync(tenant,
                    ProvisioningState.AppProvisioning,
                    "cranl_application_created", ct);
            }
            else
            {
                await TransitionAsync(tenant,
                    ProvisioningState.AppProvisioning,
                    "resuming_existing_application", ct);
            }

            // Step 5: push environment (uses the freshly-polled libpq URI)
            var envText = await BuildEnvironmentTextAsync(tenantId, databaseUrl, ct);
            await _cranl.PutEnvironmentAsync(appId!, envText, ct);
            await TransitionAsync(tenant,
                ProvisioningState.AppProvisioning,
                "environment_pushed", ct);

            // Step 6: deploy
            await _cranl.DeployApplicationAsync(appId!, ct);
            await TransitionAsync(tenant,
                ProvisioningState.AppDeploying,
                "deploy_triggered", ct);

            // Step 7: poll app until running
            var appReady = await PollApplicationUntilRunningAsync(appId!, ct);
            if (!appReady)
            {
                await TransitionAsync(tenant,
                    ProvisioningState.Failed,
                    "application_did_not_reach_running", ct);
                return;
            }

            // Step 8: fetch domains → persist the engine host into the JSONB
            // resource map (its last piece, known only once the app is up).
            var domains = await _cranl.GetApplicationDomainsAsync(appId!, ct);
            var appUrl = domains.DefaultDomain
                ?? domains.Domains.FirstOrDefault()?.Host;
            CranlResourceIds.Set(entry, CranlResourceIds.AppUrl, appUrl);

            // Step 9: ready
            await TransitionAsync(tenant, ProvisioningState.Ready, "provisioning_complete", ct);
            _logger.LogInformation(
                "Cranl provisioning complete for tenant {TenantId} (app={AppUrl})",
                tenantId, appUrl);
        }
        catch (CranlApiException ex)
        {
            _logger.LogError(ex,
                "Cranl provisioning failed for tenant {TenantId}: {Status} {Error}",
                tenantId, (int)ex.StatusCode, ex.CranlError);
            await TransitionAsync(tenant,
                ProvisioningState.Failed,
                $"cranl_api_error:{(int)ex.StatusCode}:{Truncate(ex.CranlError, 200)}",
                ct);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Don't flip to Failed on cancellation — the worker might be
            // shutting down and we'd lose the in-progress state. The next
            // poll will resume from where we left off.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Cranl provisioning for tenant {TenantId}", tenantId);
            await TransitionAsync(tenant,
                ProvisioningState.Failed,
                $"unexpected_error:{Truncate(ex.Message, 200)}",
                ct);
            throw;
        }
    }

    /// <summary>
    /// Tear down the tenant's Cranl resources in the safe order:
    /// app → db → project (Cranl rejects project deletes that still own
    /// resources). Clears the Cranl walk-state from the tenant's
    /// <c>provider_resource_ids</c> JSONB on success, keeping only the region
    /// hint for a possible re-provision.
    /// </summary>
    public async Task DeprovisionAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await LoadTenantAsync(tenantId, ct);
        var entry = _db.Entry(tenant);
        try
        {
            var appId = CranlResourceIds.Get(entry, CranlResourceIds.AppId);
            var databaseId = CranlResourceIds.Get(entry, CranlResourceIds.DatabaseId);
            var projectId = CranlResourceIds.Get(entry, CranlResourceIds.ProjectId);

            if (!string.IsNullOrEmpty(appId))
            {
                await SafeDeleteAsync(
                    () => _cranl.DeleteApplicationAsync(appId!, ct),
                    $"application {appId}");
            }
            if (!string.IsNullOrEmpty(databaseId))
            {
                await SafeDeleteAsync(
                    () => _cranl.DeleteDatabaseAsync(databaseId!, ct),
                    $"database {databaseId}");
            }
            if (!string.IsNullOrEmpty(projectId))
            {
                await SafeDeleteAsync(
                    () => _cranl.DeleteProjectAsync(projectId!, ct),
                    $"project {projectId}");
            }

            // Clear the Cranl walk-state, keeping only the region hint for a
            // possible re-provision (mirrors the old "null everything but
            // CranlRegion" behaviour).
            var region = CranlResourceIds.Get(entry, CranlResourceIds.Region);
            var kept = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(region))
            {
                kept[CranlResourceIds.Region] = region!;
            }
            CranlResourceIds.Write(entry, kept);

            await TransitionAsync(tenant, ProvisioningState.Deprovisioned, "teardown_complete", ct);
            _logger.LogInformation("Cranl deprovisioning complete for tenant {TenantId}", tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cranl deprovisioning failed for tenant {TenantId}", tenantId);
            await TransitionAsync(tenant, ProvisioningState.Failed,
                $"deprovision_error:{Truncate(ex.Message, 200)}", ct);
            throw;
        }
    }

    // ─── Internals ───────────────────────────────────────────────────────────

    private async Task<string?> PollDatabaseUntilRunningAsync(string databaseId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + DatabasePollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            CranlDatabase db;
            try
            {
                db = await _cranl.GetDatabaseAsync(databaseId, ct);
            }
            catch (CranlApiException ex) when (ex.IsRetryable)
            {
                _logger.LogWarning(ex, "Transient error polling database {Id}; retrying", databaseId);
                await Task.Delay(PollInterval, ct);
                continue;
            }

            if (string.Equals(db.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return db.BuildConnectionString();
            }
            if (string.Equals(db.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Cranl database {Id} reached error state during provisioning", databaseId);
                return null;
            }
            await Task.Delay(PollInterval, ct);
        }
        _logger.LogWarning("Database {Id} did not reach running within {Timeout}",
            databaseId, DatabasePollTimeout);
        return null;
    }

    private async Task<bool> PollApplicationUntilRunningAsync(string appId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ApplicationPollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            CranlApplication app;
            try
            {
                app = await _cranl.GetApplicationAsync(appId, ct);
            }
            catch (CranlApiException ex) when (ex.IsRetryable)
            {
                _logger.LogWarning(ex, "Transient error polling application {Id}; retrying", appId);
                await Task.Delay(PollInterval, ct);
                continue;
            }

            if (string.Equals(app.Status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(app.Status, "done", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(app.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Cranl application {Id} reached error state during deploy", appId);
                return false;
            }
            await Task.Delay(PollInterval, ct);
        }
        return false;
    }

    private async Task<string> BuildEnvironmentTextAsync(
        Guid tenantId, string databaseUrl, CancellationToken ct)
    {
        var controlPlaneUrl = _configuration["Tamma:ControlPlaneUrl"] ?? "https://api.tamma.dev";

        // Story 29-10: prefer the cabinet-backed resolver when the
        // secret has been migrated; fall through to the legacy config
        // path during the coexistence window. The resolver itself
        // owns the deprecation warning.
        string? sharedSecret = null;
        if (_secretResolver is not null)
        {
            try
            {
                sharedSecret = await _secretResolver.GetAsync(
                    StopgapSecretMap.PlatformTenantSharedSecret, ct);
            }
            catch (MissingSecretException)
            {
                // Fail-fast mode + cabinet not populated yet — surface
                // a deployment error by leaving sharedSecret null so
                // the operator sees the missing env line in the Cranl
                // app log rather than a cryptic HMAC-mismatch later.
                _logger.LogError(
                    "TAMMA_SHARED_SECRET missing from cabinet; run " +
                    "`migrate-secrets` (Story 29-9) or disable " +
                    "TAMMA_STOPGAP_FAIL_FAST for the grace window.");
            }
        }
        if (string.IsNullOrEmpty(sharedSecret))
        {
            sharedSecret = _configuration["Tamma:TenantSharedSecret"]
                ?? _configuration["Cranl:TenantSharedSecret"]
                ?? string.Empty;
        }

        var lines = new List<string>
        {
            $"DATABASE_URL={databaseUrl}",
            $"TAMMA_CONTROL_PLANE_URL={controlPlaneUrl}",
            $"TAMMA_TENANT_ID={tenantId:D}",
        };
        if (!string.IsNullOrEmpty(sharedSecret))
        {
            lines.Add($"TAMMA_SHARED_SECRET={sharedSecret}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Epic 30 Phase B (Task B2): register the freshly-ready Cranl hosting
    /// database as a <c>tenant_databases</c> pool row and move the tenant's
    /// schema onto it — so a Cranl-backed tenant ends up routed through the
    /// unified per-tenant <c>EncryptedConnectionString</c> envelope (the only
    /// DB route after Phase B Task B1). Idempotent: the pool row is keyed by a
    /// stable label (reused on a crash-resumed run rather than duplicated) and
    /// the move is skipped once the tenant already points at the row.
    ///
    /// <para>Task B3: the admin credential is passed in as the freshly-polled
    /// plaintext libpq URI (<paramref name="databaseUrl"/>) rather than
    /// decrypted from a tenant column — that column no longer exists. The pool
    /// row's <c>AdminConnectionStringEncrypted</c> becomes the sole durable
    /// home of the encrypted credential.</para>
    /// </summary>
    private async Task RegisterCranlDatabaseAndMoveAsync(
        Tenant tenant, string databaseUrl, CancellationToken ct)
    {
        // The Cranl DATABASE_URL is an owner/admin credential (design of
        // record: it CAN CREATE ROLE + CREATE SCHEMA). Cranl returns a libpq
        // URI; the pool + move engine parse admin strings with
        // NpgsqlConnectionStringBuilder (keyword form only), so normalise
        // before storing it as the pool row's admin connection string.
        var adminConn = ToNpgsqlKeywordConnectionString(databaseUrl);
        var parsed = new NpgsqlConnectionStringBuilder(adminConn);

        var label = CranlPoolRowLabel(tenant);

        // Idempotency: reuse an existing pool row for this Cranl DB. A
        // duplicate would be an aliasing hazard — two rows pointing at one
        // physical database let a move drop the live schema (TenantMoveService).
        var poolRow = await _db.TenantDatabases
            .FirstOrDefaultAsync(d => d.Label == label, ct);
        if (poolRow is null)
        {
            var now = DateTime.UtcNow;
            poolRow = new TenantDatabase
            {
                Id = Guid.NewGuid(),
                Label = label,
                Host = string.IsNullOrWhiteSpace(parsed.Host) ? "localhost" : parsed.Host,
                Port = parsed.Port,
                AdminConnectionStringEncrypted = _connProtector.Encrypt(adminConn),
                // A Cranl hosting DB is single-tenant by construction.
                PlacementClass = "dedicated",
                // TenantMoveService.EligibleFor requires the tenant's plan
                // slug to be present in TierEligibility for a dedicated row.
                TierEligibility = string.IsNullOrWhiteSpace(tenant.Plan)
                    ? Array.Empty<string>()
                    : new[] { tenant.Plan },
                TenantCapacity = 1,
                TenantCount = 0,
                Status = "active",
                KekVersion = (short)_connProtector.CurrentKekVersion,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.TenantDatabases.Add(poolRow);
        }

        // The provider resource-ids map (project/db/region ids) is already
        // buffered on the tenant's JSONB shadow column by the walk's
        // CranlResourceIds.Set calls; this SaveChanges flushes both the new
        // pool row and that map together.
        await _db.SaveChangesAsync(ct);

        // Idempotency: skip the move ONLY when a prior move PROVABLY completed
        // — the tenant points at this pool row AND its lifecycle Status is back
        // to 'active' (TenantMoveService's final step 10). A tenant that points
        // at the row but is still 'draining' committed the step-7 re-point and
        // then died before verify/DROP-source/activate (steps 8-10). Skipping
        // there would strand the tenant 'draining' forever (every write 503s,
        // unrecoverable without operator action). Fall through instead and
        // re-invoke MoveAsync — its IsResume tail sweeps stale schemas,
        // re-verifies the round-trip, and re-activates.
        var tenantEntry = _db.Entry(tenant);
        var currentDatabaseId = tenantEntry.Property<Guid?>("DatabaseId").CurrentValue;
        var lifecycleStatus = tenantEntry.Property<string?>("Status").CurrentValue;
        if (currentDatabaseId == poolRow.Id
            && string.Equals(lifecycleStatus, "active", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Tenant {TenantId} already placed on Cranl pool row {DatabaseId} "
                + "(status=active) — skipping schema move", tenant.Id, poolRow.Id);
            return;
        }

        // Move INLINE (synchronously). CranlProvisioningWorkflow itself runs
        // inside the single-slot provisioning.tenant platform task, so
        // enqueueing a tenant.move task here would deadlock (the move task
        // could never be reserved while this task holds the only slot).
        // MoveAsync issues its DDL/pg_dump/pg_restore directly via
        // IProcessRunner/ITenantDatabasePool — it enqueues no same-queue task.
        _logger.LogInformation(
            "Moving tenant {TenantId} schema onto Cranl pool row {DatabaseId} (label={Label})",
            tenant.Id, poolRow.Id, label);
        await _moveService.MoveAsync(tenant.Id, poolRow.Id, ct);
    }

    /// <summary>
    /// Stable, unique pool-row label for a tenant's Cranl DB. Keyed on the
    /// IMMUTABLE tenant id (not the admin-mutable Slug): a slug change between
    /// provisioning passes must not make the idempotency lookup miss the
    /// existing row and mint a SECOND pool row aliasing the same physical
    /// database (which the move engine's aliasing guard would then reject,
    /// wedging the tenant in Failed). Mirrors every other Cranl resource name,
    /// which all use <see cref="ShortenForName"/>.
    /// </summary>
    private static string CranlPoolRowLabel(Tenant tenant) =>
        "cranl-" + ShortenForName(tenant.Id);

    /// <summary>
    /// Convert a libpq URI (<c>postgres://</c> / <c>postgresql://</c>) to an
    /// Npgsql keyword connection string. A string already in keyword form is
    /// returned unchanged. Npgsql's builder/connection do NOT parse URIs, so
    /// the pool + move engine need keyword form.
    ///
    /// <para>The URI is parsed MANUALLY rather than via <see cref="Uri"/>:
    /// Cranl mints random passwords that are NOT percent-encoded, so a userinfo
    /// containing a URI-reserved char (<c>@ : / # ? % + </c> or space) makes
    /// <c>new Uri(...)</c> throw <see cref="UriFormatException"/> ("hostname
    /// could not be parsed" / "Invalid port specified") — which previously
    /// bricked pool-row registration and stranded the tenant in
    /// <see cref="ProvisioningState.Failed"/> on every retry. This parser is
    /// tolerant of raw OR percent-encoded userinfo, defaults a missing port to
    /// 5432, tolerates an absent database, preserves query params (e.g.
    /// <c>sslmode</c>), and strips IPv6 brackets. The final keyword string is
    /// produced by <see cref="NpgsqlConnectionStringBuilder"/>, which escapes
    /// values correctly.</para>
    /// </summary>
    internal static string ToNpgsqlKeywordConnectionString(string connectionString)
    {
        const string schemeMarker = "://";
        var schemeIdx = connectionString.IndexOf(schemeMarker, StringComparison.Ordinal);
        if (schemeIdx < 0
            || !(connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
              || connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
        {
            return connectionString;
        }

        // rest = user[:password]@host[:port][/database][?query]
        var rest = connectionString[(schemeIdx + schemeMarker.Length)..];

        // 1) userinfo — split at the LAST '@' (the password may itself contain
        //    '@'; host/port/path never do). Everything after is the authority.
        string userInfo = string.Empty;
        var authorityAndQuery = rest;
        var lastAt = rest.LastIndexOf('@');
        if (lastAt >= 0)
        {
            userInfo = rest[..lastAt];
            authorityAndQuery = rest[(lastAt + 1)..];
        }

        // 2) query string — split at the FIRST '?' in the authority portion
        //    (host/port/db never contain '?').
        string query = string.Empty;
        var authority = authorityAndQuery;
        var qIdx = authorityAndQuery.IndexOf('?');
        if (qIdx >= 0)
        {
            query = authorityAndQuery[(qIdx + 1)..];
            authority = authorityAndQuery[..qIdx];
        }

        // 3) database — split host[:port] from the db name at the FIRST '/'.
        string database = string.Empty;
        var hostPort = authority;
        var slashIdx = authority.IndexOf('/');
        if (slashIdx >= 0)
        {
            hostPort = authority[..slashIdx];
            database = authority[(slashIdx + 1)..];
        }

        // 4) host[:port], honouring IPv6 literals ([::1]:5432 → Host=::1).
        string host;
        int? port = null;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            host = close > 0 ? hostPort[1..close] : hostPort.TrimStart('[');
            var afterBracket = close >= 0 ? hostPort[(close + 1)..] : string.Empty;
            if (afterBracket.StartsWith(':') && afterBracket.Length > 1)
                port = ParsePort(afterBracket[1..]);
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon >= 0)
            {
                host = hostPort[..colon];
                var portText = hostPort[(colon + 1)..];
                if (portText.Length > 0)
                    port = ParsePort(portText);
            }
            else
            {
                host = hostPort;
            }
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Port = port ?? 5432,
        };
        if (!string.IsNullOrEmpty(host))
            builder.Host = host;

        if (userInfo.Length > 0)
        {
            var sep = userInfo.IndexOf(':');
            if (sep < 0)
            {
                builder.Username = Uri.UnescapeDataString(userInfo);
            }
            else
            {
                builder.Username = Uri.UnescapeDataString(userInfo[..sep]);
                builder.Password = Uri.UnescapeDataString(userInfo[(sep + 1)..]);
            }
        }

        if (database.Length > 0)
            builder.Database = Uri.UnescapeDataString(database);

        // Carry over query params (e.g. sslmode) best-effort — an unknown
        // keyword must not abort the whole conversion.
        if (query.Length > 0)
        {
            foreach (var raw in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = raw.Split('=', 2);
                if (kv.Length != 2) continue;
                try
                {
                    builder[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                }
                catch (ArgumentException)
                {
                    // Unknown/unsupported keyword — skip rather than fail.
                }
            }
        }

        return builder.ConnectionString;
    }

    private static int? ParsePort(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            ? p
            : null;

    private async Task SafeDeleteAsync(Func<Task> action, string description)
    {
        try
        {
            await action();
        }
        catch (CranlApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Resource already gone — treat as success.
            _logger.LogInformation("Skipped delete of {Description} — already absent", description);
        }
    }

    private async Task<Tenant> LoadTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        return tenant;
    }

    private async Task TransitionAsync(
        Tenant tenant, ProvisioningState state, string detail, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        tenant.ProvisioningState = state.ToStorageString();
        tenant.ProvisioningDetail = detail;
        tenant.ProvisioningUpdatedAt = now;
        tenant.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Tenant {TenantId} → {State} ({Detail})",
            tenant.Id, state, detail);
    }

    // Cranl tenant project name is "tamma-tenant-<short>" per the README's
    // recipe — keeps the resource list scannable in the Cranl UI.
    private static string BuildProjectName(Guid tenantId) =>
        "tamma-tenant-" + ShortenForName(tenantId);

    /// <summary>
    /// Compress the tenant uuid to its first 8 hex chars. Cranl resource
    /// names should stay short for psql/UI readability; the full uuid is
    /// always available on the tenants row.
    /// </summary>
    internal static string ShortenForName(Guid tenantId) =>
        tenantId.ToString("N").Substring(0, 8);

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max
            ? value
            : value.Substring(0, max);
}

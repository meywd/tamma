using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// State-machine walker for Cranl provisioning. Lives separately from
/// <see cref="CranlTenantProvisioner"/> so the long-running flow can be
/// invoked directly by the queue handler (and unit-tested in isolation
/// without the task-queue plumbing).
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
    private readonly TenantSecretProtector _protector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CranlProvisioningWorkflow> _logger;

    public CranlProvisioningWorkflow(
        ControlPlaneDbContext db,
        ICranlApiClient cranl,
        CranlOptions options,
        TenantSecretProtector protector,
        IConfiguration configuration,
        ILogger<CranlProvisioningWorkflow> logger)
    {
        _db = db;
        _cranl = cranl;
        _options = options;
        _protector = protector;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Run the full provisioning flow for the tenant.</summary>
    public async Task ProvisionAsync(Guid tenantId, ProvisioningOptions options, CancellationToken ct)
    {
        var tenant = await LoadTenantAsync(tenantId, ct);
        try
        {
            // Step 1: project
            if (string.IsNullOrEmpty(tenant.CranlProjectId))
            {
                var name = options.CustomName ?? BuildProjectName(tenantId);
                var project = await _cranl.CreateProjectAsync(name, _options.OrganizationId, ct);
                tenant.CranlProjectId = project.Id;
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
            if (string.IsNullOrEmpty(tenant.CranlDatabaseId))
            {
                var dbName = "tamma-" + ShortenForName(tenantId);
                var dbReq = new CreateDatabaseRequest
                {
                    Name = dbName,
                    ProjectId = tenant.CranlProjectId!,
                    Type = "postgresql",
                    ServerId = options.Region
                };
                var created = await _cranl.CreateDatabaseAsync(dbReq, ct);
                tenant.CranlDatabaseId = created.Id;
                await TransitionAsync(tenant,
                    ProvisioningState.DatabaseProvisioning,
                    "cranl_database_create_pending", ct);
            }

            // Step 3: poll db until running, capture connection string
            if (tenant.CranlDatabaseUrlEncrypted is null
                || tenant.CranlDatabaseUrlEncrypted.Length == 0)
            {
                var connectionString = await PollDatabaseUntilRunningAsync(tenant.CranlDatabaseId!, ct);
                if (connectionString is null)
                {
                    await TransitionAsync(tenant,
                        ProvisioningState.Failed,
                        "database_did_not_report_connection_string", ct);
                    return;
                }
                tenant.CranlDatabaseUrlEncrypted = _protector.Encrypt(connectionString);
                await TransitionAsync(tenant,
                    ProvisioningState.DatabaseReady,
                    "cranl_database_running", ct);
            }
            else
            {
                await TransitionAsync(tenant,
                    ProvisioningState.DatabaseReady,
                    "resuming_with_existing_connection_string", ct);
            }

            // Step 4: create application
            if (string.IsNullOrEmpty(tenant.CranlAppId))
            {
                var appName = "tamma-engine-" + ShortenForName(tenantId);
                var appReq = new CreateApplicationRequest
                {
                    Name = appName,
                    ProjectId = tenant.CranlProjectId!,
                    RepositoryId = _options.RepositoryId,
                    Branch = _options.DefaultBranch,
                    BuildType = _options.DefaultBuildType,
                    ServerId = options.Region,
                    BuildPath = _options.AppBuildPath
                };
                var app = await _cranl.CreateApplicationAsync(appReq, ct);
                tenant.CranlAppId = app.Id;
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

            // Step 5: push environment
            var envText = BuildEnvironmentText(tenantId, _protector.Decrypt(tenant.CranlDatabaseUrlEncrypted!));
            await _cranl.PutEnvironmentAsync(tenant.CranlAppId!, envText, ct);
            await TransitionAsync(tenant,
                ProvisioningState.AppProvisioning,
                "environment_pushed", ct);

            // Step 6: deploy
            await _cranl.DeployApplicationAsync(tenant.CranlAppId!, ct);
            await TransitionAsync(tenant,
                ProvisioningState.AppDeploying,
                "deploy_triggered", ct);

            // Step 7: poll app until running
            var appReady = await PollApplicationUntilRunningAsync(tenant.CranlAppId!, ct);
            if (!appReady)
            {
                await TransitionAsync(tenant,
                    ProvisioningState.Failed,
                    "application_did_not_reach_running", ct);
                return;
            }

            // Step 8: fetch domains
            var domains = await _cranl.GetApplicationDomainsAsync(tenant.CranlAppId!, ct);
            tenant.CranlAppUrl = domains.DefaultDomain
                ?? domains.Domains.FirstOrDefault()?.Host;

            // Step 9: ready
            await TransitionAsync(tenant, ProvisioningState.Ready, "provisioning_complete", ct);
            _logger.LogInformation(
                "Cranl provisioning complete for tenant {TenantId} (app={AppUrl})",
                tenantId, tenant.CranlAppUrl);
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
    /// resources). Clears the tenant's <c>cranl_*</c> columns on success.
    /// </summary>
    public async Task DeprovisionAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await LoadTenantAsync(tenantId, ct);
        try
        {
            if (!string.IsNullOrEmpty(tenant.CranlAppId))
            {
                await SafeDeleteAsync(
                    () => _cranl.DeleteApplicationAsync(tenant.CranlAppId!, ct),
                    $"application {tenant.CranlAppId}");
            }
            if (!string.IsNullOrEmpty(tenant.CranlDatabaseId))
            {
                await SafeDeleteAsync(
                    () => _cranl.DeleteDatabaseAsync(tenant.CranlDatabaseId!, ct),
                    $"database {tenant.CranlDatabaseId}");
            }
            if (!string.IsNullOrEmpty(tenant.CranlProjectId))
            {
                await SafeDeleteAsync(
                    () => _cranl.DeleteProjectAsync(tenant.CranlProjectId!, ct),
                    $"project {tenant.CranlProjectId}");
            }

            tenant.CranlProjectId = null;
            tenant.CranlDatabaseId = null;
            tenant.CranlAppId = null;
            tenant.CranlDatabaseUrlEncrypted = null;
            tenant.CranlAppUrl = null;
            // Keep CranlRegion as a hint for re-provisioning.

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

    private string BuildEnvironmentText(Guid tenantId, string databaseUrl)
    {
        var controlPlaneUrl = _configuration["Tamma:ControlPlaneUrl"] ?? "https://api.tamma.dev";
        var sharedSecret = _configuration["Tamma:TenantSharedSecret"]
            ?? _configuration["Cranl:TenantSharedSecret"]
            ?? string.Empty;
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

using System.Globalization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.AgentDispatch;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Story 28-5 AC4 step C — optional <c>pg_dump</c> backup of the tenant's
/// data, taken BEFORE <see cref="DropTenantSchemaActivity"/> drops it.
/// Gated behind <c>Backup:DeletionBackup</c>; a pure no-op when the
/// flag is off (the default relies on cluster-level Postgres backups,
/// so per-tenant snapshots are opt-in).
///
/// <para><b>Unified-tenancy Phase 2 — placement-aware:</b> a tenant with
/// a <c>SchemaName</c>/<c>DatabaseId</c> placement gets a schema-scoped
/// dump (<c>pg_dump --schema=t_&lt;hex&gt;</c>) against the assigned pool
/// row's database. Tenants without a placement (pre-Phase-2 dev runs)
/// keep the legacy whole-database dump of
/// <c>tamma_tenant_&lt;hex&gt;</c>.</para>
///
/// <para>Slots between <c>EvictTenantPool</c> and <c>DropTenantSchema</c>
/// in <c>DeleteTenantWorkflow</c>: the pool must be evicted first (no
/// cached data source), and the dump must complete before the drop.</para>
///
/// <para><b>Secret hygiene:</b> the admin password is passed to
/// <c>pg_dump</c> via the <c>PGPASSWORD</c> environment variable, never on
/// the command line — argv is world-readable via
/// <c>/proc/&lt;pid&gt;/cmdline</c>.</para>
///
/// <para>Idempotent: if the schema (or legacy database) is already gone
/// (a prior run dropped it, or the tenant never reached the create step)
/// there is nothing to back up and the activity exits cleanly.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Backup Tenant Database",
    "pg_dump the tenant schema (or legacy DB) before drop (gated by Backup:DeletionBackup). No-op when disabled.",
    Kind = ActivityKind.Task)]
public sealed class BackupTenantDatabaseActivity : TenantLifecycleActivity
{
    public override string StepName => "backup-database";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        Logger ??= context.GetService<ILogger<BackupTenantDatabaseActivity>>();

        var options = context.GetService<IOptions<TenantBackupOptions>>()?.Value
                      ?? new TenantBackupOptions();

        if (!options.DeletionBackup)
        {
            Logger?.LogInformation(
                "tenant.lifecycle.backup_database disabled_skip tenantId={TenantId}", tenantId);
            return;
        }

        var runner = context.GetRequiredService<IProcessRunner>();
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var placement = await TenantPlacementShadow.LoadAsync(
            factory, tenantId, context.CancellationToken).ConfigureAwait(false);

        if (placement.DatabaseId is not null && placement.SchemaName is not null)
        {
            var pool = context.GetRequiredService<ITenantDatabasePool>();
            await BackupSchemaAsync(
                options, pool, runner, tenantId,
                placement.DatabaseId.Value, placement.SchemaName,
                DateTime.UtcNow, Logger, context.CancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Legacy whole-DB flavor for tenants that predate schema-per-tenant
        // placement (db-per-tenant dev runs).
        var admin = context.GetRequiredService<ITenantAdminConnection>();
        await BackupAsync(
            options, admin, runner, tenantId, DateTime.UtcNow, Logger, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Unified-tenancy Phase 2 — schema-scoped pure-DI entry point,
    /// testable without a live Elsa context. Dumps ONLY the tenant's
    /// schema (<c>--schema=t_&lt;hex&gt;</c>) from the assigned pool
    /// row's database. Returns true when a dump was produced, false when
    /// skipped (disabled, or the schema does not exist — replay after a
    /// successful drop). Throws when <c>pg_dump</c> exits non-zero so
    /// the surrounding workflow aborts before the destructive drop.
    /// </summary>
    public static async Task<bool> BackupSchemaAsync(
        TenantBackupOptions options,
        ITenantDatabasePool pool,
        IProcessRunner processRunner,
        Guid tenantId,
        Guid databaseId,
        string schemaName,
        DateTime nowUtc,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        if (!options.DeletionBackup)
            return false;

        if (!await pool.SchemaExistsOnAsync(databaseId, schemaName, cancellationToken)
            .ConfigureAwait(false))
        {
            logger?.LogInformation(
                "tenant.lifecycle.backup_database idempotent_skip tenantId={TenantId} schema={Schema}",
                tenantId, schemaName);
            return false;
        }

        var info = await pool.GetConnectionInfoAsync(databaseId, cancellationToken)
            .ConfigureAwait(false);
        var destination = BuildDestination(options, schemaName, nowUtc);

        // --schema dumps the tenant's schema ONLY — neighbours on the
        // shared pool database must never land in this tenant's snapshot.
        var arguments = PgToolArguments.ForPgDump(info, destination, schemaName);

        await RunPgDumpAsync(
            options, processRunner, arguments, info.Password,
            tenantId, target: schemaName, destination, logger, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Legacy whole-database pure-DI entry point — testable without a
    /// live Elsa context. Returns true when a dump was produced, false
    /// when skipped (disabled, or the database does not exist). Throws
    /// when <c>pg_dump</c> exits non-zero so the surrounding workflow
    /// aborts before the destructive drop.
    /// </summary>
    public static async Task<bool> BackupAsync(
        TenantBackupOptions options,
        ITenantAdminConnection admin,
        IProcessRunner processRunner,
        Guid tenantId,
        DateTime nowUtc,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(processRunner);

        if (!options.DeletionBackup)
            return false;

        var dbName = TenantNaming.DatabaseName(tenantId);

        if (!await admin.DatabaseExistsAsync(dbName, cancellationToken).ConfigureAwait(false))
        {
            logger?.LogInformation(
                "tenant.lifecycle.backup_database idempotent_skip tenantId={TenantId} db={Db}",
                tenantId, dbName);
            return false;
        }

        var info = admin.GetConnectionInfo(dbName);
        var destination = BuildDestination(options, dbName, nowUtc);

        await RunPgDumpAsync(
            options, processRunner, PgToolArguments.ForPgDump(info, destination), info.Password,
            tenantId, target: dbName, destination, logger, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static string BuildDestination(
        TenantBackupOptions options, string targetName, DateTime nowUtc)
    {
        System.IO.Directory.CreateDirectory(options.Directory);
        var stamp = nowUtc.ToUniversalTime()
            .ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        return System.IO.Path.Combine(options.Directory, $"{targetName}_{stamp}.dump");
    }

    // pg_dump argv lives in PgToolArguments (Phase 4 extraction — shared
    // with TenantMoveService). Custom format, --no-password, password via
    // the environment ONLY.
    private static async Task RunPgDumpAsync(
        TenantBackupOptions options,
        IProcessRunner processRunner,
        List<string> arguments,
        string password,
        Guid tenantId,
        string target,
        string destination,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var env = new Dictionary<string, string>
        {
            ["PGPASSWORD"] = password,
        };

        var result = await processRunner.RunAsync(
            new ProcessRunRequest(
                FileName: options.PgDumpPath,
                Arguments: arguments,
                WorkingDirectory: options.Directory,
                EnvironmentOverrides: env,
                TimeoutSeconds: options.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
            throw new InvalidOperationException(
                $"pg_dump timed out after {options.TimeoutSeconds}s backing up {target}.");

        if (result.ExitCode != 0)
        {
            // Log the (truncated) stderr locally for the operator, but do
            // NOT embed it in the thrown message: TenantLifecycleActivity's
            // base persists ex.Message verbatim into the STEP_FAILED
            // platform_event, and pg_dump stderr can echo connection
            // details. Keep the durable event message scrubbed.
            logger?.LogWarning(
                "tenant.lifecycle.backup_database failed tenantId={TenantId} target={Target} exit={Exit} stderr={StdErr}",
                tenantId, target, result.ExitCode, Truncate(result.StdErr));
            throw new InvalidOperationException(
                $"pg_dump failed (exit {result.ExitCode}) backing up {target}. See logs for stderr.");
        }

        logger?.LogInformation(
            "tenant.lifecycle.backup_database completed tenantId={TenantId} target={Target} file={File}",
            tenantId, target, destination);
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }
}

using System.Globalization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.AgentDispatch;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Story 28-5 AC4 step C — optional <c>pg_dump</c> backup of the tenant
/// database, taken BEFORE <see cref="DropTenantDatabaseActivity"/> drops
/// it. Gated behind <c>Backup:DeletionBackup</c>; a pure no-op when the
/// flag is off (the shared-infrastructure default relies on cluster-level
/// Postgres backups, so per-tenant snapshots are opt-in).
///
/// <para>Slots between <c>EvictTenantPool</c> and <c>DropTenantDatabase</c>
/// in <c>DeleteTenantWorkflow</c>: the pool must be evicted first (no
/// cached data source), and the dump must complete before the drop.</para>
///
/// <para><b>Secret hygiene:</b> the admin password is passed to
/// <c>pg_dump</c> via the <c>PGPASSWORD</c> environment variable, never on
/// the command line — argv is world-readable via
/// <c>/proc/&lt;pid&gt;/cmdline</c>.</para>
///
/// <para>Idempotent: if the database is already gone (a prior run dropped
/// it, or the tenant never reached create-database) there is nothing to
/// back up and the activity exits cleanly.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Backup Tenant Database",
    "pg_dump the tenant DB before drop (gated by Backup:DeletionBackup). No-op when disabled.",
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

        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var runner = context.GetRequiredService<IProcessRunner>();

        await BackupAsync(
            options, admin, runner, tenantId, DateTime.UtcNow, Logger, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI entry point — testable without a live Elsa context.
    /// Returns true when a dump was produced, false when skipped
    /// (disabled, or the database does not exist). Throws when
    /// <c>pg_dump</c> exits non-zero so the surrounding workflow aborts
    /// before the destructive drop.
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
        System.IO.Directory.CreateDirectory(options.Directory);

        var stamp = nowUtc.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var destination = System.IO.Path.Combine(options.Directory, $"{dbName}_{stamp}.dump");

        // Custom format (-Fc) is compressed + restorable with pg_restore.
        // --no-password fails fast instead of prompting if PGPASSWORD is
        // somehow absent. Password goes through the environment ONLY.
        var arguments = new List<string>
        {
            "--host", info.Host,
            "--port", info.Port.ToString(CultureInfo.InvariantCulture),
            "--username", info.Username,
            "--dbname", info.Database,
            "--format", "custom",
            "--no-password",
            "--file", destination,
        };

        var env = new Dictionary<string, string>
        {
            ["PGPASSWORD"] = info.Password,
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
                $"pg_dump timed out after {options.TimeoutSeconds}s backing up {dbName}.");

        if (result.ExitCode != 0)
        {
            // Log the (truncated) stderr locally for the operator, but do
            // NOT embed it in the thrown message: TenantLifecycleActivity's
            // base persists ex.Message verbatim into the STEP_FAILED
            // platform_event, and pg_dump stderr can echo connection
            // details. Keep the durable event message scrubbed.
            logger?.LogWarning(
                "tenant.lifecycle.backup_database failed tenantId={TenantId} db={Db} exit={Exit} stderr={StdErr}",
                tenantId, dbName, result.ExitCode, Truncate(result.StdErr));
            throw new InvalidOperationException(
                $"pg_dump failed (exit {result.ExitCode}) backing up {dbName}. See logs for stderr.");
        }

        logger?.LogInformation(
            "tenant.lifecycle.backup_database completed tenantId={TenantId} db={Db} file={File}",
            tenantId, dbName, destination);

        return true;
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }
}

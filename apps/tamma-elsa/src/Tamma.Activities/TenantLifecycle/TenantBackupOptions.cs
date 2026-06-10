namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Story 28-5 AC4 — configuration for the optional pre-drop tenant
/// backup. Bound from the <c>Backup</c> configuration section. Disabled
/// by default: the default topology relies on
/// cluster-level Postgres backups, so per-tenant <c>pg_dump</c> snapshots
/// are opt-in (turned on once an SLA promises soft-delete recovery).
/// </summary>
public sealed class TenantBackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>
    /// When true, <see cref="BackupTenantDatabaseActivity"/> shells out to
    /// <c>pg_dump</c> to capture the tenant database to
    /// <see cref="Directory"/> before <c>DROP DATABASE</c>. When false
    /// (default) the activity is a pure no-op.
    /// </summary>
    public bool DeletionBackup { get; set; }

    /// <summary>
    /// Destination directory for the dump files. Should be a mounted,
    /// durable volume in production. The activity creates it if missing.
    /// </summary>
    public string Directory { get; set; } = "/var/backups/tamma";

    /// <summary>Path to the <c>pg_dump</c> binary (PATH-resolved by default).</summary>
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>Hard timeout for the dump, in seconds (default 30 minutes).</summary>
    public int TimeoutSeconds { get; set; } = 30 * 60;
}

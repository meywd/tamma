using System.Globalization;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 4 — shared argv builders for the Postgres CLI
/// tools the tenant lifecycle shells out to (<c>pg_dump</c> in
/// <see cref="BackupTenantDatabaseActivity"/> and the Phase 4
/// <c>TenantMoveService</c>; <c>pg_restore</c> in the move only).
/// Extracted from <c>BackupTenantDatabaseActivity</c> so both callers
/// share one arg discipline.
///
/// <para><b>Secret hygiene:</b> these builders NEVER place the password
/// in argv — argv is world-readable via <c>/proc/&lt;pid&gt;/cmdline</c>.
/// Callers must pass <see cref="TenantAdminConnectionInfo.Password"/> via
/// the <c>PGPASSWORD</c> environment variable on the
/// <c>ProcessRunRequest</c>. <c>--no-password</c> makes the tool fail
/// fast instead of prompting if the variable is somehow absent.</para>
/// </summary>
public static class PgToolArguments
{
    /// <summary>
    /// <c>pg_dump</c> argv. Custom format (<c>--format custom</c>) is
    /// compressed + restorable with <c>pg_restore</c>. When
    /// <paramref name="schemaName"/> is supplied the dump is scoped to
    /// that schema only (<c>--schema</c>) — neighbours on a shared pool
    /// database must never land in a tenant's snapshot.
    /// </summary>
    public static List<string> ForPgDump(
        TenantAdminConnectionInfo info, string destination, string? schemaName = null)
    {
        var arguments = new List<string>
        {
            "--host", info.Host,
            "--port", info.Port.ToString(CultureInfo.InvariantCulture),
            "--username", info.Username,
            "--dbname", info.Database,
        };
        if (schemaName is not null)
        {
            arguments.Add("--schema");
            arguments.Add(schemaName);
        }
        arguments.AddRange(new[]
        {
            "--format", "custom",
            "--no-password",
            "--file", destination,
        });
        return arguments;
    }

    /// <summary>
    /// <c>pg_restore</c> argv for the Phase 4 move: restore a
    /// schema-scoped custom-format dump into the TARGET pool row's
    /// database. <c>--no-owner --role &lt;tenant role&gt;</c> makes every
    /// restored object land owned by the tenant role (pg_restore issues
    /// <c>SET ROLE</c> before restoring) — the admin user on
    /// <paramref name="info"/> must therefore be allowed to <c>SET ROLE</c>
    /// to <paramref name="ownerRole"/> (superuser, or a member of the
    /// role).
    /// </summary>
    public static List<string> ForPgRestore(
        TenantAdminConnectionInfo info, string dumpFile, string ownerRole)
    {
        return new List<string>
        {
            "--host", info.Host,
            "--port", info.Port.ToString(CultureInfo.InvariantCulture),
            "--username", info.Username,
            "--dbname", info.Database,
            "--no-owner",
            "--role", ownerRole,
            "--no-password",
            dumpFile,
        };
    }
}

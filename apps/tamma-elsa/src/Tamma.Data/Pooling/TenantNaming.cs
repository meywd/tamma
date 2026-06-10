using Npgsql;

namespace Tamma.Data.Pooling;

/// <summary>
/// Story 28-5 — canonical naming for per-tenant Postgres roles + databases.
/// Centralised so <c>CreateTenantWorkflow</c>, <c>DeleteTenantWorkflow</c>,
/// and any future migration helpers all derive the same string from a
/// tenant id.
///
/// <para>The hex-encoded GUID (no hyphens) keeps role/database names
/// inside the 63-byte Postgres identifier limit while staying
/// deterministic and round-trippable: 6 chars prefix + 32 chars hex + the
/// optional <c>_elsa</c> suffix is comfortably under 63. The per-tenant
/// schema uses a shorter <c>t_</c> prefix (34 chars total) to remain
/// visually distinct from the role/database name.</para>
///
/// <para>Identifier safety: the names emitted here contain only
/// <c>[a-z0-9_]</c> and have a fixed prefix of <c>tamma_tenant_</c>. They
/// are still wrapped in double-quotes by callers to defend against any
/// future change to this scheme — never inline a tenant identifier from
/// untrusted source into raw SQL.</para>
/// </summary>
public static class TenantNaming
{
    public const string Prefix = "tamma_tenant_";
    public const string ElsaSuffix = "_elsa";

    /// <summary>
    /// 32-character lowercase hex of the tenant id with no hyphens. Stable
    /// for the lifetime of the tenant.
    /// </summary>
    public static string HexOf(Guid tenantId) => tenantId.ToString("N");

    /// <summary>
    /// Canonical role name — <c>tamma_tenant_&lt;hex&gt;</c>.
    /// </summary>
    public static string RoleName(Guid tenantId) => $"{Prefix}{HexOf(tenantId)}";

    /// <summary>
    /// Canonical app database name — same as the role name. The role is
    /// the database owner so per-tenant DDL can run without superuser.
    /// </summary>
    public static string DatabaseName(Guid tenantId) => $"{Prefix}{HexOf(tenantId)}";

    /// <summary>
    /// Canonical Elsa companion database name — <c>tamma_tenant_&lt;hex&gt;_elsa</c>.
    /// </summary>
    public static string ElsaDatabaseName(Guid tenantId) =>
        $"{Prefix}{HexOf(tenantId)}{ElsaSuffix}";

    /// <summary>
    /// Canonical per-tenant schema name — <c>t_&lt;hex&gt;</c> (unified-tenancy
    /// plan 2026-06-09 §2.2). Short prefix keeps it visually distinct from the
    /// role (<c>tamma_tenant_&lt;hex&gt;</c>) and comfortably under the 63-byte
    /// identifier limit (34 chars).
    /// </summary>
    public static string SchemaName(Guid tenantId) => $"t_{HexOf(tenantId)}";

    /// <summary>
    /// Extracts the tenant schema from a connection string's
    /// <c>Search Path</c> key: first comma-separated segment, or null when the
    /// key is absent/empty (callers treat null as "default <c>public</c>
    /// behavior, exactly as before Phase 1"). Rejects identifiers outside
    /// <c>[a-z_][a-z0-9_]*</c> — schema names only ever come from
    /// <see cref="SchemaName"/> or operator config, never user input, so
    /// anything else indicates a corrupted/hostile connection string.
    /// </summary>
    public static string? SchemaFromConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var searchPath = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
        if (string.IsNullOrWhiteSpace(searchPath))
            return null;

        var first = searchPath.Split(',')[0].Trim();
        if (first.Length == 0)
            return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(first, "^[a-z_][a-z0-9_]*$"))
            throw new ArgumentException(
                $"Search Path schema '{first}' is not a safe identifier.",
                nameof(connectionString));
        return first;
    }

    /// <summary>
    /// Quote an identifier for safe inclusion in a SQL statement.
    /// Postgres uses double-quotes; embedded double-quotes are escaped by
    /// doubling. Only used for the names this class emits — callers must
    /// not pass arbitrary user input through here.
    /// </summary>
    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException("identifier must not be empty", nameof(identifier));
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}

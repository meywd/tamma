using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Production <see cref="ITenantDatabasePool"/> over the
/// <c>tenant_databases</c> registry (unified-tenancy Phase 2). Loads the
/// pool row through the control-plane context factory, decrypts its
/// AES-GCM admin-connection envelope via the
/// <see cref="IConnectionStringDecryptor"/> seam, and opens a short-lived
/// connection per statement against the ROW's cluster — mirroring
/// <see cref="NpgsqlTenantAdminConnection"/>'s autocommit/no-transaction
/// mechanics so provisioning DDL never lands inside a user transaction.
///
/// <para>Decrypted admin strings are cached per database id (pool rows
/// rotate rarely; the cache spares a decrypt + CP round-trip on every
/// lifecycle step). <see cref="Evict"/> drops a cached entry — used by
/// tests and, later, by the Phase 4 admin CRUD after a row update.</para>
/// </summary>
public sealed class TenantDatabasePool : ITenantDatabasePool
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _contextFactory;
    private readonly IConnectionStringDecryptor _decryptor;
    private readonly ILogger<TenantDatabasePool> _logger;
    private readonly ConcurrentDictionary<Guid, string> _adminConnectionStrings = new();

    public TenantDatabasePool(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        IConnectionStringDecryptor decryptor,
        ILogger<TenantDatabasePool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(decryptor);
        _contextFactory = contextFactory;
        _decryptor = decryptor;
        _logger = logger ?? NullLogger<TenantDatabasePool>.Instance;
    }

    public async Task<string> GetAdminConnectionStringAsync(
        Guid databaseId, CancellationToken ct = default)
    {
        if (_adminConnectionStrings.TryGetValue(databaseId, out var cached))
            return cached;

        await using var context = await _contextFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await context.TenantDatabases
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"tenant_databases has no row with Id {databaseId} — the tenant's "
                + "DatabaseId points at a pool member that does not exist.");

        var adminConnectionString = _decryptor.Decrypt(
            row.AdminConnectionStringEncrypted, row.KekVersion);

        _adminConnectionStrings.TryAdd(databaseId, adminConnectionString);
        return adminConnectionString;
    }

    public async Task<int> ExecuteOnAsync(
        Guid databaseId, string commandText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            throw new ArgumentException("commandText must be supplied", nameof(commandText));

        await using var conn = await OpenAsync(databaseId, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        // Mirror NpgsqlTenantAdminConnection: provisioning DDL can take a
        // while; widen the default 30s command timeout to 5m.
        cmd.CommandTimeout = (int)TimeSpan.FromMinutes(5).TotalSeconds;
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<object?> ExecuteScalarOnAsync(
        Guid databaseId, string commandText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            throw new ArgumentException("commandText must be supplied", nameof(commandText));

        await using var conn = await OpenAsync(databaseId, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RoleExistsOnAsync(
        Guid databaseId, string roleName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("roleName must be supplied", nameof(roleName));

        await using var conn = await OpenAsync(databaseId, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_roles WHERE rolname = @rolname";
        cmd.Parameters.AddWithValue("rolname", roleName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    public async Task<bool> SchemaExistsOnAsync(
        Guid databaseId, string schemaName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentException("schemaName must be supplied", nameof(schemaName));

        await using var conn = await OpenAsync(databaseId, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM information_schema.schemata WHERE schema_name = @schema";
        cmd.Parameters.AddWithValue("schema", schemaName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    public async Task<TenantAdminConnectionInfo> GetConnectionInfoAsync(
        Guid databaseId, CancellationToken ct = default)
    {
        var adminConnectionString = await GetAdminConnectionStringAsync(databaseId, ct)
            .ConfigureAwait(false);
        var b = new NpgsqlConnectionStringBuilder(adminConnectionString);
        if (string.IsNullOrWhiteSpace(b.Database))
            throw new InvalidOperationException(
                $"tenant_databases row {databaseId}: the decrypted admin connection string "
                + "carries no Database — cannot derive pg_dump connection parts.");

        return new TenantAdminConnectionInfo(
            // Mirror NpgsqlTenantAdminConnection.GetConnectionInfo:
            // normalise Host to localhost so pg_dump always receives an
            // explicit --host.
            Host: string.IsNullOrWhiteSpace(b.Host) ? "localhost" : b.Host,
            Port: b.Port,
            Username: b.Username ?? string.Empty,
            Password: b.Password ?? string.Empty,
            Database: b.Database);
    }

    public async Task<string> GetDatabaseNameAsync(
        Guid databaseId, CancellationToken ct = default)
    {
        var adminConnectionString = await GetAdminConnectionStringAsync(databaseId, ct)
            .ConfigureAwait(false);
        var database = new NpgsqlConnectionStringBuilder(adminConnectionString).Database;
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException(
                $"tenant_databases row {databaseId}: the decrypted admin connection string "
                + "carries no Database — cannot derive the placement target database name.");
        return database;
    }

    public async Task<string> BuildTenantConnectionStringAsync(
        Guid databaseId, string roleName, string password, string schemaName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("roleName must be supplied", nameof(roleName));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("password must be supplied", nameof(password));
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentException("schemaName must be supplied", nameof(schemaName));

        var adminConnectionString = await GetAdminConnectionStringAsync(databaseId, ct)
            .ConfigureAwait(false);

        // Start from the row's admin string so Host/Port/SSL/etc. carry
        // over, then overwrite the identity-bearing fields. The admin
        // string's Database is KEPT: the pool row's database IS the
        // target — schema-per-tenant isolates via Search Path, not via a
        // per-tenant database.
        var b = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = roleName,
            Password = password,
            SearchPath = schemaName,
            ApplicationName = $"tamma-tenant;schema={schemaName}",
        };

        // Drop admin-only fields so the tenant string can't be used to
        // reach back as the admin role (mirrors NpgsqlTenantAdminConnection).
        b.Remove("Include Error Detail");

        return b.ConnectionString;
    }

    /// <summary>
    /// Drops the cached decrypted admin string for a pool row — call after
    /// the row's envelope or KEK version changes. Phase 4 promoted this
    /// from an internal test hook onto <see cref="ITenantDatabasePool"/> so
    /// the admin tenant-databases CRUD can invalidate the cache after a
    /// conn-string rotation (interface growth noted in the Phase 4 plan).
    /// </summary>
    public void EvictAdminConnection(Guid databaseId)
        => _adminConnectionStrings.TryRemove(databaseId, out _);

    private async Task<NpgsqlConnection> OpenAsync(Guid databaseId, CancellationToken ct)
    {
        var adminConnectionString = await GetAdminConnectionStringAsync(databaseId, ct)
            .ConfigureAwait(false);
        var conn = new NpgsqlConnection(adminConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "tenant.pool.admin_open_failed databaseId={DatabaseId}", databaseId);
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

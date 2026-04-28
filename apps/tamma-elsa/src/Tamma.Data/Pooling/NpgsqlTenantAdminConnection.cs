using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Production <see cref="ITenantAdminConnection"/> backed by Npgsql.
/// Opens a short-lived admin connection per call to keep DDL out of any
/// user transaction (notably <c>DROP DATABASE WITH (FORCE)</c> which
/// Postgres rejects inside a transaction block) and to keep the admin
/// pool small — provisioning is rare and bursty.
///
/// <para>Connection-string source: <c>ConnectionStrings:TenantAdmin</c>
/// when set, otherwise <c>ConnectionStrings:DefaultConnection</c>. The
/// fallback is so dev environments with a single Postgres instance work
/// without extra configuration; production is expected to wire a
/// dedicated admin connection bound to <c>tamma_provisioner</c>.</para>
/// </summary>
public sealed class NpgsqlTenantAdminConnection : ITenantAdminConnection
{
    private readonly string _adminConnectionString;
    private readonly ILogger<NpgsqlTenantAdminConnection> _logger;

    public NpgsqlTenantAdminConnection(
        IConfiguration configuration,
        ILogger<NpgsqlTenantAdminConnection>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var explicitAdmin = configuration.GetConnectionString("TenantAdmin");
        var fallback = configuration.GetConnectionString("DefaultConnection")
                       ?? configuration.GetConnectionString("ControlPlane");

        _adminConnectionString = !string.IsNullOrWhiteSpace(explicitAdmin)
            ? explicitAdmin!
            : fallback ?? throw new InvalidOperationException(
                "NpgsqlTenantAdminConnection requires ConnectionStrings:TenantAdmin "
                + "(preferred) or ConnectionStrings:DefaultConnection / "
                + "ConnectionStrings:ControlPlane to be configured.");

        _logger = logger ?? NullLogger<NpgsqlTenantAdminConnection>.Instance;
    }

    public async Task<bool> RoleExistsAsync(string roleName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("roleName must be supplied", nameof(roleName));

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_roles WHERE rolname = @rolname";
        cmd.Parameters.AddWithValue("rolname", roleName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    public async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("databaseName must be supplied", nameof(databaseName));

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @datname";
        cmd.Parameters.AddWithValue("datname", databaseName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    public async Task<int> ExecuteAsync(string commandText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            throw new ArgumentException("commandText must be supplied", nameof(commandText));

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        // Provisioning DDL can take a while (DROP DATABASE WITH FORCE on a
        // big DB, CREATE EXTENSION, etc.). Default Npgsql command timeout
        // is 30s; widen to 5m so a slow drop doesn't bubble up as a
        // spurious workflow failure. Workflow-level timeout still applies.
        cmd.CommandTimeout = (int)TimeSpan.FromMinutes(5).TotalSeconds;
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public string BuildTenantConnectionString(
        string databaseName,
        string roleName,
        string password)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("databaseName must be supplied", nameof(databaseName));
        if (string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("roleName must be supplied", nameof(roleName));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("password must be supplied", nameof(password));

        // Start from the admin connection so we inherit Host, Port, SSL,
        // TrustServerCertificate, Server-side cursors, etc. Then overwrite
        // identity-bearing fields with the tenant's role + DB.
        var b = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = databaseName,
            Username = roleName,
            Password = password,
            ApplicationName = $"tamma-tenant;db={databaseName}",
            // Pool sizing is owned by the LRU pool resolver; leave defaults
            // here so the resolver's per-tenant overrides win when it
            // builds the actual NpgsqlDataSource.
        };

        // Drop admin-only fields so the tenant string can't be used to
        // reach back as the admin role.
        b.Remove("Include Error Detail");

        return b.ConnectionString;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_adminConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "tenant.admin.open_failed");
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

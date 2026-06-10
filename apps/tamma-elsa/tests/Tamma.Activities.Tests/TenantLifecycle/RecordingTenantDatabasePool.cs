using Tamma.Data.Abstractions;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 — recording <see cref="ITenantDatabasePool"/>
/// fake shared by the delete-path activity tests
/// (<see cref="DropTenantSchemaActivityTests"/>,
/// <see cref="DropTenantRoleActivityTests"/>,
/// <see cref="BackupTenantDatabaseActivityTests"/>). Captures every
/// statement issued through <see cref="ExecuteOnAsync"/> together with
/// the pool row it targeted, so tests can assert both the SQL and the
/// placement routing.
/// </summary>
internal sealed class RecordingTenantDatabasePool : ITenantDatabasePool
{
    public List<(Guid DatabaseId, string CommandText)> ExecutedCommands { get; } = new();

    public bool RoleExists { get; set; }
    public int RoleExistsCalls { get; private set; }

    public bool SchemaExists { get; set; }
    public int SchemaExistsCalls { get; private set; }

    public string AdminConnectionString { get; set; } =
        "Host=pool.internal;Port=6432;Database=tamma_pool;Username=tamma_provisioner;Password=pool-secret-pw";

    public TenantAdminConnectionInfo Info { get; set; } = new(
        Host: "pool.internal",
        Port: 6432,
        Username: "tamma_provisioner",
        Password: "pool-secret-pw",
        Database: "tamma_pool");

    public Task<string> GetAdminConnectionStringAsync(
        Guid databaseId, CancellationToken ct = default) =>
        Task.FromResult(AdminConnectionString);

    public Task<int> ExecuteOnAsync(
        Guid databaseId, string commandText, CancellationToken ct = default)
    {
        ExecutedCommands.Add((databaseId, commandText));
        return Task.FromResult(0);
    }

    public List<(Guid DatabaseId, string CommandText)> ScalarQueries { get; } = new();

    /// <summary>Scalar handler — defaults to 0L for every query.</summary>
    public Func<Guid, string, object?> ScalarResult { get; set; } = (_, _) => 0L;

    public Task<object?> ExecuteScalarOnAsync(
        Guid databaseId, string commandText, CancellationToken ct = default)
    {
        ScalarQueries.Add((databaseId, commandText));
        return Task.FromResult(ScalarResult(databaseId, commandText));
    }

    public Task<bool> RoleExistsOnAsync(
        Guid databaseId, string roleName, CancellationToken ct = default)
    {
        RoleExistsCalls++;
        return Task.FromResult(RoleExists);
    }

    public Task<bool> SchemaExistsOnAsync(
        Guid databaseId, string schemaName, CancellationToken ct = default)
    {
        SchemaExistsCalls++;
        return Task.FromResult(SchemaExists);
    }

    public Task<string> GetDatabaseNameAsync(
        Guid databaseId, CancellationToken ct = default) =>
        Task.FromResult(Info.Database);

    public Task<string> BuildTenantConnectionStringAsync(
        Guid databaseId, string roleName, string password, string schemaName,
        CancellationToken ct = default) =>
        Task.FromResult(
            $"Host={Info.Host};Database={Info.Database};Username={roleName};Search Path={schemaName}");

    public Task<TenantAdminConnectionInfo> GetConnectionInfoAsync(
        Guid databaseId, CancellationToken ct = default) =>
        Task.FromResult(Info);

    public List<Guid> EvictedAdminConnections { get; } = new();

    public void EvictAdminConnection(Guid databaseId) =>
        EvictedAdminConnections.Add(databaseId);
}

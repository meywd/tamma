namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 28-5 — port that runs the per-tenant EF migration set against a
/// freshly-created tenant database. The production adapter constructs an
/// ad-hoc <see cref="DbContext"/> bound to the tenant connection string
/// and invokes <c>Database.MigrateAsync()</c>. Tests inject a stub that
/// simply records the request — running EF migrations against a real
/// Postgres in unit tests is too slow and out of scope.
///
/// <para>Contract:
/// <list type="bullet">
///   <item><description>Idempotent. EF's
///     <c>__EFMigrationsHistory</c> table tracks applied migrations; a
///     replay is a fast no-op.</description></item>
///   <item><description>Throws on failure (the workflow activity
///     classifies retryability per Doc 03 §5.3).</description></item>
///   <item><description>Two methods so the create workflow can address
///     the tenant app DB and (later) the tenant Elsa DB independently
///     without leaking which DbContext type to use into the workflow
///     definition.</description></item>
/// </list></para>
/// </summary>
public interface ITenantDbMigrator
{
    /// <summary>
    /// Run the tenant-app EF migration set against the database identified
    /// by <paramref name="tenantConnectionString"/>. The connection string
    /// must already grant the tenant role enough privilege to create the
    /// tenant tables (CREATEDB-on-template style permissions are not
    /// required at this point — the database itself is already there).
    /// </summary>
    Task MigrateTenantAppAsync(
        string tenantConnectionString,
        CancellationToken ct = default);

    /// <summary>
    /// Run the per-tenant Elsa migration set. Stubbed in the create
    /// workflow today (Step 5/6 of Doc 03) — the tenant Elsa DB story
    /// lands later. Provided here so the activity surface is stable.
    /// </summary>
    Task MigrateTenantElsaAsync(
        string tenantConnectionString,
        CancellationToken ct = default);
}

using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Unified-tenancy Phase 2 — the shared tenant provisioning step engine.
/// Ports the step logic of the Epic 28 lifecycle activities
/// (<c>CreateTenantRoleActivity</c>,
/// <c>EncryptAndPersistConnectionStringActivity</c>) onto the
/// <c>tenant_databases</c> pool: every DDL statement runs on the ASSIGNED
/// pool row's cluster via <see cref="ITenantDatabasePool"/> (roles are
/// cluster-scoped — the central admin connection is never correct for a
/// tenant placed elsewhere), and the tenant's data plane is a
/// <c>t_&lt;hex&gt;</c> schema + minted <c>Search Path</c> connection
/// string instead of a per-tenant database.
/// </summary>
public sealed class TenantProvisioningService : ITenantProvisioningService
{
    private readonly ITenantPlacementService _placement;
    private readonly ITenantDatabasePool _pool;
    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly ITenantDbMigrator _migrator;
    private readonly ITenantConnectionStringProtector _protector;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        ITenantPlacementService placement,
        ITenantDatabasePool pool,
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        ITenantDbMigrator migrator,
        ITenantConnectionStringProtector protector,
        ILogger<TenantProvisioningService> logger)
    {
        _placement = placement;
        _pool = pool;
        _cpFactory = cpFactory;
        _migrator = migrator;
        _protector = protector;
        _logger = logger;
    }

    public Task<TenantPlacement> AssignPlacementAsync(
        Guid tenantId, CancellationToken ct = default) =>
        _placement.AssignAsync(tenantId, ct);

    /// <summary>
    /// Ported verbatim from <c>CreateTenantRoleActivity.ProcessAsync</c>
    /// (Story 28-5), with the admin seam swapped for the placement row's
    /// pool connection. Idempotent via a pg_roles probe on the TARGET
    /// cluster; returns null on idempotent-skip — the caller decides
    /// whether a stored envelope from a prior run makes that recoverable.
    /// </summary>
    public async Task<string?> CreateRoleAsync(
        Guid tenantId, TenantPlacement placement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var roleName = TenantNaming.RoleName(tenantId);
        var quoted = TenantNaming.Quote(roleName);

        if (await _pool.RoleExistsOnAsync(placement.DatabaseId, roleName, ct))
        {
            // Leave the existing role in place. The encrypted connection
            // string from a prior run is the only path to recover the
            // password; if it was never persisted, the operator runbook
            // calls for DROP ROLE + retry (enforced in ProvisionAsync).
            _logger.LogInformation(
                "tenant.provisioning.create_role idempotent_skip tenantId={TenantId} role={Role} "
                + "databaseId={DatabaseId}",
                tenantId, roleName, placement.DatabaseId);
            return null;
        }

        var password = TenantRolePassword.Generate();

        // Defence-in-depth (mirrors the activity): the generator's alphabet
        // excludes single quotes, but never build a quoted SQL literal from
        // a candidate that could contain one.
        if (password.Contains('\''))
            throw new InvalidOperationException(
                "Generated password contained a quote — refusing to issue CREATE ROLE.");

        var sql =
            $"CREATE ROLE {quoted} WITH LOGIN PASSWORD '{password}' "
            + "NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;";

        await _pool.ExecuteOnAsync(placement.DatabaseId, sql, ct);

        _logger.LogInformation(
            "tenant.provisioning.create_role created tenantId={TenantId} role={Role} "
            + "databaseId={DatabaseId}",
            tenantId, roleName, placement.DatabaseId);
        return password;
    }

    /// <summary>
    /// Schema + grants on the placement row's database. All statements are
    /// idempotent (IF NOT EXISTS / re-grant / re-set). The tenant role
    /// OWNS its schema (CREATE SCHEMA AUTHORIZATION) so migrations can run
    /// under the tenant's own credentials; it gets NOTHING on
    /// <c>public</c> — PG15+ already denies PUBLIC CREATE there, and we
    /// deliberately do not touch public's USAGE (cluster-wide blast
    /// radius is out of scope).
    /// </summary>
    public async Task CreateSchemaAsync(
        Guid tenantId, TenantPlacement placement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var quotedRole = TenantNaming.Quote(TenantNaming.RoleName(tenantId));
        var quotedSchema = TenantNaming.Quote(placement.SchemaName);
        var databaseName = await _pool.GetDatabaseNameAsync(placement.DatabaseId, ct);
        var quotedDatabase = TenantNaming.Quote(databaseName);

        await _pool.ExecuteOnAsync(placement.DatabaseId,
            $"CREATE SCHEMA IF NOT EXISTS {quotedSchema} AUTHORIZATION {quotedRole};", ct);
        await _pool.ExecuteOnAsync(placement.DatabaseId,
            $"GRANT CONNECT ON DATABASE {quotedDatabase} TO {quotedRole};", ct);
        // Default search_path for the role in THIS database — belt and
        // braces alongside the connection string's Search Path key, and it
        // covers ad-hoc psql sessions as the tenant role.
        await _pool.ExecuteOnAsync(placement.DatabaseId,
            $"ALTER ROLE {quotedRole} IN DATABASE {quotedDatabase} SET search_path = {quotedSchema};",
            ct);

        _logger.LogInformation(
            "tenant.provisioning.create_schema completed tenantId={TenantId} schema={Schema} "
            + "database={Database} databaseId={DatabaseId}",
            tenantId, placement.SchemaName, databaseName, placement.DatabaseId);
    }

    public Task<string> BuildConnectionStringAsync(
        Guid tenantId, TenantPlacement placement, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return _pool.BuildTenantConnectionStringAsync(
            placement.DatabaseId,
            TenantNaming.RoleName(tenantId),
            password,
            placement.SchemaName,
            ct);
    }

    public async Task ProvisionAsync(Guid tenantId, CancellationToken ct = default)
    {
        var placement = await AssignPlacementAsync(tenantId, ct);
        var password = await CreateRoleAsync(tenantId, placement, ct);

        if (password is null && !await HasStoredEnvelopeAsync(tenantId, ct))
        {
            // Same recovery guidance the activity logs: an existing role
            // whose password was never sealed into the envelope is
            // unrecoverable by design.
            throw new InvalidOperationException(
                $"Tenant role '{TenantNaming.RoleName(tenantId)}' already exists on pool row "
                + $"{placement.DatabaseId} but tenants.EncryptedConnectionString is empty — the "
                + "password from the prior partial run is unrecoverable. Operator runbook: "
                + "connect to the placement database, run "
                + $"'DROP OWNED BY {TenantNaming.RoleName(tenantId)}' (drops the schema and its "
                + "contents), then 'DROP ROLE' for the same role, then retry provisioning.");
        }

        await CreateSchemaAsync(tenantId, placement, ct);

        string? connectionString = null;
        if (password is not null)
        {
            connectionString = await BuildConnectionStringAsync(tenantId, placement, password, ct);
            await _migrator.MigrateTenantAppAsync(connectionString, ct);
        }
        // else: idempotent re-run — the run that minted the stored envelope
        // already applied the migrations (MigrateAsync is a no-op replay
        // anyway, but we have no plaintext credentials to connect with).

        await PersistEnvelopeAndActivateAsync(tenantId, connectionString, ct);

        _logger.LogInformation(
            "tenant.provisioning.completed tenantId={TenantId} schema={Schema} "
            + "databaseId={DatabaseId} freshRole={FreshRole}",
            tenantId, placement.SchemaName, placement.DatabaseId, password is not null);

        // Deliberately NOT here (single-user synchronous path): welcome
        // email + pool warm-up — those are SaaS workflow concerns
        // (QueueWelcomeEmail / WarmTenantPool activities).
    }

    private async Task<bool> HasStoredEnvelopeAsync(Guid tenantId, CancellationToken ct)
    {
        // NOTE: a legacy db-per-tenant envelope (minted before Phase 2,
        // without a Search Path) would satisfy this check and cause
        // ProvisionAsync to skip re-creating the role and schema — moot
        // post-Task-4 (CreateTenantWorkflow activity deleted, zero rows
        // in prod), noted for completeness.
        await using var db = await _cpFactory.CreateDbContextAsync(ct);
        var envelope = await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => EF.Property<byte[]?>(t, "EncryptedConnectionString"))
            .FirstOrDefaultAsync(ct);
        return envelope is { Length: > 0 };
    }

    /// <summary>
    /// Encrypt + persist with the
    /// <c>EncryptAndPersistConnectionStringActivity.ShouldSkipReencrypt</c>-equivalent
    /// guard, then flip Status to 'active' — one SaveChanges. The guard
    /// applies to the no-fresh-password path (<paramref name="connectionString"/>
    /// null): re-encrypting an already-stored envelope under the same KEK
    /// would invalidate consumers that snapshot it. When a FRESH password
    /// was minted this run, we always (re-)encrypt — any prior envelope
    /// would seal a credential that no longer matches the role.
    /// </summary>
    private async Task PersistEnvelopeAndActivateAsync(
        Guid tenantId, string? connectionString, CancellationToken ct)
    {
        await using var db = await _cpFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException(
                $"Tenant '{tenantId}' not found — cannot persist provisioning outcome.");
        var entry = db.Entry(tenant);

        if (connectionString is not null)
        {
            var kek = _protector.CurrentKekVersion;
            var envelope = _protector.Encrypt(connectionString);
            entry.Property("EncryptedConnectionString").CurrentValue = envelope;
            entry.Property("KekVersion").CurrentValue = (short)kek;
            _logger.LogInformation(
                "tenant.provisioning.encrypt_creds persisted tenantId={TenantId} kek={Kek} "
                + "envelopeLen={Len}",
                tenantId, kek, envelope.Length);
        }
        else
        {
            // ShouldSkipReencrypt-equivalent: envelope already populated
            // from the prior run (verified by ProvisionAsync before the
            // schema step) — keep it byte-identical.
            _logger.LogInformation(
                "tenant.provisioning.encrypt_creds skipped (idempotent) tenantId={TenantId}",
                tenantId);
        }

        entry.Property<string?>("Status").CurrentValue = "active";
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

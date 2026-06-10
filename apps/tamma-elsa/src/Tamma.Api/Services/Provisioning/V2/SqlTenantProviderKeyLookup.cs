using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-8 — production <see cref="ITenantProviderKeyLookup"/> that
/// reads <c>tenants.provider_key</c> via parameterised raw SQL against
/// the <see cref="ControlPlaneDbContext"/>. Uses raw SQL (rather than
/// EF projections) for two reasons:
///
/// <list type="bullet">
///   <item><description>Story 30-3 owns the migration that adds the
///     physical <c>provider_key</c> column. Until 30-3 lands the
///     column may not exist — raw SQL lets us probe
///     <c>information_schema.columns</c> first and short-circuit to
///     <c>null</c> when the column is absent, instead of crashing the
///     LRU resolver on every cold-miss.</description></item>
///   <item><description>The lookup is on the hot path (every
///     LruPooledTenantConnectionResolver cold miss). Raw SQL avoids
///     the EF change-tracker overhead and stays close to a single
///     <c>SELECT … WHERE id = $1</c>.</description></item>
/// </list>
///
/// <para>Caches the column-existence probe for the life of the process
/// (the migration is one-way; the column doesn't disappear after
/// landing). When the column is missing this means <b>every</b>
/// lookup returns <c>null</c> without hitting the database — exactly
/// the behaviour the legacy fallback path needs.</para>
/// </summary>
public sealed class SqlTenantProviderKeyLookup : ITenantProviderKeyLookup
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly ILogger<SqlTenantProviderKeyLookup> _logger;

    /// <summary>
    /// Process-wide cache for the column-existence probe. Three states:
    /// <c>null</c> (not probed yet), <c>true</c> (column exists),
    /// <c>false</c> (column absent — Story 30-3 hasn't landed). The
    /// once-per-process probe keeps cold-miss latency at ~one SELECT.
    /// </summary>
    private bool? _columnExists;
    private readonly object _probeLock = new();

    public SqlTenantProviderKeyLookup(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        ILogger<SqlTenantProviderKeyLookup>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cpFactory);
        _cpFactory = cpFactory;
        _logger = logger ?? NullLogger<SqlTenantProviderKeyLookup>.Instance;
    }

    public async Task<string?> GetProviderKeyAsync(Guid tenantId, CancellationToken ct)
    {
        await using var ctx = await _cpFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Cheap one-time probe — Story 30-3 hasn't necessarily landed
        // yet on every environment we deploy onto, so we treat the
        // missing column as "no V2 routing for this deployment" and
        // fall back to the legacy path.
        if (!await EnsureColumnExistsAsync(ctx, ct).ConfigureAwait(false))
        {
            return null;
        }

        // First: check that the tenant exists at all so we can throw
        // TenantNotFoundException (the directory translates that to a
        // 404-equivalent path). Then read provider_key.
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
        // Column name is quoted PascalCase (`"ProviderKey"`) per the
        // Story 30-3 migration. Without quoting, Postgres folds the
        // identifier to lowercase (`provider_key`) which doesn't match
        // the case-preserved column → ExecuteScalar returns null →
        // V2 routing silently never activates. Same convention applies
        // for `"DeletedAt"`.
        cmd.CommandText =
            "SELECT \"ProviderKey\" FROM tenants WHERE \"Id\" = @id AND \"DeletedAt\" IS NULL LIMIT 1";
        var idParam = cmd.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = tenantId;
        cmd.Parameters.Add(idParam);

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null)
        {
            // No row — the tenant doesn't exist (or is soft-deleted).
            // Surface as TenantNotFoundException so the directory's
            // contract matches the legacy path's behaviour.
            throw new TenantNotFoundException(tenantId);
        }
        if (raw is DBNull)
        {
            // Row exists, provider_key is NULL → legacy tenant. Returns
            // null so the directory hands NotApplicable back to the
            // LRU resolver which falls through to the encrypted-column
            // legacy path.
            return null;
        }
        var key = (string)raw;
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>
    /// Probe <c>information_schema.columns</c> once for the existence
    /// of <c>tenants.provider_key</c>. Caches the answer in the
    /// process. The probe is cheap (~1ms) and is only executed at most
    /// once per process under normal operation.
    /// </summary>
    private async Task<bool> EnsureColumnExistsAsync(
        ControlPlaneDbContext ctx,
        CancellationToken ct)
    {
        // Snapshot under lock — multiple threads racing the first probe
        // each hit the DB once, but the result is settled deterministically.
        bool? cached;
        lock (_probeLock) { cached = _columnExists; }
        if (cached.HasValue) return cached.Value;

        try
        {
            var conn = ctx.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }

            await using var probe = conn.CreateCommand();
            // information_schema.columns stores column_name in
            // case-preserved form — the migration created the column as
            // `"ProviderKey"` (PascalCase) so the probe must match
            // exactly. A lowercase probe would always miss and force
            // every tenant onto the legacy fallback path.
            probe.CommandText = """
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'tenants' AND column_name = 'ProviderKey'
                LIMIT 1
                """;
            var found = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var exists = found is not null;
            lock (_probeLock) { _columnExists = exists; }
            if (!exists)
            {
                _logger.LogInformation(
                    "tenant.routing.provider_key_column_missing — falling back to legacy resolver path. " +
                    "Story 30-3 migration adds this column; run pending migrations to enable V2 routing.");
            }
            return exists;
        }
        catch (NpgsqlException ex)
        {
            // Treat probe failures as "column doesn't exist" so a
            // transient CP outage doesn't break every cold-miss with a
            // hard error. The legacy path will catch the same outage
            // on its row read and surface a sensible error.
            _logger.LogWarning(
                ex,
                "tenant.routing.provider_key_probe_failed — assuming column missing.");
            lock (_probeLock) { _columnExists = false; }
            return false;
        }
    }
}

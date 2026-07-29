using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Production <see cref="ITenantMigrationSweeper"/> (Story 44-1 AC8/AC9).
/// A caller over machinery that already exists and is already idempotent:
/// enumerate non-deleted <c>tenants</c> from the control plane, resolve each
/// through <see cref="ITenantConnectionResolver"/> (which decrypts the stored
/// per-tenant envelope), read the pending set off the tenant's own
/// <c>__TenantMigrationsHistory</c>, and — unless <c>dryRun</c> — replay via
/// <see cref="ITenantDataSourceDbMigrator.MigrateTenantAppAsync"/> (the
/// data-source flavour of <see cref="EfTenantDbMigrator"/>: the resolver's
/// <see cref="Npgsql.NpgsqlDataSource.ConnectionString"/> strips the password,
/// so the string-based seam cannot authenticate from here — see the
/// <see cref="ITenantDataSourceDbMigrator"/> doc).
///
/// <para>Failure isolation: every tenant is wrapped in its own try/catch; an
/// unreachable pool member, a missing envelope (<c>42501</c>/<c>53300</c>/…)
/// is a <c>failed</c> row, never an abort. Concurrency is bounded by a
/// semaphore because each migration takes a non-pooled physical connection
/// (<see cref="EfTenantDbMigrator"/>'s <c>Pooling=false</c> rationale).</para>
/// </summary>
public sealed class TenantMigrationSweeper : ITenantMigrationSweeper
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly ITenantConnectionResolver _resolver;
    private readonly ITenantDataSourceDbMigrator _migrator;
    private readonly ILogger<TenantMigrationSweeper> _logger;

    public TenantMigrationSweeper(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        ITenantConnectionResolver resolver,
        ITenantDataSourceDbMigrator migrator,
        ILogger<TenantMigrationSweeper>? logger = null)
    {
        _cpFactory = cpFactory;
        _resolver = resolver;
        _migrator = migrator;
        _logger = logger ?? NullLogger<TenantMigrationSweeper>.Instance;
    }

    public async Task<TenantMigrationSweepResult> SweepAsync(
        bool dryRun = false,
        int maxConcurrency = TenantMigrationSweep.DefaultMaxConcurrency,
        CancellationToken ct = default)
    {
        var bound = Math.Clamp(maxConcurrency, 1, 16);

        List<Guid> tenantIds;
        await using (var cp = await _cpFactory.CreateDbContextAsync(ct))
        {
            tenantIds = await cp.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.DeletedAt == null)
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Id)
                .ToListAsync(ct);
        }

        _logger.LogInformation(
            "tenant.migration_sweep.started tenants={Count} dryRun={DryRun} maxConcurrency={Bound}",
            tenantIds.Count, dryRun, bound);

        using var gate = new SemaphoreSlim(bound, bound);
        var entries = await Task.WhenAll(tenantIds.Select(async tenantId =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await SweepOneAsync(tenantId, dryRun, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var result = new TenantMigrationSweepResult(
            DryRun: dryRun,
            Total: entries.Length,
            Migrated: entries.Count(e => e.Outcome == TenantMigrationSweep.OutcomeMigrated),
            AlreadyCurrent: entries.Count(e => e.Outcome == TenantMigrationSweep.OutcomeAlreadyCurrent),
            Pending: entries.Count(e => e.Outcome == TenantMigrationSweep.OutcomePending),
            Failed: entries.Count(e => e.Outcome == TenantMigrationSweep.OutcomeFailed),
            Tenants: entries);

        _logger.LogInformation(
            "tenant.migration_sweep.completed total={Total} migrated={Migrated} "
            + "alreadyCurrent={AlreadyCurrent} pending={Pending} failed={Failed} dryRun={DryRun}",
            result.Total, result.Migrated, result.AlreadyCurrent, result.Pending,
            result.Failed, result.DryRun);
        return result;
    }

    private async Task<TenantMigrationSweepEntry> SweepOneAsync(
        Guid tenantId, bool dryRun, CancellationToken ct)
    {
        try
        {
            // The resolver owns the data source's lifetime (never dispose).
            // The migrator works OVER the data source — its .ConnectionString
            // has the password stripped by Npgsql, so nothing here may ever
            // treat it as a usable credential (only Search Path is read from
            // it, inside the migrator, for schema derivation).
            var dataSource = await _resolver.GetDataSourceAsync(tenantId, ct).ConfigureAwait(false);

            var pending = await _migrator.CountPendingMigrationsAsync(dataSource, ct)
                .ConfigureAwait(false);
            if (pending == 0)
            {
                return new TenantMigrationSweepEntry(
                    tenantId, TenantMigrationSweep.OutcomeAlreadyCurrent, 0, null);
            }

            if (dryRun)
            {
                return new TenantMigrationSweepEntry(
                    tenantId, TenantMigrationSweep.OutcomePending, pending, null);
            }

            await _migrator.MigrateTenantAppAsync(dataSource, ct).ConfigureAwait(false);
            return new TenantMigrationSweepEntry(
                tenantId, TenantMigrationSweep.OutcomeMigrated, pending, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            || !ct.IsCancellationRequested)
        {
            // Per-tenant isolation (AC8): one bad tenant is a row, not an abort.
            // An OperationCanceledException is only sweep-cancellation when the
            // SWEEP'S token is actually canceled — a provider/driver stack can
            // surface an OCE of its own (e.g. an internal timeout), and that is
            // one tenant's failure, never a reason to abort the whole
            // Task.WhenAll for the fleet.
            _logger.LogWarning(ex,
                "tenant.migration_sweep.tenant_failed tenantId={TenantId}", tenantId);
            return new TenantMigrationSweepEntry(
                tenantId, TenantMigrationSweep.OutcomeFailed, 0, ex.Message);
        }
    }

}

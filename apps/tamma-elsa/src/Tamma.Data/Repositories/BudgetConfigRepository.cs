using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Defaults;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IBudgetConfigRepository"/>.
///
/// <para>
/// Story 28-1 PR A (Decision #1, <c>.dev/decisions/story-28-1-design-calls.md</c>):
/// the legacy <c>budget_configs.tenant_id IS NULL</c> CP row no longer
/// carries the platform default. Reads with <c>tenantId == null</c> resolve
/// to <see cref="BudgetConfigDefaults"/>; writes are silently dropped with a
/// structured warning so callers see a stable response while defaults remain
/// code-resident.
/// </para>
///
/// <para>Tenant-scoped reads/writes (non-null <c>TenantId</c>) flow through
/// <see cref="ITenantDbContextFactory"/> exactly as before.</para>
/// </summary>
public class BudgetConfigRepository(
    ITenantDbContextFactory factory,
    ILogger<BudgetConfigRepository>? logger = null) : IBudgetConfigRepository
{
    private readonly ILogger<BudgetConfigRepository>? _logger = logger;

    public async Task<BudgetConfig?> GetAsync(Guid? tenantId, string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (tenantId is Guid tid)
        {
            await using var db = await factory.CreateAsync(tid, ct);
            return await db.BudgetConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.TenantId == tenantId && b.AccountId == accountId,
                    ct);
        }
        // Story 28-1 PR A: platform default lives in code now. Returning null
        // matches the prior "no row found" contract (callers fall through to
        // IConfiguration-derived defaults in PostgresBudgetConfigProvider).
        return null;
    }

    public async Task<BudgetConfig> UpsertAsync(BudgetConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccountId);

        if (config.TenantId is Guid tid)
        {
            await using var db = await factory.CreateAsync(tid, ct);
            return await UpsertInternal(db.BudgetConfigs, () => db.SaveChangesAsync(ct), config, ct);
        }

        // Story 28-1 PR A: platform-default writes are no-ops. Defaults live
        // in BudgetConfigDefaults / IConfiguration; mutating them via the
        // repo would silently shadow code defaults if the row reappeared.
        _logger?.LogWarning(
            "BudgetConfig.UpsertAsync called with tenantId=null for " +
            "accountId={AccountId} — platform defaults moved to code per " +
            "Story 28-1 Decision #1. Discarding the requested config.",
            config.AccountId);

        return BudgetConfigDefaults.Snapshot(config.AccountId);
    }

    private static async Task<BudgetConfig> UpsertInternal(
        DbSet<BudgetConfig> set, Func<Task<int>> save, BudgetConfig config, CancellationToken ct)
    {
        var existing = await set
            .FirstOrDefaultAsync(
                b => b.TenantId == config.TenantId && b.AccountId == config.AccountId,
                ct);

        if (existing is null)
        {
            var row = new BudgetConfig
            {
                TenantId = config.TenantId,
                AccountId = config.AccountId,
                LimitUsd = config.LimitUsd,
                AlertThreshold = config.AlertThreshold,
                PeriodDays = config.PeriodDays,
            };
            set.Add(row);
            await save();
            return row;
        }

        existing.LimitUsd = config.LimitUsd;
        existing.AlertThreshold = config.AlertThreshold;
        existing.PeriodDays = config.PeriodDays;
        existing.UpdatedAt = DateTime.UtcNow;
        await save();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid? tenantId, string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (tenantId is Guid tid)
        {
            await using var db = await factory.CreateAsync(tid, ct);
            var existing = await db.BudgetConfigs
                .FirstOrDefaultAsync(
                    b => b.TenantId == tenantId && b.AccountId == accountId,
                    ct);
            if (existing is null) return false;
            db.BudgetConfigs.Remove(existing);
            await db.SaveChangesAsync(ct);
            return true;
        }

        // Story 28-1 PR A: platform-default deletes are no-ops; nothing to
        // remove because the value comes from code, not a CP row.
        _logger?.LogWarning(
            "BudgetConfig.DeleteAsync called with tenantId=null for " +
            "accountId={AccountId} — defaults are code-resident; nothing to " +
            "remove (Story 28-1 Decision #1).",
            accountId);
        return false;
    }
}

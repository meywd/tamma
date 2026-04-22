using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IBudgetConfigRepository"/>.
///
/// <para>Epic 28: tenant-specific rows (<c>TenantId = &lt;guid&gt;</c>) flow
/// through <see cref="ITenantDbContextFactory"/>; the platform-default row
/// (<c>TenantId IS NULL</c>) lives on <see cref="ControlPlaneDbContext"/>.</para>
/// </summary>
public class BudgetConfigRepository(
    ITenantDbContextFactory factory,
    ControlPlaneDbContext cp) : IBudgetConfigRepository
{
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
        return await cp.BudgetConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.TenantId == tenantId && b.AccountId == accountId,
                ct);
    }

    public async Task<BudgetConfig> UpsertAsync(BudgetConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccountId);

        if (config.TenantId is Guid tid)
        {
            await using var db = await factory.CreateAsync(tid, ct);
            return await UpsertInternal(db.BudgetConfigs, () => db.SaveChangesAsync(ct), config);
        }
        return await UpsertInternal(cp.BudgetConfigs, () => cp.SaveChangesAsync(ct), config);
    }

    private static async Task<BudgetConfig> UpsertInternal(
        DbSet<BudgetConfig> set, Func<Task<int>> save, BudgetConfig config)
    {
        var existing = await set
            .FirstOrDefaultAsync(
                b => b.TenantId == config.TenantId && b.AccountId == config.AccountId);

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
        var cpExisting = await cp.BudgetConfigs
            .FirstOrDefaultAsync(
                b => b.TenantId == tenantId && b.AccountId == accountId,
                ct);
        if (cpExisting is null) return false;
        cp.BudgetConfigs.Remove(cpExisting);
        await cp.SaveChangesAsync(ct);
        return true;
    }
}

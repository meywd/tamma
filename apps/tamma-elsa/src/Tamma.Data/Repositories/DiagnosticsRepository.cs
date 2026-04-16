using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class DiagnosticsRepository(TammaDbContext db) : IDiagnosticsRepository
{
    public async Task<Guid> InsertAsync(ProviderDiagnostic diagnostic)
    {
        diagnostic.CreatedAt = DateTime.UtcNow;
        db.ProviderDiagnostics.Add(diagnostic);
        await db.SaveChangesAsync();
        return diagnostic.Id;
    }

    public async Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        string? providerKey, DateTime? from, DateTime? to, int limit, int offset)
    {
        var query = db.ProviderDiagnostics.AsQueryable();
        if (!string.IsNullOrEmpty(providerKey))
            query = query.Where(d => d.ProviderKey == providerKey);
        if (from.HasValue)
            query = query.Where(d => d.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(d => d.CreatedAt <= to.Value);
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(d => d.CreatedAt).Skip(offset).Take(limit).ToListAsync();
        return (items, total);
    }

    public Task<List<object>> GetReportAsync(DateTime from, DateTime to)
        => Task.FromResult(new List<object>());

    public Task<object> GetBudgetAsync(string accountId)
        => Task.FromResult<object>(new { accountId, used = 0m, limit = 0m });
}

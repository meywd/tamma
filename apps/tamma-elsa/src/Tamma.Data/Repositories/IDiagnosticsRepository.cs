using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IDiagnosticsRepository
{
    Task<Guid> InsertAsync(ProviderDiagnostic diagnostic);
    Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(string? providerKey, DateTime? from, DateTime? to, int limit, int offset);
    Task<List<object>> GetReportAsync(DateTime from, DateTime to);
    Task<object> GetBudgetAsync(string accountId);
}

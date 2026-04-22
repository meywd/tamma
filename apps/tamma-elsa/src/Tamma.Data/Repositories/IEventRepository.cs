using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IEventRepository
{
    Task<DomainEvent> AppendAsync(DomainEvent evt);
    Task<DomainEvent?> GetByIdAsync(Guid id);
    Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit);
    Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type);
    Task ClearAsync(Guid tenantId);
}

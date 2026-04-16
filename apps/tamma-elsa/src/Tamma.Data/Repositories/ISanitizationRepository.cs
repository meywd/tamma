using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface ISanitizationRepository
{
    Task<SanitizationRule?> GetRulesAsync(Guid? tenantId);
    Task<SanitizationRule> UpsertRulesAsync(Guid? tenantId, string rulesJson);
}

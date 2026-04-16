using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IPromptRepository
{
    Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action);
    Task<PromptOverride> UpsertAsync(PromptOverride prompt);
    Task<bool> DeleteAsync(Guid? userId, string scope, string? role, string? action);
    Task<List<PromptOverride>> ListAsync(Guid? userId);
}

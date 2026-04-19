using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IPromptRepository
{
    Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action);

    /// <summary>
    /// Upsert a prompt override. Returns the persisted entity and a flag
    /// indicating whether this was a fresh insert (<c>true</c>) or an update
    /// of an existing row (<c>false</c>). The flag drives DCB event emission
    /// (CREATED vs UPDATED) at the endpoint layer.
    /// </summary>
    Task<(PromptOverride Entity, bool WasCreated)> UpsertAsync(PromptOverride prompt, Guid? actingUserId = null);

    Task<bool> DeleteAsync(Guid? userId, string scope, string? role, string? action);
    Task<List<PromptOverride>> ListAsync(Guid? userId);
}

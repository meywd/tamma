using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for provider-health circuit-breaker state.
/// Only does CRUD against the <c>provider_health</c> table; full state-machine
/// semantics live in <c>Tamma.Api.Services.Providers.CircuitBreakerService</c>.
/// </summary>
public interface IProviderHealthRepository
{
    /// <summary>Fetch the row for <paramref name="providerKey"/>/<paramref name="tenantId"/>, or null.</summary>
    Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId);

    /// <summary>List all rows for a tenant (null for system-scope entries).</summary>
    Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId);

    /// <summary>
    /// Fetch the existing row or create a new one with defaults. The returned
    /// entity is change-tracked by the underlying DbContext — callers must call
    /// <see cref="SaveChangesAsync"/> after mutating.
    /// </summary>
    Task<ProviderHealth> GetOrCreateAsync(string providerKey, Guid? tenantId);

    /// <summary>Persist pending changes. Exposed so the service layer can batch mutations.</summary>
    Task SaveChangesAsync();
}

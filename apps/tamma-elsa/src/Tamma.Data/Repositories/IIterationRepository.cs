using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence seam for <c>iterations</c> (Story 44-1). Schema-only in this
/// story — Story 44-4 owns population, status transitions and the board
/// projection. <b>No mode split</b> (story AC7 / epic D6): an iteration is
/// tenant-schema content.
/// </summary>
public interface IIterationRepository
{
    Task<IterationEntity?> GetAsync(Guid id);
    Task<List<IterationEntity>> ListByProjectAsync(Guid projectId);

    /// <summary>Create an iteration. Status must be one of <c>planned|active|closed</c>.</summary>
    Task<IterationEntity> CreateAsync(IterationEntity iteration);

    /// <summary>Update name/dates/status/capacity. Bumps <see cref="IterationEntity.Version"/>.</summary>
    Task<IterationEntity?> UpdateAsync(IterationEntity iteration);

    /// <summary>
    /// Delete an iteration. Work items referencing it get
    /// <c>IterationId = NULL</c> (FK <c>SET NULL</c>) — items outlive sprints.
    /// </summary>
    Task<bool> DeleteAsync(Guid id);
}

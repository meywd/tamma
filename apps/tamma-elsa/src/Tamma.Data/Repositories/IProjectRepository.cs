using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence seam for <c>projects</c> (Story 44-1). Tenant-schema resident;
/// resolves through the ambient tenant like every tracker repository.
///
/// <para><b>No mode split (story AC7 / epic D6).</b> A project is tenant-schema
/// CONTENT, not per-principal configuration — there is deliberately no
/// user-plane / tenant-plane surface pair here (that pattern belongs to
/// <see cref="ITrackerPreferenceRepository"/> only).</para>
/// </summary>
public interface IProjectRepository
{
    Task<ProjectEntity?> GetAsync(Guid id);

    /// <summary>Lookup by the frozen key prefix (exact, never normalized).</summary>
    Task<ProjectEntity?> GetByKeyAsync(string key);

    Task<List<ProjectEntity>> ListAsync(bool includeArchived = false);

    /// <summary>
    /// Create a project. Validates <see cref="ProjectEntity.Key"/> against
    /// <c>WorkItemRef.IsValidProjectKey</c> and
    /// <see cref="ProjectEntity.EstimateScale"/> against the
    /// <c>EstimateScale</c> wire set — fail-loud <c>TammaError</c>, never a
    /// DB CHECK surprise.
    /// </summary>
    Task<ProjectEntity> CreateAsync(ProjectEntity project);

    /// <summary>
    /// Update name/description/repository binding/estimate scale/archive
    /// state. Bumps <see cref="ProjectEntity.Version"/>. Never touches
    /// <see cref="ProjectEntity.Key"/> (a re-key is
    /// <see cref="IWorkItemRepository.RekeyAsync"/>'s seam) or
    /// <see cref="ProjectEntity.NextNumber"/> (the mint owns it).
    /// </summary>
    Task<ProjectEntity?> UpdateAsync(ProjectEntity project);

    /// <summary>
    /// Delete a project. Work items RESTRICT the FK, so a non-empty project
    /// surfaces a constraint violation — 44-2 maps it to a 409.
    /// </summary>
    Task<bool> DeleteAsync(Guid id);
}

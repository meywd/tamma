namespace Tamma.Api.Services.Access;

/// <summary>
/// Story 39-20's canonical audience-resolution seam (Story 39-18 Design Decision D9).
/// Task delivery is ROLE-ADDRESSED (design review 2026-07-21): a task fans out to
/// every role-holder the resolver says may see it, not one user. 39-18 STUBS this
/// fail-closed behind <see cref="InitiatorOnlyTaskAudienceResolver"/> until 39-20
/// lands; 39-20 replaces the implementation (owning the canonical
/// <see cref="TaskRef"/>/<see cref="AudienceMember"/> shapes), so 39-18's tests run
/// against a capturing fake with no churn.
/// </summary>
public interface ITaskAudienceResolver
{
    /// <summary>
    /// Whether <paramref name="userId"/> may see <paramref name="task"/> (initiator or
    /// repo access — the per-user scoping that makes task delivery per-user, not merely
    /// per-tenant).
    /// </summary>
    Task<bool> CanSeeAsync(Guid userId, TaskRef task);

    /// <summary>
    /// The eligible audience for <paramref name="task"/> addressed to role
    /// <paramref name="roleWire"/> — every role-holder the task fans out to (one outbox
    /// row per member, D4).
    /// </summary>
    Task<IReadOnlyList<AudienceMember>> EligibleAudienceAsync(TaskRef task, string roleWire);
}

/// <summary>
/// The canonical task reference the audience is resolved against (39-20 owns this
/// shape; 39-18 pins it for the stub). Carries the tenant, the issue initiator, and
/// the repo/issue coordinates repo-access resolution needs.
/// </summary>
public sealed record TaskRef(Guid TenantId, Guid? InitiatorUserId, string? RepoKey, string? IssueId);

/// <summary>One resolved audience member (a user id + the role it holds).</summary>
public sealed record AudienceMember(Guid UserId, string RoleWire);

/// <summary>
/// Story 39-18 (D9) — the fail-closed default stub for 39-20's resolver: ONLY the
/// issue initiator sees anything. Until 39-20 (repo-access resolution) or 39-19's
/// stricter <c>ConservativeAudienceResolver</c> lands, nobody extra is admitted — the
/// safe direction. There is exactly ONE default stub, never two.
/// </summary>
public sealed class InitiatorOnlyTaskAudienceResolver : ITaskAudienceResolver
{
    public Task<bool> CanSeeAsync(Guid userId, TaskRef task)
        => Task.FromResult(task.InitiatorUserId is { } initiator && initiator == userId);

    public Task<IReadOnlyList<AudienceMember>> EligibleAudienceAsync(TaskRef task, string roleWire)
    {
        IReadOnlyList<AudienceMember> audience = task.InitiatorUserId is { } initiator
            ? new[] { new AudienceMember(initiator, roleWire) }
            : Array.Empty<AudienceMember>();
        return Task.FromResult(audience);
    }
}

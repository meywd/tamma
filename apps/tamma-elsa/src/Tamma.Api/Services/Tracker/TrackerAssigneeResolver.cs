using Tamma.Api.Dtos.Tracker;
using Tamma.Api.Services.Access;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Tracker;

/// <summary>
/// Story 44-2 AC6 / plan D5 — the assignee-picker source, with the honest
/// three-branch degradation the story requires.
///
/// <para><b>Why this class exists at all.</b>
/// <see cref="ITaskAudienceResolver.EligibleAudienceAsync"/> returns EMPTY today
/// for every input: the only implementation is
/// <see cref="InitiatorOnlyTaskAudienceResolver"/> (Story 39-18's fail-closed
/// stub) and its sole production consumer hardcodes
/// <c>InitiatorUserId: null</c> (<c>ChannelOutboxService.cs:143</c>). Calling it
/// naively yields an EMPTY assignee dropdown, which reads as a bug and generates
/// a support ticket. So: call it, and when it answers nothing, fall back to
/// tenant membership and SAY WHICH on the wire.</para>
///
/// <para><b>Why the <see cref="TaskRef"/> carries a null initiator.</b>
/// <c>/api/work-items/assignable</c> is a picker query, not a task: there is no
/// initiating user and no issue. Synthesising one (e.g. the caller, or an item's
/// creator) would make the stub answer with exactly that single user and the
/// picker would silently contain one name — the letter of AC6 satisfied and its
/// point defeated. Null is the truthful input, and it is the same value the one
/// real consumer passes.</para>
///
/// <para>When Story 39-20 replaces the DI registration, the first branch starts
/// answering and this class needs <b>no edit</b>.</para>
/// </summary>
public sealed class TrackerAssigneeResolver(
    ITaskAudienceResolver audienceResolver,
    ITammaModeProvider modeProvider,
    ITenantMembershipRepository memberships,
    ISoleUserProvider soleUser)
{
    /// <summary>The resolver answered — Story 39-20's real implementation is live.</summary>
    public const string SourceAudienceResolver = "audience-resolver";

    /// <summary>The resolver answered nothing; tenant membership is the v1 answer.</summary>
    public const string SourceTenantMembership = "tenant-membership";

    /// <summary>Single-user mode: there is exactly one principal.</summary>
    public const string SourceSingleUser = "single-user";

    /// <summary>
    /// The role wire the audience is requested for. <c>member</c> is the
    /// broadest role in the closed hierarchy, so it is the widest question this
    /// seam can ask; 39-20 owns any narrowing.
    /// </summary>
    private const string BroadestRoleWire = "member";

    /// <summary>Resolve the assignable set for the current principal/tenant.</summary>
    public async Task<AssignableResponse> ResolveAsync(Guid? tenantId, Guid? callerUserId)
    {
        if (modeProvider.Mode != TammaMode.SaaS)
        {
            // Single-user: the sole user. Prefer the authenticated caller, then
            // the sole-user provider; a hard failure there is a real
            // misconfiguration (GOVERNANCE.PRINCIPAL.NO_SOLE_USER) and is not
            // swallowed.
            var sole = callerUserId ?? await soleUser.GetSoleUserIdAsync();
            return new AssignableResponse(
                [new AssignableMemberResponse(sole, "owner")], SourceSingleUser);
        }

        if (tenantId is not Guid tenant)
        {
            throw new TammaError(
                "TRACKER.PRINCIPAL_UNRESOLVED",
                "No tenant context — a SaaS assignee query requires a resolvable tenant.",
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        var audience = await audienceResolver.EligibleAudienceAsync(
            new TaskRef(tenant, InitiatorUserId: null, RepoKey: null, IssueId: null),
            BroadestRoleWire);
        if (audience.Count > 0)
        {
            return new AssignableResponse(
                audience.Select(m => new AssignableMemberResponse(m.UserId, m.RoleWire)).ToList(),
                SourceAudienceResolver);
        }

        var members = await memberships.ListAllByTenantAsync(tenant);
        return new AssignableResponse(
            members.Select(m => new AssignableMemberResponse(m.UserId, m.Role)).ToList(),
            SourceTenantMembership);
    }
}

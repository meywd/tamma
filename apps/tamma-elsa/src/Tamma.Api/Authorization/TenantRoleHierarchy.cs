namespace Tamma.Api.Authorization;

/// <summary>
/// Tenant role hierarchy mirror of the deleted TS
/// <c>packages/api/src/routes/orgs/index.ts</c> ROLE_HIERARCHY map:
/// <c>{ owner: 2, admin: 1, member: 0 }</c>.
///
/// <para>Used by finding 012 (update-member-role) and finding 013
/// (delete-member) to enforce role-precedence invariants:</para>
/// <list type="bullet">
///   <item>Only an owner can change an owner-level role.</item>
///   <item>An admin cannot promote to or above their own level.</item>
///   <item>An admin cannot remove an owner.</item>
/// </list>
/// </summary>
public static class TenantRoleHierarchy
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { Owner, Admin, Member };

    public static bool IsValid(string? role)
        => role is not null && Allowed.Contains(role);

    /// <summary>
    /// Integer rank: owner=2, admin=1, member=0. Unknown → -1 so comparisons
    /// against known-valid roles naturally reject.
    /// </summary>
    public static int Level(string? role) => role switch
    {
        Owner => 2,
        Admin => 1,
        Member => 0,
        _ => -1,
    };

    public static bool IsAtLeast(string? role, string minRole)
        => Level(role) >= Level(minRole);
}

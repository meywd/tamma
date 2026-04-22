using System.Text.RegularExpressions;

namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-7 AC8 — guards the
/// <see cref="PostgresRoleRotationHandler"/> against rotating a role
/// it does not "own". Without this check a malformed or tampered
/// secret metadata row could drive the handler into
/// <c>ALTER ROLE postgres WITH PASSWORD ...</c>.
///
/// <para>Platform-scope whitelist is a fixed set: <c>tamma_app</c>,
/// <c>tamma_engine</c>, <c>tamma_provisioner</c>. The self-rotation
/// target (<c>tamma_admin</c> / <c>postgres</c>) is intentionally
/// excluded — rotating the role you are connected as is a foot-gun.
/// Operators rotate the admin role manually with a fresh
/// out-of-band connection.</para>
///
/// <para>Tenant-scope whitelist is a regex:
/// <c>^tamma_tenant_[0-9a-f]{32}$</c>. Each tenant DB has exactly one
/// app role matching this shape — see Story 28-5 /
/// <c>CreateTenantRoleActivity</c>.</para>
/// </summary>
public static class RoleWhitelist
{
    public static readonly IReadOnlySet<string> PlatformRoles = new HashSet<string>
    {
        "tamma_app",
        "tamma_engine",
        "tamma_provisioner",
    };

    public static readonly Regex TenantRolePattern = new(
        @"^tamma_tenant_[0-9a-f]{32}$",
        RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="roleName"/> is allowed to be rotated
    /// at the supplied scope. Operators call this before interpolating
    /// the role name into SQL.
    /// </summary>
    public static bool IsAllowed(string roleName, bool isTenantScope)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return false;
        return isTenantScope
            ? TenantRolePattern.IsMatch(roleName)
            : PlatformRoles.Contains(roleName);
    }
}

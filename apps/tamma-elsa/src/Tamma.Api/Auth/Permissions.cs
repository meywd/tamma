using System.Security.Claims;

namespace Tamma.Api.Auth;

public static class Permissions
{
    private static readonly Dictionary<string, int> RoleHierarchy = new()
    {
        ["member"] = 0,
        ["admin"] = 1,
        ["owner"] = 2
    };

    public static readonly Dictionary<string, string[]> Matrix = new()
    {
        ["dashboard:view"] = ["member", "admin", "owner"],
        ["workflows:view"] = ["member", "admin", "owner"],
        ["workflows:manage"] = ["admin", "owner"],
        ["workflows:delete"] = ["owner"],
        ["users:view"] = ["admin", "owner"],
        ["users:manage"] = ["owner"],
        ["admin:access"] = ["admin", "owner"],
        ["logs:access"] = ["admin", "owner"],
        ["elsa:access"] = ["admin", "owner"],
        ["settings:view"] = ["admin", "owner"],
        ["settings:manage"] = ["owner"],
        ["apikeys:manage"] = ["admin", "owner"],
        // Story 27-3 — Prompt Store tenant-admin path. CLAUDE.md
        // "Prompt Store Architecture / RBAC" requires PUT/DELETE override to
        // be allowed for `tenant_owner` OR `tenant_admin` (not member-only),
        // which the existing `settings:manage` permission (owner-only) does
        // NOT cover. We therefore add a dedicated `prompts:manage` permission
        // with admin+owner reach. Single-user mode is unaffected — every
        // signed-up user is auto-`owner` of their personal tenant, so the new
        // permission still grants them edit access without a code-path split.
        ["prompts:manage"] = ["admin", "owner"],
        // Story 27-10 — convention store tenant-admin path. Mirrors
        // `prompts:manage`: CLAUDE.md "Prompt Store Architecture / RBAC" (the
        // convention store follows the same tenant-scoped RBAC) requires
        // PUT/DELETE of a tenant override to be reachable by `tenant_owner` OR
        // `tenant_admin` (not member). The owner-only `settings:manage` would
        // 403 every tenant_admin, so the dedicated `conventions:manage`
        // permission grants admin+owner reach. Single-user mode is unaffected —
        // every signed-up user is auto-`owner` of their personal tenant.
        ["conventions:manage"] = ["admin", "owner"],
        // Story 31-9 — onboarding platform picker / connect. The
        // existing `settings:manage` permission is owner-only and
        // would 403 every tenant_admin trying to wire a platform
        // installation; the existing prompts:manage is named for
        // prompts only. A dedicated `platforms:manage` permission
        // with admin+owner reach keeps the picker accessible to
        // tenant admins per the Epic 31 RBAC plan.
        ["platforms:manage"] = ["admin", "owner"],
        // Story 32-1 — first-class agent entity management. Mirrors
        // prompts:manage / conventions:manage: CLAUDE.md "Prompt Store
        // Architecture / RBAC" (agents follow the same tenant-scoped RBAC)
        // requires create/publish/archive of a PRIVATE agent to be reachable
        // by tenant_owner OR tenant_admin (member → 403). Public-agent writes
        // are additionally gated by the platform-admin claim in the handler.
        // The owner-only settings:manage would 403 every tenant_admin, so the
        // dedicated agents:manage permission grants admin+owner reach.
        // Single-user mode is unaffected — every signed-up user is auto-owner
        // of their personal tenant.
        ["agents:manage"] = ["admin", "owner"],
        // Story 34-3 — BYOK pricing-mode management. Mirrors prompts:manage /
        // conventions:manage / agents:manage: CLAUDE.md "Operating Modes" makes
        // per-(tenant, provider) BYOK a tenant-scoped setting reachable by
        // tenant_owner OR tenant_admin (member → 403). The owner-only
        // settings:manage (the spec's SettingsManage label) would 403 every
        // tenant_admin, so the dedicated pricing:manage permission grants
        // admin+owner reach. Single-user mode is unaffected — every signed-up
        // user is auto-owner of their personal tenant.
        ["pricing:manage"] = ["admin", "owner"],
        // Story 39-5 — acceptance-rules management. Mirrors prompts:manage /
        // conventions:manage: CLAUDE.md "Prompt Store Architecture / RBAC" (the
        // acceptance-rules store follows the same tenant-scoped RBAC) requires
        // PUT/DELETE of a tenant override to be reachable by tenant_owner OR
        // tenant_admin (member → 403). The owner-only settings:manage would 403
        // every tenant_admin, so the dedicated acceptance-rules:manage permission
        // grants admin+owner reach. Single-user mode is unaffected — every
        // signed-up user is auto-owner of their personal tenant.
        ["acceptance-rules:manage"] = ["admin", "owner"],
        // Story 41-30 (D8) — scheduled-trigger management. Mirrors
        // prompts:manage: a tenant's schedule create/update/delete/run-now
        // must be reachable by tenant_owner OR tenant_admin (member → 403);
        // the owner-only settings:manage would 403 every tenant_admin.
        // tenant_id-NULL template rows are additionally platform-owner-only
        // in the handler. Single-user mode is unaffected — every signed-up
        // user is auto-owner of their personal tenant.
        ["schedules:manage"] = ["admin", "owner"],
        // Story 43-5/43-6 — Action Catalog automation toggles. Mirrors
        // acceptance-rules:manage: a tenant's per-action / per-group autonomy
        // assignments must be reachable by tenant_owner OR tenant_admin
        // (member → 403); the owner-only settings:manage would 403 every
        // tenant_admin. The PLATFORM CEILING is deliberately NOT covered by
        // this permission — ceiling writes ride PlatformOwnerAccess (the
        // platformRole=platform_admin claim), because the ceiling is the only
        // thing standing between a tenant admin and full automation of a
        // destructive action (epic 43 README OQ4). One permission for the
        // whole gating plane — tools:manage is never created. Single-user
        // mode is unaffected — every signed-up user is auto-owner of their
        // personal tenant.
        ["actions:manage"] = ["admin", "owner"],
    };

    public static bool HasPermission(string? role, string? permission)
    {
        // Story 16-5 audit: defensive null handling. Callers (RoleCheck endpoint,
        // PermissionHandler) already short-circuit when the role claim is missing,
        // but HasPermission itself must not throw on null inputs — failing
        // closed (return false) is the safe default for an authz primitive.
        if (role is null || permission is null)
            return false;

        if (!RoleHierarchy.TryGetValue(role, out var roleRank))
            return false;

        if (!Matrix.TryGetValue(permission, out var allowedRoles))
            return false;

        var minRank = int.MaxValue;
        foreach (var r in allowedRoles)
        {
            if (RoleHierarchy.TryGetValue(r, out var rank) && rank < minRank)
                minRank = rank;
        }

        return roleRank >= minRank;
    }

    /// <summary>
    /// Principal-shaped overload — resolves the caller's role via
    /// <see cref="ClaimsPrincipal.IsInRole"/>, which respects each identity's
    /// OWN <c>RoleClaimType</c>: bare <c>"role"</c> for production JwtBearer
    /// identities (Program.cs sets <c>MapInboundClaims=false</c> +
    /// <c>RoleClaimType="role"</c>, matching the shape <see cref="JwtService"/>
    /// mints) and <see cref="ClaimTypes.Role"/> for identities built with the
    /// <see cref="ClaimsIdentity"/> default. A hardcoded
    /// <c>FindFirst(ClaimTypes.Role)</c> never matched a real bearer JWT, which
    /// fail-closed every <see cref="PermissionRequirement"/> policy for API
    /// tokens — see
    /// <c>.dev/bugs/2026-07-29-permission-handler-role-claim-mismatch.md</c>.
    /// </summary>
    public static bool HasPermission(ClaimsPrincipal? user, string? permission)
    {
        if (user is null || permission is null)
            return false;

        // Known roles are the closed hierarchy; probing each via IsInRole is
        // both claim-shape-agnostic and fail-closed for unknown role values.
        foreach (var role in RoleHierarchy.Keys)
        {
            if (user.IsInRole(role) && HasPermission(role, permission))
                return true;
        }

        return false;
    }

    public static string[] GetRolePermissions(string? role)
    {
        if (role is null) return Array.Empty<string>();
        return Matrix
            .Where(kv => HasPermission(role, kv.Key))
            .Select(kv => kv.Key)
            .ToArray();
    }
}

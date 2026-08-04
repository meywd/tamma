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
        // Story 44-2 (AC4) — the native tracker. TWO permissions, deliberately
        // split, because the tracker is the first surface in this repo where
        // the two halves have genuinely different blast radii:
        //  * tracker:view (member+) gates work-item CREATE, PATCH, status and
        //    assignment — a tracker in which a `member` cannot file a bug or
        //    move a card is not a tracker. This is why <noun>:manage alone is
        //    wrong here.
        //  * tracker:manage (admin+owner) gates PROJECT and (at 44-4) iteration
        //    STRUCTURE, the tracker_preferences row, AND the work-item DELETE:
        //    a project key rename or delete changes every identifier everyone
        //    else quotes; in SaaS the preference row is TENANT-wide
        //    configuration (there is no per-user plane in SaaS), so it follows
        //    the prompt/convention/acceptance-rules store precedent; and the
        //    work-item delete is a HARD delete.
        //
        // NO OWNERSHIP PLANE EXISTS YET (adversarial review, 2026-07-29). Be
        // precise about what tracker:view actually admits: TrackerService does
        // NOT check creator or assignee on any route, so a `member` may patch,
        // re-status and re-assign ANY work item in the tenant, not merely "their
        // own" — and with AC7 degrading to tenant-wide visibility, they can see
        // every item too. That is accepted for the RECOVERABLE writes (AC4's
        // normative clause; a bad patch is repairable). It is NOT accepted for
        // the hard delete, which 44-2 catalogues as Destructive/reversible:false
        // and which emits no event at all in this story (44-5 owns emission) —
        // unrecoverable AND unaudited. Hence DELETE /api/work-items/{id} rides
        // tracker:manage until an ownership plane (Story 39-20's resolver) or
        // the 44-5 audit trail lands and the gate can be reconsidered.
        // Neither reuses settings:manage, which is owner-only and would 403
        // every tenant_admin. Single-user mode is unaffected — every signed-up
        // user is auto-owner of their personal tenant.
        ["tracker:view"] = ["member", "admin", "owner"],
        ["tracker:manage"] = ["admin", "owner"],
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

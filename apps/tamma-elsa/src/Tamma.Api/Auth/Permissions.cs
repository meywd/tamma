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

    public static string[] GetRolePermissions(string? role)
    {
        if (role is null) return Array.Empty<string>();
        return Matrix
            .Where(kv => HasPermission(role, kv.Key))
            .Select(kv => kv.Key)
            .ToArray();
    }
}

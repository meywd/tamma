/**
 * RBAC Permission Matrix
 *
 * Defines the three-tier role system (member, admin, owner) and
 * maps each permission to the minimum role required.
 *
 * Permissions follow the pattern: 'resource:action'.
 * Role hierarchy: member < admin < owner.
 */

/** The three user roles in ascending privilege order. */
export type Role = 'member' | 'admin' | 'owner';

/** Numeric weight for each role — used for hierarchy comparisons. */
const ROLE_HIERARCHY: Record<Role, number> = {
  member: 0,
  admin: 1,
  owner: 2,
};

/**
 * Central permission matrix.
 *
 * Each key is a permission string ('resource:action') and its value
 * is the array of roles that are explicitly granted the permission.
 * However we use the role hierarchy — any role *above* the minimum
 * listed role also inherits the permission.
 *
 * The minimum role for each permission is the lowest-rank role in
 * the array. We store arrays for readability and auditing, but the
 * runtime check uses hierarchy comparison against the minimum.
 */
export const PERMISSIONS = {
  'dashboard:view': ['member', 'admin', 'owner'],
  'workflows:view': ['member', 'admin', 'owner'],
  'workflows:manage': ['admin', 'owner'],
  'workflows:delete': ['owner'],
  'users:view': ['admin', 'owner'],
  'users:manage': ['owner'],
  'admin:access': ['admin', 'owner'],
  'logs:access': ['admin', 'owner'],
  'elsa:access': ['admin', 'owner'],
  'settings:view': ['admin', 'owner'],
  'settings:manage': ['owner'],
  'apikeys:manage': ['admin', 'owner'],
} as const;

/** A valid permission key from the matrix. */
export type Permission = keyof typeof PERMISSIONS;

/**
 * Derive the minimum role required for a given permission
 * by finding the lowest-ranked role in the allowed list.
 */
function getMinimumRole(permission: Permission): Role {
  const roles = PERMISSIONS[permission];
  let minRank = Infinity;
  let minRole: Role = 'owner';
  for (const role of roles) {
    const rank = ROLE_HIERARCHY[role as Role];
    if (rank !== undefined && rank < minRank) {
      minRank = rank;
      minRole = role as Role;
    }
  }
  return minRole;
}

/**
 * Check whether a given role has a specific permission.
 *
 * Uses role hierarchy: owner > admin > member.
 * If the permission key does not exist, returns false.
 */
export function hasPermission(role: Role, permission: Permission): boolean {
  const roleRank = ROLE_HIERARCHY[role];
  if (roleRank === undefined) return false;

  const minimumRole = getMinimumRole(permission);
  const minimumRank = ROLE_HIERARCHY[minimumRole];
  if (minimumRank === undefined) return false;

  return roleRank >= minimumRank;
}

/**
 * Return all permissions granted to a given role.
 */
export function getRolePermissions(role: Role): Permission[] {
  const result: Permission[] = [];
  for (const key of Object.keys(PERMISSIONS) as Permission[]) {
    if (hasPermission(role, key)) {
      result.push(key);
    }
  }
  return result;
}

/**
 * Check whether a string is a valid Role.
 */
export function isValidRole(value: string): value is Role {
  return value === 'member' || value === 'admin' || value === 'owner';
}

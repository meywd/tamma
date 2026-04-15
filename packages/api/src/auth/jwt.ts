/**
 * JWT utilities for user auth (Stories 18-1, 18-2, 18-3).
 *
 * Defines the UnifiedJwtPayload contract that all auth flows
 * (email+password, GitHub OAuth) produce. This ensures downstream
 * services can rely on a single token format.
 */

import type { AuthMethod } from '../persistence/user-store.js';

/** Platform-level role (global, not per-tenant). */
export type PlatformRole = 'user' | 'platform_admin';

/** Tenant-level role. */
export type TenantRole = 'member' | 'admin' | 'owner';

/**
 * Unified JWT payload contract.
 *
 * Both OAuth flows (admin, end-user) and email+password login
 * produce this same structure.
 */
export interface UnifiedJwtPayload {
  /** User UUID (primary identifier). */
  sub: string;
  /** Active tenant/org ID (null for users without a tenant). */
  tenantId: string | null;
  /** User's role within the active tenant. */
  role: TenantRole;
  /** Global platform-level role. */
  platformRole: PlatformRole;
  /** User email. */
  email: string;
  /** Display name. */
  name: string;
  /** How the user authenticated. */
  authMethod: AuthMethod;
  /** Issued at (epoch seconds). */
  iat: number;
  /** Expiry (epoch seconds). */
  exp: number;
}

/** Build the JWT claims object (without iat/exp which are auto-set by fastify-jwt). */
export function buildJwtClaims(
  userId: string,
  email: string,
  name: string,
  tenantId: string | null,
  tenantRole: TenantRole,
  platformRole: PlatformRole,
  authMethod: AuthMethod,
): Omit<UnifiedJwtPayload, 'iat' | 'exp'> {
  return {
    sub: userId,
    tenantId,
    role: tenantRole,
    platformRole,
    email,
    name,
    authMethod,
  };
}

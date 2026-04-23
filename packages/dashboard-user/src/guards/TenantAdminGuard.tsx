/**
 * TenantAdminGuard — wraps routes that require tenant admin (or
 * owner) role. The underlying `user.role` is populated by
 * /api/auth/me from the caller's membership row in the active
 * tenant; this is the same string the backend's
 * TenantRoleHierarchy compares against (`owner` > `admin` > `member`).
 *
 * When the caller is authenticated but only a member, we render a
 * 403-style message rather than redirecting. Anonymous callers are
 * handled by the outer <AuthGuard>, so we treat `user === null` as
 * impossible here and fail closed.
 */

import type { ReactNode } from 'react';
import { useAuth } from '../hooks/useAuth';

const ADMIN_OR_HIGHER = new Set(['admin', 'owner']);

export interface TenantAdminGuardProps {
  children: ReactNode;
}

export function TenantAdminGuard({
  children,
}: TenantAdminGuardProps): JSX.Element {
  const { user } = useAuth();

  if (user === null) {
    // Outer AuthGuard should have handled this; fail closed anyway.
    return (
      <div role="alert" className="p-4 text-sm text-red-700 bg-red-50 rounded-md">
        Not authenticated.
      </div>
    );
  }

  const role = user.role ?? '';
  if (!ADMIN_OR_HIGHER.has(role)) {
    return (
      <div
        role="alert"
        className="max-w-md mx-auto mt-16 p-6 bg-white rounded-lg shadow-sm border border-gray-200 text-center"
      >
        <h2 className="text-lg font-medium text-gray-900">Admin-only</h2>
        <p className="mt-2 text-sm text-gray-500">
          You need tenant admin or owner role to access this page.
        </p>
      </div>
    );
  }

  return <>{children}</>;
}

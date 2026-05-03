/**
 * Tenant-Admin Route Guard (Story 18-8).
 *
 * Reads the caller's role inside their currently-active tenant from
 * `/auth/me`. Renders the wrapped page only when the role is
 * `owner` or `admin`. Members get a friendly 403 with copy from the
 * brief AC §8.
 *
 * Note: this is **independent** of the existing `AdminGuard`, which
 * gates routes on the platform role. A user can be a tenant admin
 * without being a platform admin and vice-versa.
 */

import type { ReactNode, JSX } from 'react';
import { useCurrentTenant } from '../hooks/orgs/useCurrentTenant.js';
import { LoadingSpinner } from '../components/common/LoadingSpinner.js';

interface TenantAdminGuardProps {
  children: ReactNode;
}

export function TenantAdminGuard({ children }: TenantAdminGuardProps): JSX.Element {
  const { loading, role, tenantId, error } = useCurrentTenant();

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-[60vh]">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-4 m-6 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
        Failed to verify your tenant role: {error}
      </div>
    );
  }

  // No active tenant at all — render an org-picker hint rather than 403,
  // since the user might just not have switched into one yet.
  if (!tenantId) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <h1 className="text-xl font-semibold text-gray-900 mb-2 dark:text-gray-100">
          No active organization
        </h1>
        <p className="text-gray-500 mb-6 max-w-md dark:text-gray-400">
          You're not currently in any organization. Create or join one
          first to manage members.
        </p>
      </div>
    );
  }

  if (role !== 'owner' && role !== 'admin') {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <div className="text-6xl font-bold text-gray-300 mb-4">403</div>
        <h1 className="text-xl font-semibold text-gray-900 mb-2 dark:text-gray-100">
          Admin access required
        </h1>
        <p className="text-gray-500 mb-6 max-w-md dark:text-gray-400">
          You need admin or owner role in this organization to view
          member management.
        </p>
      </div>
    );
  }

  return <>{children}</>;
}

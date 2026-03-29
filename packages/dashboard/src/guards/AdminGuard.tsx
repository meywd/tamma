/**
 * Admin Route Guard
 *
 * Protects admin-only routes by checking the current user's role.
 * - Shows a loading spinner while fetching user info.
 * - Redirects to "/" with a toast-style notification if the user is not admin/owner.
 * - Renders children if authorized.
 */

import { Navigate } from 'react-router-dom';
import { useCurrentUser } from '../hooks/admin/useCurrentUser.js';
import { LoadingSpinner } from '../components/common/LoadingSpinner.js';

interface AdminGuardProps {
  children: React.ReactNode;
}

export function AdminGuard({ children }: AdminGuardProps): JSX.Element {
  const { user, loading, isAdmin } = useCurrentUser();

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-[60vh]">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (!user || !isAdmin) {
    return <Navigate to="/account" replace />;
  }

  return <>{children}</>;
}

/**
 * Forbidden page shown inline when needed (e.g. deep-linked directly).
 */
export function ForbiddenPage(): JSX.Element {
  return (
    <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
      <div className="text-6xl font-bold text-gray-300 mb-4">403</div>
      <h1 className="text-xl font-semibold text-gray-900 mb-2">Access Denied</h1>
      <p className="text-gray-500 mb-6">
        You do not have permission to access the admin panel.
      </p>
      <a
        href="/"
        className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
      >
        Go to Dashboard
      </a>
    </div>
  );
}

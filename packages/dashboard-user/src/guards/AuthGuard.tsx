/**
 * AuthGuard — wraps protected routes. Anonymous users get redirected to
 * `/login?redirect=<original-path>`; authenticated users see the
 * rendered children. While the initial /auth/me call is in flight we
 * render a neutral placeholder so the user never sees a flash of the
 * login page.
 */

import type { ReactNode, JSX } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { useAuth } from '../hooks/useAuth';

export interface AuthGuardProps {
  children: ReactNode;
}

export function AuthGuard({ children }: AuthGuardProps): JSX.Element {
  const { user, loading } = useAuth();
  const location = useLocation();

  if (loading) {
    return (
      <div
        role="status"
        aria-live="polite"
        className="min-h-screen flex items-center justify-center text-gray-500"
      >
        Loading...
      </div>
    );
  }

  if (user === null) {
    const redirect = encodeURIComponent(location.pathname + location.search);
    return <Navigate to={`/login?redirect=${redirect}`} replace />;
  }

  return <>{children}</>;
}

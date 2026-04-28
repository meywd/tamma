/**
 * Auth Guard — Requires authentication for wrapped routes.
 *
 * - Loading → spinner
 * - No user → redirect to /login
 * - Has user → render children
 */

import { Navigate } from 'react-router-dom';
import { useAuth } from '../hooks/useAuth.js';
import { LoadingSpinner } from '../components/common/LoadingSpinner.js';

import type { JSX } from "react";

interface AuthGuardProps {
  children: React.ReactNode;
}

export function AuthGuard({ children }: AuthGuardProps): JSX.Element {
  const { user, loading } = useAuth();

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (!user) {
    return <Navigate to="/login" replace />;
  }

  return <>{children}</>;
}

/**
 * NotFoundPage — the catch-all 404 (Story 45-2 AC5).
 *
 * Before this existed, an unmatched path rendered the router with nothing
 * matched: a blank pane for anonymous users, an empty content area inside the
 * shell for signed-in ones. Every URL the API emails a customer landed here.
 * The page echoes the path (React escapes it), links home, and offers /login
 * to anonymous visitors.
 */

import type { JSX } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../hooks/useAuth';

export function NotFoundPage(): JSX.Element {
  const location = useLocation();
  const { user } = useAuth();

  return (
    <div className="flex flex-col items-center justify-center min-h-[40vh] text-center px-4 py-12">
      <p className="text-5xl font-bold text-gray-300 mb-4" aria-hidden="true">
        404
      </p>
      <h1 className="text-xl font-semibold text-gray-900 mb-2">Page not found</h1>
      <p className="text-sm text-gray-500 mb-6 max-w-md break-all">
        There is no page at <span className="font-mono">{location.pathname}</span>.
      </p>
      <div className="flex gap-3">
        <Link
          to="/"
          className="px-4 py-2 text-sm font-medium text-white bg-gray-900 hover:bg-gray-800 rounded-md"
        >
          Go to dashboard
        </Link>
        {user === null && (
          <Link
            to="/login"
            className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
          >
            Sign in
          </Link>
        )}
      </div>
    </div>
  );
}

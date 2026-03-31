/**
 * Account Page — Shows the current user's profile info.
 *
 * Displays avatar, username, GitHub ID, email, role (read-only).
 * Provides a sign-out button.
 */

import { useAuth } from '../hooks/useAuth.js';
import { useCurrentUser } from '../hooks/admin/useCurrentUser.js';

export function AccountPage(): JSX.Element {
  const { user } = useAuth();
  const { user: fullUser } = useCurrentUser();

  if (!user) {
    return <div className="p-6 text-gray-500">Not authenticated.</div>;
  }

  function handleSignOut(): void {
    fetch('/api/auth/logout', { method: 'POST', credentials: 'include' })
      .finally(() => {
        window.location.href = '/login';
      });
  }

  return (
    <div className="p-6 max-w-2xl">
      <h1 className="text-2xl font-bold text-gray-900 mb-6">My Account</h1>

      <div className="bg-white rounded-lg border border-gray-200 p-6">
        <div className="flex items-center gap-4 mb-6">
          <img
            src={`https://github.com/${user.username}.png?size=80`}
            alt={user.username}
            className="w-16 h-16 rounded-full border-2 border-gray-200"
          />
          <div>
            <div className="text-lg font-semibold text-gray-900">{user.username}</div>
            <div className="text-sm text-gray-500">GitHub ID: {user.githubId}</div>
          </div>
        </div>

        <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-3 text-sm">
          <dt className="font-medium text-gray-500">Role</dt>
          <dd className="text-gray-900">
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-800">
              {user.role}
            </span>
          </dd>

          {fullUser?.email && (
            <>
              <dt className="font-medium text-gray-500">Email</dt>
              <dd className="text-gray-900">{fullUser.email}</dd>
            </>
          )}
        </dl>
      </div>

      <div className="mt-6">
        <button
          onClick={handleSignOut}
          className="px-4 py-2 text-sm font-medium text-red-600 border border-red-300 rounded-md hover:bg-red-50 transition-colors"
        >
          Sign Out
        </button>
      </div>
    </div>
  );
}

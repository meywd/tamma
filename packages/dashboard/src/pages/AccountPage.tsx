/**
 * Account Page — Shows the current user's profile info.
 *
 * Displays avatar, username, GitHub ID, email, role (read-only).
 * Provides a sign-out button.
 */

import { useAuth } from '../hooks/useAuth.js';
import { useCurrentUser } from '../hooks/admin/useCurrentUser.js';

import type { JSX } from "react";

export function AccountPage(): JSX.Element {
  const { user, logout } = useAuth();
  const { user: fullUser } = useCurrentUser();

  if (!user) {
    return <div className="p-6 text-gray-500 dark:text-gray-400">Not authenticated.</div>;
  }

  return (
    <div className="p-6 max-w-2xl">
      <h1 className="text-2xl font-bold text-gray-900 mb-6 dark:text-gray-100">My Account</h1>

      <div className="bg-white rounded-lg border border-gray-200 p-6 dark:bg-gray-800 dark:border-gray-700">
        <div className="flex items-center gap-4 mb-6">
          <img
            src={`https://github.com/${user.username}.png?size=80`}
            alt={user.username}
            className="w-16 h-16 rounded-full border-2 border-gray-200 dark:border-gray-700"
          />
          <div>
            <div className="text-lg font-semibold text-gray-900 dark:text-gray-100">{user.username}</div>
            <div className="text-sm text-gray-500 dark:text-gray-400">GitHub ID: {user.githubId}</div>
          </div>
        </div>

        <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-3 text-sm">
          <dt className="font-medium text-gray-500 dark:text-gray-400">Role</dt>
          <dd className="text-gray-900 dark:text-gray-100">
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200">
              {user.role}
            </span>
          </dd>

          {fullUser?.email && (
            <>
              <dt className="font-medium text-gray-500 dark:text-gray-400">Email</dt>
              <dd className="text-gray-900 dark:text-gray-100">{fullUser.email}</dd>
            </>
          )}
        </dl>
      </div>

      <div className="mt-6">
        <button
          onClick={logout}
          className="px-4 py-2 text-sm font-medium text-red-600 border border-red-300 rounded-md hover:bg-red-50 transition-colors dark:text-red-400 dark:border-red-700 dark:hover:bg-red-950"
        >
          Sign Out
        </button>
      </div>
    </div>
  );
}

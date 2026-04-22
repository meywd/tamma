/**
 * AppLayout — shell for authenticated routes. Sidebar + top bar + content.
 *
 * Minimal for story 18-5 shell; the full nav / org switcher / notification
 * bell land in later story-18-5 sub-tasks (see impl plan steps 5–9).
 */

import { Link, Outlet } from 'react-router-dom';
import { useAuth } from '../hooks/useAuth';

export function AppLayout(): JSX.Element {
  const { user, logout } = useAuth();

  return (
    <div className="min-h-screen flex bg-gray-50">
      <aside className="w-56 bg-white border-r border-gray-200 p-4">
        <div className="font-bold text-gray-900 mb-6">Tamma</div>
        <nav className="flex flex-col gap-1 text-sm">
          <Link to="/" className="px-2 py-1.5 rounded hover:bg-gray-100">
            Dashboard
          </Link>
          <Link to="/repos" className="px-2 py-1.5 rounded hover:bg-gray-100">
            Repositories
          </Link>
          <Link to="/runs" className="px-2 py-1.5 rounded hover:bg-gray-100">
            Runs
          </Link>
          <Link to="/settings" className="px-2 py-1.5 rounded hover:bg-gray-100">
            Settings
          </Link>
        </nav>
      </aside>

      <main className="flex-1 flex flex-col">
        <header className="h-12 bg-white border-b border-gray-200 flex items-center justify-end px-4 text-sm">
          <span className="text-gray-600 mr-3">{user?.email}</span>
          <button
            type="button"
            onClick={() => {
              void logout();
            }}
            className="text-gray-700 hover:text-gray-900"
          >
            Sign out
          </button>
        </header>
        <div className="flex-1 p-6">
          <Outlet />
        </div>
      </main>
    </div>
  );
}

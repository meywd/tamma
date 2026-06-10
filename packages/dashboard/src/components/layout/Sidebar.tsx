
import { NavLink } from 'react-router-dom';
import { useCurrentUser } from '../../hooks/admin/useCurrentUser.js';

import type { JSX } from "react";

interface NavGroup {
  label: string;
  items: { to: string; label: string }[];
}

const MEMBER_NAV_GROUPS: NavGroup[] = [
  {
    label: 'My Account',
    items: [
      { to: '/account', label: 'Account' },
      { to: '/keys', label: 'API Keys' },
      { to: '/settings/prompts', label: 'AI Prompts' },
      // Story 27-12: tenant convention management.
      { to: '/settings/conventions', label: 'Conventions' },
    ],
  },
  {
    // Story 18-8: tenant-admin user-mgmt UI. Visible to all members; the
    // TenantAdminGuard renders a friendly 403 if the user lacks admin/
    // owner role inside their active tenant.
    label: 'Organization',
    items: [{ to: '/settings/organization', label: 'Members & Audit' }],
  },
];

const ADMIN_NAV_GROUPS: NavGroup[] = [
  ...MEMBER_NAV_GROUPS,
  {
    label: 'Knowledge Base',
    items: [{ to: '/dashboard', label: 'Dashboard' }],
  },
  {
    label: 'Settings',
    items: [
      { to: '/settings/agents', label: 'Agents' },
      { to: '/settings/phases', label: 'Phase Mapping' },
      { to: '/settings/security', label: 'Security' },
      { to: '/settings/health', label: 'Provider Health' },
      { to: '/settings/budget', label: 'Budget & Cost' },
    ],
  },
  {
    label: 'Administration',
    items: [
      { to: '/admin', label: 'Admin Panel' },
      // Story 27-4: prompt-store admin UI.
      { to: '/admin/prompts', label: 'System Prompts' },
      // Story 27-11: convention admin UI.
      { to: '/admin/conventions', label: 'System Conventions' },
    ],
  },
];

export function Sidebar(): JSX.Element {
  const { isAdmin } = useCurrentUser();

  const navGroups = isAdmin ? ADMIN_NAV_GROUPS : MEMBER_NAV_GROUPS;

  return (
    <nav className="w-60 shrink-0 bg-gray-800 text-gray-100 py-6 flex flex-col">
      <div className="px-5 mb-8 flex items-center gap-2">
        <img src="/logo.png" alt="Tamma" className="w-8 h-8 rounded" />
        <span className="text-lg font-bold tracking-tight">Tamma</span>
      </div>
      {navGroups.map((group) => (
        <div key={group.label} className="mb-4">
          <div className="px-5 mb-1 text-xs font-semibold uppercase tracking-wider text-gray-400 dark:text-gray-500">
            {group.label}
          </div>
          <ul className="list-none m-0 p-0">
            {group.items.map((item) => (
              <li key={item.to}>
                <NavLink
                  to={item.to}
                  end={item.to === '/'}
                  className={({ isActive }) =>
                    `block w-full px-5 py-2.5 text-sm border-l-3 transition-colors ${
                      isActive
                        ? 'bg-gray-700 text-white font-semibold border-blue-500'
                        : 'text-gray-300 border-transparent hover:bg-gray-700/50 hover:text-white'
                    }`
                  }
                >
                  {item.label}
                </NavLink>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </nav>
  );
}

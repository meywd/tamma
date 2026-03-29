
import { NavLink } from 'react-router-dom';
import { useCurrentUser } from '../../hooks/admin/useCurrentUser.js';

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
    ],
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
      { to: '/settings/prompts', label: 'Prompt Templates' },
    ],
  },
  {
    label: 'Administration',
    items: [{ to: '/admin', label: 'Admin Panel' }],
  },
];

export function Sidebar(): JSX.Element {
  const { isAdmin } = useCurrentUser();

  const navGroups = isAdmin ? ADMIN_NAV_GROUPS : MEMBER_NAV_GROUPS;

  return (
    <nav className="w-60 shrink-0 bg-gray-800 text-gray-100 py-6 flex flex-col">
      <div className="px-5 mb-8 text-lg font-bold tracking-tight">Tamma</div>
      {navGroups.map((group) => (
        <div key={group.label} className="mb-4">
          <div className="px-5 mb-1 text-xs font-semibold uppercase tracking-wider text-gray-400">
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

/**
 * GovernanceTab — the admin surface for the autonomy-dial governance API
 * (`/api/actions/*`).
 *
 * Three sections, mirroring the PricingTab sub-tab shell:
 *   Dial & catalog          — where the dial sits and what it governs (read-only)
 *   Overrides               — per-group / per-action policy rows + reset-all
 *   Pending authorizations  — actions waiting on a human decision (approve/deny)
 *
 * Rendered inside the admin dashboard's AdminGuard chain; the server enforces
 * its own RBAC on every route regardless (reads AuthenticatedAny, writes and
 * decides ActionsManage). The platform ceiling routes are platform-owner only
 * and are deliberately not surfaced here.
 */

import { useState, type JSX } from 'react';
import { DialPanel } from './DialPanel.js';
import { OverridesPanel } from './OverridesPanel.js';
import { AuthorizationsPanel } from './AuthorizationsPanel.js';

type GovernanceSubTab = 'dial' | 'overrides' | 'authorizations';

interface SubTabDef {
  id: GovernanceSubTab;
  label: string;
}

const SUB_TABS: SubTabDef[] = [
  { id: 'dial', label: 'Dial & catalog' },
  { id: 'overrides', label: 'Overrides' },
  { id: 'authorizations', label: 'Pending authorizations' },
];

export function GovernanceTab(): JSX.Element {
  const [active, setActive] = useState<GovernanceSubTab>('dial');

  return (
    <div className="space-y-4">
      <div className="border-b border-gray-200 dark:border-gray-700">
        <nav className="flex -mb-px space-x-6" aria-label="Governance sub-tabs">
          {SUB_TABS.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActive(tab.id)}
              className={`py-2 px-1 border-b-2 text-sm font-medium transition-colors ${
                active === tab.id
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
              } dark:text-gray-400`}
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>

      <div>
        {active === 'dial' && <DialPanel />}
        {active === 'overrides' && <OverridesPanel />}
        {active === 'authorizations' && <AuthorizationsPanel />}
      </div>
    </div>
  );
}

/**
 * Story 34-9 — the platform-owner PRICING tab shell.
 *
 * Rendered only inside the admin dashboard's existing `AdminGuard` chain
 * (AdminLayout mounts this behind `/admin`, which is wrapped by AdminGuard), so
 * every sub-panel it hosts is platform-owner-gated UX-side; the server enforces
 * `PlatformOwnerAccess` on every route regardless (AC1).
 *
 * Sub-tabs mirror the shipped admin endpoints:
 *   Overview      → GET  /api/admin/pricing/overview        (34-9)
 *   Plans         → CRUD /api/admin/pricing/plans*          (34-2)
 *   Margins       → GET/PUT /api/admin/pricing/margins      (34-5)
 *   Custom plans  → POST /api/admin/pricing/plans/custom    (34-2) + assignment (34-4)
 *
 * Promo/credit management (AC4) is intentionally NOT surfaced here yet — the
 * 34-7 promo/credit endpoints are not shipped; the panel lands with them.
 */

import { useState, type JSX } from 'react';
import { PricingOverviewPanel } from './PricingOverviewPanel.js';
import { PlanVersionEditor } from './PlanVersionEditor.js';
import { MarginPolicyPanel } from './MarginPolicyPanel.js';
import { CustomPlanPanel } from './CustomPlanPanel.js';

type PricingSubTab = 'overview' | 'plans' | 'margins' | 'custom';

interface SubTabDef {
  id: PricingSubTab;
  label: string;
}

const SUB_TABS: SubTabDef[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'plans', label: 'Plans' },
  { id: 'margins', label: 'Margins' },
  { id: 'custom', label: 'Custom Plans' },
];

export function PricingTab(): JSX.Element {
  const [active, setActive] = useState<PricingSubTab>('overview');

  return (
    <div className="space-y-4">
      <div className="border-b border-gray-200 dark:border-gray-700">
        <nav className="flex -mb-px space-x-6" aria-label="Pricing sub-tabs">
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
        {active === 'overview' && <PricingOverviewPanel />}
        {active === 'plans' && <PlanVersionEditor />}
        {active === 'margins' && <MarginPolicyPanel />}
        {active === 'custom' && <CustomPlanPanel />}
      </div>
    </div>
  );
}

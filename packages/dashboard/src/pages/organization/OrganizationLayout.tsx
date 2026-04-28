/**
 * Organization (tenant) admin shell — Story 18-8.
 *
 * Tabbed page mirroring the platform-admin AdminLayout pattern:
 * - Members  — list, change-role dropdown, remove member
 * - Invites  — pending list, create invite, resend, revoke
 * - Audit    — tenant-scoped audit log of every member-mgmt event
 *
 * Wrapped by `TenantAdminGuard` at the route level so members see a
 * 403 page instead of empty tabs.
 */

import { useState, type JSX } from 'react';
import { MembersTab } from '../../components/organization/MembersTab.js';
import { InvitesTab } from '../../components/organization/InvitesTab.js';
import { AuditTab } from '../../components/organization/AuditTab.js';
import { useCurrentTenant } from '../../hooks/orgs/useCurrentTenant.js';

type OrgTab = 'members' | 'invites' | 'audit';

interface TabDef {
  id: OrgTab;
  label: string;
}

const TABS: TabDef[] = [
  { id: 'members', label: 'Members' },
  { id: 'invites', label: 'Invites' },
  { id: 'audit', label: 'Audit Log' },
];

export function OrganizationLayout(): JSX.Element {
  const [activeTab, setActiveTab] = useState<OrgTab>('members');
  const { me, tenantId } = useCurrentTenant();

  const tenantName = me?.memberships.find((m) => m.tenantId === tenantId)?.tenantName
    ?? 'Organization';

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-1">{tenantName}</h1>
      <p className="text-sm text-gray-500 mb-6">Manage members, invites, and audit log.</p>

      {/* Tab Navigation */}
      <div className="border-b border-gray-200 mb-6">
        <nav className="flex -mb-px space-x-8" aria-label="Organization tabs">
          {TABS.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveTab(tab.id)}
              className={`py-3 px-1 border-b-2 text-sm font-medium transition-colors ${
                activeTab === tab.id
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>

      {/* Tab Content */}
      <div>
        {activeTab === 'members' && <MembersTab />}
        {activeTab === 'invites' && <InvitesTab />}
        {activeTab === 'audit' && <AuditTab />}
      </div>
    </div>
  );
}

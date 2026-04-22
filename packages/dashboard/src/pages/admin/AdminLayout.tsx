/**
 * Admin Panel — Tabbed Layout
 *
 * Top-level admin page with tabs: Users, API Keys, System Health, Quick Links.
 * Story 28-11 adds a Tenants tab that links to the dedicated
 * /admin/tenants roster + detail pages.
 */

import { useState } from 'react';
import { Link } from 'react-router-dom';
import { UsersTab } from './UsersTab.js';
import { ApiKeysTab } from './ApiKeysTab.js';
import { HealthTab } from './HealthTab.js';
import { QuickLinksTab } from './QuickLinksTab.js';
import { AuditLogTab } from './AuditLogTab.js';

type AdminTab = 'users' | 'api-keys' | 'health' | 'links' | 'audit-log' | 'tenants';

interface TabDef {
  id: AdminTab;
  label: string;
}

const TABS: TabDef[] = [
  { id: 'users', label: 'Users' },
  { id: 'tenants', label: 'Tenants' },
  { id: 'api-keys', label: 'API Keys' },
  { id: 'health', label: 'System Health' },
  { id: 'links', label: 'Quick Links' },
  { id: 'audit-log', label: 'Audit Log' },
];

function TenantsLinkPanel(): JSX.Element {
  return (
    <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-6">
      <h2 className="text-lg font-semibold text-gray-900 mb-2">Tenants</h2>
      <p className="text-sm text-gray-600 mb-4">
        View every tenant&apos;s lifecycle status, recent platform events,
        and run state-gated admin actions (retry provisioning, initiate
        delete, force-delete stuck tenants, change plan).
      </p>
      <Link
        to="/admin/tenants"
        className="inline-flex items-center px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
      >
        Open tenants roster
      </Link>
    </div>
  );
}

export function AdminLayout(): JSX.Element {
  const [activeTab, setActiveTab] = useState<AdminTab>('users');

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6">Admin Panel</h1>

      {/* Tab Navigation */}
      <div className="border-b border-gray-200 mb-6">
        <nav className="flex -mb-px space-x-8" aria-label="Admin tabs">
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
        {activeTab === 'users' && <UsersTab />}
        {activeTab === 'tenants' && <TenantsLinkPanel />}
        {activeTab === 'api-keys' && <ApiKeysTab />}
        {activeTab === 'health' && <HealthTab />}
        {activeTab === 'links' && <QuickLinksTab />}
        {activeTab === 'audit-log' && <AuditLogTab />}
      </div>
    </div>
  );
}

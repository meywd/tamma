/**
 * Admin Panel — Tabbed Layout
 *
 * Top-level admin page with tabs: Users, API Keys, System Health, Quick Links.
 */

import { useState } from 'react';
import { UsersTab } from './UsersTab.js';
import { ApiKeysTab } from './ApiKeysTab.js';
import { HealthTab } from './HealthTab.js';
import { QuickLinksTab } from './QuickLinksTab.js';

type AdminTab = 'users' | 'api-keys' | 'health' | 'links';

interface TabDef {
  id: AdminTab;
  label: string;
}

const TABS: TabDef[] = [
  { id: 'users', label: 'Users' },
  { id: 'api-keys', label: 'API Keys' },
  { id: 'health', label: 'System Health' },
  { id: 'links', label: 'Quick Links' },
];

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
        {activeTab === 'api-keys' && <ApiKeysTab />}
        {activeTab === 'health' && <HealthTab />}
        {activeTab === 'links' && <QuickLinksTab />}
      </div>
    </div>
  );
}

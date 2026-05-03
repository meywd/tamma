/**
 * PromptsAdminPage (Story 27-4)
 *
 * Tabbed admin page for managing the prompt store. Mirrors the pattern
 * from `AdminLayout.tsx` and `OrganizationLayout.tsx` (Story 18-8).
 *
 * Tabs:
 *   - Templates       — 80-cell role+action matrix; click to edit override
 *   - System Prompts  — 8 role identity preambles
 *   - Action Defaults — 10 layer-4 safety-net templates (read-only v1)
 *   - Conventions     — 20 starter convention templates (read-only browser)
 *
 * RBAC: route is wrapped in `AdminGuard` so only admin/owner reach this
 * component.
 */

import { useState, type JSX } from 'react';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import { ActionDefaultsList } from '../../../components/prompts/ActionDefaultsList.js';
import { ConventionPreview } from '../../../components/prompts/ConventionPreview.js';
import { PromptEditDialog } from '../../../components/prompts/PromptEditDialog.js';
import { PromptTable } from '../../../components/prompts/PromptTable.js';
import { SystemPromptEditor } from '../../../components/prompts/SystemPromptEditor.js';
import { useSystemPrompts } from '../../../hooks/admin/useSystemPrompts.js';

type PromptsTab = 'templates' | 'system-prompts' | 'action-defaults' | 'conventions';

interface TabDef {
  id: PromptsTab;
  label: string;
}

const TABS: TabDef[] = [
  { id: 'templates', label: 'Role + Action Templates' },
  { id: 'system-prompts', label: 'System Prompts' },
  { id: 'action-defaults', label: 'Action Defaults' },
  { id: 'conventions', label: 'Conventions' },
];

export function PromptsAdminPage(): JSX.Element {
  const {
    data,
    loading,
    error,
    reload,
    getResolved,
    upsertOverride,
    resetOverride,
    upsertSystemPromptOverride,
    resetSystemPromptOverride,
  } = useSystemPrompts();

  const [activeTab, setActiveTab] = useState<PromptsTab>('templates');
  const [selected, setSelected] = useState<{ role: string; action: string } | null>(
    null,
  );

  const openEditor = (role: string, action: string) => {
    setSelected({ role, action });
  };

  const closeEditor = () => setSelected(null);

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-1 dark:text-gray-100">System Prompts</h1>
      <p className="text-sm text-gray-500 mb-6 dark:text-gray-400">
        Manage the role+action template grid, identity preambles, action
        defaults, and convention starters used by every Elsa workflow.
      </p>

      {/* Tab nav */}
      <div className="border-b border-gray-200 mb-6 dark:border-gray-700">
        <nav className="flex -mb-px space-x-8" aria-label="Prompts tabs">
          {TABS.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveTab(tab.id)}
              className={`py-3 px-1 border-b-2 text-sm font-medium transition-colors ${ activeTab === tab.id ? 'border-blue-500 text-blue-600' : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300' } dark:text-gray-400`}
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>

      {/* Tab content */}
      {loading && data === null ? (
        <div className="flex justify-center py-16">
          <LoadingSpinner size="lg" />
        </div>
      ) : error ? (
        <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
          <div className="font-medium mb-1">Failed to load prompts</div>
          <div className="mb-3">{error}</div>
          <button
            type="button"
            onClick={() => void reload()}
            className="px-3 py-1.5 text-xs font-medium text-red-700 border border-red-300 bg-white rounded-md hover:bg-red-100 dark:bg-gray-800 dark:text-red-300 dark:border-red-700"
          >
            Retry
          </button>
        </div>
      ) : data ? (
        <>
          {activeTab === 'templates' && (
            <PromptTable
              prompts={data.roleActionTemplates}
              onRowClick={openEditor}
            />
          )}
          {activeTab === 'system-prompts' && (
            <SystemPromptEditor
              systemPrompts={data.systemPrompts}
              upsertSystemPromptOverride={upsertSystemPromptOverride}
              resetSystemPromptOverride={resetSystemPromptOverride}
            />
          )}
          {activeTab === 'action-defaults' && (
            <ActionDefaultsList
              actionDefaults={data.actionDefaults}
              onCustomise={() => setActiveTab('templates')}
            />
          )}
          {activeTab === 'conventions' && <ConventionPreview />}
        </>
      ) : null}

      {/* Edit drawer */}
      {selected && data && (
        <PromptEditDialog
          role={selected.role}
          action={selected.action}
          systemDefault={data.roleActionTemplates.find(
            (p) => p.role === selected.role && p.action === selected.action,
          )}
          onClose={closeEditor}
          onChanged={() => void reload()}
          loadResolved={getResolved}
          saveOverride={upsertOverride}
          resetOverride={resetOverride}
        />
      )}
    </div>
  );
}

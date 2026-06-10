/**
 * ConventionsAdminPage (Story 27-11)
 *
 * Platform-owner page for managing system-default conventions. Mirrors the
 * structure of PromptsAdminPage — tabbed layout, table, inline split panel.
 *
 * RBAC: wrapped in AdminGuard so only admin/owner (platform) can reach this page.
 *
 * Features:
 *   - Filterable table: role filter + enabled toggle + text search across body.
 *   - Row click → inline split edit panel (ConventionEditor).
 *   - New Convention button → blank editor; (role, action) immutable after creation.
 *   - Save → PUT; Reset → POST .../reset; Delete → DELETE with confirmation.
 *   - All changes require confirmation dialog (delete, reset, disable).
 *   - Resolution Test panel at top of edit view (collapsible).
 *   - "System Seed" badge on every cell.
 *   - Error states inline (API failures, validation, 400 ineligible).
 */

import { useState, type JSX } from 'react';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import { ConventionTable } from '../../../components/conventions/ConventionTable.js';
import { ConventionEditor } from '../../../components/conventions/ConventionEditor.js';
import { useAdminConventions } from '../../../hooks/admin/useAdminConventions.js';
import type { ConventionResponse } from '../../../services/admin/conventions-api-client.js';

export function ConventionsAdminPage(): JSX.Element {
  const {
    conventions,
    roles,
    eligiblePairs,
    loading,
    error,
    reload,
    getDefault,
    upsert,
    reset,
    remove,
  } = useAdminConventions();

  const [selected, setSelected] = useState<{ convention: ConventionResponse | null; isNew: boolean } | null>(null);

  const openEditor = (role: string, action: string) => {
    const convention = conventions.find((c) => c.role === role && c.action === action) ?? null;
    setSelected({ convention, isNew: false });
  };

  const openNew = () => {
    setSelected({ convention: null, isNew: true });
  };

  const closeEditor = () => setSelected(null);

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-1 dark:text-gray-100">System Conventions</h1>
      <p className="text-sm text-gray-500 mb-6 dark:text-gray-400">
        Manage the system-default convention definitions used by every Elsa workflow.
        Each (role, action) cell is seeded from <code className="font-mono bg-gray-100 dark:bg-gray-800 px-1 rounded">ConventionSeedSpecs</code>.
        Editing here saves a platform-level override; Reset restores the seed value.
      </p>

      {loading && conventions.length === 0 ? (
        <div className="flex justify-center py-16">
          <LoadingSpinner size="lg" />
        </div>
      ) : error ? (
        <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
          <div className="font-medium mb-1">Failed to load conventions</div>
          <div className="mb-3">{error}</div>
          <button
            type="button"
            onClick={() => void reload()}
            className="px-3 py-1.5 text-xs font-medium text-red-700 border border-red-300 bg-white rounded-md hover:bg-red-100 dark:bg-gray-800 dark:text-red-300 dark:border-red-700"
          >
            Retry
          </button>
        </div>
      ) : (
        <ConventionTable
          conventions={conventions}
          onRowClick={openEditor}
          onNewClick={openNew}
        />
      )}

      {selected && (
        <ConventionEditor
          convention={selected.convention}
          isNew={selected.isNew}
          roles={roles}
          eligiblePairs={eligiblePairs}
          getDefault={getDefault}
          onSave={upsert}
          onReset={reset}
          onDelete={remove}
          onClose={closeEditor}
          onChanged={() => void reload()}
        />
      )}
    </div>
  );
}

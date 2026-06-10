/**
 * ConventionsPage — tenant member "Conventions" page (Story 27-12).
 *
 * Shows resolved conventions with override badges, diff view, and editor.
 * Members see a read-only banner. Admin/owner can create/edit/delete overrides.
 *
 * Routing note: available to all authenticated members via /settings/conventions.
 * Modification is gated server-side by the ConventionManage policy.
 */

import { useMemo, useState, type JSX } from 'react';
import { useTenantConventions } from '../../hooks/useTenantConventions.js';
import { useCurrentUser } from '../../hooks/admin/useCurrentUser.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';
import { TenantConventionTable } from '../../components/conventions/TenantConventionTable.js';
import { TenantConventionEditor } from '../../components/conventions/TenantConventionEditor.js';

export function ConventionsPage(): JSX.Element {
  const {
    conventions,
    loading,
    error,
    overrideCount,
    fetchConventions,
    get,
    upsertOverride,
    deleteOverride,
    getSystemDefault,
  } = useTenantConventions();
  const { user } = useCurrentUser();
  const readOnly = user?.role === 'member';

  const [selected, setSelected] = useState<{
    role: string;
    action: string;
    isNew: boolean;
  } | null>(null);

  const selectedIsOverride = useMemo(() => {
    if (!selected || selected.isNew) return false;
    return !!conventions.find(
      (c) => c.role === selected.role && c.action === selected.action,
    )?.isOverride;
  }, [conventions, selected]);

  const handleNewClick = () => {
    setSelected({ role: '', action: '', isNew: true });
  };

  return (
    <div className="p-6 max-w-5xl">
      <h1 className="text-2xl font-bold text-gray-900 mb-2 dark:text-gray-100">Conventions</h1>
      <p className="text-sm text-gray-600 mb-4 dark:text-gray-400">
        Customize the convention rules used by Tamma's AI agents for your tenant. Platform
        defaults ship with Tamma; saving a change here creates a tenant-scoped override that
        falls back to the system default when deleted.
      </p>

      {readOnly && (
        <div className="mb-4 bg-yellow-50 border border-yellow-200 text-yellow-800 text-sm p-3 rounded dark:bg-yellow-950 dark:text-yellow-200 dark:border-yellow-800">
          You have read-only access. Contact a tenant admin or owner to modify conventions.
        </div>
      )}

      {error && (
        <div className="mb-4 text-sm text-red-600 dark:text-red-400" role="alert">
          {error}
        </div>
      )}

      {loading && conventions.length === 0 ? (
        <div className="flex items-center justify-center py-20">
          <LoadingSpinner size="lg" />
        </div>
      ) : (
        <TenantConventionTable
          conventions={conventions}
          overrideCount={overrideCount}
          onRowClick={(role, action) => setSelected({ role, action, isNew: false })}
          onNewClick={handleNewClick}
        />
      )}

      {selected && !selected.isNew && (
        <TenantConventionEditor
          open={true}
          role={selected.role}
          action={selected.action}
          isOverride={selectedIsOverride}
          readOnly={readOnly}
          onClose={() => setSelected(null)}
          onSaved={() => {
            setSelected(null);
            void fetchConventions();
          }}
          get={get}
          upsertOverride={upsertOverride}
          deleteOverride={deleteOverride}
          getSystemDefault={getSystemDefault}
        />
      )}

      {selected?.isNew && (
        <TenantConventionEditor
          open={true}
          role=""
          action=""
          isOverride={false}
          readOnly={readOnly}
          onClose={() => setSelected(null)}
          onSaved={() => {
            setSelected(null);
            void fetchConventions();
          }}
          get={get}
          upsertOverride={upsertOverride}
          deleteOverride={deleteOverride}
          getSystemDefault={getSystemDefault}
        />
      )}
    </div>
  );
}

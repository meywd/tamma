/**
 * PromptsPage — tenant member "AI Prompts" page (Story 27-5). Replaces the
 * old static Prompt Templates panel with a table of all 80 role+action
 * templates plus override badges, an edit drawer, and a preview panel.
 *
 * Routing note: this route is mounted for all authenticated members, not
 * just admins. Members still see a read-only banner and cannot save or
 * reset — enforced in the editor and the API via the `SettingsManage` policy.
 */

import { useMemo, useState, type JSX } from 'react';
import { useTenantPrompts } from '../../hooks/useTenantPrompts.js';
import { useCurrentUser } from '../../hooks/admin/useCurrentUser.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';
import { TenantPromptTable } from '../../components/prompts/TenantPromptTable.js';
import { TenantPromptEditor } from '../../components/prompts/TenantPromptEditor.js';

export function PromptsPage(): JSX.Element {
  const {
    prompts,
    loading,
    error,
    overrideCount,
    fetchPrompts,
    getPrompt,
    upsertOverride,
    deleteOverride,
    renderPreview,
  } = useTenantPrompts();
  const { user } = useCurrentUser();
  const readOnly = user?.role === 'member';
  const [selected, setSelected] = useState<{ role: string; action: string } | null>(null);

  const selectedIsOverride = useMemo(() => {
    if (!selected) return false;
    return (
      prompts.find((p) => p.role === selected.role && p.action === selected.action)?.source ===
      'user'
    );
  }, [prompts, selected]);

  return (
    <div className="p-6 max-w-5xl">
      <h1 className="text-2xl font-bold text-gray-900 mb-2 dark:text-gray-100">AI Prompts</h1>
      <p className="text-sm text-gray-600 mb-4 dark:text-gray-400">
        Customize how Tamma's AI agents behave for your tenant. System defaults ship with Tamma
        and apply to everyone; saving a change here creates a tenant-scoped override that falls
        back to the default when deleted.
      </p>

      {readOnly && (
        <div className="mb-4 bg-yellow-50 border border-yellow-200 text-yellow-800 text-sm p-3 rounded dark:bg-yellow-950 dark:text-yellow-200 dark:border-yellow-800">
          You have read-only access. Contact a tenant admin or owner to modify prompts.
        </div>
      )}

      {error && (
        <div className="mb-4 text-sm text-red-600 dark:text-red-400" role="alert">
          {error}
        </div>
      )}

      {loading && prompts.length === 0 ? (
        <div className="flex items-center justify-center py-20">
          <LoadingSpinner size="lg" />
        </div>
      ) : (
        <TenantPromptTable
          prompts={prompts}
          overrideCount={overrideCount}
          onRowClick={(role, action) => setSelected({ role, action })}
        />
      )}

      {selected && (
        <TenantPromptEditor
          open={true}
          role={selected.role}
          action={selected.action}
          isOverride={selectedIsOverride}
          readOnly={readOnly}
          onClose={() => setSelected(null)}
          onSaved={() => {
            setSelected(null);
            void fetchPrompts();
          }}
          getPrompt={getPrompt}
          upsertOverride={upsertOverride}
          deleteOverride={deleteOverride}
          renderPreview={renderPreview}
        />
      )}
    </div>
  );
}

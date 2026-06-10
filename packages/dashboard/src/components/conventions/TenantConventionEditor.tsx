/**
 * TenantConventionEditor — edit panel for a single (role, action) convention
 * from the tenant-admin perspective (Story 27-12).
 *
 * Key behaviours:
 *   - When opened on a system default, shows an info banner:
 *     "This is a platform default. Saving will create a tenant override."
 *   - When opened on an existing override, shows a blue banner with a
 *     "Reset to Default" button that DELETEs the override (falls back to system).
 *   - "Compare with Default" toggle → ConventionDiff view.
 *   - Read-only for members (role === 'member') — Save/Reset buttons hidden.
 *   - Error codes surfaced inline.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog.js';
import { MarkdownEditor } from './MarkdownEditor.js';
import { ConventionDiff } from './ConventionDiff.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';
import type { UseTenantConventionsReturn } from '../../hooks/useTenantConventions.js';
import type { ConventionResponse, ApiError } from '../../services/admin/conventions-api-client.js';

interface TenantConventionEditorProps {
  open: boolean;
  role: string;
  action: string;
  isOverride: boolean;
  readOnly: boolean;
  onClose: () => void;
  onSaved: () => void;
  get: UseTenantConventionsReturn['get'];
  upsertOverride: UseTenantConventionsReturn['upsertOverride'];
  deleteOverride: UseTenantConventionsReturn['deleteOverride'];
  getSystemDefault: UseTenantConventionsReturn['getSystemDefault'];
}

function apiErrorMessage(err: unknown): string {
  const e = err as ApiError;
  if (e.code === 'INELIGIBLE_ROLE_ACTION') return 'This (role, action) pair is ineligible.';
  if (e.code === 'CONCURRENT_UPSERT_CONFLICT') return 'Concurrent conflict — please retry.';
  if (e.code === 'CONVENTION_BODY_REQUIRED') return 'Convention body is required.';
  return e.message ?? 'Operation failed';
}

export function TenantConventionEditor({
  open,
  role,
  action,
  isOverride,
  readOnly,
  onClose,
  onSaved,
  get,
  upsertOverride,
  deleteOverride,
  getSystemDefault,
}: TenantConventionEditorProps): JSX.Element | null {
  const [detail, setDetail] = useState<ConventionResponse | null>(null);
  const [systemDefault, setSystemDefault] = useState<ConventionResponse | null>(null);
  const [body, setBody] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);
  const [showDiff, setShowDiff] = useState(false);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setLoading(true);
    setSaveError(null);
    setToast(null);
    setShowDiff(false);
    void (async () => {
      const [d, sd] = await Promise.all([
        get(role, action),
        isOverride ? getSystemDefault(role, action) : Promise.resolve(null),
      ]);
      if (cancelled) return;
      setDetail(d);
      setBody(d?.body ?? '');
      setEnabled(d?.enabled ?? true);
      setSystemDefault(sd);
      setLoading(false);
    })();
    return () => {
      cancelled = true;
    };
  }, [open, role, action, isOverride, get, getSystemDefault]);

  const handleSave = useCallback(async () => {
    if (!body.trim()) {
      setSaveError('Convention body is required.');
      return;
    }
    setSaving(true);
    setSaveError(null);
    try {
      await upsertOverride(role, action, { body, enabled });
      setToast('Saved');
      onSaved();
    } catch (err) {
      setSaveError(apiErrorMessage(err));
    } finally {
      setSaving(false);
    }
  }, [body, enabled, upsertOverride, role, action, onSaved]);

  const handleReset = useCallback(async () => {
    setConfirmReset(false);
    const ok = await deleteOverride(role, action);
    if (ok) {
      setToast('Reset to default');
      onSaved();
    } else {
      setSaveError('Failed to reset override');
    }
  }, [deleteOverride, role, action, onSaved]);

  if (!open) return null;

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`Edit convention ${role}/${action}`}
      className="fixed inset-0 z-40 flex items-start justify-center py-8 overflow-auto bg-black/40"
      onClick={onClose}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="bg-white rounded-lg shadow-xl w-full max-w-3xl mx-4 dark:bg-gray-800"
      >
        <div className="flex items-center justify-between px-6 py-4 border-b border-gray-100 dark:border-gray-800">
          <div>
            <h2 className="text-lg font-semibold text-gray-900 dark:text-gray-100">
              {role} / {action}
            </h2>
            <p className="text-xs text-gray-500 dark:text-gray-400">
              Edit the convention for this role+action.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-sm text-gray-500 hover:text-gray-800 dark:text-gray-400"
          >
            Close
          </button>
        </div>

        <div className="px-6 py-4 space-y-4">
          {loading && (
            <div className="flex items-center justify-center py-8">
              <LoadingSpinner size="lg" />
            </div>
          )}

          {!loading && (
            <>
              {/* Override context banners */}
              {!isOverride && (
                <div className="bg-yellow-50 border border-yellow-200 text-yellow-800 text-xs p-3 rounded dark:bg-yellow-950 dark:text-yellow-200 dark:border-yellow-800">
                  This is a platform default. Saving will create a tenant override.
                </div>
              )}
              {isOverride && (
                <div className="bg-blue-50 border border-blue-200 text-blue-800 text-xs p-3 rounded flex items-center justify-between gap-3 dark:bg-blue-950 dark:text-blue-200 dark:border-blue-800">
                  <span>This is a tenant override.</span>
                  <div className="flex items-center gap-2">
                    {systemDefault && (
                      <button
                        type="button"
                        onClick={() => setShowDiff((v) => !v)}
                        className="px-2 py-1 text-xs font-medium text-blue-700 border border-blue-300 rounded hover:bg-blue-100 dark:text-blue-200 dark:border-blue-600"
                      >
                        {showDiff ? 'Hide' : 'Compare with Default'}
                      </button>
                    )}
                    {!readOnly && (
                      <button
                        type="button"
                        onClick={() => setConfirmReset(true)}
                        className="px-2 py-1 text-xs font-medium text-white bg-red-600 hover:bg-red-700 rounded"
                      >
                        Reset to Default
                      </button>
                    )}
                  </div>
                </div>
              )}

              {/* Diff view */}
              {showDiff && systemDefault && (
                <div>
                  <div className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-2 dark:text-gray-400">
                    Diff — Override vs System Default
                  </div>
                  <ConventionDiff
                    overrideBody={body}
                    systemBody={systemDefault.body}
                  />
                </div>
              )}

              {saveError && (
                <div className="text-sm text-red-600 dark:text-red-400" role="alert">
                  {saveError}
                </div>
              )}
              {toast && (
                <div className="text-sm text-green-700 dark:text-green-300" role="status">
                  {toast}
                </div>
              )}

              {/* Body editor */}
              <div>
                <label className="block text-xs font-medium text-gray-600 mb-1 dark:text-gray-400">
                  Convention Body
                </label>
                <MarkdownEditor
                  value={body}
                  onChange={setBody}
                  rows={10}
                  disabled={readOnly || saving}
                  placeholder="Enter the convention body…"
                />
              </div>

              {/* Enabled toggle */}
              <label className="inline-flex items-center gap-2 text-sm text-gray-700 dark:text-gray-300 cursor-pointer">
                <input
                  type="checkbox"
                  checked={enabled}
                  disabled={readOnly || saving}
                  onChange={(e) => setEnabled(e.target.checked)}
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500 dark:border-gray-600"
                />
                Enabled
              </label>
            </>
          )}
        </div>

        <div className="flex items-center justify-end gap-2 px-6 py-3 border-t border-gray-100 dark:border-gray-800">
          <button
            type="button"
            onClick={onClose}
            className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md dark:bg-gray-800 dark:text-gray-300"
          >
            Close
          </button>
          {!readOnly && (
            <button
              type="button"
              onClick={() => void handleSave()}
              disabled={saving || loading || !body.trim()}
              className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
          )}
        </div>

        <ConfirmDialog
          open={confirmReset}
          title="Reset to system default?"
          message={`This deletes your tenant override for ${role}/${action} and falls back to the platform default.`}
          confirmLabel="Reset"
          variant="danger"
          onConfirm={() => void handleReset()}
          onCancel={() => setConfirmReset(false)}
        />
        {/* `detail` is available for future version-banner use — referenced to satisfy noUnusedLocals */}
        {detail === null && null}
      </div>
    </div>
  );
}

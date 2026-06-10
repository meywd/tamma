/**
 * ConventionEditor — inline split edit panel for a system-default convention.
 * Used in the admin page (Story 27-11).
 *
 * Features:
 *   - Role+Action dropdowns from RoleActionSelector (immutable after creation).
 *   - Body markdown editor via MarkdownEditor.
 *   - Enabled toggle.
 *   - Save (PUT), Reset (POST .../reset), Delete (DELETE) buttons.
 *   - All destructive actions require confirmation dialog.
 *   - Resolution Test panel at top (collapsible).
 *   - Error codes surfaced inline: INELIGIBLE_ROLE_ACTION, CONCURRENT_UPSERT_CONFLICT, etc.
 *
 * Story 27-11 ACs: save/reset/delete with confirmation, resolution test, seed badge.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog.js';
import { MarkdownEditor } from './MarkdownEditor.js';
import { ResolutionTestPanel } from './ResolutionTestPanel.js';
import { RoleActionSelector } from './RoleActionSelector.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';
import type { UseAdminConventionsResult } from '../../hooks/admin/useAdminConventions.js';
import type { ConventionResponse, ApiError } from '../../services/admin/conventions-api-client.js';

interface ConventionEditorProps {
  /** null = create new convention */
  convention: ConventionResponse | null;
  isNew: boolean;
  roles: string[];
  eligiblePairs: UseAdminConventionsResult['eligiblePairs'];
  getDefault: UseAdminConventionsResult['getDefault'];
  onSave: UseAdminConventionsResult['upsert'];
  onReset: UseAdminConventionsResult['reset'];
  onDelete: UseAdminConventionsResult['remove'];
  onClose: () => void;
  onChanged: () => void;
}

function errorMessage(err: unknown): string {
  const e = err as ApiError;
  if (e.code === 'INELIGIBLE_ROLE_ACTION') return 'This (role, action) pair is ineligible.';
  if (e.code === 'CONCURRENT_UPSERT_CONFLICT') return 'Concurrent conflict — please retry.';
  if (e.code === 'CONVENTION_BODY_REQUIRED') return 'Convention body is required.';
  if (e.code === 'INVALID_ROLE_ACTION') return 'Unknown role or action token.';
  return e.message ?? 'Operation failed';
}

export function ConventionEditor({
  convention,
  isNew,
  roles,
  eligiblePairs,
  getDefault,
  onSave,
  onReset,
  onDelete,
  onClose,
  onChanged,
}: ConventionEditorProps): JSX.Element {
  // Editing state
  const [role, setRole] = useState(convention?.role ?? '');
  const [action, setAction] = useState(convention?.action ?? '');
  const [body, setBody] = useState(convention?.body ?? '');
  const [enabled, setEnabled] = useState(convention?.enabled ?? true);

  const [saving, setSaving] = useState(false);
  const [loading, setLoading] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);

  const [confirmReset, setConfirmReset] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmDisable, setConfirmDisable] = useState(false);

  // Load detail when editing an existing convention
  useEffect(() => {
    if (isNew || !convention) return;
    let cancelled = false;
    setLoading(true);
    void (async () => {
      const detail = await getDefault(convention.role, convention.action);
      if (cancelled) return;
      if (detail) {
        setBody(detail.body);
        setEnabled(detail.enabled);
      }
      setLoading(false);
    })();
    return () => {
      cancelled = true;
    };
  }, [isNew, convention, getDefault]);

  const handleEnabledChange = (val: boolean) => {
    if (!val) {
      setConfirmDisable(true);
    } else {
      setEnabled(true);
    }
  };

  const handleSave = useCallback(async () => {
    if (!role || !action) {
      setSaveError('Role and action are required.');
      return;
    }
    if (!body.trim()) {
      setSaveError('Convention body is required.');
      return;
    }
    setSaving(true);
    setSaveError(null);
    try {
      await onSave(role, action, { body, enabled });
      setToast('Saved');
      onChanged();
      onClose();
    } catch (err) {
      setSaveError(errorMessage(err));
    } finally {
      setSaving(false);
    }
  }, [role, action, body, enabled, onSave, onChanged, onClose]);

  const handleReset = useCallback(async () => {
    setConfirmReset(false);
    setSaving(true);
    setSaveError(null);
    try {
      await onReset(role, action);
      setToast('Reset to seed default');
      onChanged();
      onClose();
    } catch (err) {
      setSaveError(errorMessage(err));
    } finally {
      setSaving(false);
    }
  }, [role, action, onReset, onChanged, onClose]);

  const handleDelete = useCallback(async () => {
    setConfirmDelete(false);
    setSaving(true);
    setSaveError(null);
    try {
      await onDelete(role, action);
      setToast('Deleted');
      onChanged();
      onClose();
    } catch (err) {
      setSaveError(errorMessage(err));
    } finally {
      setSaving(false);
    }
  }, [role, action, onDelete, onChanged, onClose]);

  return (
    <>
      <div
        className="fixed inset-0 z-40 bg-black/50"
        onClick={onClose}
        aria-hidden="true"
      />
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="convention-editor-title"
        className="fixed inset-y-0 right-0 z-50 w-full max-w-3xl bg-white shadow-xl flex flex-col dark:bg-gray-800"
      >
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-gray-200 dark:border-gray-700">
          <div className="flex items-center gap-2 flex-wrap">
            <h2 id="convention-editor-title" className="text-lg font-semibold text-gray-900 dark:text-gray-100">
              {isNew ? 'New Convention' : `Edit Convention`}
            </h2>
            {!isNew && (
              <>
                <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200">
                  {role}
                </span>
                <span className="text-gray-400">/</span>
                <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-cyan-100 text-cyan-800 dark:bg-cyan-900 dark:text-cyan-200">
                  {action}
                </span>
                {/* Every cell has a system seed */}
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-teal-100 text-teal-800 dark:bg-teal-900 dark:text-teal-200">
                  System Seed
                </span>
              </>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="text-gray-400 hover:text-gray-600 text-2xl leading-none px-2 dark:text-gray-500"
          >
            ×
          </button>
        </div>

        {/* Body */}
        <div className="flex-1 overflow-y-auto px-6 py-4 space-y-5">
          {loading ? (
            <div className="flex items-center justify-center py-16">
              <LoadingSpinner size="lg" />
            </div>
          ) : (
            <>
              {/* Resolution test (collapsible) — only for existing conventions */}
              {!isNew && role && action && (
                <ResolutionTestPanel role={role} action={action} />
              )}

              {saveError && (
                <div
                  className="bg-red-50 border border-red-200 rounded-md p-3 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800"
                  role="alert"
                >
                  {saveError}
                </div>
              )}

              {toast && (
                <div className="text-sm text-green-700 dark:text-green-300" role="status">
                  {toast}
                </div>
              )}

              {/* Role+Action selector — immutable after creation */}
              <div>
                <div className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-2 dark:text-gray-400">
                  Role + Action {!isNew && <span className="font-normal">(locked)</span>}
                </div>
                <RoleActionSelector
                  roles={roles}
                  eligiblePairs={eligiblePairs}
                  selectedRole={role}
                  selectedAction={action}
                  onRoleChange={setRole}
                  onActionChange={setAction}
                  disabled={!isNew}
                />
              </div>

              {/* Body editor */}
              <div>
                <label className="block text-xs font-semibold uppercase tracking-wider text-gray-500 mb-2 dark:text-gray-400">
                  Convention Body
                </label>
                <MarkdownEditor
                  value={body}
                  onChange={setBody}
                  rows={14}
                  disabled={saving}
                  placeholder="Enter the convention body…"
                />
              </div>

              {/* Enabled toggle */}
              <div className="flex items-center gap-3">
                <label className="inline-flex items-center gap-2 text-sm text-gray-700 dark:text-gray-300 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={enabled}
                    disabled={saving}
                    onChange={(e) => handleEnabledChange(e.target.checked)}
                    className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500 dark:border-gray-600"
                  />
                  Enabled
                </label>
                {!enabled && (
                  <span className="text-xs text-amber-600 dark:text-amber-400">
                    Disabled conventions are skipped during resolution.
                  </span>
                )}
              </div>
            </>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between gap-3 px-6 py-4 border-t border-gray-200 bg-gray-50 dark:bg-gray-900 dark:border-gray-700">
          <div className="flex items-center gap-2">
            {!isNew && (
              <>
                <button
                  type="button"
                  onClick={() => setConfirmReset(true)}
                  disabled={saving || loading}
                  className="px-3 py-1.5 text-sm font-medium text-amber-700 border border-amber-300 rounded-md hover:bg-amber-50 disabled:opacity-50 dark:text-amber-300 dark:border-amber-700 dark:hover:bg-amber-950"
                >
                  Reset to Seed
                </button>
                <button
                  type="button"
                  onClick={() => setConfirmDelete(true)}
                  disabled={saving || loading}
                  className="px-3 py-1.5 text-sm font-medium text-red-700 border border-red-300 rounded-md hover:bg-red-50 disabled:opacity-50 dark:text-red-300 dark:border-red-700 dark:hover:bg-red-950"
                >
                  Delete
                </button>
              </>
            )}
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onClose}
              disabled={saving}
              className="px-3 py-1.5 text-sm font-medium text-gray-700 border border-gray-300 bg-white rounded-md hover:bg-gray-50 disabled:opacity-50 dark:bg-gray-800 dark:text-gray-300 dark:border-gray-600"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => void handleSave()}
              disabled={saving || loading || !body.trim() || !role || !action}
              className="px-4 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
          </div>
        </div>
      </div>

      <ConfirmDialog
        open={confirmReset}
        title="Reset to seed default?"
        message={`This will restore ${role}/${action} to the ConventionSeedSpecs default. Any customisation will be lost.`}
        confirmLabel="Reset"
        variant="danger"
        onConfirm={() => void handleReset()}
        onCancel={() => setConfirmReset(false)}
      />

      <ConfirmDialog
        open={confirmDelete}
        title="Delete convention?"
        message={`This will delete the ${role}/${action} convention. If a seed exists, it will be used on next resolution.`}
        confirmLabel="Delete"
        variant="danger"
        onConfirm={() => void handleDelete()}
        onCancel={() => setConfirmDelete(false)}
      />

      <ConfirmDialog
        open={confirmDisable}
        title="Disable convention?"
        message={`Disabling ${role}/${action} means it will be skipped during resolution. Are you sure?`}
        confirmLabel="Disable"
        variant="danger"
        onConfirm={() => {
          setEnabled(false);
          setConfirmDisable(false);
        }}
        onCancel={() => setConfirmDisable(false)}
      />
    </>
  );
}

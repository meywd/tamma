/**
 * SystemPromptEditor (Story 27-4 AC 8)
 *
 * Tab listing the 8 role identity preambles. Backend exposes them in
 * the `systemPrompts` field of `GET /api/prompts/system` and accepts
 * per-user overrides via `PUT /api/prompts/system/{role}` (the action
 * axis is intentionally absent — see CLAUDE.md "role-system" scope).
 *
 * Each row collapses to a card; clicking "Edit" opens an inline
 * textarea that posts the override on save.
 */

import { useState, type JSX } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog.js';
import { roleLabel, ROLES } from './prompt-constants.js';
import type { UpsertPromptRequest } from '../../services/admin/prompts-api-client.js';

interface SystemPromptEditorProps {
  /** `{ [role]: identityPrompt }` — supplied by the parent page. */
  systemPrompts: Record<string, string>;
  upsertSystemPromptOverride: (
    role: string,
    body: UpsertPromptRequest,
  ) => Promise<void>;
  resetSystemPromptOverride: (role: string) => Promise<void>;
}

interface EditorState {
  role: string;
  draft: string;
  /** Saved baseline used to detect dirty state + reset confirmations. */
  baseline: string;
}

export function SystemPromptEditor({
  systemPrompts,
  upsertSystemPromptOverride,
  resetSystemPromptOverride,
}: SystemPromptEditorProps): JSX.Element {
  const [editing, setEditing] = useState<EditorState | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmReset, setConfirmReset] = useState<string | null>(null);

  const beginEdit = (role: string) => {
    const value = systemPrompts[role] ?? '';
    setEditing({ role, draft: value, baseline: value });
    setError(null);
  };

  const handleSave = async () => {
    if (!editing) return;
    setSaving(true);
    setError(null);
    try {
      await upsertSystemPromptOverride(editing.role, {
        // The role-system scope only consumes the `template` field as
        // the preamble body — `systemPrompt` etc. are unused server-side
        // for this scope but the DTO requires `template` to be set.
        template: editing.draft,
      });
      setEditing(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    if (!confirmReset) return;
    const role = confirmReset;
    setConfirmReset(null);
    setSaving(true);
    setError(null);
    try {
      await resetSystemPromptOverride(role);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to reset');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div>
      <p className="text-sm text-gray-600 mb-4 dark:text-gray-400">
        Identity preambles prepended to every LLM call for each role. Saving an
        edit creates a per-user override; resetting removes it so the call
        falls back to the system-shipped preamble.
      </p>

      {error && (
        <div className="mb-4 bg-red-50 border border-red-200 rounded-md p-3 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
          {error}
        </div>
      )}

      <div className="space-y-3">
        {ROLES.map(({ id }) => {
          const value = systemPrompts[id] ?? '';
          const isEditing = editing?.role === id;
          return (
            <div
              key={id}
              className="bg-white border border-gray-200 rounded-lg shadow-sm p-4 dark:bg-gray-800 dark:border-gray-700"
            >
              <div className="flex items-center justify-between mb-3">
                <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-800">
                  {roleLabel(id)}
                </span>
                <div className="flex items-center gap-2">
                  {!isEditing && (
                    <>
                      <button
                        type="button"
                        onClick={() => setConfirmReset(id)}
                        className="px-3 py-1 text-xs font-medium text-red-700 border border-red-300 rounded-md hover:bg-red-50 dark:text-red-300 dark:border-red-700 dark:hover:bg-red-950"
                      >
                        Reset override
                      </button>
                      <button
                        type="button"
                        onClick={() => beginEdit(id)}
                        className="px-3 py-1 text-xs font-medium text-blue-600 border border-blue-300 rounded-md hover:bg-blue-50 dark:text-blue-400"
                      >
                        Edit
                      </button>
                    </>
                  )}
                </div>
              </div>

              {isEditing ? (
                <div>
                  <textarea
                    value={editing.draft}
                    onChange={(e) =>
                      setEditing({ ...editing, draft: e.target.value })
                    }
                    rows={6}
                    spellCheck={false}
                    disabled={saving}
                    aria-label={`System prompt for ${roleLabel(id)}`}
                    className="w-full px-3 py-2 text-sm font-mono leading-6 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50 dark:border-gray-600"
                  />
                  <div className="flex items-center justify-end gap-2 mt-2">
                    <button
                      type="button"
                      onClick={() => setEditing(null)}
                      disabled={saving}
                      className="px-3 py-1 text-xs font-medium text-gray-700 border border-gray-300 bg-white rounded-md hover:bg-gray-50 disabled:opacity-50 dark:bg-gray-800 dark:text-gray-300 dark:border-gray-600 dark:hover:bg-gray-800"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      onClick={() => void handleSave()}
                      disabled={
                        saving || editing.draft.length === 0 ||
                        editing.draft === editing.baseline
                      }
                      className="px-3 py-1 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
                    >
                      {saving ? 'Saving…' : 'Save override'}
                    </button>
                  </div>
                </div>
              ) : value.length > 0 ? (
                <pre className="text-xs font-mono leading-relaxed whitespace-pre-wrap break-words text-gray-700 bg-gray-50 border border-gray-100 rounded-md px-3 py-2 max-h-32 overflow-y-auto dark:bg-gray-900 dark:text-gray-300 dark:border-gray-800">
                  {value}
                </pre>
              ) : (
                <p className="text-xs italic text-gray-500 dark:text-gray-400">
                  No system preamble shipped for this role.
                </p>
              )}
            </div>
          );
        })}
      </div>

      <ConfirmDialog
        open={confirmReset !== null}
        title="Reset role-system override?"
        message={
          confirmReset
            ? `This will delete your override of the ${roleLabel(confirmReset)} system preamble.`
            : ''
        }
        confirmLabel="Reset"
        variant="danger"
        onConfirm={() => void handleReset()}
        onCancel={() => setConfirmReset(null)}
      />
    </div>
  );
}

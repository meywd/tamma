/**
 * PromptEditDialog (Story 27-4 AC 5-7, 10-11)
 *
 * Modal drawer for editing a single role+action prompt cell. Loads the
 * resolved prompt for the calling user (`GET /api/prompts/{role}/{action}`)
 * so the editor reflects current overrides; saving writes a USER OVERRIDE
 * via `PUT /api/prompts/{role}/{action}`.
 *
 * The "Reset to Default" button issues `DELETE /api/prompts/{role}/{action}`
 * after a confirmation dialog — backend semantics are reset-to-default
 * (the override row is removed and resolution falls back to the system
 * default for that cell). The system-shipped template itself is immutable
 * and ships in code (`SystemPrompts.RoleActionTemplates`), so even an
 * "owner" cannot delete it — they can only customise on top of it.
 */

import { useEffect, useMemo, useRef, useState } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';
import type {
  PromptResponse,
  UpsertPromptRequest,
} from '../../services/admin/prompts-api-client.js';
import { extractVariables } from './extract-variables.js';
import { TemplateEditor } from './TemplateEditor.js';
import { VariableChips } from './VariableChips.js';
import { actionLabel, roleLabel } from './prompt-constants.js';

interface PromptEditDialogProps {
  role: string;
  action: string;
  /**
   * The system-shipped baseline (immutable; useful for showing "ships
   * with…" badges and for the Reset confirmation copy). Optional because
   * the page might not have it yet during the very first render.
   */
  systemDefault?: PromptResponse | undefined;
  onClose: () => void;
  /** Called after a successful save / reset, so the parent can refresh. */
  onChanged: () => void;
  loadResolved: (role: string, action: string) => Promise<PromptResponse>;
  saveOverride: (
    role: string,
    action: string,
    body: UpsertPromptRequest,
  ) => Promise<PromptResponse>;
  resetOverride: (role: string, action: string) => Promise<void>;
}

export function PromptEditDialog({
  role,
  action,
  systemDefault,
  onClose,
  onChanged,
  loadResolved,
  saveOverride,
  resetOverride,
}: PromptEditDialogProps): JSX.Element {
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [template, setTemplate] = useState('');
  const [systemPrompt, setSystemPrompt] = useState('');
  const [enableTools, setEnableTools] = useState(false);
  const [maxTokens, setMaxTokens] = useState(4096);

  const [resolved, setResolved] = useState<PromptResponse | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);

  const editorRef = useRef<HTMLTextAreaElement>(null);

  // Load the resolved prompt when the dialog opens (or role/action changes).
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setLoadError(null);
    loadResolved(role, action)
      .then((r) => {
        if (cancelled) return;
        setResolved(r);
        setTemplate(r.template);
        setSystemPrompt(r.systemPrompt ?? '');
        setEnableTools(r.enableTools);
        setMaxTokens(r.maxTokens);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setLoadError(err instanceof Error ? err.message : 'Failed to load prompt');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [role, action, loadResolved]);

  // Auto-extract variables on every keystroke — server is source of
  // truth on save, but live extraction gives the user instant feedback.
  const variables = useMemo(() => extractVariables(template), [template]);

  const isOverride = resolved?.source === 'user';

  const handleSave = async () => {
    setSaving(true);
    setSaveError(null);
    try {
      const body: UpsertPromptRequest = {
        template,
        systemPrompt: systemPrompt.length > 0 ? systemPrompt : null,
        variables,
        enableTools,
        maxTokens,
      };
      await saveOverride(role, action, body);
      onChanged();
      onClose();
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    setConfirmReset(false);
    setSaving(true);
    setSaveError(null);
    try {
      await resetOverride(role, action);
      onChanged();
      onClose();
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Failed to reset');
    } finally {
      setSaving(false);
    }
  };

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
        aria-labelledby="prompt-edit-title"
        className="fixed inset-y-0 right-0 z-50 w-full max-w-3xl bg-white shadow-xl flex flex-col"
      >
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-gray-200">
          <div className="flex items-center gap-2 flex-wrap">
            <h2 id="prompt-edit-title" className="text-lg font-semibold text-gray-900">
              Edit prompt
            </h2>
            <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-800">
              {roleLabel(role)}
            </span>
            <span className="text-gray-400">/</span>
            <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-cyan-100 text-cyan-800">
              {actionLabel(action)}
            </span>
            {isOverride ? (
              <span className="ml-1 inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-amber-100 text-amber-800">
                user override
              </span>
            ) : (
              <span className="ml-1 inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-700">
                system default
              </span>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="text-gray-400 hover:text-gray-600 text-2xl leading-none px-2"
          >
            ×
          </button>
        </div>

        {/* Body */}
        <div className="flex-1 overflow-y-auto px-6 py-4">
          {loading ? (
            <div className="flex items-center justify-center py-16">
              <LoadingSpinner size="lg" />
            </div>
          ) : loadError ? (
            <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700">
              {loadError}
            </div>
          ) : (
            <div className="space-y-5">
              {saveError && (
                <div className="bg-red-50 border border-red-200 rounded-md p-3 text-sm text-red-700">
                  {saveError}
                </div>
              )}

              <div>
                <label
                  htmlFor="prompt-template"
                  className="block text-xs font-semibold uppercase tracking-wider text-gray-500 mb-2"
                >
                  Template
                </label>
                <TemplateEditor
                  id="prompt-template"
                  ref={editorRef}
                  value={template}
                  onChange={setTemplate}
                  rows={18}
                  disabled={saving}
                />
                <p className="mt-2 text-xs text-gray-500">
                  Use <code className="font-mono text-purple-700">{'{{variable}}'}</code> tokens
                  for runtime substitutions. Variables are auto-extracted on save.
                </p>
              </div>

              <div>
                <div className="flex items-center justify-between mb-2">
                  <span className="text-xs font-semibold uppercase tracking-wider text-gray-500">
                    Variables ({variables.length})
                  </span>
                </div>
                <VariableChips
                  variables={variables}
                  editorRef={editorRef}
                  onInsert={setTemplate}
                />
              </div>

              <div>
                <label
                  htmlFor="prompt-system"
                  className="block text-xs font-semibold uppercase tracking-wider text-gray-500 mb-2"
                >
                  System prompt override (optional)
                </label>
                <textarea
                  id="prompt-system"
                  value={systemPrompt}
                  onChange={(e) => setSystemPrompt(e.target.value)}
                  rows={4}
                  placeholder="Leave empty to use the role's default system prompt."
                  disabled={saving}
                  spellCheck={false}
                  className="w-full px-3 py-2 text-sm font-mono leading-6 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
                />
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                  <label
                    htmlFor="prompt-max-tokens"
                    className="block text-xs font-semibold uppercase tracking-wider text-gray-500 mb-2"
                  >
                    Max tokens
                  </label>
                  <input
                    id="prompt-max-tokens"
                    type="number"
                    min={1}
                    value={maxTokens}
                    onChange={(e) => setMaxTokens(Number(e.target.value))}
                    disabled={saving}
                    className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
                  />
                </div>
                <div className="flex items-end">
                  <label className="inline-flex items-center gap-2 text-sm text-gray-700">
                    <input
                      type="checkbox"
                      checked={enableTools}
                      onChange={(e) => setEnableTools(e.target.checked)}
                      disabled={saving}
                      className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    />
                    Enable tools
                  </label>
                </div>
              </div>

              {systemDefault && isOverride && (
                <details className="border border-gray-200 rounded-md">
                  <summary className="cursor-pointer px-3 py-2 text-xs font-medium text-gray-600 bg-gray-50 rounded-md">
                    Show system default for comparison
                  </summary>
                  <pre className="px-3 py-2 text-xs font-mono whitespace-pre-wrap break-words text-gray-700 max-h-64 overflow-y-auto border-t border-gray-200">
                    {systemDefault.template}
                  </pre>
                </details>
              )}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between gap-3 px-6 py-4 border-t border-gray-200 bg-gray-50">
          <div>
            {isOverride && (
              <button
                type="button"
                onClick={() => setConfirmReset(true)}
                disabled={saving || loading}
                className="px-3 py-1.5 text-sm font-medium text-red-700 border border-red-300 rounded-md hover:bg-red-50 disabled:opacity-50"
              >
                Reset to default
              </button>
            )}
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onClose}
              disabled={saving}
              className="px-3 py-1.5 text-sm font-medium text-gray-700 border border-gray-300 bg-white rounded-md hover:bg-gray-50 disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => void handleSave()}
              disabled={saving || loading || template.length === 0}
              className="px-4 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
            >
              {saving ? 'Saving…' : 'Save override'}
            </button>
          </div>
        </div>
      </div>

      <ConfirmDialog
        open={confirmReset}
        title="Reset to system default?"
        message={`This will delete your override for ${roleLabel(role)} / ${actionLabel(action)}. The next render will fall back to the system-shipped template.`}
        confirmLabel="Reset"
        variant="danger"
        onConfirm={() => void handleReset()}
        onCancel={() => setConfirmReset(false)}
      />
    </>
  );
}

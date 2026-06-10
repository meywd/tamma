/**
 * TenantPromptEditor — edit panel for a single role+action prompt from the
 * tenant-member perspective (Story 27-5 AC #4–#10, #13).
 *
 * Key behaviours:
 *   - When opened on a system default, shows a "saving will create an
 *     override" info banner (AC #5).
 *   - When opened on an existing override, shows a blue banner with a
 *     "Reset to Default" button that deletes the override and falls back to
 *     the shipped template (AC #7).
 *   - Variables list is auto-extracted from `{{name}}` tokens in the body —
 *     matches the server-side `VariablePattern` in PromptStoreService so the
 *     UI preview mirrors what the renderer will actually substitute.
 *   - Read-only mode (for regular members) hides the Save and Reset buttons
 *     (AC #13); the preview panel + convention picker are still visible so
 *     they can explore the prompt catalogue.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog.js';
import { ConventionSelector } from './ConventionSelector.js';
import { PromptPreview } from './PromptPreview.js';
import type {
  PromptDetail,
  RenderedResult,
  UpsertPromptInput,
} from '../../hooks/useTenantPrompts.js';

interface TenantPromptEditorProps {
  open: boolean;
  role: string;
  action: string;
  isOverride: boolean;
  readOnly: boolean;
  onClose: () => void;
  onSaved: () => void;
  getPrompt: (role: string, action: string) => Promise<PromptDetail | null>;
  upsertOverride: (
    role: string,
    action: string,
    input: UpsertPromptInput,
  ) => Promise<PromptDetail>;
  deleteOverride: (role: string, action: string) => Promise<boolean>;
  renderPreview: (
    role: string,
    action: string,
    variables: Record<string, string>,
  ) => Promise<RenderedResult | null>;
}

const VARIABLE_PATTERN = /\{\{([^}]{1,64})\}\}/g;

function extractVariables(template: string): string[] {
  const seen = new Set<string>();
  let match: RegExpExecArray | null;
  VARIABLE_PATTERN.lastIndex = 0;
  while ((match = VARIABLE_PATTERN.exec(template)) !== null) {
    seen.add(match[1]!.trim());
  }
  return Array.from(seen);
}

export function TenantPromptEditor(props: TenantPromptEditorProps): JSX.Element | null {
  const {
    open,
    role,
    action,
    isOverride,
    readOnly,
    onClose,
    onSaved,
    getPrompt,
    upsertOverride,
    deleteOverride,
    renderPreview,
  } = props;

  const [detail, setDetail] = useState<PromptDetail | null>(null);
  const [template, setTemplate] = useState('');
  const [systemPrompt, setSystemPrompt] = useState('');
  const [enableTools, setEnableTools] = useState(false);
  const [maxTokens, setMaxTokens] = useState(4096);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);
  const [loading, setLoading] = useState(false);

  const templateRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setLoading(true);
    setSaveError(null);
    setToast(null);
    void (async () => {
      const d = await getPrompt(role, action);
      if (cancelled) return;
      setDetail(d);
      setTemplate(d?.template ?? '');
      setSystemPrompt(d?.systemPrompt ?? '');
      setEnableTools(d?.enableTools ?? false);
      setMaxTokens(d?.maxTokens ?? 4096);
      setLoading(false);
    })();
    return () => {
      cancelled = true;
    };
  }, [open, role, action, getPrompt]);

  const variables = useMemo(() => extractVariables(template), [template]);

  const handleSave = useCallback(async () => {
    setSaving(true);
    setSaveError(null);
    try {
      await upsertOverride(role, action, {
        template,
        systemPrompt,
        variables,
        enableTools,
        maxTokens,
      });
      setToast('Saved');
      onSaved();
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Failed to save override');
    } finally {
      setSaving(false);
    }
  }, [
    upsertOverride,
    role,
    action,
    template,
    systemPrompt,
    variables,
    enableTools,
    maxTokens,
    onSaved,
  ]);

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

  const handleInsertConventions = useCallback(
    (text: string) => {
      const textarea = templateRef.current;
      if (!textarea) {
        setTemplate((t) => (t.includes('{{conventions}}')
          ? t.replace('{{conventions}}', text)
          : `${t}\n\n${text}`));
        return;
      }
      const start = textarea.selectionStart ?? template.length;
      const end = textarea.selectionEnd ?? template.length;
      const next = `${template.slice(0, start)}${text}${template.slice(end)}`;
      setTemplate(next);
    },
    [template],
  );

  if (!open) return null;

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`Edit prompt ${role}/${action}`}
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
            <p className="text-xs text-gray-500 dark:text-gray-400">Edit the prompt template for this role+action.</p>
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
          {loading && <div className="text-sm text-gray-500 dark:text-gray-400">Loading…</div>}

          {!isOverride && (
            <div className="bg-yellow-50 border border-yellow-200 text-yellow-800 text-xs p-3 rounded dark:bg-yellow-950 dark:text-yellow-200 dark:border-yellow-800">
              This is a system default. Saving will create an override for your tenant.
            </div>
          )}
          {isOverride && (
            <div className="bg-blue-50 border border-blue-200 text-blue-800 text-xs p-3 rounded flex items-center justify-between gap-3 dark:bg-blue-950 dark:text-blue-200 dark:border-blue-800">
              <span>This is a tenant override.</span>
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

          <div>
            <label
              htmlFor="prompt-template-body"
              className="block text-xs font-medium text-gray-600 mb-1 dark:text-gray-400"
            >
              Template
            </label>
            <textarea
              id="prompt-template-body"
              ref={templateRef}
              value={template}
              onChange={(e) => setTemplate(e.target.value)}
              rows={10}
              readOnly={readOnly}
              className="w-full px-3 py-2 text-sm font-mono border border-gray-300 rounded-md dark:border-gray-600"
            />
          </div>

          <div>
            <div className="block text-xs font-medium text-gray-600 mb-1 dark:text-gray-400">Variables detected</div>
            <div
              data-testid="extracted-variables"
              className="text-xs font-mono text-purple-700"
            >
              {variables.length === 0
                ? 'None'
                : variables.map((v) => `{{${v}}}`).join('  ')}
            </div>
          </div>

          <div>
            <label
              htmlFor="prompt-system-body"
              className="block text-xs font-medium text-gray-600 mb-1 dark:text-gray-400"
            >
              System Prompt
            </label>
            <textarea
              id="prompt-system-body"
              value={systemPrompt}
              onChange={(e) => setSystemPrompt(e.target.value)}
              rows={4}
              readOnly={readOnly}
              className="w-full px-3 py-2 text-sm font-mono border border-gray-300 rounded-md dark:border-gray-600"
            />
          </div>

          <div className="flex flex-wrap gap-4">
            <label className="flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300">
              <input
                type="checkbox"
                checked={enableTools}
                disabled={readOnly}
                onChange={(e) => setEnableTools(e.target.checked)}
              />
              Enable tools
            </label>
            <div className="flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300">
              <label htmlFor="prompt-max-tokens">Max tokens</label>
              <input
                id="prompt-max-tokens"
                type="number"
                min={128}
                max={200_000}
                value={maxTokens}
                disabled={readOnly}
                onChange={(e) => setMaxTokens(Number(e.target.value) || 4096)}
                className="w-24 px-2 py-1 border border-gray-300 rounded-md dark:border-gray-600"
              />
            </div>
          </div>

          {!readOnly && <ConventionSelector onInsert={handleInsertConventions} />}

          <PromptPreview
            role={role}
            action={action}
            variables={variables}
            renderPreview={renderPreview}
          />
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
              onClick={handleSave}
              disabled={saving || loading}
              className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
          )}
        </div>

        <ConfirmDialog
          open={confirmReset}
          title="Reset prompt to default"
          message="This deletes your tenant override and falls back to the Tamma-shipped system default. This cannot be undone."
          confirmLabel="Reset"
          cancelLabel="Cancel"
          variant="danger"
          onConfirm={() => {
            void handleReset();
          }}
          onCancel={() => setConfirmReset(false)}
        />
        {/* `detail` is available for future enhancements (version banner etc.); referenced to appease `noUnusedLocals`. */}
        {detail === null && null}
      </div>
    </div>
  );
}

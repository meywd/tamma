/**
 * PromptPreview — collapsible panel that lets a tenant admin submit test
 * variable values and see the rendered prompt output (Story 27-5 AC #9, #10).
 *
 * Rendering is triggered by an explicit button click — not a keystroke — to
 * avoid hitting the render endpoint on every keypress.
 */

import { useState, type JSX } from 'react';
import type { RenderedResult } from '../../hooks/useTenantPrompts.js';

interface PromptPreviewProps {
  role: string;
  action: string;
  variables: string[];
  renderPreview: (
    role: string,
    action: string,
    variables: Record<string, string>,
  ) => Promise<RenderedResult | null>;
}

export function PromptPreview({
  role,
  action,
  variables,
  renderPreview,
}: PromptPreviewProps): JSX.Element {
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<RenderedResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleRender(): Promise<void> {
    setLoading(true);
    setError(null);
    try {
      const rendered = await renderPreview(role, action, values);
      if (rendered === null) {
        setError('Render failed');
      } else {
        setResult(rendered);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Render failed');
    } finally {
      setLoading(false);
    }
  }

  return (
    <details className="mt-4 border border-gray-200 rounded-lg p-4 dark:border-gray-700">
      <summary className="cursor-pointer font-medium text-gray-800 dark:text-gray-200">Preview / Test</summary>
      <div className="mt-4 space-y-4">
        {variables.length === 0 ? (
          <p className="text-xs text-gray-500 dark:text-gray-400">
            No variables detected in the template. Click Render Preview to see the raw text.
          </p>
        ) : (
          <div className="space-y-2">
            {variables.map((varName) => {
              const inputId = `preview-var-${varName}`;
              return (
                <div key={varName} className="flex items-center gap-3">
                  <label
                    htmlFor={inputId}
                    className="text-xs font-mono text-purple-700 w-40 shrink-0"
                  >
                    {`{{${varName}}}`}
                  </label>
                  <input
                    id={inputId}
                    type="text"
                    value={values[varName] ?? ''}
                    onChange={(e) =>
                      setValues((prev) => ({ ...prev, [varName]: e.target.value }))
                    }
                    className="flex-1 px-2 py-1 text-sm border border-gray-300 rounded-md dark:border-gray-600"
                  />
                </div>
              );
            })}
          </div>
        )}

        <button
          type="button"
          onClick={handleRender}
          disabled={loading}
          className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
        >
          {loading ? 'Rendering…' : 'Render Preview'}
        </button>

        {error && <div className="text-sm text-red-600 dark:text-red-400">{error}</div>}

        {result && (
          <div className="space-y-3">
            <div>
              <h4 className="text-sm font-semibold text-gray-700 dark:text-gray-300">Rendered Template</h4>
              <pre className="mt-1 bg-gray-50 p-3 text-xs whitespace-pre-wrap break-words overflow-x-auto dark:bg-gray-900">
                {result.renderedTemplate}
              </pre>
            </div>
            {result.renderedSystemPrompt && (
              <div>
                <h4 className="text-sm font-semibold text-gray-700 dark:text-gray-300">System Prompt</h4>
                <pre className="mt-1 bg-gray-50 p-3 text-xs whitespace-pre-wrap break-words overflow-x-auto dark:bg-gray-900">
                  {result.renderedSystemPrompt}
                </pre>
              </div>
            )}
            {result.unresolvedVariables.length > 0 && (
              <div
                data-testid="unresolved-variables"
                className="text-sm text-red-600 dark:text-red-400"
              >
                Unresolved:{' '}
                {result.unresolvedVariables.map((v) => `{{${v}}}`).join(', ')}
              </div>
            )}
          </div>
        )}
      </div>
    </details>
  );
}

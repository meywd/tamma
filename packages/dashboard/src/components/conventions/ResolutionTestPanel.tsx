/**
 * ResolutionTestPanel — collapsible panel that POSTs to
 * `POST /api/conventions/resolve` and shows the resolved convention body
 * along with its source badge.
 *
 * Handles:
 *   - 404 → "no convention" empty state
 *   - 400 INELIGIBLE_ROLE_ACTION → validation error inline
 *   - 409 CONCURRENT_UPSERT_CONFLICT → retry hint
 *
 * Story 27-11 AC: Resolution Test panel at top of edit view (collapsible).
 */

import { useState, type JSX } from 'react';
import { conventionsApi, type ConventionResponse, type ApiError } from '../../services/admin/conventions-api-client.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';

interface ResolutionTestPanelProps {
  role: string;
  action: string;
}

export function ResolutionTestPanel({ role, action }: ResolutionTestPanelProps): JSX.Element {
  const [open, setOpen] = useState(false);
  const [result, setResult] = useState<ConventionResponse | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const run = async () => {
    setLoading(true);
    setResult(null);
    setNotFound(false);
    setError(null);
    try {
      const r = await conventionsApi.resolve({ role, action });
      setResult(r);
    } catch (err) {
      const e = err as ApiError;
      if (e.status === 404) {
        setNotFound(true);
      } else if (e.status === 400 && e.code === 'INELIGIBLE_ROLE_ACTION') {
        setError('This (role, action) pair is ineligible — no convention can exist for it.');
      } else if (e.status === 409) {
        setError('Concurrent conflict detected. Wait a moment and try again.');
      } else {
        setError(e.message ?? 'Resolution failed');
      }
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="border border-gray-200 rounded-md dark:border-gray-700">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="w-full flex items-center justify-between px-4 py-2.5 text-sm font-medium text-gray-700 bg-gray-50 rounded-md hover:bg-gray-100 dark:bg-gray-900 dark:text-gray-300 dark:hover:bg-gray-800"
      >
        <span>Resolution Test</span>
        <span className="text-gray-400 dark:text-gray-500">{open ? '▲' : '▼'}</span>
      </button>

      {open && (
        <div className="px-4 py-3 space-y-3 border-t border-gray-200 dark:border-gray-700">
          <p className="text-xs text-gray-500 dark:text-gray-400">
            Calls <code className="font-mono bg-gray-100 dark:bg-gray-800 px-1 rounded">POST /api/conventions/resolve</code> with{' '}
            <code className="font-mono bg-gray-100 dark:bg-gray-800 px-1 rounded">{`{role: "${role}", action: "${action}"}`}</code>{' '}
            and shows the resolved convention.
          </p>

          <button
            type="button"
            onClick={() => void run()}
            disabled={loading || !role || !action}
            className="px-3 py-1.5 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
          >
            {loading ? 'Testing…' : 'Run Resolution Test'}
          </button>

          {loading && (
            <div className="flex items-center gap-2 text-sm text-gray-500 dark:text-gray-400">
              <LoadingSpinner size="sm" />
              <span>Resolving…</span>
            </div>
          )}

          {error && (
            <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-md px-3 py-2 dark:text-red-300 dark:bg-red-950 dark:border-red-800" role="alert">
              {error}
            </div>
          )}

          {notFound && (
            <div className="text-sm text-gray-500 bg-gray-50 border border-gray-200 rounded-md px-3 py-2 dark:bg-gray-900 dark:border-gray-700 dark:text-gray-400">
              No convention found for this (role, action) pair.
            </div>
          )}

          {result && (
            <div className="space-y-2">
              <div className="flex items-center gap-2">
                <span className="text-xs font-medium text-gray-600 dark:text-gray-400">Source:</span>
                <span
                  className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
                    result.source === 'tenant'
                      ? 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200'
                      : 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300'
                  }`}
                >
                  {result.source}
                </span>
                <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${result.enabled ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200' : 'bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-200'}`}>
                  {result.enabled ? 'enabled' : 'disabled'}
                </span>
              </div>
              <pre className="text-xs font-mono whitespace-pre-wrap break-words text-gray-700 bg-gray-50 border border-gray-100 rounded-md px-3 py-2 max-h-48 overflow-y-auto dark:bg-gray-900 dark:text-gray-300 dark:border-gray-800">
                {result.body}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

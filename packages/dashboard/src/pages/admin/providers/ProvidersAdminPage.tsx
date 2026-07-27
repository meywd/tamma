/**
 * ProvidersAdminPage (Story 46-2)
 *
 * Platform-owner page for runtime provider & model management (Epic 46): one
 * row per catalogue provider from `GET /api/admin/providers/status` — key
 * status, enabled toggle, current default model + provenance — with a
 * per-provider live model picker (fetch-on-open), save (PUT settings), and
 * reset-to-default (DELETE settings).
 *
 * RBAC: wrapped in AdminGuard (route); the API is PlatformOwnerAccess-gated.
 *
 * Provenance rule (Story 43-1): every provider name, model id, base URL, key
 * status, and source badge on this page comes from the server's response —
 * nothing is restated client-side. No key material is ever rendered or
 * requested here (46-0 AC7); key remediation links to /admin/secrets.
 */

import { Fragment, useCallback, useEffect, useState, type JSX } from 'react';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import {
  providersAdminApi,
  type ProviderStatusRow as ProviderStatusRowData,
  type PutProviderSettingsResponse,
} from '../../../services/admin/providers-api-client.js';
import { ProviderRow } from './ProviderRow.js';
import { ModelPicker } from './ModelPicker.js';

export function ProvidersAdminPage(): JSX.Element {
  const [rows, setRows] = useState<ProviderStatusRowData[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedKey, setExpandedKey] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await providersAdminApi.listProviders();
      setRows(response.providers);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load providers');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  /** Save the platform default model (AC4): PUT, then re-fetch the roster so
   * the row's model + source badge reflect the server's resolution. */
  const handleSaveModel = useCallback(
    async (key: string, model: string): Promise<PutProviderSettingsResponse> => {
      const response = await providersAdminApi.putProviderSettings(key, {
        defaultModel: model,
      });
      await reload();
      return response;
    },
    [reload],
  );

  /** Enable/disable (AC5): optimistic flip, PUT `{enabled}`, reflect the
   * response; revert and rethrow on failure (row surfaces the error). */
  const handleToggleEnabled = useCallback(async (key: string, enabled: boolean) => {
    setRows((prev) => prev.map((r) => (r.key === key ? { ...r, enabled } : r)));
    try {
      const response = await providersAdminApi.putProviderSettings(key, { enabled });
      setRows((prev) =>
        prev.map((r) => (r.key === key ? { ...r, enabled: response.enabled } : r)),
      );
      if (!response.enabled) {
        // A disabled provider's picker collapses — controls are inert except re-enable.
        setExpandedKey((current) => (current === key ? null : current));
      }
    } catch (err) {
      setRows((prev) => prev.map((r) => (r.key === key ? { ...r, enabled: !enabled } : r)));
      throw err;
    }
  }, []);

  /** Reset to default (AC4): DELETE the platform row, then re-fetch so the
   * row shows the server-resolved fallback source. */
  const handleReset = useCallback(
    async (key: string) => {
      await providersAdminApi.deleteProviderSettings(key);
      await reload();
    },
    [reload],
  );

  const toggleExpand = (key: string): void => {
    setExpandedKey((current) => (current === key ? null : key));
  };

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-1 dark:text-gray-100">
        Provider Settings
      </h1>
      <p className="text-sm text-gray-500 mb-6 dark:text-gray-400">
        Enable or disable each catalogue provider and pick its platform default model from the
        provider&apos;s own live model list — changes take effect without a deploy. API keys are
        managed on the Secrets page, never here.
      </p>

      {loading && rows.length === 0 ? (
        <div className="flex justify-center py-16">
          <LoadingSpinner size="lg" />
        </div>
      ) : error ? (
        <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
          <div className="font-medium mb-1">Failed to load providers</div>
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
        <div className="overflow-x-auto border border-gray-200 rounded-md dark:border-gray-700">
          <table className="min-w-full text-sm">
            <thead className="bg-gray-50 dark:bg-gray-800">
              <tr>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">
                  Provider
                </th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">
                  Dialect
                </th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">
                  Base URL
                </th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">
                  Key
                </th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">
                  Default model
                </th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">
                  Enabled
                </th>
                <th className="px-4 py-2" />
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <Fragment key={row.key}>
                  <ProviderRow
                    row={row}
                    expanded={expandedKey === row.key}
                    onToggleExpand={toggleExpand}
                    onToggleEnabled={handleToggleEnabled}
                  />
                  {expandedKey === row.key && (
                    <tr data-testid={`provider-panel-${row.key}`}>
                      <td colSpan={7} className="px-4 py-3 bg-gray-50 dark:bg-gray-900">
                        <ModelPicker row={row} onSave={handleSaveModel} onReset={handleReset} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

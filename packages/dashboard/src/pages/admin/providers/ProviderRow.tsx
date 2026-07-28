/**
 * ProviderRow (Story 46-2)
 *
 * One roster row of the provider settings table: display name (+ muted
 * key/aliases sub-line), dialect (or transport for non-HTTP providers),
 * effective base URL, three-state key status, current model + provenance
 * badge, enabled toggle, and the Edit affordance that opens the model picker.
 *
 * Everything rendered here comes from the server's status row — the page
 * restates no provider or model knowledge (Story 43-1 provenance rule). The
 * SOURCE_BADGE_LABELS map below is the one permitted text mapping (plan D4).
 */

import { useState, type JSX } from 'react';
import { Link } from 'react-router-dom';
import type {
  ProviderModelSource,
  ProviderStatusRow as ProviderStatusRowData,
} from '../../../services/admin/providers-api-client.js';

/**
 * The one permitted provenance text mapping (plan D4) — exported so tests
 * import it instead of restating strings. Unmapped sources render verbatim.
 */
export const SOURCE_BADGE_LABELS: Partial<Record<ProviderModelSource, string>> = {
  'platform-db': 'set here',
  config: 'from deployment config',
  descriptor: 'built-in default',
};

const SOURCE_BADGE_CLASS: Partial<Record<ProviderModelSource, string>> = {
  'platform-db': 'bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300',
  config: 'bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300',
  descriptor: 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300',
};

const KEY_STATUS_LABEL: Record<ProviderStatusRowData['keyStatus'], string> = {
  configured: 'Key configured',
  missing: 'Key missing',
  not_required: 'No key required',
};

const KEY_STATUS_CLASS: Record<ProviderStatusRowData['keyStatus'], string> = {
  configured: 'bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300',
  missing: 'bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-300',
  not_required: 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300',
};

export interface ProviderRowProps {
  row: ProviderStatusRowData;
  expanded: boolean;
  onToggleExpand: (key: string) => void;
  /** PUTs `{enabled}`; throws on failure (surfaced inline here). */
  onToggleEnabled: (key: string, enabled: boolean) => Promise<void>;
}

export function ProviderRow({
  row,
  expanded,
  onToggleExpand,
  onToggleEnabled,
}: ProviderRowProps): JSX.Element {
  const [toggling, setToggling] = useState(false);
  const [toggleError, setToggleError] = useState<string | null>(null);

  const handleToggle = async (): Promise<void> => {
    setToggling(true);
    setToggleError(null);
    try {
      await onToggleEnabled(row.key, !row.enabled);
    } catch (err) {
      setToggleError(err instanceof Error ? err.message : 'Failed to update provider');
    } finally {
      setToggling(false);
    }
  };

  return (
    <tr
      data-testid={`provider-row-${row.key}`}
      className={`border-t border-gray-100 dark:border-gray-800 ${
        row.enabled ? '' : 'opacity-60 bg-gray-50 dark:bg-gray-900'
      }`}
    >
      <td className="px-4 py-2 align-top">
        <div className="font-medium text-gray-900 dark:text-gray-100">{row.displayName}</div>
        <div className="text-xs text-gray-400 font-mono dark:text-gray-500">
          {row.key}
          {row.aliases.length > 0 && ` · ${row.aliases.join(', ')}`}
        </div>
      </td>
      <td className="px-4 py-2 align-top text-gray-700 dark:text-gray-300">
        {row.dialect ?? row.transport}
      </td>
      <td className="px-4 py-2 align-top font-mono text-xs text-gray-500 dark:text-gray-400 break-all">
        {row.effectiveBaseUrl ?? '—'}
      </td>
      <td className="px-4 py-2 align-top">
        <span
          data-testid={`provider-keystatus-${row.key}`}
          className={`inline-block px-2 py-0.5 rounded text-xs font-medium ${KEY_STATUS_CLASS[row.keyStatus]}`}
        >
          {KEY_STATUS_LABEL[row.keyStatus]}
        </span>
        {row.keyStatus !== 'not_required' && (
          <div>
            <Link
              to="/admin/secrets"
              className="text-xs text-blue-600 hover:underline dark:text-blue-400"
            >
              Manage in Secrets
            </Link>
          </div>
        )}
      </td>
      <td className="px-4 py-2 align-top">
        <div className="font-mono text-sm text-gray-900 dark:text-gray-100">
          {row.currentModel ?? '—'}
        </div>
        {row.source != null && (
          <span
            data-testid={`provider-source-${row.key}`}
            className={`inline-block mt-1 px-2 py-0.5 rounded text-xs font-medium ${
              SOURCE_BADGE_CLASS[row.source] ??
              'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300'
            }`}
          >
            {SOURCE_BADGE_LABELS[row.source] ?? row.source}
          </span>
        )}
      </td>
      <td className="px-4 py-2 align-top">
        <button
          type="button"
          role="switch"
          aria-checked={row.enabled}
          aria-label={`Enabled: ${row.displayName}`}
          data-testid={`provider-toggle-${row.key}`}
          disabled={toggling}
          onClick={() => void handleToggle()}
          className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors disabled:cursor-wait ${
            row.enabled ? 'bg-blue-600' : 'bg-gray-300 dark:bg-gray-600'
          }`}
        >
          <span
            className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
              row.enabled ? 'translate-x-4' : 'translate-x-1'
            }`}
          />
        </button>
        {toggleError && (
          <div
            data-testid={`provider-toggle-error-${row.key}`}
            className="mt-1 text-xs text-red-600 dark:text-red-400"
          >
            {toggleError}
          </div>
        )}
      </td>
      <td className="px-4 py-2 align-top text-right">
        <button
          type="button"
          data-testid={`provider-edit-${row.key}`}
          disabled={!row.enabled}
          onClick={() => onToggleExpand(row.key)}
          className="px-3 py-1.5 text-xs font-medium text-gray-700 border border-gray-300 bg-white rounded-md hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed dark:bg-gray-800 dark:text-gray-300 dark:border-gray-600 dark:hover:bg-gray-700"
        >
          {expanded ? 'Close' : 'Edit'}
        </button>
      </td>
    </tr>
  );
}

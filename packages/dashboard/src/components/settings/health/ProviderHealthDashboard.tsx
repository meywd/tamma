
import { useProviderHealth } from '../../../hooks/settings/useProviderHealth.js';
import { LoadingSpinner } from '../../common/LoadingSpinner.js';
import { ProviderHealthCard } from './ProviderHealthCard.js';

import type { JSX } from "react";

export function ProviderHealthDashboard(): JSX.Element {
  const { status, loading, error, reload } = useProviderHealth();

  const entries = Object.entries(status);

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <p className="text-sm text-gray-500">
          Auto-refreshes every 10 seconds. {entries.length} provider(s) tracked.
        </p>
        <button
          onClick={reload}
          disabled={loading}
          className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
        >
          Refresh
        </button>
      </div>

      {error && <div className="mb-4 text-sm text-red-600">{error}</div>}

      {loading && entries.length === 0 && <LoadingSpinner />}

      {entries.length === 0 && !loading && (
        <div className="text-center py-12 text-gray-500">
          <p className="text-lg font-medium">No providers tracked yet</p>
          <p className="text-sm mt-1">Provider health data will appear once providers are used.</p>
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        {entries.map(([key, entry]) => (
          <ProviderHealthCard key={key} providerKey={key} entry={entry} />
        ))}
      </div>
    </div>
  );
}

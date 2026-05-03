
import { Card } from '../../common/Card.js';
import { Badge } from '../../common/Badge.js';
import type { HealthStatusEntry } from '../../../services/settings/settings-api-client.js';

import type { JSX } from "react";

interface ProviderHealthCardProps {
  providerKey: string;
  entry: HealthStatusEntry;
}

export function ProviderHealthCard({ providerKey, entry }: ProviderHealthCardProps): JSX.Element {
  const [provider, model] = providerKey.split(':');

  const variant = entry.circuitOpen ? 'error' : entry.failures > 0 ? 'warning' : 'healthy';
  const statusText = entry.circuitOpen ? 'Circuit Open' : entry.failures > 0 ? 'Degraded' : 'Healthy';

  return (
    <Card>
      <div className="flex items-start justify-between mb-3">
        <div>
          <div className="font-semibold text-gray-900 dark:text-gray-100">{provider}</div>
          {model && model !== 'default' && (
            <div className="text-sm text-gray-500 dark:text-gray-400">{model}</div>
          )}
        </div>
        <Badge variant={variant}>{statusText}</Badge>
      </div>
      <div className="space-y-1 text-sm">
        <div className="flex justify-between">
          <span className="text-gray-500 dark:text-gray-400">Healthy</span>
          <span className={entry.healthy ? 'text-green-600 font-medium' : 'text-red-600 font-medium'}>
            {entry.healthy ? 'Yes' : 'No'}
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-500 dark:text-gray-400">Recent failures</span>
          <span className={entry.failures > 0 ? 'text-yellow-600 font-medium' : 'text-gray-900'}>
            {entry.failures}
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-500 dark:text-gray-400">Circuit breaker</span>
          <span className={entry.circuitOpen ? 'text-red-600 font-medium' : 'text-gray-900'}>
            {entry.circuitOpen ? 'Open' : 'Closed'}
          </span>
        </div>
      </div>
    </Card>
  );
}

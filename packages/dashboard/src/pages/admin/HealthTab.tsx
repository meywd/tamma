/**
 * System Health Tab
 *
 * Fetches /api/admin/health and displays cards for each service:
 * PostgreSQL, ELSA Server, OpenSearch, RabbitMQ, ChromaDB, Tamma API.
 * Green/red status indicators with response time and last checked timestamp.
 */

import { useSystemHealth } from '../../hooks/admin/useSystemHealth.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';
import { Card } from '../../components/common/Card.js';

import type { JSX } from "react";

function formatTime(dateStr: string): string {
  return new Date(dateStr).toLocaleTimeString();
}

const STATUS_STYLES = {
  healthy: {
    dot: 'bg-green-500',
    bg: 'border-green-200',
    text: 'text-green-700',
    label: 'Healthy',
  },
  unhealthy: {
    dot: 'bg-red-500',
    bg: 'border-red-200',
    text: 'text-red-700',
    label: 'Unhealthy',
  },
  unknown: {
    dot: 'bg-gray-400',
    bg: 'border-gray-200',
    text: 'text-gray-500',
    label: 'Unknown',
  },
} as const;

export function HealthTab(): JSX.Element {
  const { services, loading, error, reload } = useSystemHealth();

  if (loading && services.length === 0) {
    return (
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700">
        {error}
      </div>
    );
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-lg font-semibold text-gray-900">System Health</h2>
        <button
          type="button"
          onClick={() => void reload()}
          disabled={loading}
          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 hover:bg-gray-50 rounded-md disabled:opacity-50"
        >
          {loading ? 'Checking...' : 'Refresh'}
        </button>
      </div>

      {services.length === 0 ? (
        <div className="text-center py-12 text-gray-500">
          <p className="text-lg mb-2">No health data</p>
          <p className="text-sm">Health information will appear once the system reports status.</p>
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {services.map((service) => {
            const style = STATUS_STYLES[service.status];

            return (
              <Card key={service.name} className={`border-l-4 ${style.bg}`}>
                <div className="flex items-start justify-between">
                  <div>
                    <div className="flex items-center gap-2 mb-1">
                      <span className={`inline-block h-2.5 w-2.5 rounded-full ${style.dot}`} />
                      <h3 className="text-sm font-semibold text-gray-900">{service.name}</h3>
                    </div>
                    <p className={`text-sm font-medium ${style.text}`}>{style.label}</p>
                  </div>

                  {service.responseTime !== null && (
                    <span className="text-xs text-gray-400">{service.responseTime}ms</span>
                  )}
                </div>

                {service.details && (
                  <p className="mt-2 text-xs text-gray-500">{service.details}</p>
                )}

                <p className="mt-3 text-xs text-gray-400">
                  Checked: {formatTime(service.checkedAt)}
                </p>
              </Card>
            );
          })}
        </div>
      )}
    </div>
  );
}

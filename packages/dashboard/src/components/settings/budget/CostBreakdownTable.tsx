
import type { DiagnosticsEvent } from '@tamma/shared';

import type { JSX } from "react";

interface CostBreakdownTableProps {
  events: DiagnosticsEvent[];
}

interface CostRow {
  provider: string;
  model: string;
  calls: number;
  totalCost: number;
  avgLatency: number;
}

export function CostBreakdownTable({ events }: CostBreakdownTableProps): JSX.Element {
  // Aggregate cost by provider+model
  const aggregation = new Map<string, CostRow>();

  for (const event of events) {
    if (!('providerName' in event)) continue;
    if (event.type !== 'provider:complete') continue;

    const key = `${event.providerName}:${event.model ?? 'default'}`;
    const existing = aggregation.get(key);

    if (existing) {
      existing.calls++;
      existing.totalCost += event.costUsd ?? 0;
      existing.avgLatency =
        (existing.avgLatency * (existing.calls - 1) + (event.latencyMs ?? 0)) / existing.calls;
    } else {
      aggregation.set(key, {
        provider: event.providerName,
        model: event.model ?? 'default',
        calls: 1,
        totalCost: event.costUsd ?? 0,
        avgLatency: event.latencyMs ?? 0,
      });
    }
  }

  const rows = [...aggregation.values()].sort((a, b) => b.totalCost - a.totalCost);

  if (rows.length === 0) {
    return (
      <p className="text-sm text-gray-500 italic py-4 dark:text-gray-400">
        No cost data available. Provider call diagnostics will appear here.
      </p>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-gray-200 dark:border-gray-700">
            <th className="text-left py-2 font-semibold text-gray-900 dark:text-gray-100">Provider</th>
            <th className="text-left py-2 font-semibold text-gray-900 dark:text-gray-100">Model</th>
            <th className="text-right py-2 font-semibold text-gray-900 dark:text-gray-100">Calls</th>
            <th className="text-right py-2 font-semibold text-gray-900 dark:text-gray-100">Total Cost</th>
            <th className="text-right py-2 font-semibold text-gray-900 dark:text-gray-100">Avg Latency</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={`${row.provider}:${row.model}`} className="border-b border-gray-100 dark:border-gray-800">
              <td className="py-2 font-medium text-gray-900 dark:text-gray-100">{row.provider}</td>
              <td className="py-2 text-gray-600 dark:text-gray-400">{row.model}</td>
              <td className="py-2 text-right text-gray-900 dark:text-gray-100">{row.calls}</td>
              <td className="py-2 text-right text-gray-900 dark:text-gray-100">${row.totalCost.toFixed(4)}</td>
              <td className="py-2 text-right text-gray-600 dark:text-gray-400">{row.avgLatency.toFixed(0)}ms</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="border-t-2 border-gray-200 dark:border-gray-700">
            <td colSpan={3} className="py-2 font-semibold text-gray-900 dark:text-gray-100">
              Total
            </td>
            <td className="py-2 text-right font-semibold text-gray-900 dark:text-gray-100">
              ${rows.reduce((sum, r) => sum + r.totalCost, 0).toFixed(4)}
            </td>
            <td />
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

/**
 * LatencyBar — horizontal bar visualizing p50 / p95 / p99 latency percentiles.
 * Story 23-12 (AC4). The bar is segmented green (→p50) / yellow (→p95) /
 * red (→p99) against a shared scale so multiple bars compare at a glance.
 */

import type { JSX } from 'react';

interface LatencyBarProps {
  p50: number;
  p95: number;
  p99: number;
  /** Scale maximum. Defaults to p99. */
  max?: number;
  unit?: string;
  className?: string;
}

function pct(value: number, scale: number): number {
  if (scale <= 0) return 0;
  return Math.max(0, Math.min(100, (value / scale) * 100));
}

export function LatencyBar({
  p50,
  p95,
  p99,
  max,
  unit = 'ms',
  className = '',
}: LatencyBarProps): JSX.Element {
  const scale = max ?? (p99 || 1);
  const p50Pct = pct(p50, scale);
  const p95Pct = pct(p95, scale);
  const p99Pct = pct(p99, scale);

  const seg1 = p50Pct;
  const seg2 = Math.max(0, p95Pct - p50Pct);
  const seg3 = Math.max(0, p99Pct - p95Pct);

  return (
    <div data-testid="latency-bar" className={className}>
      <div className="flex h-2.5 w-full overflow-hidden rounded-full bg-gray-100 dark:bg-gray-700">
        <div className="h-full bg-green-500" style={{ width: `${seg1}%` }} />
        <div className="h-full bg-yellow-500" style={{ width: `${seg2}%` }} />
        <div className="h-full bg-red-500" style={{ width: `${seg3}%` }} />
      </div>
      <div className="mt-1.5 flex justify-between text-xs text-gray-500 dark:text-gray-400">
        <span>
          <span className="font-medium text-gray-700 dark:text-gray-300">p50</span> {p50}
          {unit}
        </span>
        <span>
          <span className="font-medium text-gray-700 dark:text-gray-300">p95</span> {p95}
          {unit}
        </span>
        <span>
          <span className="font-medium text-gray-700 dark:text-gray-300">p99</span> {p99}
          {unit}
        </span>
      </div>
    </div>
  );
}

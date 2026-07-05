/**
 * MetricCard — single metric tile: label, value, unit, a trend arrow, and an
 * optional inline sparkline. Story 23-12 (AC4).
 */

import type { JSX, ReactNode } from 'react';
import type { StatusTone } from './StatusBadge.js';

export type MetricTrend = 'up' | 'down' | 'flat';

interface MetricCardProps {
  label: string;
  value: ReactNode;
  unit?: string;
  trend?: MetricTrend;
  /** Text next to the trend arrow, e.g. "+12%". */
  trendLabel?: string;
  /** Whether an "up" trend is a good thing (drives arrow color). Defaults true. */
  trendIsGood?: boolean;
  /** Values for the inline sparkline (needs >= 2 points to render). */
  sparkline?: number[];
  /** Left accent tone. */
  tone?: StatusTone;
  hint?: string;
  onClick?: () => void;
  className?: string;
}

const ACCENT_CLASSES: Record<StatusTone, string> = {
  green: 'border-l-green-500',
  yellow: 'border-l-yellow-500',
  red: 'border-l-red-500',
  gray: 'border-l-gray-300 dark:border-l-gray-600',
  blue: 'border-l-blue-500',
};

const TREND_GLYPH: Record<MetricTrend, string> = {
  up: '▲',
  down: '▼',
  flat: '—',
};

function trendColorClass(trend: MetricTrend, trendIsGood: boolean): string {
  if (trend === 'flat') return 'text-gray-500 dark:text-gray-400';
  const good = trend === 'up' ? trendIsGood : !trendIsGood;
  return good ? 'text-green-600 dark:text-green-400' : 'text-red-600 dark:text-red-400';
}

function Sparkline({ points }: { points: number[] }): JSX.Element | null {
  if (points.length < 2) return null;
  const width = 100;
  const height = 28;
  const min = Math.min(...points);
  const max = Math.max(...points);
  const span = max - min || 1;
  const step = width / (points.length - 1);
  const d = points
    .map((p, i) => {
      const x = i * step;
      const y = height - ((p - min) / span) * height;
      return `${i === 0 ? 'M' : 'L'}${x.toFixed(2)},${y.toFixed(2)}`;
    })
    .join(' ');

  return (
    <svg
      data-testid="metric-sparkline"
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      className="h-7 w-full text-blue-500"
      aria-hidden="true"
    >
      <path d={d} fill="none" stroke="currentColor" strokeWidth={1.5} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

export function MetricCard({
  label,
  value,
  unit,
  trend,
  trendLabel,
  trendIsGood = true,
  sparkline,
  tone,
  hint,
  onClick,
  className = '',
}: MetricCardProps): JSX.Element {
  const accent = tone ? `border-l-4 ${ACCENT_CLASSES[tone]}` : '';
  const clickable = onClick !== undefined;

  return (
    <div
      data-testid="metric-card"
      onClick={onClick}
      className={`rounded-lg border border-gray-200 bg-white p-4 shadow-sm dark:border-gray-700 dark:bg-gray-800 ${accent} ${
        clickable ? 'cursor-pointer transition-shadow hover:shadow-md' : ''
      } ${className}`}
    >
      <div className="text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
        {label}
      </div>
      <div className="mt-1 flex items-baseline gap-1">
        <span className="text-2xl font-bold text-gray-900 dark:text-gray-100">{value}</span>
        {unit && <span className="text-sm text-gray-500 dark:text-gray-400">{unit}</span>}
      </div>
      {(trend || trendLabel) && (
        <div className={`mt-1 flex items-center gap-1 text-xs ${trendColorClass(trend ?? 'flat', trendIsGood)}`}>
          {trend && (
            <span data-testid="metric-trend" aria-hidden="true">
              {TREND_GLYPH[trend]}
            </span>
          )}
          {trendLabel && <span>{trendLabel}</span>}
        </div>
      )}
      {sparkline && sparkline.length >= 2 && (
        <div className="mt-3">
          <Sparkline points={sparkline} />
        </div>
      )}
      {hint && <div className="mt-2 text-xs text-gray-400 dark:text-gray-500">{hint}</div>}
    </div>
  );
}

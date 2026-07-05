/**
 * Story 34-9 (AC6) — one entitlement metric: its limit, current usage, and a
 * headroom bar. Consumes the `CheckHeadroom`-derived `currentUsage` / `remaining`
 * / `isOver` fields from `GET /api/pricing/entitlements` verbatim — it does NOT
 * recompute any quota math (34-6 owns headroom; AC13). An unlimited entitlement
 * (`limitValue == null`) renders as "Unlimited" with no bar.
 */

import type { JSX } from 'react';
import { metricKeyLabel, type ResolvedEntitlementLine } from '../../api/pricing';

function prettyMetric(key: string): string {
  return metricKeyLabel(key)
    .split('_')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

export function EntitlementBar({ line }: { line: ResolvedEntitlementLine }): JSX.Element {
  const label = prettyMetric(line.metricKey);
  const unlimited = line.limitValue === null;
  const usage = line.currentUsage ?? 0;

  // Bar percentage is display-only (clamped) — the authoritative over/remaining
  // values come from the server's headroom calc.
  const pct =
    unlimited || line.limitValue === 0
      ? 0
      : Math.min(100, Math.round((usage / (line.limitValue as number)) * 100));

  return (
    <div className="space-y-1" data-testid={`entitlement-${line.metricKey}`}>
      <div className="flex items-center justify-between text-sm">
        <span className="font-medium text-gray-800">{label}</span>
        {unlimited ? (
          <span className="text-xs font-medium text-emerald-700">Unlimited</span>
        ) : (
          <span className={`text-xs ${line.isOver ? 'text-red-700 font-semibold' : 'text-gray-500'}`}>
            {line.currentUsage ?? '—'} / {line.limitValue}
            {line.isOver ? ' (over limit)' : ''}
          </span>
        )}
      </div>
      {!unlimited && (
        <div className="h-2 w-full rounded bg-gray-100 overflow-hidden" aria-hidden="true">
          <div
            className={`h-full rounded ${line.isOver ? 'bg-red-500' : 'bg-blue-500'}`}
            style={{ width: `${line.isOver ? 100 : pct}%` }}
          />
        </div>
      )}
    </div>
  );
}

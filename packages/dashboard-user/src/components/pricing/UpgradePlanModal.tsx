/**
 * Story 34-9 (AC8) — the tenant upgrade/change-plan modal.
 *
 * The tenant picks a PUBLIC plan; before committing, the UI computes and shows
 * the entitlement GAINS and LOSSES by diffing the target plan's entitlements
 * against the current RESOLVED entitlement set + the `CheckHeadroom` usage
 * (34-6). A downgrade that would put the tenant over a new limit is flagged as a
 * non-blocking violation. Commit calls `POST /api/pricing/subscribe` (34-4); the
 * response's flagged-violation list is surfaced as a non-blocking warning.
 *
 * This is a PURE set-diff over server-resolved numbers — no pricing/headroom math
 * is duplicated here (AC13).
 */

import { useMemo, useState, type JSX } from 'react';
import {
  tenantPricingApi,
  metricKeyLabel,
  type PlanSnapshotDto,
  type ResolvedEntitlementLine,
} from '../../api/pricing';
import { ApiError } from '../../api/client';

interface DeltaLine {
  metric: string;
  kind: 'gain' | 'loss' | 'same';
  // How the metric changed set-membership between the two plans:
  //  - 'compare' → present on BOTH plans; the limits are compared
  //  - 'added'   → present on the TARGET only → a newly granted entitlement
  //  - 'removed' → present on CURRENT only → an entitlement the target drops
  change: 'compare' | 'added' | 'removed';
  detail: string;
  violation: boolean;
}

function prettyMetric(key: string): string {
  return key
    .split('_')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

function limitText(limit: number | null): string {
  return limit === null ? 'Unlimited' : String(limit);
}

/**
 * Pure set-diff of target-plan entitlements vs the current resolved set + usage.
 *
 * `GET /api/pricing/entitlements` and a `PlanSnapshot` each return ONLY the
 * metrics that plan GRANTS — plans define different subsets (e.g. starter omits
 * rag_storage_mb / benchmark_retention_days). So there are THREE cases, and a
 * metric absent from a plan means "not granted", NOT "unlimited":
 *   (a) present on BOTH   → compare limits (server semantic: limitValue null ⇒
 *                           unlimited, correct only for a PRESENT metric);
 *   (b) present on TARGET only → an ADDED entitlement (a new capability → gain);
 *   (c) present on CURRENT only → a REMOVED entitlement (capability dropped → loss).
 * An absent metric is NEVER folded into Infinity/"Unlimited".
 */
export function computeDelta(
  current: ResolvedEntitlementLine[],
  target: PlanSnapshotDto,
): DeltaLine[] {
  const currentByMetric = new Map<string, ResolvedEntitlementLine>();
  for (const line of current) currentByMetric.set(metricKeyLabel(line.metricKey), line);

  const targetByMetric = new Map<string, number | null>();
  for (const e of target.entitlements) targetByMetric.set(metricKeyLabel(e.metricKey), e.limitValue);

  const metrics = new Set<string>([...currentByMetric.keys(), ...targetByMetric.keys()]);
  const lines: DeltaLine[] = [];

  for (const metric of metrics) {
    const cur = currentByMetric.get(metric);
    const hasCurrent = cur !== undefined;
    const hasTarget = targetByMetric.has(metric);
    const usage = cur?.currentUsage ?? 0;

    // (b) ADDED — granted by the target but not held today. A new capability
    // (gain). It can still be an immediate over-limit if we already meter usage
    // for it, so keep the violation check honest.
    if (hasTarget && !hasCurrent) {
      const tgtLimit = targetByMetric.get(metric) ?? null;
      lines.push({
        metric: prettyMetric(metric),
        kind: 'gain',
        change: 'added',
        detail: `New — ${limitText(tgtLimit)}`,
        violation: tgtLimit !== null && usage > tgtLimit,
      });
      continue;
    }

    // (c) REMOVED — held today but dropped by the target. A lost capability
    // (loss). There is no new limit to breach, so it is never a violation.
    if (hasCurrent && !hasTarget) {
      lines.push({
        metric: prettyMetric(metric),
        kind: 'loss',
        change: 'removed',
        detail: 'Removed',
        violation: false,
      });
      continue;
    }

    // (a) PRESENT on both → compare limits. null ⇒ unlimited (Infinity).
    const curLimit = cur ? cur.limitValue : null;
    const tgtLimit = targetByMetric.get(metric) ?? null;
    const curVal = curLimit === null ? Infinity : curLimit;
    const tgtVal = tgtLimit === null ? Infinity : tgtLimit;

    let kind: DeltaLine['kind'] = 'same';
    if (tgtVal > curVal) kind = 'gain';
    else if (tgtVal < curVal) kind = 'loss';

    // A downgrade that would put current usage over the NEW limit is a violation.
    const violation = tgtLimit !== null && usage > tgtLimit;

    if (kind === 'same' && !violation) continue;

    lines.push({
      metric: prettyMetric(metric),
      kind,
      change: 'compare',
      detail: `${limitText(curLimit)} → ${limitText(tgtLimit)}`,
      violation,
    });
  }

  return lines.sort((a, b) => a.metric.localeCompare(b.metric));
}

export function UpgradePlanModal({
  plans,
  currentEntitlements,
  currentPlanId,
  canMutate,
  onClose,
  onSubscribed,
}: {
  plans: PlanSnapshotDto[];
  currentEntitlements: ResolvedEntitlementLine[];
  currentPlanId: string | null;
  canMutate: boolean;
  onClose: () => void;
  onSubscribed: () => void;
}): JSX.Element {
  const [selectedSlug, setSelectedSlug] = useState<string>('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [warning, setWarning] = useState<string | null>(null);

  const selectedPlan = useMemo(
    () => plans.find((p) => p.slug === selectedSlug) ?? null,
    [plans, selectedSlug],
  );

  const delta = useMemo(
    () => (selectedPlan ? computeDelta(currentEntitlements, selectedPlan) : []),
    [selectedPlan, currentEntitlements],
  );

  const commit = async (): Promise<void> => {
    if (!selectedPlan) return;
    setSubmitting(true);
    setError(null);
    setWarning(null);
    try {
      const resp = await tenantPricingApi.subscribe({ planSlug: selectedPlan.slug });
      if (resp.violations && resp.violations.length > 0) {
        setWarning(`Subscribed with warnings: ${resp.violations.join('; ')}`);
      }
      onSubscribed();
    } catch (err) {
      if (err instanceof ApiError) {
        const body = err.body as { error?: string; message?: string } | null;
        setError(body?.message ?? body?.error ?? `Subscribe failed (${err.status}).`);
      } else {
        setError(err instanceof Error ? err.message : 'Subscribe failed.');
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div
      role="dialog"
      aria-labelledby="upgrade-modal-title"
      aria-modal="true"
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
    >
      <div className="bg-white rounded-lg shadow-lg p-5 w-full max-w-lg max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-3">
          <h2 id="upgrade-modal-title" className="text-lg font-medium">
            Change plan
          </h2>
          <button
            type="button"
            aria-label="Close"
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600"
          >
            ×
          </button>
        </div>

        <label className="block text-xs text-gray-600 mb-1" htmlFor="plan-select">
          Choose a plan
        </label>
        <select
          id="plan-select"
          aria-label="Choose a plan"
          value={selectedSlug}
          onChange={(e) => setSelectedSlug(e.target.value)}
          className="w-full border border-gray-300 rounded px-2 py-1 text-sm mb-4"
        >
          <option value="">Select a plan…</option>
          {plans.map((p) => (
            <option key={p.planId} value={p.slug} disabled={p.planId === currentPlanId}>
              {p.displayName} (v{p.version}){p.planId === currentPlanId ? ' — current' : ''}
            </option>
          ))}
        </select>

        {selectedPlan && (
          <div className="space-y-2 mb-4">
            <h3 className="text-sm font-semibold text-gray-800">Entitlement changes</h3>
            {delta.length === 0 ? (
              <p className="text-sm text-gray-500">No entitlement changes for this plan.</p>
            ) : (
              <ul className="space-y-1">
                {delta.map((d) => (
                  <li
                    key={d.metric}
                    className={`flex items-center justify-between text-sm px-2 py-1 rounded ${
                      d.violation
                        ? 'bg-red-50 text-red-700'
                        : d.kind === 'gain'
                          ? 'bg-emerald-50 text-emerald-700'
                          : d.kind === 'loss'
                            ? 'bg-amber-50 text-amber-700'
                            : 'bg-gray-50 text-gray-600'
                    }`}
                  >
                    <span className="font-medium">{d.metric}</span>
                    <span>
                      {d.change === 'added'
                        ? '＋ '
                        : d.change === 'removed'
                          ? '－ '
                          : d.kind === 'gain'
                            ? '▲ '
                            : d.kind === 'loss'
                              ? '▼ '
                              : ''}
                      {d.detail}
                      {d.violation ? ' — over new limit' : ''}
                    </span>
                  </li>
                ))}
              </ul>
            )}
            {delta.some((d) => d.violation) && (
              <p role="alert" className="text-xs text-red-700">
                This change would put current usage over a new limit. You can still proceed; the
                server applies its overage policy.
              </p>
            )}
          </div>
        )}

        {error !== null && (
          <div role="alert" className="p-2 text-sm text-red-700 bg-red-50 rounded mb-3">
            {error}
          </div>
        )}
        {warning !== null && (
          <div role="status" className="p-2 text-sm text-amber-800 bg-amber-50 rounded mb-3">
            {warning}
          </div>
        )}

        <div className="flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="px-3 py-1.5 text-sm border border-gray-300 rounded"
          >
            Cancel
          </button>
          {canMutate && (
            <button
              type="button"
              disabled={!selectedPlan || submitting || selectedPlan.planId === currentPlanId}
              onClick={() => void commit()}
              className="px-3 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
            >
              {submitting ? 'Subscribing…' : 'Confirm change'}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

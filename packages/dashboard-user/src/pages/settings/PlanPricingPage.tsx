/**
 * Story 34-9 (AC6/AC8/AC9/AC11) — the tenant Plan & Pricing page (/settings/billing).
 *
 * Renders the current plan + version, the resolved entitlement set with
 * usage-vs-limit bars (from `GET /api/pricing/entitlements` + `CheckHeadroom`),
 * a sell-price-only cost estimate widget, and an upgrade/change-plan flow with
 * an entitlement-delta preview. It NEVER shows platform cost or margin — only the
 * sell price (AC6/AC7).
 *
 * Per-mode gating (AC11): the change-plan control matches the SERVER, which
 * gates `POST /api/pricing/subscribe` with `SettingsManage` = `settings:manage`
 * = OWNER-ONLY (Permissions.cs). So `canMutate = role === 'owner' || role === ''`
 * — the SaaS `owner` and the single-user sole user (no membership role → `''`)
 * can change the plan; a SaaS `tenant_admin` (and `member`) get the plan/
 * entitlements READ-ONLY, because a `tenant_admin` subscribe would 403. The UI
 * gate is UX-only — the server stays authoritative.
 *
 * Deferred (dependency endpoints not shipped): BYOK per-provider mode toggle
 * (34-3), credit balance + trial banner + promo redeem (34-7). They land with
 * their endpoints.
 */

import { useCallback, useEffect, useMemo, useState, type JSX } from 'react';
import { useAuth } from '../../hooks/useAuth';
import {
  tenantPricingApi,
  type PlanSnapshotDto,
  type ResolvedEntitlementsResponse,
} from '../../api/pricing';
import { ApiError } from '../../api/client';
import { EntitlementBar } from '../../components/pricing/EntitlementBar';
import { CostEstimateWidget } from '../../components/pricing/CostEstimateWidget';
import { UpgradePlanModal } from '../../components/pricing/UpgradePlanModal';

export function PlanPricingPage(): JSX.Element {
  const { user } = useAuth();
  const role = user?.role ?? '';
  // Change-plan mirrors the server's owner-only SettingsManage on the subscribe
  // route: only a SaaS owner or the single-user sole user (no role → '') may
  // mutate. A tenant_admin (and member) are read-only — an admin subscribe 403s.
  const canMutate = role === 'owner' || role === '';

  const [entitlements, setEntitlements] = useState<ResolvedEntitlementsResponse | null>(null);
  const [plans, setPlans] = useState<PlanSnapshotDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [noActivePlan, setNoActivePlan] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showUpgrade, setShowUpgrade] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    setNoActivePlan(false);

    // Public plans are best-effort (used for the plan name + upgrade picker).
    try {
      const p = await tenantPricingApi.listPublicPlans();
      setPlans(p.plans);
    } catch {
      setPlans([]);
    }

    try {
      const ent = await tenantPricingApi.getEntitlements();
      setEntitlements(ent);
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setEntitlements(null);
        setNoActivePlan(true);
      } else {
        setError(err instanceof Error ? err.message : 'Failed to load plan & pricing');
      }
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const currentPlan = useMemo(() => {
    if (!entitlements) return null;
    const match = plans.find((p) => p.planId === entitlements.planId);
    return {
      name: entitlements.isCustom ? 'Custom plan' : (match?.displayName ?? 'Current plan'),
      slug: match?.slug ?? null,
      version: entitlements.planVersion,
      isCustom: entitlements.isCustom,
      planId: entitlements.planId,
    };
  }, [entitlements, plans]);

  return (
    <div className="space-y-6 max-w-3xl">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Plan &amp; Pricing</h1>
        <p className="mt-1 text-sm text-gray-500">
          Your current plan, entitlement headroom, and cost estimates.
          {!canMutate && ' You have read-only access; ask an owner to change the plan.'}
        </p>
      </div>

      {error !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          {error}
        </div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500">Loading…</p>
      ) : noActivePlan ? (
        <div className="bg-white border border-gray-200 rounded-md p-6 text-center">
          <h2 className="text-lg font-medium text-gray-900">No active plan</h2>
          <p className="mt-1 text-sm text-gray-500">
            Your organization is not on a plan yet.
            {canMutate ? ' Choose one to get started.' : ' Ask an owner to choose a plan.'}
          </p>
          {canMutate && plans.length > 0 && (
            <button
              type="button"
              onClick={() => setShowUpgrade(true)}
              className="mt-4 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
            >
              Choose a plan
            </button>
          )}
        </div>
      ) : (
        currentPlan && (
          <>
            <section className="bg-white border border-gray-200 rounded-md p-4">
              <div className="flex items-start justify-between">
                <div>
                  <div className="text-xs uppercase tracking-wide text-gray-400">Current plan</div>
                  <div className="text-xl font-semibold text-gray-900">
                    {currentPlan.name}
                    {currentPlan.isCustom && (
                      <span className="ml-2 inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-purple-100 text-purple-800 align-middle">
                        custom
                      </span>
                    )}
                  </div>
                  <div className="text-xs text-gray-500 mt-1">
                    Version {currentPlan.version}
                    {currentPlan.slug ? ` · ${currentPlan.slug}` : ''}
                  </div>
                </div>
                {canMutate && (
                  <button
                    type="button"
                    onClick={() => setShowUpgrade(true)}
                    className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
                  >
                    Change plan
                  </button>
                )}
              </div>
            </section>

            <section className="bg-white border border-gray-200 rounded-md p-4 space-y-4">
              <h2 className="text-sm font-semibold text-gray-900">Entitlements &amp; headroom</h2>
              {entitlements && entitlements.limits.length > 0 ? (
                <div className="space-y-4">
                  {entitlements.limits.map((line) => (
                    <EntitlementBar key={line.metricKey} line={line} />
                  ))}
                </div>
              ) : (
                <p className="text-sm text-gray-500">No entitlements resolved for this plan.</p>
              )}
            </section>

            <CostEstimateWidget />
          </>
        )
      )}

      {showUpgrade && (
        <UpgradePlanModal
          plans={plans}
          currentEntitlements={entitlements?.limits ?? []}
          currentPlanId={entitlements?.planId ?? null}
          canMutate={canMutate}
          onClose={() => setShowUpgrade(false)}
          onSubscribed={() => {
            setShowUpgrade(false);
            void load();
          }}
        />
      )}
    </div>
  );
}

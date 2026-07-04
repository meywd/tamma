/**
 * Story 34-9 (AC2) — the admin PLAN-VERSION editor panel.
 *
 * Lists the public (non-custom) plan catalog from `GET /api/admin/pricing/plans`
 * (every status), opens a create/version editor over features / typed
 * entitlements / prices, and submits via `POST` (create v1) or `PUT` (new
 * version). Plan versions are immutable after activation (34-1): a save of an
 * existing slug creates a NEW version + deprecates the prior, so on save we
 * reload the catalog to re-render the supersede chain. Deprecation surfaces the
 * server's 409 (active tenant assignments) with the affected-tenant count and a
 * "Deprecate anyway (force)" opt-in.
 *
 * The UI never duplicates the versioning/supersede logic (34-1 owns it) — it
 * maps the form onto the wire DTOs and renders the returned PlanSnapshot.
 */

import { useCallback, useEffect, useMemo, useState, type JSX } from 'react';
import {
  adminPricingApi,
  AdminPricingApiError,
  METRIC_KEYS,
  ENTITLEMENT_PERIODS,
  OVERAGE_MODES,
  PRICING_MODES,
  metricKeyLabel,
  type PlanSnapshot,
  type PlanEntitlementBody,
  type PlanFeatureBody,
  type PlanPriceBody,
} from '../../../services/admin/admin-pricing-client.js';
import { StatusPill } from './PricingOverviewPanel.js';

interface EntitlementRow {
  metricKey: string;
  limit: string; // "" ⇒ unlimited
  period: string;
  overageMode: string;
}

interface FeatureRow {
  featureKey: string;
  boolValue: boolean;
}

interface PriceRow {
  pricingMode: string;
  recurringUsd: string;
  seatUsd: string;
}

type EditorMode = { kind: 'create' } | { kind: 'version'; slug: string; displayName: string };

const BILLING_INTERVALS = ['monthly', 'annual'];

function defaultEntitlement(): EntitlementRow {
  return { metricKey: METRIC_KEYS[0], limit: '', period: 'monthly', overageMode: 'block' };
}

function defaultPrice(): PriceRow {
  return { pricingMode: 'platform_provided', recurringUsd: '0', seatUsd: '0' };
}

export function PlanVersionEditor(): JSX.Element {
  const [plans, setPlans] = useState<PlanSnapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editor, setEditor] = useState<EditorMode | null>(null);
  const [saveResult, setSaveResult] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const resp = await adminPricingApi.listPlans({ isCustom: false });
      setPlans(resp.plans);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load plans');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const onSaved = useCallback(
    (snapshot: PlanSnapshot, message: string) => {
      setEditor(null);
      setSaveResult(message.replace('{v}', String(snapshot.version)));
      void load();
    },
    [load],
  );

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">Public plans</h3>
        <button
          type="button"
          onClick={() => {
            setSaveResult(null);
            setEditor({ kind: 'create' });
          }}
          className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
        >
          New plan
        </button>
      </div>

      {saveResult !== null && (
        <div role="status" className="p-3 text-sm text-green-800 bg-green-50 rounded-md">
          {saveResult}
        </div>
      )}
      {error !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          {error}
        </div>
      )}

      {editor !== null && (
        <PlanEditorForm
          mode={editor}
          onCancel={() => setEditor(null)}
          onSaved={onSaved}
        />
      )}

      {loading ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">Loading plans…</p>
      ) : plans.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">
          No public plans yet. Click “New plan” to create the first price-book entry.
        </p>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden dark:bg-gray-800 dark:border-gray-700">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-600 dark:bg-gray-900 dark:text-gray-400">
              <tr>
                <th className="px-3 py-2 text-left">Plan</th>
                <th className="px-3 py-2 text-left">Slug</th>
                <th className="px-3 py-2 text-right">Version</th>
                <th className="px-3 py-2 text-left">Status</th>
                <th className="px-3 py-2 text-left">Entitlements</th>
                <th className="px-3 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 dark:divide-gray-700">
              {plans.map((p) => (
                <PlanRow key={p.planId} plan={p} onChanged={load} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function PlanRow({
  plan,
  onChanged,
}: {
  plan: PlanSnapshot;
  onChanged: () => Promise<void>;
}): JSX.Element {
  const [deprecating, setDeprecating] = useState(false);
  const [affected, setAffected] = useState<number | null>(null);
  const [rowError, setRowError] = useState<string | null>(null);

  const doDeprecate = async (force: boolean): Promise<void> => {
    setDeprecating(true);
    setRowError(null);
    try {
      await adminPricingApi.deprecateVersion(plan.slug, plan.version, force);
      setAffected(null);
      await onChanged();
    } catch (err) {
      if (err instanceof AdminPricingApiError && err.status === 409) {
        const count = (err.body as { affectedTenantCount?: number }).affectedTenantCount ?? 0;
        setAffected(count);
      } else {
        setRowError(err instanceof Error ? err.message : 'Deprecate failed');
      }
    } finally {
      setDeprecating(false);
    }
  };

  const entitlementSummary = plan.entitlements
    .map((e) => `${metricKeyLabel(e.metricKey)}=${e.limitValue ?? '∞'}`)
    .join(', ');

  return (
    <tr className="hover:bg-gray-50 dark:hover:bg-gray-700/40 align-top">
      <td className="px-3 py-2 text-gray-900 dark:text-gray-100">{plan.displayName}</td>
      <td className="px-3 py-2 font-mono text-xs text-gray-600 dark:text-gray-400">{plan.slug}</td>
      <td className="px-3 py-2 text-right text-gray-700 dark:text-gray-300">v{plan.version}</td>
      <td className="px-3 py-2">
        <StatusPill status={plan.status} />
      </td>
      <td className="px-3 py-2 text-xs text-gray-500 dark:text-gray-400 max-w-xs truncate">
        {entitlementSummary || '—'}
      </td>
      <td className="px-3 py-2 text-right whitespace-nowrap">
        {plan.status === 'active' && affected === null && (
          <button
            type="button"
            disabled={deprecating}
            onClick={() => void doDeprecate(false)}
            className="px-2 py-1 text-xs border border-red-300 text-red-700 rounded hover:bg-red-50 disabled:opacity-50"
          >
            Deprecate
          </button>
        )}
        {affected !== null && (
          <div className="text-xs text-right space-y-1">
            <div className="text-amber-700">
              {affected} tenant{affected === 1 ? '' : 's'} on this version.
            </div>
            <div className="flex justify-end gap-1">
              <button
                type="button"
                onClick={() => setAffected(null)}
                className="px-2 py-1 border border-gray-300 rounded hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={deprecating}
                onClick={() => void doDeprecate(true)}
                className="px-2 py-1 border border-red-600 bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50"
              >
                Deprecate anyway (force)
              </button>
            </div>
          </div>
        )}
        {rowError !== null && <div className="text-xs text-red-700 mt-1">{rowError}</div>}
      </td>
    </tr>
  );
}

function PlanEditorForm({
  mode,
  onCancel,
  onSaved,
}: {
  mode: EditorMode;
  onCancel: () => void;
  onSaved: (snapshot: PlanSnapshot, message: string) => void;
}): JSX.Element {
  const isCreate = mode.kind === 'create';
  const [slug, setSlug] = useState('');
  const [displayName, setDisplayName] = useState(mode.kind === 'version' ? mode.displayName : '');
  const [billingInterval, setBillingInterval] = useState('monthly');
  const [entitlements, setEntitlements] = useState<EntitlementRow[]>([defaultEntitlement()]);
  const [prices, setPrices] = useState<PriceRow[]>([defaultPrice()]);
  const [features, setFeatures] = useState<FeatureRow[]>([]);
  // Version mode: leaving a collection "unreplaced" sends null ⇒ copy prior version.
  const [replaceEntitlements, setReplaceEntitlements] = useState(isCreate);
  const [replacePrices, setReplacePrices] = useState(isCreate);
  const [replaceFeatures, setReplaceFeatures] = useState(isCreate);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  const title = useMemo(
    () => (isCreate ? 'Create plan' : `New version of ${mode.kind === 'version' ? mode.slug : ''}`),
    [isCreate, mode],
  );

  const toEntitlementBodies = (): PlanEntitlementBody[] =>
    entitlements.map((e) => ({
      metricKey: e.metricKey,
      limitValue: e.limit.trim() === '' ? null : Number(e.limit),
      period: e.period,
      overageMode: e.overageMode,
    }));

  const toPriceBodies = (): PlanPriceBody[] =>
    prices.map((p) => ({
      pricingMode: p.pricingMode,
      recurringUsd: Number(p.recurringUsd) || 0,
      seatUsd: Number(p.seatUsd) || 0,
    }));

  const toFeatureBodies = (): PlanFeatureBody[] =>
    features
      .filter((f) => f.featureKey.trim() !== '')
      .map((f) => ({ featureKey: f.featureKey.trim(), boolValue: f.boolValue }));

  const submit = async (): Promise<void> => {
    setFormError(null);
    if (isCreate && slug.trim() === '') {
      setFormError('Slug is required.');
      return;
    }
    if (displayName.trim() === '') {
      setFormError('Display name is required.');
      return;
    }
    setSaving(true);
    try {
      let snapshot: PlanSnapshot;
      if (isCreate) {
        snapshot = await adminPricingApi.createPlan({
          slug: slug.trim(),
          displayName: displayName.trim(),
          billingInterval,
          entitlements: toEntitlementBodies(),
          prices: toPriceBodies(),
          features: toFeatureBodies(),
        });
        onSaved(snapshot, `Created ${snapshot.slug} v{v}.`);
      } else if (mode.kind === 'version') {
        snapshot = await adminPricingApi.versionPlan(mode.slug, {
          displayName: displayName.trim(),
          billingInterval,
          entitlements: replaceEntitlements ? toEntitlementBodies() : null,
          prices: replacePrices ? toPriceBodies() : null,
          features: replaceFeatures ? toFeatureBodies() : null,
        });
        onSaved(snapshot, `New version created: ${snapshot.slug} v{v} (prior version deprecated).`);
      }
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="bg-white rounded-lg border border-blue-200 shadow-sm p-4 space-y-4 dark:bg-gray-800 dark:border-blue-900">
      <h4 className="text-sm font-semibold text-gray-900 dark:text-gray-100">{title}</h4>

      {formError !== null && (
        <div role="alert" className="p-2 text-sm text-red-700 bg-red-50 rounded">
          {formError}
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
        {isCreate && (
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Slug
            <input
              aria-label="Slug"
              value={slug}
              onChange={(e) => setSlug(e.target.value)}
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            />
          </label>
        )}
        <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
          Display name
          <input
            aria-label="Display name"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
          />
        </label>
        <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
          Billing interval
          <select
            aria-label="Billing interval"
            value={billingInterval}
            onChange={(e) => setBillingInterval(e.target.value)}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
          >
            {BILLING_INTERVALS.map((b) => (
              <option key={b} value={b}>
                {b}
              </option>
            ))}
          </select>
        </label>
      </div>

      <EntitlementEditor
        rows={entitlements}
        setRows={setEntitlements}
        replace={replaceEntitlements}
        setReplace={setReplaceEntitlements}
        allowCopyPrior={!isCreate}
      />

      <PriceEditor
        rows={prices}
        setRows={setPrices}
        replace={replacePrices}
        setReplace={setReplacePrices}
        allowCopyPrior={!isCreate}
      />

      <FeatureEditor
        rows={features}
        setRows={setFeatures}
        replace={replaceFeatures}
        setReplace={setReplaceFeatures}
        allowCopyPrior={!isCreate}
      />

      <div className="flex justify-end gap-2">
        <button
          type="button"
          onClick={onCancel}
          className="px-3 py-1.5 text-sm border border-gray-300 rounded dark:border-gray-600"
        >
          Cancel
        </button>
        <button
          type="button"
          disabled={saving}
          onClick={() => void submit()}
          className="px-3 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
      </div>
    </div>
  );
}

function CollectionHeader({
  title,
  replace,
  setReplace,
  allowCopyPrior,
  onAdd,
}: {
  title: string;
  replace: boolean;
  setReplace: (v: boolean) => void;
  allowCopyPrior: boolean;
  onAdd?: () => void;
}): JSX.Element {
  return (
    <div className="flex items-center justify-between">
      <div className="flex items-center gap-2">
        <span className="text-xs font-semibold uppercase text-gray-500 dark:text-gray-400">
          {title}
        </span>
        {allowCopyPrior && (
          <label className="flex items-center gap-1 text-xs text-gray-500 dark:text-gray-400">
            <input
              type="checkbox"
              checked={replace}
              onChange={(e) => setReplace(e.target.checked)}
            />
            Replace (else copy prior version)
          </label>
        )}
      </div>
      {onAdd && replace && (
        <button
          type="button"
          onClick={onAdd}
          className="text-xs px-2 py-0.5 border border-gray-300 rounded hover:bg-gray-50 dark:border-gray-600"
        >
          + Add
        </button>
      )}
    </div>
  );
}

function EntitlementEditor({
  rows,
  setRows,
  replace,
  setReplace,
  allowCopyPrior,
}: {
  rows: EntitlementRow[];
  setRows: (r: EntitlementRow[]) => void;
  replace: boolean;
  setReplace: (v: boolean) => void;
  allowCopyPrior: boolean;
}): JSX.Element {
  const update = (i: number, patch: Partial<EntitlementRow>): void =>
    setRows(rows.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));

  return (
    <div className="space-y-2">
      <CollectionHeader
        title="Entitlements"
        replace={replace}
        setReplace={setReplace}
        allowCopyPrior={allowCopyPrior}
        onAdd={() => setRows([...rows, defaultEntitlement()])}
      />
      {replace &&
        rows.map((r, i) => (
          <div key={i} className="grid grid-cols-2 md:grid-cols-5 gap-2 items-end">
            <label className="flex flex-col text-[10px] text-gray-500">
              Metric
              <select
                aria-label={`Entitlement metric ${i}`}
                value={r.metricKey}
                onChange={(e) => update(i, { metricKey: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              >
                {METRIC_KEYS.map((m) => (
                  <option key={m} value={m}>
                    {m}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex flex-col text-[10px] text-gray-500">
              Limit (blank=∞)
              <input
                aria-label={`Entitlement limit ${i}`}
                value={r.limit}
                inputMode="numeric"
                onChange={(e) => update(i, { limit: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              />
            </label>
            <label className="flex flex-col text-[10px] text-gray-500">
              Period
              <select
                aria-label={`Entitlement period ${i}`}
                value={r.period}
                onChange={(e) => update(i, { period: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              >
                {ENTITLEMENT_PERIODS.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex flex-col text-[10px] text-gray-500">
              Overage
              <select
                aria-label={`Entitlement overage ${i}`}
                value={r.overageMode}
                onChange={(e) => update(i, { overageMode: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              >
                {OVERAGE_MODES.map((o) => (
                  <option key={o} value={o}>
                    {o}
                  </option>
                ))}
              </select>
            </label>
            <button
              type="button"
              onClick={() => setRows(rows.filter((_, idx) => idx !== i))}
              className="px-2 py-1 text-xs text-red-600 border border-red-200 rounded hover:bg-red-50"
            >
              Remove
            </button>
          </div>
        ))}
    </div>
  );
}

function PriceEditor({
  rows,
  setRows,
  replace,
  setReplace,
  allowCopyPrior,
}: {
  rows: PriceRow[];
  setRows: (r: PriceRow[]) => void;
  replace: boolean;
  setReplace: (v: boolean) => void;
  allowCopyPrior: boolean;
}): JSX.Element {
  const update = (i: number, patch: Partial<PriceRow>): void =>
    setRows(rows.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));

  return (
    <div className="space-y-2">
      <CollectionHeader
        title="Prices"
        replace={replace}
        setReplace={setReplace}
        allowCopyPrior={allowCopyPrior}
        onAdd={() => setRows([...rows, defaultPrice()])}
      />
      {replace &&
        rows.map((r, i) => (
          <div key={i} className="grid grid-cols-2 md:grid-cols-4 gap-2 items-end">
            <label className="flex flex-col text-[10px] text-gray-500">
              Pricing mode
              <select
                aria-label={`Price mode ${i}`}
                value={r.pricingMode}
                onChange={(e) => update(i, { pricingMode: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              >
                {PRICING_MODES.map((m) => (
                  <option key={m} value={m}>
                    {m}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex flex-col text-[10px] text-gray-500">
              Recurring $
              <input
                aria-label={`Price recurring ${i}`}
                value={r.recurringUsd}
                inputMode="decimal"
                onChange={(e) => update(i, { recurringUsd: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              />
            </label>
            <label className="flex flex-col text-[10px] text-gray-500">
              Seat $
              <input
                aria-label={`Price seat ${i}`}
                value={r.seatUsd}
                inputMode="decimal"
                onChange={(e) => update(i, { seatUsd: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              />
            </label>
            <button
              type="button"
              onClick={() => setRows(rows.filter((_, idx) => idx !== i))}
              className="px-2 py-1 text-xs text-red-600 border border-red-200 rounded hover:bg-red-50"
            >
              Remove
            </button>
          </div>
        ))}
    </div>
  );
}

function FeatureEditor({
  rows,
  setRows,
  replace,
  setReplace,
  allowCopyPrior,
}: {
  rows: FeatureRow[];
  setRows: (r: FeatureRow[]) => void;
  replace: boolean;
  setReplace: (v: boolean) => void;
  allowCopyPrior: boolean;
}): JSX.Element {
  const update = (i: number, patch: Partial<FeatureRow>): void =>
    setRows(rows.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));

  return (
    <div className="space-y-2">
      <CollectionHeader
        title="Features"
        replace={replace}
        setReplace={setReplace}
        allowCopyPrior={allowCopyPrior}
        onAdd={() => setRows([...rows, { featureKey: '', boolValue: true }])}
      />
      {replace &&
        rows.map((r, i) => (
          <div key={i} className="grid grid-cols-2 md:grid-cols-3 gap-2 items-end">
            <label className="flex flex-col text-[10px] text-gray-500">
              Feature key
              <input
                aria-label={`Feature key ${i}`}
                value={r.featureKey}
                onChange={(e) => update(i, { featureKey: e.target.value })}
                className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
              />
            </label>
            <label className="flex items-center gap-1 text-xs text-gray-500 mt-4">
              <input
                type="checkbox"
                aria-label={`Feature enabled ${i}`}
                checked={r.boolValue}
                onChange={(e) => update(i, { boolValue: e.target.checked })}
              />
              Enabled
            </label>
            <button
              type="button"
              onClick={() => setRows(rows.filter((_, idx) => idx !== i))}
              className="px-2 py-1 text-xs text-red-600 border border-red-200 rounded hover:bg-red-50"
            >
              Remove
            </button>
          </div>
        ))}
    </div>
  );
}

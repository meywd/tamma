/**
 * DialPanel — the read-only "where is the dial and what does it govern" view.
 *
 * Reads GET /api/actions/dial + /catalog + /policy and renders the current
 * dial position plus the full action catalog grouped by group: wire key,
 * plain-English name, effective minimum autonomy, and whether the row is
 * machinery (deterministic plumbing the dial never governs — shown, but
 * marked, and never editable).
 *
 * The dial range comes from the server (/dial); nothing here restates or
 * validates a bound.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import {
  actionsPolicyApi,
  type ActionPolicyResponse,
  type AutonomyDialInfo,
  type CatalogAction,
  type PolicyAction,
} from '../../../services/admin/actions-policy-api-client.js';

export function DialPanel(): JSX.Element {
  const [dial, setDial] = useState<AutonomyDialInfo | null>(null);
  const [catalog, setCatalog] = useState<CatalogAction[]>([]);
  const [policy, setPolicy] = useState<ActionPolicyResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [dialInfo, catalogRows, policyView] = await Promise.all([
        actionsPolicyApi.getDial(),
        actionsPolicyApi.getCatalog(),
        actionsPolicyApi.getPolicy(),
      ]);
      setDial(dialInfo);
      setCatalog(catalogRows);
      setPolicy(policyView);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load the autonomy dial');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (loading) {
    return (
      <div className="flex justify-center py-16">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error !== null || dial === null || policy === null) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
        <div className="font-medium mb-1">Failed to load the autonomy dial</div>
        <div className="mb-3">{error ?? 'No data'}</div>
        <button
          type="button"
          onClick={() => void load()}
          className="px-3 py-1.5 text-xs font-medium text-red-700 border border-red-300 bg-white rounded-md hover:bg-red-100 dark:bg-gray-800 dark:text-red-300 dark:border-red-700"
        >
          Retry
        </button>
      </div>
    );
  }

  const policyByKey = new Map<string, PolicyAction>(policy.actions.map((a) => [a.key, a]));

  return (
    <div className="space-y-6">
      {/* Current dial */}
      <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 dark:bg-gray-800 dark:border-gray-700">
        <div className="flex items-baseline gap-3">
          <span
            data-testid="dial-current"
            className="text-4xl font-bold text-gray-900 dark:text-gray-100"
          >
            {policy.dial.current}
          </span>
          <span className="text-sm text-gray-500 dark:text-gray-400">Autonomy dial</span>
        </div>
        <p className="text-sm text-gray-600 mt-2 dark:text-gray-400">
          Levels run {dial.min} (most supervised) to {dial.max} (full auto); a fresh
          deployment starts at {dial.default}. An action runs without a person once the
          dial reaches its minimum autonomy. The dial itself is set per document type on
          the Acceptance Rules page (the <span className="font-mono">base</span> row).
        </p>
      </div>

      {/* Catalog grouped by group */}
      {policy.groups.map((group) => {
        const members = catalog.filter((c) => c.group === group.group);
        if (members.length === 0) return null;
        return (
          <div key={group.group}>
            <h3 className="text-sm font-semibold text-gray-900 mb-1 dark:text-gray-100">
              <span className="font-mono">{group.group}</span>
            </h3>
            <p className="text-xs text-gray-500 mb-2 dark:text-gray-400">{group.description}</p>
            <div className="overflow-x-auto border border-gray-200 rounded-md dark:border-gray-700">
              <table className="min-w-full text-sm">
                <thead className="bg-gray-50 dark:bg-gray-800">
                  <tr>
                    <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Action</th>
                    <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Min autonomy</th>
                    <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">At current dial</th>
                  </tr>
                </thead>
                <tbody>
                  {members.map((action) => {
                    const resolved = policyByKey.get(action.key);
                    const machinery = resolved?.isMachinery === true;
                    return (
                      <tr
                        key={action.key}
                        data-testid={`dial-row-${action.key}`}
                        className="border-t border-gray-100 dark:border-gray-800"
                      >
                        <td className="px-4 py-2">
                          <div className="text-gray-900 dark:text-gray-100">{action.title}</div>
                          <div className="font-mono text-xs text-gray-500 dark:text-gray-400">{action.key}</div>
                        </td>
                        <td className="px-4 py-2 text-gray-700 dark:text-gray-300">
                          {machinery
                            ? '—'
                            : resolved !== undefined && resolved.minAutonomy > dial.max
                              ? 'Always a person'
                              : (resolved?.minAutonomy ?? action.defaultMinAutonomy)}
                        </td>
                        <td className="px-4 py-2">
                          {machinery ? (
                            <span
                              data-testid={`dial-machinery-${action.key}`}
                              className="inline-block px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400"
                            >
                              Not dial-governed
                            </span>
                          ) : resolved?.automatedAtLevel === true ? (
                            <span className="inline-block px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300">
                              Runs automatically
                            </span>
                          ) : (
                            <span className="inline-block px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300">
                              Needs a person
                            </span>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        );
      })}
    </div>
  );
}

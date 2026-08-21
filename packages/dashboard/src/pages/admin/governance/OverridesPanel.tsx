/**
 * OverridesPanel — edit the stored policy rows on top of the shipped defaults.
 *
 * Per-group: set/clear a minimum-autonomy threshold
 *   (PUT/DELETE /api/actions/policy/groups/{group}[/threshold]).
 * Per-action: force an above-dial action on (the per-action threshold toggle),
 *   flip enabled/enforce, or remove the stored row
 *   (PUT/DELETE /api/actions/policy/actions/{ns}/{key}/…).
 * Reset all: POST /api/actions/policy/reset behind an inline confirm.
 *
 * Provenance comes from the resolved policy view: each action row's `source`
 * names the tier that supplied its effective threshold (system default vs
 * group/action override vs platform ceiling).
 *
 * The dial range is server-owned: the group threshold input mirrors the
 * min/max the API reports as an affordance only — no value is validated here,
 * and a server rejection is shown as the error it returns.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import {
  actionsPolicyApi,
  type ActionPolicyResponse,
  type ActionPolicySource,
  type PolicyAction,
} from '../../../services/admin/actions-policy-api-client.js';

const SOURCE_LABEL: Record<ActionPolicySource, string> = {
  'system-default': 'Default',
  'group-override': 'Group override',
  'action-override': 'Action override',
  'platform-ceiling': 'Platform ceiling',
  'always-escalate-legacy': 'Always-escalate floor',
};

const SOURCE_CLASS: Record<ActionPolicySource, string> = {
  'system-default': 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300',
  'group-override': 'bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300',
  'action-override': 'bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300',
  'platform-ceiling': 'bg-purple-100 text-purple-800 dark:bg-purple-950 dark:text-purple-300',
  'always-escalate-legacy': 'bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300',
};

function GroupThresholdEditor({
  group,
  currentOverride,
  min,
  max,
  onSet,
  onClear,
}: {
  group: string;
  currentOverride: number | null;
  min: number;
  max: number;
  onSet: (value: number) => void;
  onClear: () => void;
}): JSX.Element {
  const [text, setText] = useState<string>(
    currentOverride !== null ? String(currentOverride) : '',
  );
  return (
    <div className="flex items-center gap-2">
      <input
        type="number"
        aria-label={`Threshold for ${group}`}
        // Affordance only — the validated range is server-owned and these
        // bounds come from the /dial response, not a client constant.
        min={min}
        max={max}
        value={text}
        onChange={(e) => setText(e.target.value)}
        className="w-20 rounded border border-gray-300 px-2 py-1 text-sm dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
      />
      <button
        type="button"
        onClick={() => {
          if (text.trim() !== '') onSet(Number(text.trim()));
        }}
        className="px-2 py-1 text-xs font-medium text-white bg-blue-600 rounded hover:bg-blue-700"
      >
        Set
      </button>
      {currentOverride !== null && (
        <button
          type="button"
          onClick={onClear}
          className="px-2 py-1 text-xs font-medium text-red-700 border border-red-300 rounded hover:bg-red-50 dark:text-red-300 dark:border-red-700 dark:hover:bg-red-950"
        >
          Clear
        </button>
      )}
    </div>
  );
}

export function OverridesPanel(): JSX.Element {
  const [policy, setPolicy] = useState<ActionPolicyResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [confirmingReset, setConfirmingReset] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setPolicy(await actionsPolicyApi.getPolicy());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load the policy');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const mutate = useCallback(
    async (fn: () => Promise<unknown>) => {
      setActionError(null);
      try {
        await fn();
        await load();
      } catch (err) {
        setActionError(err instanceof Error ? err.message : 'Change failed');
      }
    },
    [load],
  );

  if (loading && policy === null) {
    return (
      <div className="flex justify-center py-16">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error !== null || policy === null) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
        <div className="font-medium mb-1">Failed to load the policy</div>
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

  const dialGoverned = policy.actions.filter((a) => !a.isMachinery);
  const machinery = policy.actions.filter((a) => a.isMachinery);

  const renderActionRow = (action: PolicyAction): JSX.Element => (
    <tr
      key={action.key}
      data-testid={`override-row-${action.key}`}
      className="border-t border-gray-100 dark:border-gray-800"
    >
      <td className="px-4 py-2">
        <div className="text-gray-900 dark:text-gray-100">{action.title}</div>
        <div className="font-mono text-xs text-gray-500 dark:text-gray-400">{action.key}</div>
      </td>
      <td className="px-4 py-2 text-gray-700 dark:text-gray-300">
        {action.isMachinery
          ? '—'
          : action.minAutonomy > policy.dial.max
            ? 'Always a person'
            : action.minAutonomy}
      </td>
      <td className="px-4 py-2">
        {action.isMachinery ? (
          <span className="inline-block px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
            Not dial-governed
          </span>
        ) : (
          <span
            data-testid={`override-source-${action.key}`}
            className={`inline-block px-2 py-0.5 rounded text-xs font-medium ${SOURCE_CLASS[action.source]}`}
          >
            {SOURCE_LABEL[action.source]}
          </span>
        )}
      </td>
      <td className="px-4 py-2">
        <button
          type="button"
          aria-label={`${action.enabled ? 'Disable' : 'Enable'} ${action.key}`}
          onClick={() =>
            void mutate(() => actionsPolicyApi.setActionEnabled(action.key, !action.enabled))
          }
          className={`px-2 py-1 text-xs font-medium rounded border ${
            action.enabled
              ? 'text-green-800 border-green-300 bg-green-50 dark:text-green-300 dark:border-green-700 dark:bg-green-950'
              : 'text-gray-600 border-gray-300 bg-gray-50 dark:text-gray-300 dark:border-gray-600 dark:bg-gray-800'
          }`}
        >
          {action.enabled ? 'Enabled' : 'Disabled'}
        </button>
      </td>
      <td className="px-4 py-2">
        {action.isMachinery ? (
          <span className="text-xs text-gray-400 dark:text-gray-500">—</span>
        ) : (
          <button
            type="button"
            aria-label={`${action.enforce ? 'Stop enforcing' : 'Enforce'} ${action.key}`}
            onClick={() =>
              void mutate(() => actionsPolicyApi.setActionEnforce(action.key, !action.enforce))
            }
            className={`px-2 py-1 text-xs font-medium rounded border ${
              action.enforce
                ? 'text-blue-800 border-blue-300 bg-blue-50 dark:text-blue-300 dark:border-blue-700 dark:bg-blue-950'
                : 'text-gray-600 border-gray-300 bg-gray-50 dark:text-gray-300 dark:border-gray-600 dark:bg-gray-800'
            }`}
          >
            {action.enforce ? 'Enforced' : 'Advisory'}
          </button>
        )}
      </td>
      <td className="px-4 py-2">
        <div className="flex items-center gap-2">
          {!action.isMachinery && action.editable && (
            <button
              type="button"
              aria-label={`Force ${action.key} on`}
              title="Run this action automatically even though the dial has not reached its level. The stored value is the server's own minimum — nothing is computed here."
              onClick={() =>
                void mutate(() =>
                  actionsPolicyApi.setActionThreshold(action.key, policy.dial.min),
                )
              }
              className="px-2 py-1 text-xs font-medium text-white bg-blue-600 rounded hover:bg-blue-700"
            >
              Force on
            </button>
          )}
          {!action.isMachinery && action.levelOwned && (
            <span className="text-xs text-gray-500 dark:text-gray-400">
              Automated by the dial
            </span>
          )}
          {action.source === 'action-override' && (
            <button
              type="button"
              aria-label={`Remove override for ${action.key}`}
              onClick={() => void mutate(() => actionsPolicyApi.deleteActionOverride(action.key))}
              className="px-2 py-1 text-xs font-medium text-red-700 border border-red-300 rounded hover:bg-red-50 dark:text-red-300 dark:border-red-700 dark:hover:bg-red-950"
            >
              Remove override
            </button>
          )}
        </div>
      </td>
    </tr>
  );

  return (
    <div className="space-y-6">
      {actionError !== null && (
        <div
          role="alert"
          className="bg-red-50 border border-red-200 rounded-md p-3 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800"
        >
          {actionError}
        </div>
      )}

      {/* Reset all */}
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600 dark:text-gray-400">
          Overrides stored on top of the shipped defaults. Removing them all puts every
          action back on its default level.
        </p>
        {confirmingReset ? (
          <div className="flex items-center gap-2">
            <span className="text-sm text-red-700 dark:text-red-300">
              Remove every stored override?
            </span>
            <button
              type="button"
              onClick={() => {
                setConfirmingReset(false);
                void mutate(() => actionsPolicyApi.resetPolicy());
              }}
              className="px-3 py-1.5 text-xs font-medium text-white bg-red-600 rounded-md hover:bg-red-700"
            >
              Yes, remove all
            </button>
            <button
              type="button"
              onClick={() => setConfirmingReset(false)}
              className="px-3 py-1.5 text-xs font-medium text-gray-700 border border-gray-300 rounded-md hover:bg-gray-50 dark:text-gray-200 dark:border-gray-600 dark:hover:bg-gray-800"
            >
              Cancel
            </button>
          </div>
        ) : (
          <button
            type="button"
            onClick={() => setConfirmingReset(true)}
            className="px-3 py-1.5 text-xs font-medium text-red-700 border border-red-300 rounded-md hover:bg-red-50 dark:text-red-300 dark:border-red-700 dark:hover:bg-red-950"
          >
            Reset all overrides
          </button>
        )}
      </div>

      {/* Group thresholds */}
      <div>
        <h3 className="text-sm font-semibold text-gray-900 mb-2 dark:text-gray-100">
          Group thresholds
        </h3>
        <div className="overflow-x-auto border border-gray-200 rounded-md dark:border-gray-700">
          <table className="min-w-full text-sm">
            <thead className="bg-gray-50 dark:bg-gray-800">
              <tr>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Group</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Actions</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Override</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Edit</th>
              </tr>
            </thead>
            <tbody>
              {policy.groups.map((group) => (
                <tr
                  key={group.group}
                  data-testid={`group-row-${group.group}`}
                  className="border-t border-gray-100 dark:border-gray-800"
                >
                  <td className="px-4 py-2">
                    <div className="font-mono text-gray-900 dark:text-gray-100">{group.group}</div>
                    <div className="text-xs text-gray-500 dark:text-gray-400">{group.description}</div>
                  </td>
                  <td className="px-4 py-2 text-gray-700 dark:text-gray-300">{group.members}</td>
                  <td className="px-4 py-2 text-gray-700 dark:text-gray-300">
                    {group.principalRow?.minAutonomy ?? '—'}
                  </td>
                  <td className="px-4 py-2">
                    <GroupThresholdEditor
                      group={group.group}
                      currentOverride={group.principalRow?.minAutonomy ?? null}
                      min={policy.dial.min}
                      max={policy.dial.max}
                      onSet={(value) =>
                        void mutate(() => actionsPolicyApi.setGroupThreshold(group.group, value))
                      }
                      onClear={() =>
                        void mutate(() => actionsPolicyApi.deleteGroupOverride(group.group))
                      }
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {/* Per-action rows */}
      <div>
        <h3 className="text-sm font-semibold text-gray-900 mb-2 dark:text-gray-100">Actions</h3>
        <div className="overflow-x-auto border border-gray-200 rounded-md dark:border-gray-700">
          <table className="min-w-full text-sm">
            <thead className="bg-gray-50 dark:bg-gray-800">
              <tr>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Action</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Min autonomy</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Source</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Enabled</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Enforcement</th>
                <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Edit</th>
              </tr>
            </thead>
            <tbody>
              {dialGoverned.map(renderActionRow)}
              {machinery.map(renderActionRow)}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

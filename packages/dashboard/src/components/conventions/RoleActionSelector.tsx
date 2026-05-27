/**
 * RoleActionSelector — dual dropdown for picking a (role, action) pair that is
 * eligible according to the registry. When a role is selected, the action
 * dropdown is scoped to only the actions that are eligible for that role.
 * Picking an ineligible pair is prevented at the UI level.
 *
 * Story 27-11 AC: prevents picking ineligible pairs.
 */

import { useMemo, type JSX } from 'react';
import type { EligiblePair } from '../../hooks/admin/useAdminConventions.js';

interface RoleActionSelectorProps {
  roles: string[];
  eligiblePairs: EligiblePair[];
  selectedRole: string;
  selectedAction: string;
  onRoleChange: (role: string) => void;
  onActionChange: (action: string) => void;
  disabled?: boolean;
}

export function RoleActionSelector({
  roles,
  eligiblePairs,
  selectedRole,
  selectedAction,
  onRoleChange,
  onActionChange,
  disabled = false,
}: RoleActionSelectorProps): JSX.Element {
  const actionsForRole = useMemo(() => {
    if (!selectedRole) return [];
    return eligiblePairs
      .filter((p) => p.role === selectedRole)
      .map((p) => p.action)
      .sort();
  }, [eligiblePairs, selectedRole]);

  const handleRoleChange = (role: string) => {
    onRoleChange(role);
    // Clear action if it's no longer eligible for the new role.
    const stillEligible = eligiblePairs.some(
      (p) => p.role === role && p.action === selectedAction,
    );
    if (!stillEligible) {
      onActionChange('');
    }
  };

  return (
    <div className="flex flex-wrap gap-4">
      <div className="flex-1 min-w-[160px]">
        <label
          htmlFor="convention-role-select"
          className="block text-xs font-semibold uppercase tracking-wider text-gray-500 mb-1 dark:text-gray-400"
        >
          Role
        </label>
        <select
          id="convention-role-select"
          value={selectedRole}
          onChange={(e) => handleRoleChange(e.target.value)}
          disabled={disabled}
          className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50 dark:border-gray-600 dark:bg-gray-800"
        >
          <option value="">— select role —</option>
          {roles.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </select>
      </div>

      <div className="flex-1 min-w-[160px]">
        <label
          htmlFor="convention-action-select"
          className="block text-xs font-semibold uppercase tracking-wider text-gray-500 mb-1 dark:text-gray-400"
        >
          Action
        </label>
        <select
          id="convention-action-select"
          value={selectedAction}
          onChange={(e) => onActionChange(e.target.value)}
          disabled={disabled || !selectedRole}
          className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50 dark:border-gray-600 dark:bg-gray-800"
        >
          <option value="">— select action —</option>
          {actionsForRole.map((a) => (
            <option key={a} value={a}>
              {a}
            </option>
          ))}
        </select>
        {selectedRole && actionsForRole.length === 0 && (
          <p className="mt-1 text-xs text-amber-600 dark:text-amber-400">
            No eligible actions for this role.
          </p>
        )}
      </div>
    </div>
  );
}

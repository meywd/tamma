/**
 * TenantPromptTable — lists the 80 role+action prompts with an "Override"
 * badge when the tenant has customised the shipped default (Story 27-5
 * AC #2, AC #3, AC #11, AC #12).
 */

import { useMemo, useState, type JSX } from 'react';
import type { ResolvedPrompt } from '../../hooks/useTenantPrompts.js';
import { OverrideBadge } from './OverrideBadge.js';

interface TenantPromptTableProps {
  prompts: ResolvedPrompt[];
  overrideCount: number;
  onRowClick: (role: string, action: string) => void;
}

export function TenantPromptTable({
  prompts,
  overrideCount,
  onRowClick,
}: TenantPromptTableProps): JSX.Element {
  const [roleFilter, setRoleFilter] = useState('all');
  const [actionFilter, setActionFilter] = useState('all');

  const roles = useMemo(
    () => Array.from(new Set(prompts.map((p) => p.role))).sort(),
    [prompts],
  );
  const actions = useMemo(
    () => Array.from(new Set(prompts.map((p) => p.action))).sort(),
    [prompts],
  );

  const filtered = useMemo(
    () =>
      prompts.filter(
        (p) =>
          (roleFilter === 'all' || p.role === roleFilter) &&
          (actionFilter === 'all' || p.action === actionFilter),
      ),
    [prompts, roleFilter, actionFilter],
  );

  return (
    <div>
      <div className="mb-4 text-sm text-gray-600 dark:text-gray-400">
        <strong>{overrideCount}</strong> of {prompts.length} prompts overridden
      </div>

      <div className="flex flex-wrap gap-4 mb-4">
        <div>
          <label htmlFor="prompt-role-filter" className="block text-xs text-gray-500 mb-1 dark:text-gray-400">
            Filter by role
          </label>
          <select
            id="prompt-role-filter"
            aria-label="Filter by role"
            value={roleFilter}
            onChange={(e) => setRoleFilter(e.target.value)}
            className="px-3 py-1.5 text-sm border border-gray-300 rounded-md dark:border-gray-600"
          >
            <option value="all">All Roles</option>
            {roles.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="prompt-action-filter" className="block text-xs text-gray-500 mb-1 dark:text-gray-400">
            Filter by action
          </label>
          <select
            id="prompt-action-filter"
            aria-label="Filter by action"
            value={actionFilter}
            onChange={(e) => setActionFilter(e.target.value)}
            className="px-3 py-1.5 text-sm border border-gray-300 rounded-md dark:border-gray-600"
          >
            <option value="all">All Actions</option>
            {actions.map((a) => (
              <option key={a} value={a}>
                {a}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div className="overflow-x-auto bg-white border border-gray-200 rounded-lg dark:bg-gray-800 dark:border-gray-700">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 text-gray-600 dark:bg-gray-900 dark:text-gray-400">
            <tr>
              <th className="px-4 py-2 text-left font-medium">Role</th>
              <th className="px-4 py-2 text-left font-medium">Action</th>
              <th className="px-4 py-2 text-left font-medium">Source</th>
              <th className="px-4 py-2 text-left font-medium">Tools</th>
              <th className="px-4 py-2 text-left font-medium">Max Tokens</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((p) => {
              const isOverride = p.source === 'user';
              return (
                <tr
                  key={`${p.role}:${p.action}`}
                  data-testid={`prompt-row-${p.role}-${p.action}`}
                  onClick={() => onRowClick(p.role, p.action)}
                  className={`cursor-pointer border-t border-gray-100 hover:bg-gray-50 ${ isOverride ? 'bg-blue-50 border-l-4 border-l-blue-400' : '' } dark:border-gray-800 dark:hover:bg-gray-800`}
                >
                  <td className="px-4 py-2 font-mono text-xs">{p.role}</td>
                  <td className="px-4 py-2 font-mono text-xs">{p.action}</td>
                  <td className="px-4 py-2">
                    <OverrideBadge source={p.source} />
                  </td>
                  <td className="px-4 py-2">{p.enableTools ? 'Yes' : 'No'}</td>
                  <td className="px-4 py-2">{p.maxTokens}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
        {filtered.length === 0 && (
          <div className="py-10 text-center text-sm text-gray-500 dark:text-gray-400">
            No prompts match the current filters.
          </div>
        )}
      </div>
    </div>
  );
}

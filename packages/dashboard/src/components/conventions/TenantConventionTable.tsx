/**
 * TenantConventionTable — lists the resolved conventions with override
 * highlighting: badge + left-border + background tint for overridden rows.
 *
 * Story 27-12 AC: resolved list with isOverride highlighting, count indicator,
 * filter by role + source.
 */

import { useMemo, useState, type JSX } from 'react';
import type { ConventionResponse } from '../../services/admin/conventions-api-client.js';
import { ConventionOverrideBadge } from './OverrideBadge.js';

interface TenantConventionTableProps {
  conventions: ConventionResponse[];
  overrideCount: number;
  onRowClick: (role: string, action: string) => void;
  onNewClick: () => void;
}

type SourceFilter = 'all' | 'override' | 'system';

export function TenantConventionTable({
  conventions,
  overrideCount,
  onRowClick,
  onNewClick,
}: TenantConventionTableProps): JSX.Element {
  const [roleFilter, setRoleFilter] = useState('all');
  const [sourceFilter, setSourceFilter] = useState<SourceFilter>('all');

  const roles = useMemo(
    () => Array.from(new Set(conventions.map((c) => c.role))).sort(),
    [conventions],
  );

  const filtered = useMemo(() => {
    return conventions.filter((c) => {
      if (roleFilter !== 'all' && c.role !== roleFilter) return false;
      if (sourceFilter === 'override' && !c.isOverride) return false;
      if (sourceFilter === 'system' && c.isOverride) return false;
      return true;
    });
  }, [conventions, roleFilter, sourceFilter]);

  return (
    <div>
      <div className="mb-4 flex items-center justify-between flex-wrap gap-2">
        <div className="text-sm text-gray-600 dark:text-gray-400">
          <strong>{overrideCount}</strong> of {conventions.length} conventions overridden
        </div>
        <button
          type="button"
          onClick={onNewClick}
          className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
        >
          New Convention
        </button>
      </div>

      <div className="flex flex-wrap gap-4 mb-4">
        <div>
          <label
            htmlFor="tenant-conv-role-filter"
            className="block text-xs text-gray-500 mb-1 dark:text-gray-400"
          >
            Filter by role
          </label>
          <select
            id="tenant-conv-role-filter"
            aria-label="Filter by role"
            value={roleFilter}
            onChange={(e) => setRoleFilter(e.target.value)}
            className="px-3 py-1.5 text-sm border border-gray-300 rounded-md dark:border-gray-600 dark:bg-gray-800"
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
          <label
            htmlFor="tenant-conv-source-filter"
            className="block text-xs text-gray-500 mb-1 dark:text-gray-400"
          >
            Filter by source
          </label>
          <select
            id="tenant-conv-source-filter"
            aria-label="Filter by source"
            value={sourceFilter}
            onChange={(e) => setSourceFilter(e.target.value as SourceFilter)}
            className="px-3 py-1.5 text-sm border border-gray-300 rounded-md dark:border-gray-600 dark:bg-gray-800"
          >
            <option value="all">All Sources</option>
            <option value="override">Overrides Only</option>
            <option value="system">System Defaults Only</option>
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
              <th className="px-4 py-2 text-left font-medium">Enabled</th>
              <th className="px-4 py-2 text-left font-medium">Version</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((c) => {
              const isOverride = !!c.isOverride;
              return (
                <tr
                  key={`${c.role}:${c.action}`}
                  data-testid={`tenant-conv-row-${c.role}-${c.action}`}
                  onClick={() => onRowClick(c.role, c.action)}
                  className={`cursor-pointer border-t border-gray-100 hover:bg-gray-50 ${
                    isOverride
                      ? 'bg-blue-50 border-l-4 border-l-blue-400 dark:bg-blue-950'
                      : ''
                  } dark:border-gray-800 dark:hover:bg-gray-700`}
                >
                  <td className="px-4 py-2 font-mono text-xs">{c.role}</td>
                  <td className="px-4 py-2 font-mono text-xs">{c.action}</td>
                  <td className="px-4 py-2">
                    <ConventionOverrideBadge source={c.source} isOverride={isOverride} />
                  </td>
                  <td className="px-4 py-2">
                    {c.enabled ? (
                      <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200">
                        Yes
                      </span>
                    ) : (
                      <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
                        No
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-2 text-xs text-gray-500 dark:text-gray-400">
                    v{c.version}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        {filtered.length === 0 && (
          <div className="py-10 text-center text-sm text-gray-500 dark:text-gray-400">
            No conventions match the current filters.
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * ConventionTable — filterable, searchable table of system-default conventions
 * for the admin page. Columns: Role, Action, Enabled, Source, Last Updated.
 * NO Name/Description columns.
 *
 * Each row has a "System Seed" badge (every (role,action) in the registry is
 * seeded by ConventionSeedSpecs, so the badge is always shown).
 *
 * Story 27-11 AC: table with role filter + enabled toggle + text search.
 */

import { useMemo, useState, type JSX } from 'react';
import type { ConventionResponse } from '../../services/admin/conventions-api-client.js';

interface ConventionTableProps {
  conventions: ConventionResponse[];
  onRowClick: (role: string, action: string) => void;
  onNewClick: () => void;
}

export function ConventionTable({
  conventions,
  onRowClick,
  onNewClick,
}: ConventionTableProps): JSX.Element {
  const [roleFilter, setRoleFilter] = useState('all');
  const [enabledFilter, setEnabledFilter] = useState<'all' | 'enabled' | 'disabled'>('all');
  const [searchQuery, setSearchQuery] = useState('');

  const roles = useMemo(
    () => Array.from(new Set(conventions.map((c) => c.role))).sort(),
    [conventions],
  );

  const filtered = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    return conventions.filter((c) => {
      if (roleFilter !== 'all' && c.role !== roleFilter) return false;
      if (enabledFilter === 'enabled' && !c.enabled) return false;
      if (enabledFilter === 'disabled' && c.enabled) return false;
      if (q.length > 0 && !c.body.toLowerCase().includes(q)) return false;
      return true;
    });
  }, [conventions, roleFilter, enabledFilter, searchQuery]);

  const fmt = (iso: string) => {
    try {
      return new Date(iso).toLocaleDateString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
      });
    } catch {
      return iso;
    }
  };

  return (
    <div>
      {/* Filter bar */}
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <select
          aria-label="Filter by role"
          value={roleFilter}
          onChange={(e) => setRoleFilter(e.target.value)}
          className="text-sm border border-gray-300 rounded-md px-3 py-1.5 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 min-w-[160px] dark:bg-gray-800 dark:border-gray-600"
        >
          <option value="all">All Roles ({roles.length})</option>
          {roles.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </select>

        <select
          aria-label="Filter by enabled state"
          value={enabledFilter}
          onChange={(e) => setEnabledFilter(e.target.value as 'all' | 'enabled' | 'disabled')}
          className="text-sm border border-gray-300 rounded-md px-3 py-1.5 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 min-w-[140px] dark:bg-gray-800 dark:border-gray-600"
        >
          <option value="all">All States</option>
          <option value="enabled">Enabled</option>
          <option value="disabled">Disabled</option>
        </select>

        <input
          type="search"
          aria-label="Search convention body"
          placeholder="Search body content…"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          className="text-sm border border-gray-300 rounded-md px-3 py-1.5 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 flex-1 max-w-xs dark:bg-gray-800 dark:border-gray-600"
        />

        <span className="text-xs text-gray-500 dark:text-gray-400">
          {filtered.length} of {conventions.length} conventions
        </span>

        <button
          type="button"
          onClick={onNewClick}
          className="ml-auto px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
        >
          New Convention
        </button>
      </div>

      {/* Table */}
      <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden dark:bg-gray-800 dark:border-gray-700">
        <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
          <thead className="bg-gray-50 dark:bg-gray-900">
            <tr>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Role
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Action
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Enabled
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Source
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Last Updated
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Seed
              </th>
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200 dark:bg-gray-800 dark:divide-gray-700">
            {filtered.length === 0 ? (
              <tr>
                <td
                  colSpan={6}
                  className="px-4 py-8 text-center text-sm text-gray-500 dark:text-gray-400"
                >
                  No conventions match the current filters.
                </td>
              </tr>
            ) : (
              filtered.map((c) => (
                <tr
                  key={`${c.role}/${c.action}`}
                  data-testid={`convention-row-${c.role}-${c.action}`}
                  onClick={() => onRowClick(c.role, c.action)}
                  className="hover:bg-gray-50 cursor-pointer dark:hover:bg-gray-700"
                >
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200">
                      {c.role}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-cyan-100 text-cyan-800 dark:bg-cyan-900 dark:text-cyan-200">
                      {c.action}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
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
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span
                      className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
                        c.source === 'tenant'
                          ? 'bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-200'
                          : 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300'
                      }`}
                    >
                      {c.source}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap text-xs text-gray-500 dark:text-gray-400">
                    {fmt(c.updatedAt)}
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    {/* Every (role,action) in the registry is seeded by ConventionSeedSpecs */}
                    <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-teal-100 text-teal-800 dark:bg-teal-900 dark:text-teal-200">
                      System Seed
                    </span>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

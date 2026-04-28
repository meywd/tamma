/**
 * PromptTable (Story 27-4 AC 2-5)
 *
 * Filterable, searchable table of every shipped role+action template
 * (≤80 rows). Rendered inside the "Role + Action Templates" tab of the
 * admin prompts page. Clicking any row hands `(role, action)` up to the
 * page so it can open the edit drawer.
 *
 * Filters are local state — 80 rows fit comfortably in memory and the
 * server has no list-with-filter endpoint, so all reduction is client-side.
 */

import { useMemo, useState, type JSX } from 'react';
import type { PromptResponse } from '../../services/admin/prompts-api-client.js';
import {
  ACTIONS,
  ROLES,
  actionLabel,
  roleLabel,
} from './prompt-constants.js';

interface PromptTableProps {
  prompts: PromptResponse[];
  onRowClick: (role: string, action: string) => void;
}

export function PromptTable({
  prompts,
  onRowClick,
}: PromptTableProps): JSX.Element {
  const [roleFilter, setRoleFilter] = useState<string>('all');
  const [actionFilter, setActionFilter] = useState<string>('all');
  const [searchQuery, setSearchQuery] = useState('');

  const filtered = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    return prompts.filter((p) => {
      if (roleFilter !== 'all' && p.role !== roleFilter) return false;
      if (actionFilter !== 'all' && p.action !== actionFilter) return false;
      if (q.length > 0 && !p.template.toLowerCase().includes(q)) return false;
      return true;
    });
  }, [prompts, roleFilter, actionFilter, searchQuery]);

  return (
    <div>
      {/* Filter bar */}
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <select
          aria-label="Filter by role"
          value={roleFilter}
          onChange={(e) => setRoleFilter(e.target.value)}
          className="text-sm border border-gray-300 rounded-md px-3 py-1.5 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 min-w-[160px]"
        >
          <option value="all">All Roles ({ROLES.length})</option>
          {ROLES.map((r) => (
            <option key={r.id} value={r.id}>
              {r.label}
            </option>
          ))}
        </select>
        <select
          aria-label="Filter by action"
          value={actionFilter}
          onChange={(e) => setActionFilter(e.target.value)}
          className="text-sm border border-gray-300 rounded-md px-3 py-1.5 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 min-w-[180px]"
        >
          <option value="all">All Actions ({ACTIONS.length})</option>
          {ACTIONS.map((a) => (
            <option key={a.id} value={a.id}>
              {a.label}
            </option>
          ))}
        </select>
        <input
          type="search"
          aria-label="Search template content"
          placeholder="Search template content..."
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          className="text-sm border border-gray-300 rounded-md px-3 py-1.5 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 flex-1 max-w-xs"
        />
        <span className="text-xs text-gray-500 ml-auto">
          {filtered.length} of {prompts.length} templates
        </span>
      </div>
      {/* Table */}
      <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Role
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Action
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Source
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Tools
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Max Tokens
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Variables
              </th>
              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Preview
              </th>
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200">
            {filtered.length === 0 ? (
              <tr>
                <td
                  colSpan={7}
                  className="px-4 py-8 text-center text-sm text-gray-500"
                >
                  No templates match the current filters.
                </td>
              </tr>
            ) : (
              filtered.map((p) => (
                <tr
                  key={`${p.role ?? '_'}/${p.action ?? '_'}`}
                  className="hover:bg-gray-50 cursor-pointer"
                  onClick={() => {
                    if (p.role && p.action) onRowClick(p.role, p.action);
                  }}
                >
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-800">
                      {roleLabel(p.role)}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-cyan-100 text-cyan-800">
                      {actionLabel(p.action)}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span
                      className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
                        p.source === 'user'
                          ? 'bg-amber-100 text-amber-800'
                          : 'bg-gray-100 text-gray-700'
                      }`}
                    >
                      {p.source === 'user' ? 'override' : 'system'}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    {p.enableTools ? (
                      <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-800">
                        on
                      </span>
                    ) : (
                      <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-600">
                        off
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap text-xs text-gray-600 font-mono">
                    {p.maxTokens.toLocaleString()}
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap text-xs text-gray-600 font-mono">
                    {p.variables?.length ?? 0}
                  </td>
                  <td className="px-4 py-3 text-xs text-gray-500 max-w-md truncate">
                    {p.template.slice(0, 80).replace(/\s+/g, ' ')}
                    {p.template.length > 80 ? '…' : ''}
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

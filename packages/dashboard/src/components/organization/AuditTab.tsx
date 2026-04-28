/**
 * Audit Tab (Story 18-8 AC 7)
 *
 * Renders the tenant-scoped audit log returned by Story 18-7's
 * `GET /api/v1/orgs/:tenantId/audit`. Filter chip-group applies a
 * type-prefix filter server-side; pagination is server-side too.
 *
 * Actor resolution today is best-effort: we read `tags.userId` and try
 * to resolve it against the cached members list (loaded by `MembersTab`).
 * When the actor isn't a current member (e.g. removed), we fall back to
 * the raw user-id; the audit-summary mapper handles both cases.
 */

import { useMemo, useState, type JSX } from 'react';
import { useOrgAudit } from '../../hooks/orgs/useOrgAudit.js';
import { useOrgMembers } from '../../hooks/orgs/useOrgMembers.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';
import { Badge } from '../common/Badge.js';
import {
  AUDIT_EVENT_FAMILIES,
  eventToSummary,
} from '../../services/orgs/audit-summary.js';
import { mapOrgError } from '../../services/orgs/error-copy.js';
import type { AuditEvent } from '../../services/orgs/org-api-client.js';

const PAGE_SIZE = 50;

function safeParse(value: string): Record<string, unknown> {
  try {
    const parsed = JSON.parse(value) as unknown;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
    return {};
  } catch {
    return {};
  }
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString();
}

interface RenderedRow {
  id: string;
  type: string;
  createdAt: string;
  summary: string;
  icon: string;
  actorId: string | null;
}

function buildRow(
  evt: AuditEvent,
  resolveActor: (userId: string | null) => string | null,
): RenderedRow {
  const tags = safeParse(evt.tags);
  const data = safeParse(evt.data);
  const actorId = typeof tags['userId'] === 'string' ? (tags['userId'] as string) : null;
  const actorName = resolveActor(actorId);
  const summary = eventToSummary({ type: evt.type, data, actor: actorName });
  return {
    id: evt.id,
    type: evt.type,
    createdAt: evt.createdAt,
    summary: summary.summary,
    icon: summary.icon,
    actorId,
  };
}

export function AuditTab(): JSX.Element {
  const [familyPrefix, setFamilyPrefix] = useState<string>('');
  const [page, setPage] = useState(0);
  const queryOptions = useMemo(
    () => ({ type: familyPrefix, limit: PAGE_SIZE, offset: page * PAGE_SIZE }),
    [familyPrefix, page],
  );
  const { events, total, loading, error } = useOrgAudit(queryOptions);
  const { members } = useOrgMembers();

  const memberIndex = useMemo(() => {
    const idx = new Map<string, string>();
    for (const m of members) {
      idx.set(m.userId, m.displayName ?? m.email ?? m.userId);
    }
    return idx;
  }, [members]);

  const resolveActor = (userId: string | null): string | null => {
    if (!userId) return null;
    return memberIndex.get(userId) ?? userId;
  };

  const rows = useMemo(
    () => events.map((e) => buildRow(e, resolveActor)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [events, memberIndex],
  );

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <div>
      {/* Family filter chips */}
      <div className="flex flex-wrap gap-2 mb-4">
        {AUDIT_EVENT_FAMILIES.map((f) => (
          <button
            key={f.label}
            type="button"
            onClick={() => {
              setFamilyPrefix(f.prefix);
              setPage(0);
            }}
            className={`px-3 py-1 text-xs rounded-full border transition-colors ${
              familyPrefix === f.prefix
                ? 'bg-blue-600 text-white border-blue-600'
                : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'
            }`}
          >
            {f.label}
          </button>
        ))}
      </div>

      {error && (
        <div className="mb-4 bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-700">
          {mapOrgError(error)}
        </div>
      )}

      {loading && rows.length === 0 ? (
        <div className="flex justify-center py-12">
          <LoadingSpinner size="lg" />
        </div>
      ) : rows.length === 0 ? (
        <div className="bg-gray-50 border border-gray-200 rounded-lg p-6 text-center text-sm text-gray-500">
          No audit events match the current filter.
        </div>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  When
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Type
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Summary
                </th>
              </tr>
            </thead>
            <tbody className="bg-white divide-y divide-gray-200">
              {rows.map((r) => (
                <tr key={r.id} className="hover:bg-gray-50">
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                    {formatTimestamp(r.createdAt)}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap">
                    <Badge variant="neutral">{r.type}</Badge>
                  </td>
                  <td className="px-6 py-4 text-sm text-gray-900">{r.summary}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Pagination */}
      <div className="flex items-center justify-between mt-4 text-sm text-gray-600">
        <div>
          Showing {rows.length === 0 ? 0 : page * PAGE_SIZE + 1}–
          {page * PAGE_SIZE + rows.length} of {total}
        </div>
        <div className="space-x-2">
          <button
            type="button"
            onClick={() => setPage((p) => Math.max(0, p - 1))}
            disabled={page === 0}
            className="px-3 py-1 border border-gray-300 rounded-md text-sm disabled:opacity-50"
          >
            Prev
          </button>
          <span className="px-2">
            {page + 1} / {totalPages}
          </span>
          <button
            type="button"
            onClick={() => setPage((p) => p + 1)}
            disabled={page + 1 >= totalPages}
            className="px-3 py-1 border border-gray-300 rounded-md text-sm disabled:opacity-50"
          >
            Next
          </button>
        </div>
      </div>
    </div>
  );
}

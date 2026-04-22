import type { AdminTenantEventItem } from '../../../../services/admin/admin-tenants-client.js';

/**
 * Story 28-11 — renders the recent platform_events feed for a single
 * tenant. The server returns the last 100 rows (most recent first);
 * rendering stays simple + performant with no virtualisation because the
 * cap is fixed and small.
 *
 * Tags / data are raw JSON strings from the backend — we pretty-print
 * them inline so the admin can eyeball a workflow step's payload without
 * leaving the page.
 */

interface EventsTimelineProps {
  events: AdminTenantEventItem[];
}

function formatTimestamp(iso: string): string {
  const date = new Date(iso);
  return date.toLocaleString();
}

function safePretty(raw: string): string {
  if (!raw) return '{}';
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function eventColor(type: string): string {
  if (type.endsWith('.SUCCESS')) return 'text-green-700 border-green-200 bg-green-50';
  if (type.endsWith('.FAILED')) return 'text-red-700 border-red-200 bg-red-50';
  if (type.includes('STEP_STARTED') || type.includes('REQUESTED'))
    return 'text-blue-700 border-blue-200 bg-blue-50';
  if (type.includes('STEP_COMPLETED'))
    return 'text-emerald-700 border-emerald-200 bg-emerald-50';
  return 'text-gray-700 border-gray-200 bg-gray-50';
}

export function EventsTimeline({ events }: EventsTimelineProps): JSX.Element {
  if (events.length === 0) {
    return (
      <div className="text-sm text-gray-500 italic py-4">
        No platform events recorded for this tenant yet.
      </div>
    );
  }

  return (
    <ol className="space-y-3">
      {events.map((evt) => (
        <li
          key={evt.id}
          className={`border rounded-md p-3 ${eventColor(evt.type)}`}
        >
          <div className="flex items-center justify-between mb-1">
            <code className="text-xs font-mono font-semibold">{evt.type}</code>
            <time className="text-xs text-gray-500 font-mono">
              {formatTimestamp(evt.createdAt)}
            </time>
          </div>
          <details className="text-xs font-mono text-gray-600">
            <summary className="cursor-pointer select-none hover:text-gray-800">
              tags / data
            </summary>
            <div className="mt-2 grid grid-cols-1 md:grid-cols-2 gap-2">
              <pre className="bg-white/70 rounded p-2 overflow-x-auto">
                tags: {safePretty(evt.tags)}
              </pre>
              <pre className="bg-white/70 rounded p-2 overflow-x-auto">
                data: {safePretty(evt.data)}
              </pre>
            </div>
          </details>
        </li>
      ))}
    </ol>
  );
}

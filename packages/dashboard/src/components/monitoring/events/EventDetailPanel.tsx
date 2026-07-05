/**
 * EventDetailPanel — inline detail view for a single DCB event (Story 23-3,
 * AC11/AC12). Shows the full event returned by the 4-7 query API
 * (id / timestamp / sequence / issue / tags / data) with the tag bag and data
 * payload pretty-printed, plus a "Copy JSON" action.
 *
 * Note: the 4-7 query projection does not surface the event `metadata`
 * envelope separately — the panel renders every field the query API returns.
 */

import { useMemo, useState, type JSX } from 'react';
import { StatusBadge } from '../StatusBadge.js';
import { eventTone, tagValue } from './event-explorer-utils.js';
import type { DomainEventRow } from '../../../hooks/monitoring/useEventQuery.js';

interface EventDetailPanelProps {
  event: DomainEventRow;
  onClose: () => void;
}

function Field({ label, children }: { label: string; children: JSX.Element | string }): JSX.Element {
  return (
    <div className="min-w-0">
      <dt className="text-xs font-semibold uppercase tracking-wide text-gray-400 dark:text-gray-500">
        {label}
      </dt>
      <dd className="mt-0.5 break-words text-sm text-gray-800 dark:text-gray-200">{children}</dd>
    </div>
  );
}

function JsonBlock({ label, value }: { label: string; value: unknown }): JSX.Element {
  return (
    <div>
      <p className="mb-1 text-xs font-semibold uppercase tracking-wide text-gray-400 dark:text-gray-500">
        {label}
      </p>
      <pre className="max-h-72 overflow-auto rounded-md border border-gray-200 bg-gray-50 p-3 text-xs text-gray-800 dark:border-gray-700 dark:bg-gray-950 dark:text-gray-200">
        {JSON.stringify(value ?? {}, null, 2)}
      </pre>
    </div>
  );
}

export function EventDetailPanel({ event, onClose }: EventDetailPanelProps): JSX.Element {
  const [copied, setCopied] = useState(false);
  const json = useMemo(() => JSON.stringify(event, null, 2), [event]);

  const handleCopy = async (): Promise<void> => {
    try {
      if (navigator?.clipboard?.writeText) {
        await navigator.clipboard.writeText(json);
        setCopied(true);
        globalThis.setTimeout(() => setCopied(false), 1500);
      }
    } catch {
      // clipboard unavailable — silently ignore
    }
  };

  const correlationId = tagValue(event.tags, 'correlationId');
  const actor = tagValue(event.tags, 'userId') || tagValue(event.tags, 'actor');

  return (
    <section
      data-testid="event-detail-panel"
      aria-label="Event detail"
      className="mb-4 rounded-lg border border-gray-200 bg-white p-4 shadow-sm dark:border-gray-700 dark:bg-gray-900"
    >
      <div className="mb-3 flex items-start justify-between gap-3">
        <div className="flex items-center gap-2">
          <StatusBadge status={eventTone(event.type)} showDot={false}>
            {event.type}
          </StatusBadge>
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => void handleCopy()}
            className="rounded-md border border-gray-300 px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            {copied ? 'Copied!' : 'Copy JSON'}
          </button>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close detail"
            className="rounded-md p-1 text-gray-500 hover:bg-gray-100 dark:hover:bg-gray-700"
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth={2}
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18 18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
      </div>

      <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">
        <Field label="Event ID">
          <code className="text-xs">{event.id}</code>
        </Field>
        <Field label="Sequence">{String(event.sequenceNumber)}</Field>
        <Field label="Timestamp">{event.createdAt}</Field>
        <Field label="Issue #">{event.issueNumber != null ? `#${event.issueNumber}` : '—'}</Field>
        <Field label="Correlation">
          {correlationId ? <code className="text-xs">{correlationId}</code> : '—'}
        </Field>
        <Field label="Actor">{actor || '—'}</Field>
      </dl>

      <div className="mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2">
        <JsonBlock label="Tags" value={event.tags} />
        <JsonBlock label="Data" value={event.data} />
      </div>
    </section>
  );
}

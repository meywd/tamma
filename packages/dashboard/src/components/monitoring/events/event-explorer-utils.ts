/**
 * Pure helpers for the Event Store Explorer (Story 23-3): event-type color
 * coding (AC3), tag extraction, client-side summary aggregation (group-by-type
 * + over-time bucketing for the frequency panel, AC17), and client-side JSON /
 * CSV export of the loaded result set (AC20/AC21).
 *
 * These are all deterministic and side-effect-free (except the DOM download
 * trigger, which is guarded) so they can be unit-tested in isolation.
 */

import type { DomainEventRow } from '../../../hooks/monitoring/useEventQuery.js';
import type { StatusTone } from '../StatusBadge.js';
import type { TimeSeriesPoint } from '../TimeSeriesChart.js';

// Explicit event names from AC3. Our DCB events also follow the
// AGGREGATE.ACTION.STATUS convention (e.g. CODE.GENERATED.SUCCESS), so the
// keyword fallbacks below catch the dotted variants too.
const SUCCESS_NAMES = new Set([
  'PLAN_APPROVED',
  'IMPLEMENTATION_COMPLETED',
  'PR_MERGED',
  'ISSUE_CLOSED',
]);
const FAILURE_NAMES = new Set(['IMPLEMENTATION_FAILED', 'ERROR_OCCURRED', 'PLAN_REJECTED']);
const PROGRESS_NAMES = new Set([
  'ISSUE_SELECTED',
  'ISSUE_ANALYZED',
  'PLAN_GENERATED',
  'BRANCH_CREATED',
  'PR_CREATED',
]);
const MONITORING_NAMES = new Set(['STATE_TRANSITION', 'CI_CHECK_STARTED']);
const CLEANUP_NAMES = new Set(['BRANCH_DELETED']);

/**
 * Map an event type to a {@link StatusTone} for color coding (AC3):
 * green = success, red = failure, blue = progress, yellow = monitoring,
 * gray = cleanup. Failure is checked before success so a `.FAILED` never reads
 * as green.
 */
export function eventTone(type: string): StatusTone {
  const t = type.toUpperCase();

  if (FAILURE_NAMES.has(t) || t.endsWith('.FAILED') || /FAIL|ERROR|REJECT|DENIED/.test(t)) {
    return 'red';
  }
  if (
    SUCCESS_NAMES.has(t) ||
    t.endsWith('.SUCCESS') ||
    /APPROVED|COMPLETED|MERGED|SUCCESS|CLOSED|PASSED/.test(t)
  ) {
    return 'green';
  }
  if (CLEANUP_NAMES.has(t) || /DELETED|CLEANUP|REMOVED|PURGED/.test(t)) {
    return 'gray';
  }
  if (MONITORING_NAMES.has(t) || /TRANSITION|CHECK_STARTED|HEARTBEAT|STARTED/.test(t)) {
    return 'yellow';
  }
  if (PROGRESS_NAMES.has(t)) {
    return 'blue';
  }
  // Everything else (in-flight / informational) reads as progress.
  return 'blue';
}

/** Safe string extraction of a single tag value. */
export function tagValue(tags: Record<string, unknown> | null | undefined, key: string): string {
  if (!tags) return '';
  const v = tags[key];
  if (v == null) return '';
  return typeof v === 'string' ? v : String(v);
}

/** A short one-line preview of the tag bag for a table cell. */
export function formatTagsPreview(
  tags: Record<string, unknown> | null | undefined,
  max = 3,
): string {
  if (!tags) return '';
  const entries = Object.entries(tags).filter(([, v]) => v != null);
  if (entries.length === 0) return '';
  const shown = entries
    .slice(0, max)
    .map(([k, v]) => `${k}=${typeof v === 'string' ? v : JSON.stringify(v)}`)
    .join(', ');
  return entries.length > max ? `${shown}, +${entries.length - max} more` : shown;
}

/** Count events per type, sorted by count desc then type asc (AC17). */
export function groupByType(events: DomainEventRow[]): Array<{ type: string; count: number }> {
  const counts = new Map<string, number>();
  for (const e of events) counts.set(e.type, (counts.get(e.type) ?? 0) + 1);
  return [...counts.entries()]
    .map(([type, count]) => ({ type, count }))
    .sort((a, b) => b.count - a.count || a.type.localeCompare(b.type));
}

/**
 * Bucket loaded events into `bucketCount` equal time buckets across the
 * observed [min,max] window and count events per bucket, for the frequency
 * chart (AC17). Returns an ascending-by-time series.
 */
export function bucketOverTime(events: DomainEventRow[], bucketCount = 24): TimeSeriesPoint[] {
  if (events.length === 0 || bucketCount < 1) return [];
  const times = events
    .map((e) => new Date(e.createdAt).getTime())
    .filter((n) => Number.isFinite(n));
  if (times.length === 0) return [];

  const min = Math.min(...times);
  const max = Math.max(...times);
  if (min === max) return [{ timestamp: min, value: times.length }];

  const width = (max - min) / bucketCount;
  const counts = new Array<number>(bucketCount).fill(0);
  for (const t of times) {
    let idx = Math.floor((t - min) / width);
    if (idx >= bucketCount) idx = bucketCount - 1;
    if (idx < 0) idx = 0;
    counts[idx] = (counts[idx] ?? 0) + 1;
  }
  return counts.map((value, i) => ({ timestamp: min + i * width, value }));
}

/** Pretty-printed JSON export of the loaded event objects (AC21). */
export function eventsToJson(events: DomainEventRow[]): string {
  return JSON.stringify(events, null, 2);
}

const CSV_COLUMNS = [
  'id',
  'type',
  'createdAt',
  'sequenceNumber',
  'issueNumber',
  'correlationId',
  'actor',
  'data',
] as const;

function csvCell(value: string | number | null | undefined): string {
  const s = value == null ? '' : String(value);
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

/** CSV export flattening the data field to a single stringified column (AC21). */
export function eventsToCsv(events: DomainEventRow[]): string {
  const header = CSV_COLUMNS.join(',');
  const rows = events.map((e) =>
    [
      e.id,
      e.type,
      e.createdAt,
      e.sequenceNumber,
      e.issueNumber ?? '',
      tagValue(e.tags, 'correlationId'),
      tagValue(e.tags, 'userId') || tagValue(e.tags, 'actor'),
      JSON.stringify(e.data ?? {}),
    ]
      .map(csvCell)
      .join(','),
  );
  return [header, ...rows].join('\r\n');
}

/** Build a filter-aware export filename, e.g. `tamma-events-AGENT.TASK-2026-07-05.json`. */
export function exportFilename(ext: 'json' | 'csv', typeContext?: string): string {
  const date = new Date().toISOString().slice(0, 10);
  const suffix = typeContext ? `-${typeContext.replace(/[^A-Za-z0-9._-]/g, '_')}` : '';
  return `tamma-events${suffix}-${date}.${ext}`;
}

/** Trigger a client-side file download. No-op outside a DOM environment. */
export function triggerDownload(filename: string, mime: string, content: string): void {
  if (typeof document === 'undefined') return;
  const blob = new Blob([content], { type: mime });
  const url = typeof URL.createObjectURL === 'function' ? URL.createObjectURL(blob) : '';
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  if (url && typeof URL.revokeObjectURL === 'function') URL.revokeObjectURL(url);
}

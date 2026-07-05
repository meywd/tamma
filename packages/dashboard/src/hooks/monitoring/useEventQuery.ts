/**
 * useEventQuery — data hook for the Event Store Explorer (Story 23-3).
 *
 * Wraps the Story 4-7 keyset-paginated query API:
 *   `GET /api/engine/events/query`
 * (EngineEndpoints.QueryEvents — tenant-scoped, WorkflowsView, filters by
 * time window / correlationId / actor / event type exact|prefix, keyset cursor).
 *
 * The endpoint is forward-only (each `nextCursor` is the oldest sequence number
 * on the page; the next page is strictly older), so pagination here is an
 * accumulate / "load more" model: {@link UseEventQueryResult.runQuery} resets
 * and fetches the first page; {@link UseEventQueryResult.loadMore} appends the
 * next page using the retained cursor + filters.
 *
 * The response is emitted with ASP.NET Core's default camelCase policy, so the
 * anonymous projection `{ Id, Type, tags, data, CreatedAt, IssueNumber,
 * SequenceNumber }` arrives as `{ id, type, tags, data, createdAt, issueNumber,
 * sequenceNumber }`. `tags` / `data` are already JSON-parsed server-side.
 */

import { useCallback, useRef, useState } from 'react';

/** A single DCB event row as returned by the 4-7 query projection. */
export interface DomainEventRow {
  id: string;
  type: string;
  /** Parsed JSONB tag bag (correlationId, userId, issueId, …) or null. */
  tags: Record<string, unknown> | null;
  /** Parsed JSONB event payload or null. */
  data: Record<string, unknown> | null;
  /** ISO-8601 event timestamp. */
  createdAt: string;
  issueNumber: number | null;
  sequenceNumber: number;
}

export type EventTypeMatch = 'exact' | 'prefix';

/** The filter set applied to a query. All fields optional except `limit`. */
export interface EventQueryFilters {
  type?: string;
  typeMatch?: EventTypeMatch;
  correlationId?: string;
  actor?: string;
  from?: Date;
  to?: Date;
  limit?: number;
  /** Request the exact match count (an unbounded scan; only on the first page). */
  includeTotal?: boolean;
}

export interface UseEventQueryResult {
  events: DomainEventRow[];
  /** Exact match count when computed (first page requests it), else null. */
  total: number | null;
  hasMore: boolean;
  loading: boolean;
  error: string | null;
  lastUpdated: Date | null;
  /** Reset + fetch the first page for `filters`. */
  runQuery: (filters: EventQueryFilters) => Promise<void>;
  /** Append the next (older) page using the retained cursor + filters. */
  loadMore: () => Promise<void>;
}

interface WireEvent {
  id: string;
  type: string;
  tags: unknown;
  data: unknown;
  createdAt: string;
  issueNumber: number | null;
  sequenceNumber: number;
}

interface WireResponse {
  events: WireEvent[];
  total: number | null;
  limit: number;
  nextCursor: number | null;
  hasMore: boolean;
}

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function toRow(e: WireEvent): DomainEventRow {
  return {
    id: e.id,
    type: e.type,
    tags: isRecord(e.tags) ? e.tags : null,
    data: isRecord(e.data) ? e.data : null,
    createdAt: e.createdAt,
    issueNumber: e.issueNumber,
    sequenceNumber: e.sequenceNumber,
  };
}

function buildUrl(filters: EventQueryFilters, cursor: number | null): string {
  const params = new URLSearchParams();
  const type = filters.type?.trim();
  if (type) {
    params.set('type', type);
    if (filters.typeMatch === 'prefix') params.set('prefix', 'true');
  }
  const correlationId = filters.correlationId?.trim();
  if (correlationId) params.set('correlationId', correlationId);
  const actor = filters.actor?.trim();
  if (actor) params.set('actor', actor);
  if (filters.from) params.set('from', filters.from.toISOString());
  if (filters.to) params.set('to', filters.to.toISOString());
  params.set('limit', String(filters.limit ?? 50));
  if (filters.includeTotal) params.set('includeTotal', 'true');
  if (cursor != null) params.set('cursor', String(cursor));
  return `${API_BASE}/api/engine/events/query?${params.toString()}`;
}

async function fetchEvents(url: string): Promise<WireResponse> {
  const res = await fetch(url, {
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
  });
  if (!res.ok) {
    let message = `HTTP ${res.status}`;
    try {
      const body = (await res.json()) as { error?: string };
      if (body?.error) message = body.error;
    } catch {
      // keep the default status message
    }
    throw new Error(message);
  }
  return (await res.json()) as WireResponse;
}

export function useEventQuery(): UseEventQueryResult {
  const [events, setEvents] = useState<DomainEventRow[]>([]);
  const [total, setTotal] = useState<number | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  // Retained across renders so loadMore re-uses the exact filters + cursor of
  // the active query without re-triggering React state churn.
  const filtersRef = useRef<EventQueryFilters | null>(null);
  const cursorRef = useRef<number | null>(null);

  const runQuery = useCallback(async (filters: EventQueryFilters): Promise<void> => {
    filtersRef.current = filters;
    cursorRef.current = null;
    setLoading(true);
    setError(null);
    try {
      // Force includeTotal on the first page so the footer can show a count.
      const res = await fetchEvents(buildUrl({ ...filters, includeTotal: true }, null));
      setEvents(res.events.map(toRow));
      setTotal(res.total);
      setHasMore(res.hasMore);
      cursorRef.current = res.nextCursor;
      setLastUpdated(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load events');
    } finally {
      setLoading(false);
    }
  }, []);

  const loadMore = useCallback(async (): Promise<void> => {
    const filters = filtersRef.current;
    const cursor = cursorRef.current;
    if (!filters || cursor == null) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetchEvents(buildUrl({ ...filters, includeTotal: false }, cursor));
      setEvents((prev) => [...prev, ...res.events.map(toRow)]);
      setHasMore(res.hasMore);
      cursorRef.current = res.nextCursor;
      setLastUpdated(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load more events');
    } finally {
      setLoading(false);
    }
  }, []);

  return { events, total, hasMore, loading, error, lastUpdated, runQuery, loadMore };
}

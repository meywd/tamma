/**
 * useMonitoringSSE — subscribes to a Server-Sent Events endpoint with automatic
 * reconnection.
 *
 * Story 23-12 (AC5). Exposes `{ data, connected, status, error, reconnectAttempt }`.
 * Reconnects with exponential backoff (1s, 2s, 4s, ... capped at 30s) and tears
 * the connection down on unmount. The `EventSource` is created through an
 * injectable factory so the hook is testable without a live browser stream.
 */

import { useEffect, useRef, useState } from 'react';

export type SSEConnectionStatus = 'connecting' | 'connected' | 'disconnected' | 'reconnecting';

/** Minimal surface of the browser `EventSource` the hook relies on. */
export interface EventSourceLike {
  onopen: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: string }) => void) | null;
  onerror: ((ev: unknown) => void) | null;
  close: () => void;
}

export interface UseMonitoringSSEOptions<T> {
  /** When false (or url is null) the hook stays disconnected. Defaults to true. */
  enabled?: boolean;
  /** Parse a raw event payload into `T`. Defaults to `JSON.parse`. */
  parse?: (raw: string) => T;
  /** Factory for the underlying stream — injected in tests. */
  eventSourceFactory?: (url: string) => EventSourceLike;
  /** Cap for the reconnection backoff. Defaults to 30_000ms. */
  maxBackoffMs?: number;
}

export interface UseMonitoringSSEResult<T> {
  data: T | null;
  connected: boolean;
  status: SSEConnectionStatus;
  error: Error | null;
  reconnectAttempt: number;
}

const BASE_BACKOFF_MS = 1000;
const DEFAULT_MAX_BACKOFF_MS = 30_000;

export function useMonitoringSSE<T = unknown>(
  url: string | null,
  options: UseMonitoringSSEOptions<T> = {},
): UseMonitoringSSEResult<T> {
  const { enabled = true, parse, eventSourceFactory, maxBackoffMs = DEFAULT_MAX_BACKOFF_MS } =
    options;

  const [data, setData] = useState<T | null>(null);
  const [status, setStatus] = useState<SSEConnectionStatus>('disconnected');
  const [error, setError] = useState<Error | null>(null);
  const [reconnectAttempt, setReconnectAttempt] = useState(0);

  // Keep callbacks fresh without re-running the connection effect.
  const parseRef = useRef(parse);
  parseRef.current = parse;
  const factoryRef = useRef(eventSourceFactory);
  factoryRef.current = eventSourceFactory;

  useEffect(() => {
    if (!enabled || url === null) {
      setStatus('disconnected');
      return;
    }

    let source: EventSourceLike | null = null;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    let attempt = 0;
    let cancelled = false;

    const scheduleReconnect = (): void => {
      if (cancelled) return;
      setStatus('reconnecting');
      const delay = Math.min(BASE_BACKOFF_MS * 2 ** attempt, maxBackoffMs);
      attempt += 1;
      setReconnectAttempt(attempt);
      retryTimer = setTimeout(connect, delay);
    };

    function connect(): void {
      if (cancelled) return;
      setStatus(attempt === 0 ? 'connecting' : 'reconnecting');

      const factory =
        factoryRef.current ??
        ((u: string): EventSourceLike => new EventSource(u) as unknown as EventSourceLike);

      try {
        source = factory(url as string);
      } catch (err) {
        setError(err instanceof Error ? err : new Error(String(err)));
        scheduleReconnect();
        return;
      }

      source.onopen = (): void => {
        if (cancelled) return;
        attempt = 0;
        setReconnectAttempt(0);
        setError(null);
        setStatus('connected');
      };

      source.onmessage = (ev: { data: string }): void => {
        if (cancelled) return;
        try {
          const parsed = parseRef.current
            ? parseRef.current(ev.data)
            : (JSON.parse(ev.data) as T);
          setData(parsed);
        } catch (err) {
          setError(err instanceof Error ? err : new Error(String(err)));
        }
      };

      source.onerror = (): void => {
        if (cancelled) return;
        if (source) {
          source.close();
          source = null;
        }
        scheduleReconnect();
      };
    }

    connect();

    return () => {
      cancelled = true;
      if (retryTimer !== null) clearTimeout(retryTimer);
      if (source) source.close();
      setStatus('disconnected');
    };
  }, [url, enabled, maxBackoffMs]);

  return {
    data,
    connected: status === 'connected',
    status,
    error,
    reconnectAttempt,
  };
}

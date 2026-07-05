/**
 * useRunStreamTail — a LIVE tail of one managed LLM run's tool-loop.
 *
 * Story 23-2 (Agent Monitor). Composes the Story 23-12 {@link useMonitoringSSE}
 * primitive (auto-reconnect / backoff / injectable factory) over the Story 32-23
 * streaming run tap:
 *   `GET /api/v1/llm/runs/{correlationId}/stream`  (SSE, tenant-scoped)
 *
 * The 32-23 stream emits NAMED SSE frames (`event: tool_call`, `event: token`,
 * `event: final`, …) whereas `useMonitoringSSE` wires only the default
 * `onmessage` channel. {@link createRunStreamEventSource} bridges the two: it
 * registers a listener per frame kind on the real browser `EventSource` and
 * forwards each as an `onmessage` payload of the shape `"{kind}\n{json}"`, which
 * {@link parseRunStreamFrame} decodes. Tests inject a fake factory that drives
 * the same `onmessage` seam directly — no live browser stream required.
 *
 * Every scrubbed frame carries a per-run monotonic `seq` + its `correlationId`
 * (Story 32-23 AC9 allowlist), so frames are accumulated idempotently (dedup by
 * `kind:seq`) and stale frames from a previous run are dropped by correlationId.
 * On a terminal `final`/`end` frame the tail marks itself `done` and disables the
 * subscription so a finished run never triggers a reconnect storm.
 *
 * Read-only: no cost / margin is ever surfaced — the 32-23 scrubber ships only
 * agent activity/status fields.
 */

import { useEffect, useRef, useState } from 'react';
import {
  useMonitoringSSE,
  type EventSourceLike,
  type SSEConnectionStatus,
} from './useMonitoringSSE.js';

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

/** The closed frame vocabulary of the 32-23 run tap (plus the `end` marker). */
export const RUN_STREAM_FRAME_KINDS = [
  'token',
  'tool_call',
  'tool_result',
  'question',
  'answer',
  'final',
  'replay',
  'end',
] as const;

export type RunStreamFrameKind = (typeof RUN_STREAM_FRAME_KINDS)[number];

/** One decoded frame from the run tap. */
export interface RunStreamFrame {
  /** SSE event name — the frame kind. */
  kind: RunStreamFrameKind;
  /** Per-run monotonic sequence (bus-assigned). Absent on the raw `end` marker. */
  seq: number | null;
  /** The run this frame belongs to (absent on the raw `end` marker). */
  correlationId: string | null;
  /** Scrubbed, allowlisted fields (toolName, success, delta, reason, …). */
  payload: Record<string, unknown>;
  /** Client receipt time (ms epoch) for display ordering. */
  receivedAt: number;
}

export interface UseRunStreamTailResult {
  /** Frames received so far, in arrival order. */
  frames: RunStreamFrame[];
  status: SSEConnectionStatus;
  connected: boolean;
  error: Error | null;
  /** True once a terminal `final`/`end` frame arrived — the tail is closed. */
  done: boolean;
  reconnectAttempt: number;
}

export interface UseRunStreamTailOptions {
  /** Factory for the underlying stream — injected in tests. */
  eventSourceFactory?: (url: string) => EventSourceLike;
}

/**
 * Build the tenant-scoped run-tap URL for a run. The server resolves the tenant
 * from the caller's session; a foreign / unknown run returns 404 (Story 32-23
 * AC2), so no tenant id is ever sent from the browser.
 */
export function runStreamUrl(correlationId: string): string {
  return `${API_BASE}/api/v1/llm/runs/${encodeURIComponent(correlationId)}/stream`;
}

/**
 * Decode a bridged run-tap message (`"{kind}\n{json}"`) into a
 * {@link RunStreamFrame}. Throws on malformed input (the hook surfaces it as an
 * error), never returns a partial frame.
 */
export function parseRunStreamFrame(raw: string): RunStreamFrame {
  const nl = raw.indexOf('\n');
  if (nl < 0) throw new Error('malformed run-stream frame');
  const kind = raw.slice(0, nl) as RunStreamFrameKind;
  const parsed: unknown = JSON.parse(raw.slice(nl + 1));
  const payload: Record<string, unknown> =
    typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : {};
  const seqVal = payload['seq'];
  const corrVal = payload['correlationId'];
  return {
    kind,
    seq: typeof seqVal === 'number' ? seqVal : null,
    correlationId: typeof corrVal === 'string' ? corrVal : null,
    payload,
    receivedAt: Date.now(),
  };
}

/**
 * Real-browser factory: wraps an `EventSource`, bridging every named run-tap
 * frame onto the single `onmessage` channel `useMonitoringSSE` listens on.
 * `withCredentials` forwards the session cookie so the tenant guard runs.
 */
export function createRunStreamEventSource(url: string): EventSourceLike {
  const es = new EventSource(url, { withCredentials: true });
  const wrapper: EventSourceLike = {
    onopen: null,
    onmessage: null,
    onerror: null,
    close: () => es.close(),
  };
  es.onopen = (ev): void => wrapper.onopen?.(ev);
  es.onerror = (ev): void => wrapper.onerror?.(ev);
  for (const kind of RUN_STREAM_FRAME_KINDS) {
    es.addEventListener(kind, (ev: MessageEvent): void => {
      wrapper.onmessage?.({ data: `${kind}\n${ev.data}` });
    });
  }
  return wrapper;
}

/**
 * Subscribe to a single run's live tool-loop. The `correlationId` is expected to
 * be stable for the hook's lifetime (mount the consuming panel with
 * `key={correlationId}` so switching runs starts a fresh subscription).
 */
export function useRunStreamTail(
  correlationId: string,
  options: UseRunStreamTailOptions = {},
): UseRunStreamTailResult {
  const [frames, setFrames] = useState<RunStreamFrame[]>([]);
  const [done, setDone] = useState(false);
  const seenRef = useRef<Set<string>>(new Set());

  const factory = options.eventSourceFactory ?? createRunStreamEventSource;

  const { data, status, connected, error, reconnectAttempt } = useMonitoringSSE<RunStreamFrame>(
    runStreamUrl(correlationId),
    {
      enabled: !done,
      parse: parseRunStreamFrame,
      eventSourceFactory: factory,
    },
  );

  useEffect(() => {
    if (!data) return;
    // Drop a stale frame lingering from a prior run (defence-in-depth; the
    // consuming panel is keyed by correlationId so this is belt-and-braces).
    if (data.correlationId !== null && data.correlationId !== correlationId) return;

    const key = `${data.kind}:${data.seq ?? 'end'}`;
    if (seenRef.current.has(key)) return; // idempotent (StrictMode double-invoke)
    seenRef.current.add(key);

    setFrames((prev) => [...prev, data]);
    if (data.kind === 'final' || data.kind === 'end') setDone(true);
  }, [data, correlationId]);

  return { frames, status, connected, error, done, reconnectAttempt };
}

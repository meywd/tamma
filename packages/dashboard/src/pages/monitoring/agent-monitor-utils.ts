/**
 * agent-monitor-utils — pure derivations for the Agent Monitor (Story 23-2).
 *
 * The page loads the `AGENT.*` managed-agent event family from the Story 4-7
 * query API (`GET /api/engine/events/query?type=AGENT.&prefix=true`, tenant-
 * scoped) and derives, entirely client-side:
 *   • the ACTIVE runs (an `AGENT.RUN.STARTED` with no matching terminal
 *     `AGENT.RUN.SUCCESS`/`AGENT.RUN.FAILED` in the loaded window), and
 *   • a small activity summary (started / active / succeeded / failed / tool
 *     calls) — agent activity & status only, never cost or margin.
 *
 * `AGENT.` (with the trailing dot) is a strict dotted prefix, so it selects the
 * managed-agent family (`AGENT.RUN.*`, `AGENT.TASK.*`, `AGENT.TOOL_CALL.*`,
 * `AGENT.ITERATION.*`, `AGENT.PANEL.*`) and NOT the `AGENT_DISPATCH.*` family
 * (underscore — different aggregate).
 */

import type { StatusKind } from '../../components/monitoring/StatusBadge.js';
import type { DomainEventRow } from '../../hooks/monitoring/useEventQuery.js';

export const AGENT_EVENT_PREFIX = 'AGENT.';

export const AGENT_RUN_STARTED = 'AGENT.RUN.STARTED';
export const AGENT_RUN_SUCCESS = 'AGENT.RUN.SUCCESS';
export const AGENT_RUN_FAILED = 'AGENT.RUN.FAILED';
export const AGENT_TOOL_CALL_SUCCESS = 'AGENT.TOOL_CALL.SUCCESS';
export const AGENT_TOOL_CALL_FAILED = 'AGENT.TOOL_CALL.FAILED';

/** One in-flight managed run (a STARTED with no terminal in the loaded set). */
export interface ActiveRun {
  correlationId: string;
  agentId: string | null;
  role: string | null;
  provider: string | null;
  model: string | null;
  /** ISO-8601 timestamp of the `AGENT.RUN.STARTED` event. */
  startedAt: string;
  /** `AGENT.TOOL_CALL.*` count observed for this run in the loaded window. */
  toolCalls: number;
}

/** Activity counters over the loaded window (no economics). */
export interface AgentActivitySummary {
  started: number;
  active: number;
  succeeded: number;
  failed: number;
  toolCalls: number;
}

/** Read a string tag off an event row (null when absent / non-string). */
export function tagString(row: DomainEventRow, key: string): string | null {
  const value = row.tags?.[key];
  return typeof value === 'string' && value.length > 0 ? value : null;
}

/** The correlationId tag of an event row (the managed-run id), or null. */
export function correlationOf(row: DomainEventRow): string | null {
  return tagString(row, 'correlationId');
}

/**
 * Derive the ACTIVE runs from a loaded `AGENT.*` event set: an
 * `AGENT.RUN.STARTED` whose correlationId has no terminal `AGENT.RUN.SUCCESS`/
 * `AGENT.RUN.FAILED` in the same set. Newest-first. Runs whose STARTED aged out
 * of the window are simply not shown (a monitoring, not accounting, view).
 */
export function deriveActiveRuns(events: readonly DomainEventRow[]): ActiveRun[] {
  const terminal = new Set<string>();
  const toolCalls = new Map<string, number>();

  for (const e of events) {
    const cid = correlationOf(e);
    if (cid === null) continue;
    if (e.type === AGENT_RUN_SUCCESS || e.type === AGENT_RUN_FAILED) {
      terminal.add(cid);
    } else if (e.type === AGENT_TOOL_CALL_SUCCESS || e.type === AGENT_TOOL_CALL_FAILED) {
      toolCalls.set(cid, (toolCalls.get(cid) ?? 0) + 1);
    }
  }

  // Keep the newest STARTED per correlationId (first wins if the caller already
  // sorted newest-first; otherwise compare timestamps explicitly).
  const startedByRun = new Map<string, DomainEventRow>();
  for (const e of events) {
    if (e.type !== AGENT_RUN_STARTED) continue;
    const cid = correlationOf(e);
    if (cid === null || terminal.has(cid)) continue;
    const existing = startedByRun.get(cid);
    if (!existing || e.createdAt > existing.createdAt) startedByRun.set(cid, e);
  }

  const runs: ActiveRun[] = [];
  for (const [cid, e] of startedByRun) {
    runs.push({
      correlationId: cid,
      agentId: tagString(e, 'agentId'),
      role: tagString(e, 'role'),
      provider: tagString(e, 'provider'),
      model: tagString(e, 'model'),
      startedAt: e.createdAt,
      toolCalls: toolCalls.get(cid) ?? 0,
    });
  }

  runs.sort((a, b) => (a.startedAt < b.startedAt ? 1 : a.startedAt > b.startedAt ? -1 : 0));
  return runs;
}

/** Derive the activity summary counters over the loaded window. */
export function deriveSummary(events: readonly DomainEventRow[]): AgentActivitySummary {
  let started = 0;
  let succeeded = 0;
  let failed = 0;
  let toolCalls = 0;

  for (const e of events) {
    switch (e.type) {
      case AGENT_RUN_STARTED:
        started += 1;
        break;
      case AGENT_RUN_SUCCESS:
        succeeded += 1;
        break;
      case AGENT_RUN_FAILED:
        failed += 1;
        break;
      case AGENT_TOOL_CALL_SUCCESS:
      case AGENT_TOOL_CALL_FAILED:
        toolCalls += 1;
        break;
      default:
        break;
    }
  }

  return { started, active: deriveActiveRuns(events).length, succeeded, failed, toolCalls };
}

/**
 * Map an `AGENT.*` event type to a status-badge kind for the activity table.
 * SUCCESS→green, FAILED→red, STARTED→blue, PARTIAL→yellow, others→gray.
 */
export function agentEventTone(type: string): StatusKind {
  if (type.endsWith('.FAILED') || type.endsWith('.WRITE_FAILED')) return 'down';
  if (type.endsWith('.SUCCESS')) return 'healthy';
  if (type === AGENT_RUN_STARTED) return 'info';
  if (type.endsWith('.PARTIAL')) return 'degraded';
  return 'unknown';
}

/** Map a run-stream frame to a badge kind for the live tail. */
export function frameTone(kind: string, success: boolean | null): StatusKind {
  switch (kind) {
    case 'tool_call':
      return 'info';
    case 'tool_result':
      return success === false ? 'down' : 'healthy';
    case 'final':
      return success === false ? 'down' : 'healthy';
    case 'question':
      return 'degraded';
    case 'answer':
      return 'info';
    case 'end':
      return 'unknown';
    default:
      return 'unknown';
  }
}

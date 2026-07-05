// @vitest-environment jsdom
import {
  agentEventTone,
  correlationOf,
  deriveActiveRuns,
  deriveSummary,
  frameTone,
  tagString,
} from '../agent-monitor-utils.js';
import type { DomainEventRow } from '../../../hooks/monitoring/useEventQuery.js';

function row(over: Partial<DomainEventRow>): DomainEventRow {
  return {
    id: over.id ?? 'id-1',
    type: over.type ?? 'AGENT.RUN.STARTED',
    tags: over.tags ?? null,
    data: over.data ?? null,
    createdAt: over.createdAt ?? '2026-07-05T12:00:00.000Z',
    issueNumber: over.issueNumber ?? null,
    sequenceNumber: over.sequenceNumber ?? 1,
  };
}

describe('agent-monitor-utils', () => {
  describe('tagString / correlationOf', () => {
    it('reads a string tag and null-safes absent / non-string tags', () => {
      const r = row({ tags: { agentId: 'a1', correlationId: 'run-1', n: 5 } });
      expect(tagString(r, 'agentId')).toBe('a1');
      expect(correlationOf(r)).toBe('run-1');
      expect(tagString(r, 'n')).toBeNull();
      expect(tagString(row({ tags: null }), 'agentId')).toBeNull();
    });
  });

  describe('deriveActiveRuns', () => {
    it('returns STARTED runs with no terminal event, newest-first, with tool-call counts', () => {
      const events = [
        row({
          id: 'e3',
          type: 'AGENT.TOOL_CALL.SUCCESS',
          createdAt: '2026-07-05T12:00:30.000Z',
          tags: { correlationId: 'run-A' },
        }),
        row({
          id: 'e2',
          type: 'AGENT.RUN.STARTED',
          createdAt: '2026-07-05T12:00:10.000Z',
          tags: { correlationId: 'run-B', agentId: 'coder', role: 'dev', provider: 'anthropic', model: 'sonnet' },
        }),
        row({
          id: 'e1',
          type: 'AGENT.RUN.STARTED',
          createdAt: '2026-07-05T12:00:00.000Z',
          tags: { correlationId: 'run-A', agentId: 'planner', role: 'architect', provider: 'openai', model: 'gpt-4o' },
        }),
      ];

      const active = deriveActiveRuns(events);
      expect(active.map((r) => r.correlationId)).toEqual(['run-B', 'run-A']); // newest-first
      const runA = active.find((r) => r.correlationId === 'run-A');
      expect(runA?.toolCalls).toBe(1);
      expect(runA?.agentId).toBe('planner');
      expect(runA?.provider).toBe('openai');
    });

    it('excludes a run once a terminal SUCCESS/FAILED is present', () => {
      const events = [
        row({ id: 'a', type: 'AGENT.RUN.STARTED', tags: { correlationId: 'run-1' } }),
        row({ id: 'b', type: 'AGENT.RUN.SUCCESS', tags: { correlationId: 'run-1' } }),
        row({ id: 'c', type: 'AGENT.RUN.STARTED', tags: { correlationId: 'run-2' } }),
        row({ id: 'd', type: 'AGENT.RUN.FAILED', tags: { correlationId: 'run-2' } }),
      ];
      expect(deriveActiveRuns(events)).toEqual([]);
    });

    it('ignores STARTED events with no correlationId', () => {
      const events = [row({ type: 'AGENT.RUN.STARTED', tags: { agentId: 'x' } })];
      expect(deriveActiveRuns(events)).toEqual([]);
    });
  });

  describe('deriveSummary', () => {
    it('counts started / active / succeeded / failed / tool-calls', () => {
      const events = [
        row({ id: '1', type: 'AGENT.RUN.STARTED', tags: { correlationId: 'r1' } }),
        row({ id: '2', type: 'AGENT.RUN.STARTED', tags: { correlationId: 'r2' } }),
        row({ id: '3', type: 'AGENT.RUN.SUCCESS', tags: { correlationId: 'r1' } }),
        row({ id: '4', type: 'AGENT.TOOL_CALL.SUCCESS', tags: { correlationId: 'r2' } }),
        row({ id: '5', type: 'AGENT.TOOL_CALL.FAILED', tags: { correlationId: 'r2' } }),
      ];
      expect(deriveSummary(events)).toEqual({
        started: 2,
        active: 1, // r2 has no terminal
        succeeded: 1,
        failed: 0,
        toolCalls: 2,
      });
    });
  });

  describe('agentEventTone', () => {
    it('maps event types to badge kinds', () => {
      expect(agentEventTone('AGENT.RUN.SUCCESS')).toBe('healthy');
      expect(agentEventTone('AGENT.TOOL_CALL.FAILED')).toBe('down');
      expect(agentEventTone('AGENT.RUN.STARTED')).toBe('info');
      expect(agentEventTone('AGENT.TASK.PARTIAL')).toBe('degraded');
      expect(agentEventTone('AGENT.ITERATION.COMPLETED')).toBe('unknown');
    });
  });

  describe('frameTone', () => {
    it('maps run-stream frame kinds to badge kinds', () => {
      expect(frameTone('tool_call', null)).toBe('info');
      expect(frameTone('tool_result', true)).toBe('healthy');
      expect(frameTone('tool_result', false)).toBe('down');
      expect(frameTone('final', false)).toBe('down');
      expect(frameTone('question', null)).toBe('degraded');
      expect(frameTone('token', null)).toBe('unknown');
    });
  });
});

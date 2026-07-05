// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import {
  parseRunStreamFrame,
  runStreamUrl,
  useRunStreamTail,
} from '../useRunStreamTail.js';
import type { EventSourceLike } from '../useMonitoringSSE.js';

interface FakeSource extends EventSourceLike {
  close: ReturnType<typeof vi.fn>;
}

function makeFactory() {
  const instances: FakeSource[] = [];
  const factory = vi.fn((_url: string): EventSourceLike => {
    const inst: FakeSource = { onopen: null, onmessage: null, onerror: null, close: vi.fn() };
    instances.push(inst);
    return inst;
  });
  return { factory, instances };
}

/** Emit a bridged run-tap frame the way {@link createRunStreamEventSource} does. */
function emit(inst: FakeSource | undefined, kind: string, payload: Record<string, unknown>): void {
  inst?.onmessage?.({ data: `${kind}\n${JSON.stringify(payload)}` });
}

describe('parseRunStreamFrame', () => {
  it('decodes kind + seq + correlationId + payload', () => {
    const f = parseRunStreamFrame('tool_call\n{"correlationId":"run-1","seq":3,"toolName":"grep","turn":2}');
    expect(f.kind).toBe('tool_call');
    expect(f.seq).toBe(3);
    expect(f.correlationId).toBe('run-1');
    expect(f.payload['toolName']).toBe('grep');
  });

  it('null-safes a missing seq / correlationId (the raw end marker)', () => {
    const f = parseRunStreamFrame('end\n{"reason":"already_complete"}');
    expect(f.kind).toBe('end');
    expect(f.seq).toBeNull();
    expect(f.correlationId).toBeNull();
    expect(f.payload['reason']).toBe('already_complete');
  });

  it('throws on a frame with no newline separator', () => {
    expect(() => parseRunStreamFrame('nope')).toThrow();
  });
});

describe('runStreamUrl', () => {
  it('builds the tenant-scoped 32-23 tap path and URL-encodes the id', () => {
    expect(runStreamUrl('a b')).toBe('/api/v1/llm/runs/a%20b/stream');
  });
});

describe('useRunStreamTail', () => {
  it('subscribes via the injected factory and accumulates frames in order', () => {
    const { factory, instances } = makeFactory();
    const { result } = renderHook(() =>
      useRunStreamTail('run-1', { eventSourceFactory: factory }),
    );

    expect(factory).toHaveBeenCalledWith('/api/v1/llm/runs/run-1/stream');
    const inst = instances[0];
    act(() => inst?.onopen?.({}));
    expect(result.current.connected).toBe(true);

    act(() => emit(inst, 'tool_call', { correlationId: 'run-1', seq: 1, toolName: 'grep' }));
    act(() => emit(inst, 'tool_result', { correlationId: 'run-1', seq: 2, toolName: 'grep', success: true }));

    expect(result.current.frames.map((f) => f.kind)).toEqual(['tool_call', 'tool_result']);
    expect(result.current.done).toBe(false);
  });

  it('dedups a repeated (kind:seq) frame (StrictMode double-invoke safety)', () => {
    const { factory, instances } = makeFactory();
    const { result } = renderHook(() =>
      useRunStreamTail('run-1', { eventSourceFactory: factory }),
    );
    const inst = instances[0];
    act(() => emit(inst, 'tool_call', { correlationId: 'run-1', seq: 1, toolName: 'grep' }));
    act(() => emit(inst, 'tool_call', { correlationId: 'run-1', seq: 1, toolName: 'grep' }));
    expect(result.current.frames).toHaveLength(1);
  });

  it('drops a stale frame from a different run', () => {
    const { factory, instances } = makeFactory();
    const { result } = renderHook(() =>
      useRunStreamTail('run-1', { eventSourceFactory: factory }),
    );
    act(() => emit(instances[0], 'tool_call', { correlationId: 'other-run', seq: 1, toolName: 'x' }));
    expect(result.current.frames).toHaveLength(0);
  });

  it('marks done on a terminal final frame and closes the stream', () => {
    const { factory, instances } = makeFactory();
    const { result } = renderHook(() =>
      useRunStreamTail('run-1', { eventSourceFactory: factory }),
    );
    const inst = instances[0];
    act(() =>
      emit(inst, 'final', { correlationId: 'run-1', seq: 9, success: true, totalTurns: 3, totalTokens: 120 }),
    );
    expect(result.current.done).toBe(true);
    expect(result.current.frames[0]?.kind).toBe('final');
    // Disabling the subscription tears the source down.
    expect(inst?.close).toHaveBeenCalled();
  });
});

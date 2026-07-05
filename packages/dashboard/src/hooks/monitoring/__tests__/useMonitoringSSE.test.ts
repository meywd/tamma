// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import { useMonitoringSSE, type EventSourceLike } from '../useMonitoringSSE.js';

interface FakeSource extends EventSourceLike {
  close: ReturnType<typeof vi.fn>;
}

function makeFactory() {
  const instances: FakeSource[] = [];
  const factory = vi.fn((_url: string): EventSourceLike => {
    const inst: FakeSource = {
      onopen: null,
      onmessage: null,
      onerror: null,
      close: vi.fn(),
    };
    instances.push(inst);
    return inst;
  });
  return { factory, instances };
}

describe('useMonitoringSSE', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('connects through the injected factory and reports connected on open', () => {
    const { factory, instances } = makeFactory();
    const { result } = renderHook(() =>
      useMonitoringSSE('/api/monitoring/stream', { eventSourceFactory: factory }),
    );

    expect(factory).toHaveBeenCalledWith('/api/monitoring/stream');
    const inst = instances[0];
    expect(inst).toBeDefined();

    act(() => inst?.onopen?.({}));
    expect(result.current.connected).toBe(true);
    expect(result.current.status).toBe('connected');
  });

  it('parses incoming messages', () => {
    const { factory, instances } = makeFactory();
    const { result } = renderHook(() =>
      useMonitoringSSE<{ n: number }>('/sse', { eventSourceFactory: factory }),
    );
    act(() => instances[0]?.onmessage?.({ data: '{"n":42}' }));
    expect(result.current.data).toEqual({ n: 42 });
  });

  it('reconnects with backoff after an error', async () => {
    const { factory, instances } = makeFactory();
    const { result } = renderHook(() => useMonitoringSSE('/sse', { eventSourceFactory: factory }));

    act(() => instances[0]?.onerror?.({}));
    expect(instances[0]?.close).toHaveBeenCalled();
    expect(result.current.status).toBe('reconnecting');
    expect(result.current.reconnectAttempt).toBe(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });
    expect(factory).toHaveBeenCalledTimes(2);
  });

  it('closes the stream and stops on unmount', () => {
    const { factory, instances } = makeFactory();
    const { unmount } = renderHook(() => useMonitoringSSE('/sse', { eventSourceFactory: factory }));
    const inst = instances[0];
    unmount();
    expect(inst?.close).toHaveBeenCalled();
  });

  it('stays disconnected when url is null', () => {
    const { factory } = makeFactory();
    const { result } = renderHook(() =>
      useMonitoringSSE(null, { eventSourceFactory: factory }),
    );
    expect(factory).not.toHaveBeenCalled();
    expect(result.current.status).toBe('disconnected');
  });
});

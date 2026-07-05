// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import { useAutoRefresh } from '../useAutoRefresh.js';

function setVisibility(state: DocumentVisibilityState): void {
  Object.defineProperty(document, 'visibilityState', {
    configurable: true,
    get: () => state,
  });
}

describe('useAutoRefresh', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    window.localStorage.clear();
    setVisibility('visible');
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('refresh() invokes the fetcher and records lastUpdated', async () => {
    const fetcher = vi.fn().mockResolvedValue(undefined);
    const { result } = renderHook(() => useAutoRefresh(fetcher));

    await act(async () => {
      await result.current.refresh();
    });

    expect(fetcher).toHaveBeenCalledOnce();
    expect(result.current.lastUpdated).toBeInstanceOf(Date);
    expect(result.current.loading).toBe(false);
  });

  it('captures fetcher errors', async () => {
    const fetcher = vi.fn().mockRejectedValue(new Error('nope'));
    const { result } = renderHook(() => useAutoRefresh(fetcher));

    await act(async () => {
      await result.current.refresh();
    });

    expect(result.current.error?.message).toBe('nope');
  });

  it('calls the fetcher on the configured interval', async () => {
    const fetcher = vi.fn().mockResolvedValue(undefined);
    const { result } = renderHook(() => useAutoRefresh(fetcher));

    act(() => result.current.setInterval(5000));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });

    expect(fetcher).toHaveBeenCalled();
  });

  it('persists the interval to localStorage', () => {
    const fetcher = vi.fn().mockResolvedValue(undefined);
    const { result } = renderHook(() =>
      useAutoRefresh(fetcher, { storageKey: 'tamma.test.autoRefresh' }),
    );

    act(() => result.current.setInterval(30000));
    expect(window.localStorage.getItem('tamma.test.autoRefresh')).toBe('30000');

    act(() => result.current.setInterval(null));
    expect(window.localStorage.getItem('tamma.test.autoRefresh')).toBe('off');
  });

  it('hydrates the initial interval from localStorage', () => {
    window.localStorage.setItem('tamma.test.autoRefresh', '10000');
    const fetcher = vi.fn().mockResolvedValue(undefined);
    const { result } = renderHook(() =>
      useAutoRefresh(fetcher, { storageKey: 'tamma.test.autoRefresh' }),
    );
    expect(result.current.interval).toBe(10000);
  });

  it('pauses the interval while the tab is hidden', async () => {
    const fetcher = vi.fn().mockResolvedValue(undefined);
    const { result } = renderHook(() => useAutoRefresh(fetcher));

    setVisibility('hidden');
    act(() => result.current.setInterval(5000));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(15000);
    });

    expect(fetcher).not.toHaveBeenCalled();
  });
});

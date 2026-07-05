// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { useTimeRange } from '../useTimeRange.js';

function wrapperFor(initialEntry: string) {
  return ({ children }: { children: ReactNode }) => (
    <MemoryRouter initialEntries={[initialEntry]}>{children}</MemoryRouter>
  );
}

const HOUR = 60 * 60 * 1000;

describe('useTimeRange', () => {
  it('defaults to the 24h preset', () => {
    const { result } = renderHook(() => useTimeRange(), { wrapper: wrapperFor('/monitoring') });
    expect(result.current.preset).toBe('24h');
    const diff = result.current.range.end.getTime() - result.current.range.start.getTime();
    expect(diff).toBe(24 * HOUR);
  });

  it('reads the preset from the URL query string', () => {
    const { result } = renderHook(() => useTimeRange(), {
      wrapper: wrapperFor('/monitoring?range=6h'),
    });
    expect(result.current.preset).toBe('6h');
    const diff = result.current.range.end.getTime() - result.current.range.start.getTime();
    expect(diff).toBe(6 * HOUR);
  });

  it('updates the preset via setPreset', () => {
    const { result } = renderHook(() => useTimeRange(), { wrapper: wrapperFor('/monitoring') });
    act(() => result.current.setPreset('7d'));
    expect(result.current.preset).toBe('7d');
  });

  it('supports an explicit custom range', () => {
    const { result } = renderHook(() => useTimeRange(), { wrapper: wrapperFor('/monitoring') });
    const start = new Date('2026-01-01T00:00:00.000Z');
    const end = new Date('2026-01-02T00:00:00.000Z');
    act(() => result.current.setCustomRange(start, end));
    expect(result.current.preset).toBe('custom');
    expect(result.current.range.start.toISOString()).toBe(start.toISOString());
    expect(result.current.range.end.toISOString()).toBe(end.toISOString());
  });
});

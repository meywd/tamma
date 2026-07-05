/**
 * useTimeRange — manages the monitoring dashboards' global time range.
 *
 * Story 23-12 (AC7). Presets convert to a concrete `{ start, end }` window for
 * API calls, and the selection is persisted in the URL query string (`?range=`,
 * plus `?start=`/`?end=` for custom windows) so a monitoring view is
 * shareable/bookmarkable and survives a reload.
 */

import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';

export type TimeRangePreset = '1h' | '6h' | '24h' | '7d' | '30d' | 'custom';

export interface TimeRange {
  start: Date;
  end: Date;
}

export interface TimeRangeOption {
  value: TimeRangePreset;
  label: string;
}

export const TIME_RANGE_PRESETS: readonly TimeRangeOption[] = [
  { value: '1h', label: 'Last 1h' },
  { value: '6h', label: 'Last 6h' },
  { value: '24h', label: 'Last 24h' },
  { value: '7d', label: 'Last 7d' },
  { value: '30d', label: 'Last 30d' },
  { value: 'custom', label: 'Custom' },
];

const PRESET_MS: Record<Exclude<TimeRangePreset, 'custom'>, number> = {
  '1h': 60 * 60 * 1000,
  '6h': 6 * 60 * 60 * 1000,
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000,
  '30d': 30 * 24 * 60 * 60 * 1000,
};

function isPreset(value: string | null): value is TimeRangePreset {
  return (
    value === '1h' ||
    value === '6h' ||
    value === '24h' ||
    value === '7d' ||
    value === '30d' ||
    value === 'custom'
  );
}

export interface UseTimeRangeResult {
  preset: TimeRangePreset;
  range: TimeRange;
  setPreset: (preset: TimeRangePreset) => void;
  setCustomRange: (start: Date, end: Date) => void;
}

export function useTimeRange(defaultPreset: TimeRangePreset = '24h'): UseTimeRangeResult {
  const [searchParams, setSearchParams] = useSearchParams();

  const rawPreset = searchParams.get('range');
  const preset: TimeRangePreset = isPreset(rawPreset) ? rawPreset : defaultPreset;

  const startParam = searchParams.get('start');
  const endParam = searchParams.get('end');

  const range = useMemo<TimeRange>(() => {
    const end = new Date();
    if (preset === 'custom') {
      const customStart = startParam
        ? new Date(startParam)
        : new Date(end.getTime() - PRESET_MS['24h']);
      const customEnd = endParam ? new Date(endParam) : end;
      return { start: customStart, end: customEnd };
    }
    return { start: new Date(end.getTime() - PRESET_MS[preset]), end };
  }, [preset, startParam, endParam]);

  const setPreset = useCallback(
    (next: TimeRangePreset) => {
      setSearchParams(
        (prev) => {
          const params = new URLSearchParams(prev);
          params.set('range', next);
          if (next !== 'custom') {
            params.delete('start');
            params.delete('end');
          }
          return params;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  const setCustomRange = useCallback(
    (start: Date, end: Date) => {
      setSearchParams(
        (prev) => {
          const params = new URLSearchParams(prev);
          params.set('range', 'custom');
          params.set('start', start.toISOString());
          params.set('end', end.toISOString());
          return params;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  return { preset, range, setPreset, setCustomRange };
}

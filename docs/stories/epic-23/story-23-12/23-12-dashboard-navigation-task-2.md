# Task 2: MonitoringLayout & Shared Monitoring Hooks

**Story:** 23-12-dashboard-navigation
**Epic:** 23

## Task Description

Create the `MonitoringLayout` page shell that all monitoring screens use, plus the shared hooks: `useMonitoringSSE`, `useAutoRefresh`, and `useTimeRange`. The layout provides a consistent header with title, description, last-updated timestamp, auto-refresh controls, time range selector, and SSE connection status.

## Acceptance Criteria

- `MonitoringLayout` component renders page header, auto-refresh toggle, time range selector, and connection status
- Auto-refresh toggle offers off/5s/10s/30s/60s options, persisted in localStorage
- Time range selector offers Last 1h/6h/24h/7d/30d presets plus Custom, persisted in URL query params
- Connection status indicator shows connected (green), disconnected (red), reconnecting (yellow)
- `useMonitoringSSE` handles SSE connection with exponential backoff reconnection
- `useAutoRefresh` pauses polling when browser tab is hidden
- `useTimeRange` converts presets to `{ start: Date; end: Date }` for API calls

## Implementation Details

### Technical Requirements

- [ ] Create `packages/dashboard/src/hooks/monitoring/useMonitoringSSE.ts`:
  ```typescript
  export interface UseMonitoringSSEOptions {
    url: string;
    enabled?: boolean;                // default true
    onEvent?: (event: string, data: unknown) => void;
  }

  export interface UseMonitoringSSEResult {
    data: unknown | null;             // last received data
    connected: boolean;
    error: string | null;
    reconnectAttempt: number;
  }

  export function useMonitoringSSE(options: UseMonitoringSSEOptions): UseMonitoringSSEResult;
  ```
  - Uses native `EventSource` API
  - On `onerror`: close connection, increment reconnect attempt, schedule reconnect with exponential backoff `Math.min(1000 * 2^attempt, 30000)`
  - On `onopen`: reset reconnect attempt to 0, set connected to true
  - On `onmessage`: parse JSON, update data, call `onEvent` callback
  - Cleans up `EventSource` on unmount
  - If `enabled` is false, does not connect

- [ ] Create `packages/dashboard/src/hooks/monitoring/useAutoRefresh.ts`:
  ```typescript
  export interface UseAutoRefreshOptions {
    fetchFn: () => Promise<void>;
    defaultInterval?: number;     // ms, 0 = off
    storageKey?: string;          // localStorage key for persisting interval
  }

  export interface UseAutoRefreshResult {
    loading: boolean;
    error: string | null;
    lastUpdated: Date | null;
    refresh: () => Promise<void>;
    interval: number;                    // current interval in ms
    setInterval: (ms: number) => void;   // 0 = off, 5000, 10000, 30000, 60000
  }

  export function useAutoRefresh(options: UseAutoRefreshOptions): UseAutoRefreshResult;
  ```
  - Stores interval in state, syncs to localStorage via `storageKey`
  - On mount, reads persisted interval from localStorage (default to `defaultInterval`)
  - Sets up `setInterval` with the current interval; clears and restarts when interval changes
  - Uses `document.addEventListener('visibilitychange', ...)` to pause/resume
  - When `document.visibilityState === 'hidden'`, clears the timer; when `visible`, resumes and triggers immediate fetch
  - `refresh()` manually triggers `fetchFn`, sets loading/error/lastUpdated

- [ ] Create `packages/dashboard/src/hooks/monitoring/useTimeRange.ts`:
  ```typescript
  export type TimeRangePreset = '1h' | '6h' | '24h' | '7d' | '30d' | 'custom';

  export interface TimeRange {
    start: Date;
    end: Date;
  }

  export interface UseTimeRangeResult {
    preset: TimeRangePreset;
    range: TimeRange;
    setPreset: (preset: TimeRangePreset) => void;
    setCustomRange: (start: Date, end: Date) => void;
    sinceMs: number;    // helper: range.start.getTime()
    untilMs: number;    // helper: range.end.getTime()
  }

  export function useTimeRange(defaultPreset?: TimeRangePreset): UseTimeRangeResult;
  ```
  - Converts presets to absolute date ranges: `1h` = now - 1 hour to now, etc.
  - Stores preset in URL search params via `useSearchParams()`: `?timeRange=24h`
  - Custom range stores `start` and `end` in URL params: `?timeRange=custom&start=...&end=...`
  - When preset changes, recalculates range relative to current time
  - Provides `sinceMs` and `untilMs` convenience getters for API calls

- [ ] Create `packages/dashboard/src/components/monitoring/MonitoringLayout.tsx`:
  ```typescript
  export interface MonitoringLayoutProps {
    title: string;
    description?: string;
    children: React.ReactNode;
    sseUrl?: string;                    // if provided, shows connection status
    autoRefreshFn?: () => Promise<void>;
    defaultRefreshInterval?: number;     // default 0 (off)
    showTimeRange?: boolean;             // default true
  }

  export function MonitoringLayout(props: MonitoringLayoutProps): JSX.Element;
  ```
  - Renders a page header with `title` and `description`
  - Shows "Last updated: Xs ago" timestamp that updates every second
  - Shows a "Refresh" button that calls `autoRefreshFn`
  - Shows auto-refresh interval dropdown: Off, 5s, 10s, 30s, 60s
  - Shows time range selector with preset buttons and custom option
  - If `sseUrl` is provided, shows connection status badge using `useMonitoringSSE`
  - Uses Tailwind CSS classes consistent with existing dashboard styling
  - Passes `useTimeRange` result to children via context or render prop

- [ ] Create `packages/dashboard/src/components/monitoring/MonitoringContext.tsx`:
  ```typescript
  import { createContext, useContext } from 'react';
  import type { TimeRange, TimeRangePreset } from '../../hooks/monitoring/useTimeRange.js';

  export interface MonitoringContextValue {
    timeRange: { preset: TimeRangePreset; range: TimeRange; sinceMs: number; untilMs: number };
    refreshTrigger: number;  // increments on each refresh, children can use as useEffect dep
  }

  export const MonitoringContext = createContext<MonitoringContextValue | null>(null);

  export function useMonitoringContext(): MonitoringContextValue;
  ```

### Files to Create

- CREATE `packages/dashboard/src/hooks/monitoring/useMonitoringSSE.ts`
- CREATE `packages/dashboard/src/hooks/monitoring/useAutoRefresh.ts`
- CREATE `packages/dashboard/src/hooks/monitoring/useTimeRange.ts`
- CREATE `packages/dashboard/src/components/monitoring/MonitoringLayout.tsx`
- CREATE `packages/dashboard/src/components/monitoring/MonitoringContext.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/__tests__/useMonitoringSSE.test.ts`
- CREATE `packages/dashboard/src/hooks/monitoring/__tests__/useAutoRefresh.test.ts`
- CREATE `packages/dashboard/src/hooks/monitoring/__tests__/useTimeRange.test.ts`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/MonitoringLayout.test.tsx`

### Dependencies

- `react`, `react-router-dom` (`useSearchParams`) (existing)
- `zustand` (existing, for optional state management)
- Native `EventSource` API (browser built-in)
- `document.visibilityState` API (browser built-in)
- `localStorage` API (browser built-in)

## Testing Strategy

### Unit Tests -- useMonitoringSSE.test.ts

- [ ] Test initial state: connected=false, data=null, error=null, reconnectAttempt=0
- [ ] Test connects to SSE endpoint on mount
- [ ] Test sets connected=true on EventSource open
- [ ] Test updates data on message event
- [ ] Test calls onEvent callback on message
- [ ] Test sets error and increments reconnectAttempt on error
- [ ] Test reconnects with exponential backoff (1s, 2s, 4s, max 30s)
- [ ] Test cleans up EventSource on unmount
- [ ] Test does not connect when enabled=false
- [ ] Test reconnects when enabled transitions from false to true

### Unit Tests -- useAutoRefresh.test.ts

- [ ] Test initial fetch on mount
- [ ] Test polling at configured interval
- [ ] Test stops polling when interval set to 0
- [ ] Test pauses polling when document.visibilityState is 'hidden'
- [ ] Test resumes and immediate-fetches when visibility returns to 'visible'
- [ ] Test `refresh()` triggers manual fetch
- [ ] Test loading/error state management during fetch
- [ ] Test lastUpdated updates on successful fetch
- [ ] Test persists interval to localStorage
- [ ] Test reads persisted interval from localStorage on mount

### Unit Tests -- useTimeRange.test.ts

- [ ] Test default preset is '1h'
- [ ] Test preset '1h' produces range of last 1 hour
- [ ] Test preset '24h' produces range of last 24 hours
- [ ] Test setPreset updates range and URL params
- [ ] Test custom range sets start and end
- [ ] Test sinceMs and untilMs return correct epoch milliseconds
- [ ] Test reads preset from URL params on mount
- [ ] Test reads custom range from URL params on mount

### Unit Tests -- MonitoringLayout.test.tsx

- [ ] Test renders title and description
- [ ] Test renders children
- [ ] Test renders refresh button that calls autoRefreshFn
- [ ] Test renders auto-refresh interval dropdown
- [ ] Test renders time range selector with preset buttons
- [ ] Test shows SSE connection status when sseUrl is provided
- [ ] Test does not show SSE status when sseUrl is not provided
- [ ] Test "Last updated" timestamp displays and updates

## Completion Checklist

- [ ] `useMonitoringSSE` hook with reconnection logic
- [ ] `useAutoRefresh` hook with visibility pause
- [ ] `useTimeRange` hook with URL persistence
- [ ] `MonitoringLayout` component with all controls
- [ ] `MonitoringContext` for sharing time range with children
- [ ] All hooks tested individually
- [ ] Layout component renders correctly
- [ ] TypeScript strict mode compiles without errors

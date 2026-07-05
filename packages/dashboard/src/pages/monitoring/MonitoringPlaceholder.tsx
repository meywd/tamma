/**
 * MonitoringPlaceholder — scaffold body for a monitoring page whose data view is
 * delivered by a later Epic-23 story. Story 23-12.
 *
 * It wires the real shared shell + hooks (`MonitoringLayout` + `useAutoRefresh` +
 * `useTimeRange`) around an EmptyState, so every route is live and interactive
 * today and each page author simply swaps the body (or replaces the file) with
 * their charts/tables in the follow-up story.
 */

import { useEffect, type JSX, type ReactNode } from 'react';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { useAutoRefresh } from '../../hooks/monitoring/useAutoRefresh.js';
import { useTimeRange } from '../../hooks/monitoring/useTimeRange.js';

interface MonitoringPlaceholderProps {
  title: string;
  description: string;
  /** The follow-up story that fills the page in, e.g. "Story 23-1". */
  storyRef: string;
  /** localStorage key for the auto-refresh preference. */
  storageKey: string;
  /** Optional real content; when omitted a "coming soon" EmptyState is shown. */
  children?: ReactNode;
}

export function MonitoringPlaceholder({
  title,
  description,
  storyRef,
  storageKey,
  children,
}: MonitoringPlaceholderProps): JSX.Element {
  const { preset, setPreset } = useTimeRange();
  const { lastUpdated, loading, refresh, interval, setInterval } = useAutoRefresh(
    // No data source yet — the real fetcher arrives with the page's own story.
    () => {},
    { storageKey },
  );

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return (
    <MonitoringLayout
      title={title}
      description={description}
      lastUpdated={lastUpdated}
      loading={loading}
      onRefresh={() => {
        void refresh();
      }}
      autoRefreshInterval={interval}
      onAutoRefreshChange={setInterval}
      timeRange={preset}
      onTimeRangeChange={setPreset}
      connectionStatus="disconnected"
    >
      {children ?? (
        <EmptyState
          title={`${title} — coming soon`}
          description={`This monitoring view is scaffolded by Story 23-12. Its data and visualizations arrive in ${storyRef}.`}
        />
      )}
    </MonitoringLayout>
  );
}

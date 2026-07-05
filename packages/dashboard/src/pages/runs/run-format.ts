/**
 * Small formatting helpers shared by the Runs list + Run detail pages
 * (Story 21-4). Dependency-free (no dayjs in the dashboard bundle).
 */

import type { StatusKind } from '../../components/monitoring/StatusBadge.js';

/** Human-readable run status → StatusBadge tone. */
export function runStatusKind(status: string): StatusKind {
  switch (status.toLowerCase()) {
    case 'completed':
    case 'succeeded':
    case 'success':
      return 'healthy';
    case 'running':
    case 'pending':
    case 'queued':
      return 'info';
    case 'failed':
    case 'error':
      return 'down';
    case 'cancelled':
    case 'canceled':
      return 'unknown';
    default:
      return 'unknown';
  }
}

/** "2m 34s" / "1h 12m" / "540ms" / "—" (null). */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '—';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds}s`;
  return `${seconds}s`;
}

/** USD cost with adaptive precision ("$0.0000" for sub-dollar, "$12.34" else). */
export function formatCost(usd: number | null | undefined): string {
  if (usd == null) return '$0.00';
  if (usd === 0) return '$0.00';
  return `$${usd.toFixed(usd < 1 ? 4 : 2)}`;
}

/** Locale timestamp; falls back to the raw string when unparseable. */
export function formatTimestamp(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** Short run id for dense table cells (first 8 chars of the GUID). */
export function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}

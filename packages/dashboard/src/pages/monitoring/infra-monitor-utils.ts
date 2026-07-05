/**
 * Pure helpers for the Infrastructure Monitor (Story 23-8): byte / uptime
 * formatting and the dependency-status → badge-kind mapping. Kept dependency-
 * free and side-effect-free so they unit-test in isolation.
 */

import type { StatusKind } from '../../components/monitoring/StatusBadge.js';

/** Human-readable bytes (binary units). `1536` → `"1.5 KB"`. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  const exponent = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  const value = bytes / Math.pow(1024, exponent);
  const rounded = value >= 100 || exponent === 0 ? Math.round(value) : Math.round(value * 10) / 10;
  return `${rounded} ${units[exponent]}`;
}

/** Compact duration from whole seconds. `93784` → `"1d 2h 3m"`. */
export function formatUptime(totalSeconds: number): string {
  if (!Number.isFinite(totalSeconds) || totalSeconds <= 0) return '0m';
  const days = Math.floor(totalSeconds / 86_400);
  const hours = Math.floor((totalSeconds % 86_400) / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);

  const parts: string[] = [];
  if (days > 0) parts.push(`${days}d`);
  if (hours > 0) parts.push(`${hours}h`);
  if (minutes > 0 || parts.length === 0) parts.push(`${minutes}m`);
  return parts.join(' ');
}

/** Map a dependency probe status to a monitoring badge kind. */
export function dependencyKind(status: string): StatusKind {
  switch (status) {
    case 'healthy':
      return 'healthy';
    case 'unhealthy':
      return 'down';
    default:
      return 'unknown';
  }
}

/** Left-accent tone for a utilisation percentage (green → yellow → red). */
export function usageTone(percent: number): 'green' | 'yellow' | 'red' {
  if (percent >= 90) return 'red';
  if (percent >= 75) return 'yellow';
  return 'green';
}

/**
 * StatusBadge — colored status pill (green / yellow / red / gray / blue) with an
 * optional leading dot. Story 23-12 (AC4).
 *
 * Accepts either a semantic status (`healthy`/`degraded`/`down`/`unknown`/`info`)
 * or a raw tone, so callers can map their own domain states onto a consistent
 * visual vocabulary shared across every monitoring screen.
 */

import type { JSX, ReactNode } from 'react';

export type StatusTone = 'green' | 'yellow' | 'red' | 'gray' | 'blue';

export type StatusKind =
  | 'healthy'
  | 'degraded'
  | 'down'
  | 'unknown'
  | 'info'
  | StatusTone;

const TONE_BY_KIND: Record<StatusKind, StatusTone> = {
  healthy: 'green',
  degraded: 'yellow',
  down: 'red',
  unknown: 'gray',
  info: 'blue',
  green: 'green',
  yellow: 'yellow',
  red: 'red',
  gray: 'gray',
  blue: 'blue',
};

const TONE_CLASSES: Record<StatusTone, string> = {
  green: 'bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300',
  yellow: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/40 dark:text-yellow-300',
  red: 'bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300',
  gray: 'bg-gray-100 text-gray-700 dark:bg-gray-700 dark:text-gray-300',
  blue: 'bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300',
};

const DOT_CLASSES: Record<StatusTone, string> = {
  green: 'bg-green-500',
  yellow: 'bg-yellow-500',
  red: 'bg-red-500',
  gray: 'bg-gray-400',
  blue: 'bg-blue-500',
};

interface StatusBadgeProps {
  status: StatusKind;
  children?: ReactNode;
  label?: string;
  showDot?: boolean;
  className?: string;
}

export function StatusBadge({
  status,
  children,
  label,
  showDot = true,
  className = '',
}: StatusBadgeProps): JSX.Element {
  const tone = TONE_BY_KIND[status];
  const content: ReactNode = children ?? label ?? status;

  return (
    <span
      data-testid="status-badge"
      data-tone={tone}
      className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-medium ${TONE_CLASSES[tone]} ${className}`}
    >
      {showDot && (
        <span className={`h-2 w-2 rounded-full ${DOT_CLASSES[tone]}`} aria-hidden="true" />
      )}
      {content}
    </span>
  );
}

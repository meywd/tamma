/**
 * MetricGrid — responsive grid wrapper for MetricCards, 1-4 columns depending on
 * viewport. Story 23-12 (AC4).
 */

import type { JSX, ReactNode } from 'react';

export type MetricGridColumns = 1 | 2 | 3 | 4;

interface MetricGridProps {
  children: ReactNode;
  /** Maximum columns at the largest breakpoint. Defaults to 4. */
  columns?: MetricGridColumns;
  className?: string;
}

const COLUMN_CLASSES: Record<MetricGridColumns, string> = {
  1: 'grid-cols-1',
  2: 'grid-cols-1 sm:grid-cols-2',
  3: 'grid-cols-1 sm:grid-cols-2 lg:grid-cols-3',
  4: 'grid-cols-1 sm:grid-cols-2 lg:grid-cols-4',
};

export function MetricGrid({ children, columns = 4, className = '' }: MetricGridProps): JSX.Element {
  return (
    <div data-testid="metric-grid" className={`grid gap-4 ${COLUMN_CLASSES[columns]} ${className}`}>
      {children}
    </div>
  );
}

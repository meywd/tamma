/**
 * EmptyState — consistent "nothing here yet" panel with an icon, title,
 * description, and optional action button. Story 23-12 (AC4).
 */

import type { JSX, ReactNode } from 'react';

interface EmptyStateAction {
  label: string;
  onClick: () => void;
}

interface EmptyStateProps {
  title: string;
  description?: string;
  icon?: ReactNode;
  action?: EmptyStateAction;
  className?: string;
}

function DefaultIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      className="h-10 w-10 text-gray-300 dark:text-gray-600"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M3.75 12h16.5M3.75 6.75h16.5M3.75 17.25h16.5"
      />
    </svg>
  );
}

export function EmptyState({
  title,
  description,
  icon,
  action,
  className = '',
}: EmptyStateProps): JSX.Element {
  return (
    <div
      data-testid="empty-state"
      className={`flex flex-col items-center justify-center rounded-lg border border-dashed border-gray-200 py-12 px-6 text-center dark:border-gray-700 ${className}`}
    >
      <div className="mb-3">{icon ?? <DefaultIcon />}</div>
      <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">{title}</h3>
      {description && (
        <p className="mt-1 max-w-md text-sm text-gray-500 dark:text-gray-400">{description}</p>
      )}
      {action && (
        <button
          type="button"
          onClick={action.onClick}
          className="mt-4 inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
        >
          {action.label}
        </button>
      )}
    </div>
  );
}

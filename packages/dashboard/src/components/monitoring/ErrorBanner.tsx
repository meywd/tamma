/**
 * ErrorBanner — dismissible error banner with an optional retry button.
 * Story 23-12 (AC4). Works uncontrolled (self-dismisses) and reports dismissal
 * through the optional `onDismiss` callback.
 */

import { useState, type JSX } from 'react';

interface ErrorBannerProps {
  message: string;
  title?: string;
  onRetry?: () => void;
  onDismiss?: () => void;
  className?: string;
}

export function ErrorBanner({
  message,
  title = 'Something went wrong',
  onRetry,
  onDismiss,
  className = '',
}: ErrorBannerProps): JSX.Element | null {
  const [dismissed, setDismissed] = useState(false);

  if (dismissed) return null;

  const handleDismiss = (): void => {
    setDismissed(true);
    onDismiss?.();
  };

  return (
    <div
      role="alert"
      data-testid="error-banner"
      className={`flex items-start gap-3 rounded-md border border-red-200 bg-red-50 p-4 dark:border-red-900/50 dark:bg-red-950/40 ${className}`}
    >
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={1.8}
        className="mt-0.5 h-5 w-5 shrink-0 text-red-500"
        aria-hidden="true"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M12 9v3.75m0 3.75h.007M10.29 3.86 1.82 18a1.5 1.5 0 0 0 1.29 2.25h17.78A1.5 1.5 0 0 0 22.18 18L13.71 3.86a1.5 1.5 0 0 0-2.42 0Z"
        />
      </svg>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-semibold text-red-800 dark:text-red-300">{title}</p>
        <p className="mt-0.5 break-words text-sm text-red-700 dark:text-red-400">{message}</p>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        {onRetry && (
          <button
            type="button"
            onClick={onRetry}
            className="rounded-md bg-red-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-red-700"
          >
            Retry
          </button>
        )}
        <button
          type="button"
          onClick={handleDismiss}
          aria-label="Dismiss"
          className="rounded-md p-1 text-red-500 hover:bg-red-100 dark:hover:bg-red-900/40"
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth={2}
            className="h-4 w-4"
            aria-hidden="true"
          >
            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18 18 6M6 6l12 12" />
          </svg>
        </button>
      </div>
    </div>
  );
}

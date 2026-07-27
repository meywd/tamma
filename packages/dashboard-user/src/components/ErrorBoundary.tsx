/**
 * ErrorBoundary — root-level render-error boundary (Story 45-2 AC7).
 *
 * Ported from the admin console's AdminErrorBoundary
 * (packages/dashboard/src/pages/admin/AdminErrorBoundary.tsx) but mounted at
 * the ROOT (main.tsx wraps <App />), not around a subtree — the admin app
 * wraps only its lazy admin routes and leaves its own root unprotected; we
 * copy the component, not the placement (45-2 D7).
 */

import { Component, type ErrorInfo, type ReactNode } from 'react';

interface Props {
  children: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
}

export class ErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    // Log to console in development; will integrate with telemetry later
    console.error('[ErrorBoundary]', error, info.componentStack);
  }

  handleRetry = (): void => {
    this.setState({ hasError: false, error: null });
  };

  override render(): ReactNode {
    if (this.state.hasError) {
      return (
        <div className="flex flex-col items-center justify-center min-h-screen text-center px-4">
          <div className="text-5xl text-gray-300 mb-4" aria-hidden="true">
            &#9888;
          </div>
          <h2 className="text-xl font-semibold text-gray-900 mb-2">Something went wrong</h2>
          <p className="text-sm text-gray-500 mb-6 max-w-md">
            An unexpected error occurred. You can try again or return to the dashboard.
          </p>
          <div className="flex gap-3">
            <button
              type="button"
              onClick={this.handleRetry}
              className="px-4 py-2 text-sm font-medium text-white bg-gray-900 hover:bg-gray-800 rounded-md"
            >
              Retry
            </button>
            <a
              href="/"
              className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
            >
              Return to Dashboard
            </a>
          </div>
          {import.meta.env.DEV && this.state.error && (
            <pre className="mt-6 text-left text-xs text-red-600 bg-red-50 p-4 rounded-lg max-w-lg overflow-auto">
              {this.state.error.message}
              {'\n'}
              {this.state.error.stack}
            </pre>
          )}
        </div>
      );
    }

    return this.props.children;
  }
}

/**
 * Root error boundary tests (Story 45-2 AC7).
 */

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { useState, type JSX } from 'react';
import { ErrorBoundary } from './ErrorBoundary';

function Bomb({ shouldThrow }: { shouldThrow: boolean }): JSX.Element {
  if (shouldThrow) {
    throw new Error('kaboom');
  }
  return <p>app content</p>;
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});

describe('ErrorBoundary', () => {
  it('renders the fallback when a child throws', () => {
    // React logs the caught error; keep the test output clean.
    vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <ErrorBoundary>
        <Bomb shouldThrow />
      </ErrorBoundary>,
    );

    expect(screen.getByText(/something went wrong/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /return to dashboard/i })).toBeInTheDocument();
    expect(screen.queryByText('app content')).toBeNull();
  });

  it('Retry resets the boundary and re-renders a now-healthy child', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});

    function Harness(): JSX.Element {
      const [shouldThrow, setShouldThrow] = useState(true);
      return (
        <>
          <button type="button" onClick={() => setShouldThrow(false)}>
            defuse
          </button>
          <ErrorBoundary>
            <Bomb shouldThrow={shouldThrow} />
          </ErrorBoundary>
        </>
      );
    }

    render(<Harness />);
    expect(screen.getByText(/something went wrong/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'defuse' }));
    fireEvent.click(screen.getByRole('button', { name: /retry/i }));

    expect(screen.getByText('app content')).toBeInTheDocument();
    expect(screen.queryByText(/something went wrong/i)).toBeNull();
  });

  it('shows the stack in DEV', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <ErrorBoundary>
        <Bomb shouldThrow />
      </ErrorBoundary>,
    );

    // vitest runs with import.meta.env.DEV = true.
    expect(screen.getByText(/kaboom/)).toBeInTheDocument();
  });

  it('hides the stack outside DEV', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.stubEnv('DEV', false);

    render(
      <ErrorBoundary>
        <Bomb shouldThrow />
      </ErrorBoundary>,
    );

    expect(screen.getByText(/something went wrong/i)).toBeInTheDocument();
    expect(screen.queryByText(/kaboom/)).toBeNull();
  });
});

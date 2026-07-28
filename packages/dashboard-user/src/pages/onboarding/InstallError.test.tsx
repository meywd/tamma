/**
 * InstallError tests (Story 45-2 AC3). The redirect contract was read from
 * GitHubEndpoints.Callback: failures carry ?reason=<code> (the router
 * service's ErrorReason, or the literal "server_error").
 */

import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { InstallError } from './InstallError';

function renderPage(url: string): void {
  render(
    <MemoryRouter initialEntries={[url]}>
      <InstallError />
    </MemoryRouter>,
  );
}

afterEach(() => cleanup());

describe('InstallError', () => {
  it('shows the failure with its reason code and links back to platform setup', () => {
    renderPage('/onboarding/error?reason=server_error');

    expect(screen.getByText(/install failed/i)).toBeInTheDocument();
    expect(screen.getByText('server_error')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /back to platform setup/i })).toHaveAttribute(
      'href',
      '/onboarding/platforms',
    );
  });

  it('renders without a reason too', () => {
    renderPage('/onboarding/error');
    expect(screen.getByText(/install failed/i)).toBeInTheDocument();
  });
});

/**
 * InstallSuccess tests (Story 45-2 AC3). The redirect contract was read from
 * GitHubEndpoints.Callback: plain success carries no params; the
 * Marketplace-first orphan install carries ?orphan=1&installation_id=.
 */

import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { InstallSuccess } from './InstallSuccess';

function renderPage(url: string): void {
  render(
    <MemoryRouter initialEntries={[url]}>
      <InstallSuccess />
    </MemoryRouter>,
  );
}

afterEach(() => cleanup());

describe('InstallSuccess', () => {
  it('confirms a linked install and links to /settings/platforms', () => {
    renderPage('/onboarding/success');

    expect(screen.getByText(/github app installed/i)).toBeInTheDocument();
    expect(screen.getByText(/linked to your organization/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /view connected platforms/i })).toHaveAttribute(
      'href',
      '/settings/platforms',
    );
  });

  it('explains the orphan (Marketplace-first) install variant', () => {
    renderPage('/onboarding/success?orphan=1&installation_id=12345');

    expect(screen.getByText(/not\s+yet linked/i)).toBeInTheDocument();
    expect(screen.getByText(/#12345/)).toBeInTheDocument();
  });
});

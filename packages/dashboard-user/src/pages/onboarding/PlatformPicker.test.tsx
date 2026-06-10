/**
 * PlatformPicker tests. Invariants we care about:
 *   - Renders all 6 PlatformKind cards from the backend response.
 *   - Marks `available: false` cards as "coming soon" + disables their
 *     link (so the user can't navigate into a half-wired install
 *     form).
 *   - Marks `available: true` cards as a real link.
 *   - Surfaces network errors as a 'role=alert' so screen readers
 *     announce them.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { PlatformPicker } from './PlatformPicker';

function jsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function renderPicker() {
  return render(
    <MemoryRouter>
      <PlatformPicker />
    </MemoryRouter>,
  );
}

const kinds = [
  'GitHub',
  'Gitea',
  'Forgejo',
  'GitLab',
  'Bitbucket',
  'AzureDevOps',
] as const;

describe('PlatformPicker', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('renders one card per PlatformKind', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse({
        items: kinds.map((kind) => ({
          kind,
          displayName: kind,
          available: kind === 'Gitea',
          capabilities: ['Actions', 'Secrets'],
          authMode:
            kind === 'GitHub'
              ? 'github_app'
              : kind === 'Bitbucket' || kind === 'AzureDevOps'
                ? 'coming_soon'
                : 'personal_access_token',
        })),
        count: 6,
      }),
    );
    globalThis.fetch = fetchMock;

    renderPicker();

    await waitFor(() => {
      expect(screen.getAllByRole('listitem').length).toBe(6);
    });

    // Every kind name renders as a heading (no exception thrown).
    for (const kind of kinds) {
      expect(screen.getByText(kind)).toBeInTheDocument();
    }
  });

  it('marks Gitea as a real link and Bitbucket as coming soon', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse({
        items: kinds.map((kind) => ({
          kind,
          displayName: kind,
          available: kind === 'Gitea',
          capabilities: ['Actions'],
          authMode:
            kind === 'Bitbucket' || kind === 'AzureDevOps'
              ? 'coming_soon'
              : 'personal_access_token',
        })),
        count: 6,
      }),
    );
    globalThis.fetch = fetchMock;

    renderPicker();

    // Wait for the cards to render.
    await waitFor(() => {
      expect(screen.getAllByRole('listitem').length).toBe(6);
    });

    // Gitea — available card has an aria-labeled link to /onboarding/platforms/Gitea/install
    const giteaLink = screen.getByLabelText(/connect Gitea/i) as HTMLAnchorElement;
    expect(giteaLink.tagName).toBe('A');
    expect(giteaLink.getAttribute('href')).toBe(
      '/onboarding/platforms/Gitea/install',
    );

    // Bitbucket — disabled card has no link.
    expect(screen.getByLabelText(/Bitbucket — coming soon/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/connect Bitbucket/i)).toBeNull();
  });

  it('renders a role=alert when the API call fails', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      new Response('boom', { status: 500 }),
    );
    globalThis.fetch = fetchMock;

    renderPicker();

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
    });
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { renderWithRouter } from '../../test/render-helpers.js';
import { OnboardingPage } from './OnboardingPage.js';
import type { OnboardingStatus } from '../../services/onboarding/onboarding-api-client.js';

function stubStatus(status: OnboardingStatus): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.includes('/v1/onboarding/status')) {
        return new Response(JSON.stringify(status), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return new Response('{}', { status: 404 });
    }),
  );
}

describe('OnboardingPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('renders the connect-github step when install is missing', async () => {
    stubStatus({
      emailVerified: true,
      hasOrg: true,
      tenantId: 't-1',
      hasInstallation: false,
      installationCount: 0,
      installations: [],
    });

    renderWithRouter(<OnboardingPage />, { initialEntries: ['/onboarding'] });

    await waitFor(() => {
      expect(screen.getByText('Connect Tamma to GitHub')).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: /Install Tamma on GitHub/i })).toBeInTheDocument();
    // Skip option present.
    expect(screen.getByRole('button', { name: /Skip for now/i })).toBeInTheDocument();
  });

  it('renders the review-repos step with installation details when linked', async () => {
    stubStatus({
      emailVerified: true,
      hasOrg: true,
      tenantId: 't-1',
      hasInstallation: true,
      installationCount: 1,
      installations: [
        {
          installationId: 4242,
          accountLogin: 'acme-corp',
          accountType: 'Organization',
          suspended: false,
          repoCount: 2,
          repos: [
            { repoId: 1, fullName: 'acme-corp/api' },
            { repoId: 2, fullName: 'acme-corp/web' },
          ],
        },
      ],
    });

    renderWithRouter(<OnboardingPage />, { initialEntries: ['/onboarding'] });

    await waitFor(() => {
      expect(screen.getByText('Installation complete')).toBeInTheDocument();
    });
    expect(screen.getByText('acme-corp')).toBeInTheDocument();
    expect(screen.getByText('acme-corp/api')).toBeInTheDocument();
    expect(screen.getByText('acme-corp/web')).toBeInTheDocument();
    // "Manage on GitHub" link points at GitHub's installation settings.
    const manageLink = screen.getByRole('link', { name: /Manage on GitHub/i });
    expect(manageLink).toHaveAttribute(
      'href',
      'https://github.com/organizations/acme-corp/settings/installations/4242',
    );
  });

  it('renders the verify-email step when email is unverified', async () => {
    stubStatus({
      emailVerified: false,
      hasOrg: false,
      tenantId: null,
      hasInstallation: false,
      installationCount: 0,
      installations: [],
    });

    renderWithRouter(<OnboardingPage />, { initialEntries: ['/onboarding'] });

    await waitFor(() => {
      expect(screen.getByText('Verify your email')).toBeInTheDocument();
    });
  });

  it('shows error banner when status fetch fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('{}', { status: 500 })),
    );

    renderWithRouter(<OnboardingPage />, { initialEntries: ['/onboarding'] });

    await waitFor(() => {
      expect(screen.getByText("Couldn't load your status")).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: /Try again/i })).toBeInTheDocument();
  });

  it('renders the orphan banner when ?orphan=1 query param is present', async () => {
    stubStatus({
      emailVerified: true,
      hasOrg: true,
      tenantId: 't-1',
      hasInstallation: false,
      installationCount: 0,
      installations: [],
    });

    renderWithRouter(<OnboardingPage />, {
      initialEntries: ['/onboarding?orphan=1&installation_id=12345'],
    });

    await waitFor(() => {
      // Banner mentions the orphan id so the user knows which install we
      // received but couldn't bind.
      expect(screen.getByText(/12345/)).toBeInTheDocument();
    });
  });
});

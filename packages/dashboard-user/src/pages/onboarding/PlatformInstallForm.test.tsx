/**
 * PlatformInstallForm tests. Invariants we care about:
 *   - PAT-style platforms render base URL + token inputs.
 *   - Submit POSTs to /api/onboarding/install with the right body.
 *   - 400 with hint surfaces inline.
 *   - GitHub renders the deep-link button (not the form).
 *   - "coming soon" platforms render the placeholder.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { PlatformInstallForm } from './PlatformInstallForm';

function jsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function renderForKind(kind: string) {
  return render(
    <MemoryRouter initialEntries={[`/onboarding/platforms/${kind}/install`]}>
      <Routes>
        <Route
          path="/onboarding/platforms/:kind/install"
          element={<PlatformInstallForm />}
        />
        <Route path="/settings/platforms" element={<div>Connected</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

const platformsResponse = (overrides: { kind: string; authMode: string; available: boolean }) => ({
  items: [
    {
      kind: overrides.kind,
      displayName: overrides.kind,
      available: overrides.available,
      capabilities: ['Actions'],
      authMode: overrides.authMode,
    },
  ],
  count: 1,
});

describe('PlatformInstallForm', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('renders PAT form for Gitea', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse(
        platformsResponse({
          kind: 'Gitea',
          authMode: 'personal_access_token',
          available: true,
        }),
      ),
    );
    globalThis.fetch = fetchMock;

    renderForKind('Gitea');

    await waitFor(() => {
      expect(screen.getByLabelText(/personal access token/i)).toBeInTheDocument();
    });
    expect(screen.getByLabelText(/base url/i)).toBeInTheDocument();
  });

  it('renders deep-link for GitHub App, not the form', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse(
        platformsResponse({
          kind: 'GitHub',
          authMode: 'github_app',
          available: true,
        }),
      ),
    );
    globalThis.fetch = fetchMock;

    renderForKind('GitHub');

    await waitFor(() => {
      expect(screen.getByText(/install tamma on github/i)).toBeInTheDocument();
    });
    // No PAT input — the GitHub flow goes through the App install URL.
    expect(screen.queryByLabelText(/personal access token/i)).toBeNull();
  });

  it('renders coming-soon for Bitbucket', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse(
        platformsResponse({
          kind: 'Bitbucket',
          authMode: 'coming_soon',
          available: false,
        }),
      ),
    );
    globalThis.fetch = fetchMock;

    renderForKind('Bitbucket');

    await waitFor(() => {
      expect(screen.getByText(/on the roadmap/i)).toBeInTheDocument();
    });
    expect(screen.queryByLabelText(/personal access token/i)).toBeNull();
  });

  it('POSTs install body when form is submitted', async () => {
    const fetchMock = vi.fn();
    fetchMock.mockResolvedValueOnce(
      jsonResponse(
        platformsResponse({
          kind: 'Gitea',
          authMode: 'personal_access_token',
          available: true,
        }),
      ),
    );
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        installationId: '00000000-0000-0000-0000-000000000001',
        kind: 'Gitea',
        baseUrl: 'https://gitea.example.com',
        externalId: null,
        status: 'connected',
      }),
    );
    globalThis.fetch = fetchMock;

    renderForKind('Gitea');

    const user = userEvent.setup();

    await waitFor(() => {
      expect(screen.getByLabelText(/personal access token/i)).toBeInTheDocument();
    });

    await user.clear(screen.getByLabelText(/base url/i));
    await user.type(
      screen.getByLabelText(/base url/i),
      'https://gitea.example.com',
    );
    await user.type(screen.getByLabelText(/personal access token/i), 'tok-1');
    await user.click(screen.getByRole('button', { name: /connect gitea/i }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledTimes(2);
    });

    const submitCall = fetchMock.mock.calls[1];
    expect(submitCall?.[0] as string).toContain('/api/onboarding/install');
    const init = submitCall?.[1] as RequestInit;
    expect(init.method).toBe('POST');
    const body = JSON.parse(init.body as string);
    expect(body).toEqual({
      kind: 'Gitea',
      baseUrl: 'https://gitea.example.com',
      externalId: null,
      credentialPlaintext: 'tok-1',
    });
  });

  it('renders the backend hint inline when the install fails', async () => {
    const fetchMock = vi.fn();
    fetchMock.mockResolvedValueOnce(
      jsonResponse(
        platformsResponse({
          kind: 'Gitea',
          authMode: 'personal_access_token',
          available: true,
        }),
      ),
    );
    fetchMock.mockResolvedValueOnce(
      jsonResponse(
        { error: 'auth_probe_failed', hint: 'Could not authenticate.' },
        400,
      ),
    );
    globalThis.fetch = fetchMock;

    renderForKind('Gitea');

    const user = userEvent.setup();

    await waitFor(() => {
      expect(screen.getByLabelText(/personal access token/i)).toBeInTheDocument();
    });

    await user.clear(screen.getByLabelText(/base url/i));
    await user.type(
      screen.getByLabelText(/base url/i),
      'https://gitea.example.com',
    );
    await user.type(screen.getByLabelText(/personal access token/i), 'bad');
    await user.click(screen.getByRole('button', { name: /connect gitea/i }));

    await waitFor(() => {
      expect(screen.getByText(/could not authenticate/i)).toBeInTheDocument();
    });
  });

  it('rejects an empty token before sending', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse(
        platformsResponse({
          kind: 'Gitea',
          authMode: 'personal_access_token',
          available: true,
        }),
      ),
    );
    globalThis.fetch = fetchMock;

    renderForKind('Gitea');

    const user = userEvent.setup();

    await waitFor(() => {
      expect(screen.getByLabelText(/personal access token/i)).toBeInTheDocument();
    });

    await user.clear(screen.getByLabelText(/base url/i));
    await user.type(
      screen.getByLabelText(/base url/i),
      'https://gitea.example.com',
    );
    // Empty PAT — the browser's required attribute should prevent
    // submit. Force the click via the form's onSubmit handler so we
    // verify our own JS-level guard fires before any network call.
    const form = screen
      .getByRole('button', { name: /connect gitea/i })
      .closest('form');
    expect(form).not.toBeNull();
    // Bypass the `required` attribute by removing it temporarily so
    // we can exercise the JS guard.
    const tokenInput = screen.getByLabelText(
      /personal access token/i,
    ) as HTMLInputElement;
    tokenInput.removeAttribute('required');
    screen.getByLabelText(/base url/i).removeAttribute('required');
    await user.click(screen.getByRole('button', { name: /connect gitea/i }));

    // Only the initial /platforms fetch happened; no install POST.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });
});

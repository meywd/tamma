// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { ReposPage } from '../ReposPage.js';

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/repos']}>
      <ReposPage />
    </MemoryRouter>,
  );
}

describe('ReposPage', () => {
  it('renders connected repositories from /api/v1/repos', async () => {
    fetchMock.mockResolvedValue(
      okResponse({
        tenantId: 't1',
        count: 2,
        repos: [
          {
            id: 'r1',
            name: 'acme/widgets',
            platform: 'github',
            baseUrl: 'https://api.github.com',
            externalId: '42',
            status: 'connected',
            isPrimary: true,
            connectedAt: '2026-04-16T12:00:00.000Z',
            updatedAt: '2026-04-16T12:00:00.000Z',
          },
          {
            id: 'r2',
            name: 'gitlab:99',
            platform: 'gitlab',
            baseUrl: 'https://gitlab.example.com',
            externalId: '99',
            status: 'suspended',
            isPrimary: false,
            connectedAt: '2026-04-15T12:00:00.000Z',
            updatedAt: '2026-04-16T12:00:00.000Z',
          },
        ],
      }),
    );

    renderPage();

    expect(await screen.findByRole('heading', { name: 'acme/widgets' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'gitlab:99' })).toBeInTheDocument();
    expect(screen.getAllByTestId('repo-card')).toHaveLength(2);
    // The request scopes to the caller's tenant — no tenant id in the URL.
    const urls = fetchMock.mock.calls.map((c) => String(c[0]));
    expect(urls.some((u) => u.includes('/api/v1/repos'))).toBe(true);
  });

  it('shows an empty state with a connect CTA when no repos are connected', async () => {
    fetchMock.mockResolvedValue(okResponse({ tenantId: 't1', count: 0, repos: [] }));
    renderPage();
    expect(await screen.findByTestId('empty-state')).toHaveTextContent(
      'No repositories connected yet',
    );
  });

  it('surfaces an error banner when the request fails (e.g. no_active_tenant)', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 404,
      json: async () => ({ error: 'no_active_tenant' }),
    } as unknown as Response);
    renderPage();
    expect(await screen.findByTestId('error-banner')).toHaveTextContent('no_active_tenant');
  });
});

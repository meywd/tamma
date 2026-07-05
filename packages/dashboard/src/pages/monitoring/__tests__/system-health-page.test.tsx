// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { SystemHealthPage } from '../SystemHealthPage.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

function errResponse(status: number, body: unknown): Response {
  return { ok: false, status, json: async () => body } as unknown as Response;
}

const healthResponse = { status: 'ok', timestamp: '2026-07-05T12:00:00Z', version: '2.0.0' };

const providerHealthResponse = {
  providers: [
    {
      providerKey: 'anthropic-claude',
      state: 'Closed',
      status: 'healthy',
      failureCount: 0,
      lastFailure: null,
    },
    {
      providerKey: 'openai',
      state: 'Open',
      status: 'down',
      failureCount: 5,
      lastFailure: '2026-07-05T10:00:00Z',
    },
  ],
  byKey: {},
};

const deepResponse = {
  from: '2026-07-04T12:00:00Z',
  to: '2026-07-05T12:00:00Z',
  totalCalls: 10,
  totalErrors: 2,
  totalTokens: 0,
  totalCost: 0,
  providers: [],
};

const eventsResponse = {
  events: [
    {
      id: 'e1',
      type: 'CODE.GENERATED.SUCCESS',
      tags: { correlationId: 'run-1' },
      data: {},
      createdAt: '2026-07-05T11:59:00Z',
      issueNumber: 42,
      sequenceNumber: 2,
    },
    {
      id: 'e2',
      type: 'CODE.GENERATED.FAILED',
      tags: { correlationId: 'run-2', status: 'failed' },
      data: {},
      createdAt: '2026-07-05T11:58:00Z',
      issueNumber: 42,
      sequenceNumber: 1,
    },
  ],
  total: 2,
  limit: 100,
  nextCursor: null,
  hasMore: false,
};

const fetchMock = vi.fn();

/** Route by URL; per-source overrides let a test fail one source in isolation. */
function routeFetch(
  over: Partial<{ health: Response; providers: Response; deep: Response; events: Response }> = {},
): void {
  fetchMock.mockImplementation((url: string) => {
    const u = String(url);
    if (u.includes('/api/providers/diagnostics/deep'))
      return Promise.resolve(over.deep ?? okResponse(deepResponse));
    if (u.includes('/api/providers/health'))
      return Promise.resolve(over.providers ?? okResponse(providerHealthResponse));
    if (u.includes('/api/engine/events/query'))
      return Promise.resolve(over.events ?? okResponse(eventsResponse));
    if (u.includes('/api/health')) return Promise.resolve(over.health ?? okResponse(healthResponse));
    return Promise.resolve(okResponse({}));
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  routeFetch();
  vi.stubGlobal('fetch', fetchMock);
  mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/monitoring/health']}>
      <SystemHealthPage />
    </MemoryRouter>,
  );
}

describe('SystemHealthPage', () => {
  it('composes the four health/diagnostics/event sources into one overview', async () => {
    renderPage();

    // Service status cards (one per composed source).
    expect(await screen.findByText('AI providers')).toBeInTheDocument();
    expect(screen.getByText('API')).toBeInTheDocument();
    expect(screen.getByText('Diagnostics')).toBeInTheDocument();
    expect(screen.getByText('Event store')).toBeInTheDocument();

    // Provider health roll-up (anthropic healthy, openai down → 1/2 healthy).
    expect(screen.getAllByText('1/2 healthy').length).toBeGreaterThan(0);

    // Key metrics.
    expect(screen.getByText('Active runs')).toBeInTheDocument();
    expect(screen.getByText('Error rate')).toBeInTheDocument();
    expect(screen.getByText('20.0%')).toBeInTheDocument(); // 2 / 10 provider calls
    expect(screen.getByText('Throughput')).toBeInTheDocument();

    // Provider detail table + circuit-breaker badges.
    expect(screen.getByText('anthropic-claude')).toBeInTheDocument();
    expect(screen.getByText('openai')).toBeInTheDocument();
    expect(screen.getByText('Circuit open')).toBeInTheDocument();

    // Recent-events table with an error-flagged row.
    expect(screen.getByText('CODE.GENERATED.SUCCESS')).toBeInTheDocument();
    expect(screen.getByText('CODE.GENERATED.FAILED')).toBeInTheDocument();

    // All four existing sources were queried.
    await waitFor(() => {
      const urls = fetchMock.mock.calls.map((c) => String(c[0]));
      expect(urls.some((u) => u.includes('/api/health'))).toBe(true);
      expect(urls.some((u) => u.includes('/api/providers/health'))).toBe(true);
      expect(urls.some((u) => u.includes('/api/providers/diagnostics/deep'))).toBe(true);
      expect(urls.some((u) => u.includes('/api/engine/events/query'))).toBe(true);
    });
  });

  it('links out to the Provider Diagnostics and Event Explorer detail pages', async () => {
    renderPage();
    await screen.findByText('AI providers');

    const providerLinks = screen.getAllByRole('link', { name: /provider diagnostics|view diagnostics/i });
    expect(providerLinks.some((a) => a.getAttribute('href') === '/monitoring/providers')).toBe(true);

    const eventLinks = screen.getAllByRole('link', { name: /event explorer/i });
    expect(eventLinks.some((a) => a.getAttribute('href') === '/monitoring/events')).toBe(true);
  });

  it('degrades a single unavailable source to a down service card (fail-soft)', async () => {
    routeFetch({ events: errResponse(500, { error: 'boom' }) });
    renderPage();

    // The rest of the overview still renders…
    expect(await screen.findByText('AI providers')).toBeInTheDocument();
    // …and the failed source shows its down detail instead of blanking the page.
    expect(screen.getByText('Query unavailable')).toBeInTheDocument();
    // No page-level error banner for a partial failure.
    expect(screen.queryByTestId('error-banner')).not.toBeInTheDocument();
  });

  it('shows a page-level error banner only when every source fails', async () => {
    fetchMock.mockImplementation(() => Promise.reject(new Error('network down')));
    renderPage();
    expect(await screen.findByTestId('error-banner')).toHaveTextContent('network down');
  });
});

describe('SystemHealthPage RBAC (inherited from the route AdminGuard)', () => {
  it('renders for an admin', async () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
    render(
      <MemoryRouter initialEntries={['/monitoring/health']}>
        <AdminGuard>
          <SystemHealthPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'System Health' })).toBeInTheDocument();
  });

  it('does NOT render for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u2', role: 'member' }, loading: false, isAdmin: false });
    render(
      <MemoryRouter initialEntries={['/monitoring/health']}>
        <AdminGuard>
          <SystemHealthPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(screen.queryByRole('heading', { name: 'System Health' })).not.toBeInTheDocument();
  });
});

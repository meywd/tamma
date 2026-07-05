// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { ProviderDiagnosticsPage } from '../ProviderDiagnosticsPage.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

function deepReport(over: Partial<Record<string, unknown>> = {}): unknown {
  return {
    from: '2026-07-05T00:00:00.000Z',
    to: '2026-07-05T12:00:00.000Z',
    totalCalls: 6,
    totalErrors: 1,
    totalTokens: 65,
    totalCost: 6.5,
    providers: [
      {
        providerKey: 'anthropic-claude',
        totalCalls: 4,
        successCount: 3,
        failureCount: 1,
        successRate: 0.75,
        errorRate: 0.25,
        latency: { p50: 200, p95: 400, p99: 400, max: 400, avg: 250 },
        totalTokens: 60,
        inputTokens: 60,
        outputTokens: 0,
        totalCost: 6,
        errors: [{ errorClass: 'rate_limit', count: 1, share: 1 }],
        models: [
          {
            model: 'claude-sonnet-4',
            totalCalls: 4,
            successCount: 3,
            successRate: 0.75,
            totalCost: 6,
            totalTokens: 60,
            avgLatencyMs: 250,
          },
        ],
      },
      {
        providerKey: 'openai',
        totalCalls: 2,
        successCount: 2,
        failureCount: 0,
        successRate: 1,
        errorRate: 0,
        latency: { p50: 50, p95: 60, p99: 60, max: 60, avg: 55 },
        totalTokens: 5,
        inputTokens: 5,
        outputTokens: 0,
        totalCost: 0.5,
        errors: [],
        models: [
          {
            model: 'gpt-4o',
            totalCalls: 2,
            successCount: 2,
            successRate: 1,
            totalCost: 0.5,
            totalTokens: 5,
            avgLatencyMs: 55,
          },
        ],
      },
    ],
    ...over,
  };
}

const healthResponse = {
  providers: [
    {
      providerKey: 'anthropic-claude',
      state: 'Closed',
      status: 'healthy',
      failureCount: 0,
      lastSuccess: null,
      lastFailure: null,
      circuitOpenUntil: null,
      healthy: true,
      circuitOpen: false,
      halfOpen: false,
    },
  ],
  byKey: {},
};

const fetchMock = vi.fn();

function routeFetch(deep: unknown): void {
  fetchMock.mockImplementation((url: string) => {
    if (String(url).includes('/diagnostics/deep')) return Promise.resolve(okResponse(deep));
    if (String(url).includes('/health')) return Promise.resolve(okResponse(healthResponse));
    return Promise.resolve(okResponse({}));
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  routeFetch(deepReport());
  vi.stubGlobal('fetch', fetchMock);
  mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/monitoring/providers']}>
      <ProviderDiagnosticsPage />
    </MemoryRouter>,
  );
}

describe('ProviderDiagnosticsPage', () => {
  it('renders per-provider cards, latency and error classification from the deep report', async () => {
    renderPage();

    expect(await screen.findByRole('heading', { name: 'anthropic-claude' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'openai' })).toBeInTheDocument();

    // Latency percentiles (LatencyBar renders p50/p95/p99 labels + values).
    expect(screen.getAllByText(/p50/).length).toBeGreaterThan(0);
    expect(screen.getByText('200ms')).toBeInTheDocument();

    // Error classification table.
    expect(screen.getByText('rate_limit')).toBeInTheDocument();

    // Model usage + cost comparison table.
    expect(screen.getByText('claude-sonnet-4')).toBeInTheDocument();

    // Health badge sourced from /api/providers/health (anthropic-claude is
    // "healthy"; openai has no breaker entry so derives "Healthy" from its 0
    // error rate).
    expect(screen.getAllByText('Healthy').length).toBeGreaterThan(0);

    // Both data sources were queried.
    await waitFor(() => {
      const urls = fetchMock.mock.calls.map((c) => String(c[0]));
      expect(urls.some((u) => u.includes('/api/providers/diagnostics/deep'))).toBe(true);
      expect(urls.some((u) => u.includes('/api/providers/health'))).toBe(true);
    });
  });

  it('filters the visible provider cards via the provider select', async () => {
    renderPage();
    await screen.findByRole('heading', { name: 'anthropic-claude' });

    await userEvent.selectOptions(screen.getByLabelText('Provider filter'), 'openai');

    expect(screen.queryByRole('heading', { name: 'anthropic-claude' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'openai' })).toBeInTheDocument();
  });

  it('shows an empty state when no provider activity is recorded', async () => {
    routeFetch(deepReport({ providers: [], totalCalls: 0, totalErrors: 0, totalTokens: 0, totalCost: 0 }));
    renderPage();
    expect(await screen.findByTestId('empty-state')).toBeInTheDocument();
  });

  it('surfaces an error banner when the deep report request fails', async () => {
    fetchMock.mockImplementation((url: string) => {
      if (String(url).includes('/diagnostics/deep')) {
        return Promise.resolve({ ok: false, status: 500, json: async () => ({ error: 'boom' }) } as unknown as Response);
      }
      return Promise.resolve(okResponse(healthResponse));
    });
    renderPage();
    expect(await screen.findByTestId('error-banner')).toHaveTextContent('boom');
  });
});

describe('ProviderDiagnosticsPage RBAC (inherited from the route AdminGuard)', () => {
  it('renders for an admin', async () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
    render(
      <MemoryRouter initialEntries={['/monitoring/providers']}>
        <AdminGuard>
          <ProviderDiagnosticsPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'Providers' })).toBeInTheDocument();
  });

  it('does NOT render for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u2', role: 'member' }, loading: false, isAdmin: false });
    render(
      <MemoryRouter initialEntries={['/monitoring/providers']}>
        <AdminGuard>
          <ProviderDiagnosticsPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(screen.queryByRole('heading', { name: 'Providers' })).not.toBeInTheDocument();
  });
});

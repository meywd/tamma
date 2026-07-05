// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { InfrastructureMonitorPage } from '../InfrastructureMonitorPage.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

function errorResponse(status: number): Response {
  return { ok: false, status, json: async () => ({}) } as unknown as Response;
}

const SNAPSHOT = {
  runtime: {
    frameworkDescription: '.NET 8.0.20',
    osDescription: 'Ubuntu 24.04.4 LTS',
    processArchitecture: 'X64',
    processorCount: 6,
    cpuUsagePercent: 12.5,
    uptimeSeconds: 93_784,
    startedAt: new Date(Date.now() - 93_784_000).toISOString(),
  },
  process: {
    threadCount: 25,
    threadPoolThreadCount: 7,
    threadPoolPendingWorkItems: 0,
    threadPoolCompletedWorkItems: 100,
    gen0Collections: 3,
    gen1Collections: 2,
    gen2Collections: 1,
  },
  memory: {
    workingSetBytes: 314_572_800,
    privateMemoryBytes: 314_572_800,
    managedHeapBytes: 42_000_000,
    gcHeapSizeBytes: 70_000_000,
    memoryLimitBytes: 1_073_741_824,
    memoryUsedBytes: 314_572_800,
    memoryUsagePercent: 29.3,
    memoryLimitSource: 'cgroup',
  },
  disks: [
    {
      name: '/',
      driveFormat: 'ext4',
      totalBytes: 210_518_392_832,
      freeBytes: 37_529_903_104,
      usedBytes: 172_988_489_728,
      usedPercent: 82.17,
    },
  ],
  dependencies: [
    { name: 'PostgreSQL', status: 'healthy', responseTimeMs: 3, detail: null },
    { name: 'RabbitMQ', status: 'unhealthy', responseTimeMs: 5000, detail: 'unreachable' },
    { name: 'ELSA Server', status: 'unknown', responseTimeMs: 0, detail: 'URL not configured' },
  ],
  collectedAt: new Date().toISOString(),
};

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue(okResponse(SNAPSHOT));
  vi.stubGlobal('fetch', fetchMock);
  mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/monitoring/infrastructure']}>
      <InfrastructureMonitorPage />
    </MemoryRouter>,
  );
}

describe('InfrastructureMonitorPage', () => {
  it('fetches and renders the live infrastructure snapshot', async () => {
    renderPage();

    // Headline metrics from the snapshot.
    expect(await screen.findByText('300 MB')).toBeInTheDocument(); // memory used
    expect(screen.getByText('1d 2h 3m')).toBeInTheDocument(); // uptime
    expect(screen.getByText('.NET 8.0.20')).toBeInTheDocument(); // runtime footer
    // Disk row.
    expect(screen.getByText('ext4')).toBeInTheDocument();

    // It hit the platform-owner infra endpoint.
    await waitFor(() => {
      const urls = fetchMock.mock.calls.map((c) => String(c[0]));
      expect(urls.some((u) => u.includes('/api/admin/monitoring/infrastructure'))).toBe(true);
    });
  });

  it('renders dependency status badges, including a down dependency', async () => {
    renderPage();

    expect(await screen.findByText('PostgreSQL')).toBeInTheDocument();
    expect(screen.getByText('RabbitMQ')).toBeInTheDocument();
    expect(screen.getByText('ELSA Server')).toBeInTheDocument();

    // The DOWN dependency surfaces its (sanitized) detail, not a healthy label.
    expect(screen.getByText('unreachable')).toBeInTheDocument();
    expect(screen.getByText('URL not configured')).toBeInTheDocument();
  });

  it('shows an error banner when the endpoint fails', async () => {
    fetchMock.mockResolvedValue(errorResponse(500));
    renderPage();
    expect(await screen.findByText(/HTTP 500/)).toBeInTheDocument();
  });

  it('shows an empty state when the snapshot is unavailable (403)', async () => {
    // A member who somehow reaches the fetch gets a 403 → error banner, never
    // a partial render of process internals.
    fetchMock.mockResolvedValue(errorResponse(403));
    renderPage();
    expect(await screen.findByText(/HTTP 403/)).toBeInTheDocument();
  });
});

describe('InfrastructureMonitorPage RBAC (inherited from the route AdminGuard)', () => {
  it('renders for an admin', async () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
    render(
      <MemoryRouter initialEntries={['/monitoring/infrastructure']}>
        <AdminGuard>
          <InfrastructureMonitorPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'Infrastructure' })).toBeInTheDocument();
  });

  it('does NOT render for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u2', role: 'member' }, loading: false, isAdmin: false });
    render(
      <MemoryRouter initialEntries={['/monitoring/infrastructure']}>
        <AdminGuard>
          <InfrastructureMonitorPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(screen.queryByRole('heading', { name: 'Infrastructure' })).not.toBeInTheDocument();
  });
});

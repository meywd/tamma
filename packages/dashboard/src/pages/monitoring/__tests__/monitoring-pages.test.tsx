// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { MonitoringOverviewPage } from '../MonitoringOverviewPage.js';
import { SystemHealthPage } from '../SystemHealthPage.js';
import { monitoringRoutes } from '../index.js';
import { MONITORING_NAV_ITEMS } from '../monitoring-nav.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

function renderInRouter(node: React.ReactNode, path = '/monitoring/health') {
  return render(<MemoryRouter initialEntries={[path]}>{node}</MemoryRouter>);
}

describe('monitoringRoutes', () => {
  it('registers the overview + one route per monitoring section', () => {
    const paths = monitoringRoutes.map((r) => r.path);
    expect(paths).toEqual([
      '/monitoring',
      ...MONITORING_NAV_ITEMS.map((i) => i.to),
    ]);
    // Every route has a (guarded, lazy) element.
    expect(monitoringRoutes.every((r) => r.element != null)).toBe(true);
  });
});

describe('MonitoringOverviewPage', () => {
  it('links to every monitoring section', () => {
    renderInRouter(<MonitoringOverviewPage />, '/monitoring');
    expect(screen.getByRole('heading', { name: 'Monitoring' })).toBeInTheDocument();
    for (const item of MONITORING_NAV_ITEMS) {
      const link = screen.getByRole('link', { name: new RegExp(item.label) });
      expect(link).toHaveAttribute('href', item.to);
    }
  });
});

describe('monitoring page RBAC (mirrors the route AdminGuard)', () => {
  beforeEach(() => {
    // System Health composes existing endpoints on mount — stub fetch so the
    // page renders without hitting the network.
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(okResponse({}))),
    );
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it('renders the System Health overview for an admin', () => {
    mockUseCurrentUser.mockReturnValue({
      user: { id: 'u1', role: 'admin' },
      loading: false,
      isAdmin: true,
    });
    renderInRouter(
      <AdminGuard>
        <SystemHealthPage />
      </AdminGuard>,
    );
    expect(screen.getByRole('heading', { name: 'System Health' })).toBeInTheDocument();
    // The overview renders its composed sections, not the scaffold placeholder.
    expect(screen.getByRole('heading', { name: 'Services' })).toBeInTheDocument();
  });

  it('does NOT render the page for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({
      user: { id: 'u2', role: 'member' },
      loading: false,
      isAdmin: false,
    });
    renderInRouter(
      <AdminGuard>
        <SystemHealthPage />
      </AdminGuard>,
    );
    expect(screen.queryByRole('heading', { name: 'System Health' })).not.toBeInTheDocument();
  });
});

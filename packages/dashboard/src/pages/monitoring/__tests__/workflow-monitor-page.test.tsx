// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { WorkflowMonitorPage } from '../WorkflowMonitorPage.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

/** A run createdAt inside the default 24h window. */
const recent = new Date(Date.now() - 60_000).toISOString();

interface WireRun {
  id: string;
  definitionId: string;
  status: string;
  currentActivity: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
}

function run(over: Partial<WireRun>): WireRun {
  return {
    id: over.id ?? 'comp0001-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    definitionId: over.definitionId ?? 'def-1',
    status: over.status ?? 'completed',
    currentActivity: over.currentActivity ?? null,
    createdAt: over.createdAt ?? recent,
    startedAt: over.startedAt ?? recent,
    completedAt: over.completedAt ?? null,
    durationMs: over.durationMs ?? null,
  };
}

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

const DEFAULT_RUNS: WireRun[] = [
  run({ id: 'comp0001-aaaa-4aaa-8aaa-aaaaaaaaaaaa', definitionId: 'def-1', status: 'completed', durationMs: 120_000 }),
  run({ id: 'fail0002-bbbb-4bbb-8bbb-bbbbbbbbbbbb', definitionId: 'def-2', status: 'failed' }),
];

const DEFAULT_SUMMARY = {
  tenantId: 't-1',
  from: null,
  to: null,
  total: 2,
  byStatus: [
    { status: 'completed', count: 1 },
    { status: 'failed', count: 1 },
  ],
  byDefinition: [
    { definitionId: 'def-1', definitionName: 'code-workflow', count: 1 },
    { definitionId: 'def-2', definitionName: 'triage-workflow', count: 1 },
  ],
};

const fetchMock = vi.fn();

function stubApi(runs: WireRun[] = DEFAULT_RUNS, summary: unknown = DEFAULT_SUMMARY): void {
  fetchMock.mockImplementation((url: string | URL) => {
    const u = String(url);
    // /runs/summary must be matched BEFORE the /runs prefix.
    if (u.includes('/api/v1/runs/summary')) {
      return Promise.resolve(okResponse(summary));
    }
    if (u.includes('/api/v1/runs')) {
      return Promise.resolve(
        okResponse({ tenantId: 't-1', total: runs.length, page: 1, pageSize: 100, runs }),
      );
    }
    return Promise.resolve(okResponse({}));
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  stubApi();
  vi.stubGlobal('fetch', fetchMock);
  mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/monitoring/workflows']}>
      <WorkflowMonitorPage />
    </MemoryRouter>,
  );
}

describe('WorkflowMonitorPage', () => {
  it('renders workflow instances from /api/v1/runs and windowed counts from the summary', async () => {
    renderPage();

    // Run rows (short-id cells).
    expect(await screen.findByText('comp0001')).toBeInTheDocument();
    expect(screen.getByText('fail0002')).toBeInTheDocument();

    // Per-status + per-definition breakdown from the summary endpoint.
    expect(screen.getByText('completed · 1')).toBeInTheDocument();
    expect(screen.getByText('code-workflow · 1')).toBeInTheDocument();

    // Both existing endpoints were composed (list + summary).
    await waitFor(() => {
      const urls = fetchMock.mock.calls.map((c) => String(c[0]));
      expect(urls.some((u) => u.includes('/api/v1/runs/summary'))).toBe(true);
      expect(urls.some((u) => /\/api\/v1\/runs(\?|$)/.test(u))).toBe(true);
    });
  });

  it('filters the table by status', async () => {
    renderPage();
    await screen.findByText('comp0001');

    await userEvent.selectOptions(screen.getByLabelText('Status filter'), 'failed');

    expect(screen.getByText('fail0002')).toBeInTheDocument();
    expect(screen.queryByText('comp0001')).not.toBeInTheDocument();
  });

  it('filters the table by definition', async () => {
    renderPage();
    await screen.findByText('comp0001');

    await userEvent.selectOptions(screen.getByLabelText('Definition filter'), 'def-2');

    expect(screen.getByText('fail0002')).toBeInTheDocument();
    expect(screen.queryByText('comp0001')).not.toBeInTheDocument();
  });

  it('shows an empty state when there are no workflow instances', async () => {
    stubApi([], { ...DEFAULT_SUMMARY, total: 0, byStatus: [], byDefinition: [] });
    renderPage();
    expect(await screen.findByTestId('empty-state')).toBeInTheDocument();
  });
});

describe('WorkflowMonitorPage RBAC (inherited from the route AdminGuard)', () => {
  it('renders for an admin', async () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
    render(
      <MemoryRouter initialEntries={['/monitoring/workflows']}>
        <AdminGuard>
          <WorkflowMonitorPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'Workflows' })).toBeInTheDocument();
  });

  it('does NOT render for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u2', role: 'member' }, loading: false, isAdmin: false });
    render(
      <MemoryRouter initialEntries={['/monitoring/workflows']}>
        <AdminGuard>
          <WorkflowMonitorPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(screen.queryByRole('heading', { name: 'Workflows' })).not.toBeInTheDocument();
  });
});

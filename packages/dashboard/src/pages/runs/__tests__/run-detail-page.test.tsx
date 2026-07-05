// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { RunDetailPage } from '../RunDetailPage.js';

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

const RUN_ID = '11111111-aaaa-bbbb-cccc-000000000001';

const DETAIL = {
  id: RUN_ID,
  definitionId: 'def1',
  status: 'completed',
  currentActivity: null,
  createdAt: '2026-04-16T12:00:00.000Z',
  startedAt: '2026-04-16T12:00:00.000Z',
  completedAt: '2026-04-16T12:03:00.000Z',
  durationMs: 180000,
  provider: 'anthropic-claude',
  issueNumber: 258,
  repository: 'acme/widgets',
  prUrl: 'https://github.com/acme/widgets/pull/7',
  filesChanged: ['src/foo.ts', 'src/bar.ts'],
  totalCostUsd: 2.0,
  eventCount: 2,
  events: [
    {
      id: 'e1',
      type: 'AGENT.TASK.STARTED',
      tags: { correlationId: RUN_ID, provider: 'anthropic-claude' },
      data: {},
      createdAt: '2026-04-16T12:00:00.000Z',
      sequenceNumber: 1,
    },
    {
      id: 'e2',
      type: 'AGENT.TASK.SUCCESS',
      tags: { correlationId: RUN_ID },
      data: { costUsd: 2.0 },
      createdAt: '2026-04-16T12:03:00.000Z',
      sequenceNumber: 2,
    },
  ],
  logs: ['2026-04-16T12:00:00.000Z  AGENT.TASK.STARTED', '2026-04-16T12:03:00.000Z  AGENT.TASK.SUCCESS'],
};

function renderPage(entry = `/runs/${RUN_ID}`): void {
  render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route path="/runs/:runId" element={<RunDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('RunDetailPage', () => {
  it('renders run stats, cost, timeline and logs', async () => {
    fetchMock.mockResolvedValue(okResponse(DETAIL));
    renderPage();

    expect(await screen.findByText('acme/widgets · issue #258')).toBeInTheDocument();
    // Tenant's OWN recorded cost (never a platform margin).
    expect(screen.getByText('$2.00')).toBeInTheDocument();
    expect(screen.getByText('anthropic-claude')).toBeInTheDocument();
    expect(screen.getByText('AGENT.TASK.SUCCESS')).toBeInTheDocument();
    expect(screen.getByTestId('run-logs')).toHaveTextContent('AGENT.TASK.STARTED');
    // PR link + files changed.
    expect(screen.getByRole('link', { name: /pull\/7/ })).toBeInTheDocument();
    expect(screen.getByText('src/foo.ts')).toBeInTheDocument();
    // The detail request targets the run id, tenant resolved server-side.
    const urls = fetchMock.mock.calls.map((c) => String(c[0]));
    expect(urls.some((u) => u.includes(`/api/v1/runs/${RUN_ID}`))).toBe(true);
  });

  it('renders a friendly not-found state on a 404 run_not_found', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 404,
      json: async () => ({ error: 'run_not_found' }),
    } as unknown as Response);
    renderPage();
    expect(await screen.findByTestId('empty-state')).toHaveTextContent('Run not found');
  });

  it('surfaces an error banner on a non-404 failure', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 500,
      json: async () => ({ error: 'boom' }),
    } as unknown as Response);
    renderPage();
    expect(await screen.findByTestId('error-banner')).toHaveTextContent('boom');
  });
});

// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { RunsPage } from '../RunsPage.js';

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

const RUNS = {
  tenantId: 't1',
  total: 2,
  page: 1,
  pageSize: 100,
  runs: [
    {
      id: '11111111-aaaa-bbbb-cccc-000000000001',
      definitionId: 'def1',
      status: 'completed',
      currentActivity: 'done',
      createdAt: '2026-04-16T12:00:00.000Z',
      startedAt: '2026-04-16T12:00:00.000Z',
      completedAt: '2026-04-16T12:02:00.000Z',
      durationMs: 120000,
    },
    {
      id: '22222222-aaaa-bbbb-cccc-000000000002',
      definitionId: 'def1',
      status: 'failed',
      currentActivity: null,
      createdAt: '2026-04-16T11:00:00.000Z',
      startedAt: '2026-04-16T11:00:00.000Z',
      completedAt: '2026-04-16T11:01:00.000Z',
      durationMs: 60000,
    },
  ],
};

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/runs']}>
      <Routes>
        <Route path="/runs" element={<RunsPage />} />
        <Route path="/runs/:runId" element={<div>run detail opened</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('RunsPage', () => {
  it('renders the run list with status + duration', async () => {
    fetchMock.mockResolvedValue(okResponse(RUNS));
    renderPage();

    expect(await screen.findByTestId('data-table')).toBeInTheDocument();
    expect(screen.getByText('11111111')).toBeInTheDocument();
    // "completed" appears both in the status filter <option> and the row badge.
    expect(screen.getAllByText('completed').length).toBeGreaterThan(0);
    expect(screen.getByText('2m 0s')).toBeInTheDocument();
    const urls = fetchMock.mock.calls.map((c) => String(c[0]));
    expect(urls.some((u) => u.includes('/api/v1/runs'))).toBe(true);
  });

  it('filters runs by status', async () => {
    fetchMock.mockResolvedValue(okResponse(RUNS));
    renderPage();
    await screen.findByTestId('data-table');

    await userEvent.selectOptions(screen.getByLabelText('Status filter'), 'failed');

    expect(screen.queryByText('11111111')).not.toBeInTheDocument();
    expect(screen.getByText('22222222')).toBeInTheDocument();
  });

  it('navigates to the run detail on row click', async () => {
    fetchMock.mockResolvedValue(okResponse(RUNS));
    renderPage();
    await screen.findByTestId('data-table');

    await userEvent.click(screen.getByText('11111111'));
    expect(await screen.findByText('run detail opened')).toBeInTheDocument();
  });

  it('shows an empty state when there are no runs', async () => {
    fetchMock.mockResolvedValue(okResponse({ tenantId: 't1', total: 0, page: 1, pageSize: 100, runs: [] }));
    renderPage();
    expect(await screen.findByTestId('empty-state')).toHaveTextContent('No workflow runs yet');
  });

  it('surfaces an error banner when the request fails', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 500,
      json: async () => ({ error: 'boom' }),
    } as unknown as Response);
    renderPage();
    expect(await screen.findByTestId('error-banner')).toHaveTextContent('boom');
  });
});

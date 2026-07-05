// @vitest-environment jsdom
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { AgentMonitorPage } from '../AgentMonitorPage.js';
import type { EventSourceLike } from '../../../hooks/monitoring/useMonitoringSSE.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

interface WireEvent {
  id: string;
  type: string;
  tags: unknown;
  data: unknown;
  createdAt: string;
  issueNumber: number | null;
  sequenceNumber: number;
}

function wireEvent(over: Partial<WireEvent>): WireEvent {
  return {
    id: over.id ?? 'id-1',
    type: over.type ?? 'AGENT.RUN.STARTED',
    tags: over.tags ?? { correlationId: 'run-1' },
    data: over.data ?? null,
    createdAt: over.createdAt ?? '2026-07-05T12:00:00.000Z',
    issueNumber: over.issueNumber ?? null,
    sequenceNumber: over.sequenceNumber ?? 1,
  };
}

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

function pageBody(events: WireEvent[]): unknown {
  return { events, total: events.length, limit: 200, nextCursor: null, hasMore: false };
}

// One active run (run-1, no terminal), plus a completed run (run-2).
const ACTIVE_SET: WireEvent[] = [
  wireEvent({
    id: 'e1',
    type: 'AGENT.RUN.STARTED',
    createdAt: '2026-07-05T12:00:10.000Z',
    tags: { correlationId: 'run-1', agentId: 'coder', role: 'dev', provider: 'anthropic', model: 'sonnet' },
  }),
  wireEvent({
    id: 'e2',
    type: 'AGENT.TOOL_CALL.SUCCESS',
    createdAt: '2026-07-05T12:00:12.000Z',
    tags: { correlationId: 'run-1', agentId: 'coder' },
  }),
  wireEvent({
    id: 'e3',
    type: 'AGENT.RUN.STARTED',
    createdAt: '2026-07-05T12:00:00.000Z',
    tags: { correlationId: 'run-2', agentId: 'planner' },
  }),
  wireEvent({
    id: 'e4',
    type: 'AGENT.RUN.SUCCESS',
    createdAt: '2026-07-05T12:00:05.000Z',
    tags: { correlationId: 'run-2', agentId: 'planner' },
  }),
];

const fetchMock = vi.fn();

/** A fake EventSource factory for the live tail (mirrors the useMonitoringSSE seam). */
function makeStreamFactory() {
  const instances: Array<EventSourceLike & { close: ReturnType<typeof vi.fn> }> = [];
  const factory = vi.fn((_url: string): EventSourceLike => {
    const inst = { onopen: null, onmessage: null, onerror: null, close: vi.fn() } as EventSourceLike & {
      close: ReturnType<typeof vi.fn>;
    };
    instances.push(inst);
    return inst;
  });
  return { factory, instances };
}

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue(okResponse(pageBody(ACTIVE_SET)));
  vi.stubGlobal('fetch', fetchMock);
  mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(factory?: (url: string) => EventSourceLike): void {
  render(
    <MemoryRouter initialEntries={['/monitoring/agents']}>
      {factory ? <AgentMonitorPage eventSourceFactory={factory} /> : <AgentMonitorPage />}
    </MemoryRouter>,
  );
}

describe('AgentMonitorPage', () => {
  it('queries the AGENT.* family via the 4-7 prefix query', async () => {
    renderPage();
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const url = String(fetchMock.mock.calls[0]?.[0]);
    expect(url).toContain('/api/engine/events/query');
    expect(url).toContain('type=AGENT.');
    expect(url).toContain('prefix=true');
  });

  it('renders the activity summary and the active-runs table (STARTED w/o terminal)', async () => {
    renderPage();
    // Active run run-1 shows its agent; the completed run-2 is NOT active.
    expect(await screen.findByText('1 in flight')).toBeInTheDocument();
    const activeRegion = screen.getByRole('region', { name: 'Active runs' });
    expect(within(activeRegion).getByText('coder')).toBeInTheDocument();
    // Recent activity table lists the raw AGENT.* events.
    expect(screen.getByText('AGENT.TOOL_CALL.SUCCESS')).toBeInTheDocument();
  });

  it('shows an empty active-runs state when nothing is in flight', async () => {
    fetchMock.mockResolvedValue(
      okResponse(
        pageBody([
          wireEvent({ id: 'a', type: 'AGENT.RUN.STARTED', tags: { correlationId: 'r' } }),
          wireEvent({ id: 'b', type: 'AGENT.RUN.SUCCESS', tags: { correlationId: 'r' } }),
        ]),
      ),
    );
    renderPage();
    expect(await screen.findByText('No active runs')).toBeInTheDocument();
  });

  it('shows a global empty state when there is no agent activity', async () => {
    fetchMock.mockResolvedValue(okResponse(pageBody([])));
    renderPage();
    // Target the settled page-level empty state by its unique description
    // (distinct from the DataTable's transient empty message).
    expect(
      await screen.findByText('No AGENT.* events were recorded in the selected time range.'),
    ).toBeInTheDocument();
  });

  it('LIVE tail: taps a run and streams its tool-loop frames via the 32-23 stream', async () => {
    const { factory, instances } = makeStreamFactory();
    renderPage(factory);

    // Tap the active run.
    await userEvent.click(await screen.findByRole('button', { name: 'Tap live' }));

    // The panel mounts and subscribes to the tenant-scoped 32-23 tap URL.
    const panel = await screen.findByTestId('run-stream-panel');
    await waitFor(() => expect(factory).toHaveBeenCalled());
    expect(String(factory.mock.calls[0]?.[0])).toBe('/api/v1/llm/runs/run-1/stream');

    const inst = instances[0];
    act(() => inst?.onopen?.({}));
    expect(within(panel).getByText('Live')).toBeInTheDocument();

    // Drive live frames (bridged shape: "{kind}\n{json}").
    act(() =>
      inst?.onmessage?.({
        data: 'tool_call\n{"correlationId":"run-1","seq":1,"toolName":"grep","turn":1}',
      }),
    );
    act(() =>
      inst?.onmessage?.({
        data: 'tool_result\n{"correlationId":"run-1","seq":2,"toolName":"grep","success":true,"durationMs":42}',
      }),
    );

    expect(within(panel).getByText('grep (turn 1)')).toBeInTheDocument();
    expect(within(panel).getByText(/grep ok/)).toBeInTheDocument();

    // A terminal `final` closes the tail.
    act(() =>
      inst?.onmessage?.({
        data: 'final\n{"correlationId":"run-1","seq":3,"success":true,"totalTurns":2,"totalTokens":120}',
      }),
    );
    expect(within(panel).getByText('Completed')).toBeInTheDocument();
    expect(inst?.close).toHaveBeenCalled();
  });
});

describe('AgentMonitorPage RBAC (inherited from the route AdminGuard)', () => {
  it('renders for an admin', async () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
    render(
      <MemoryRouter initialEntries={['/monitoring/agents']}>
        <AdminGuard>
          <AgentMonitorPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'Agent Monitor' })).toBeInTheDocument();
  });

  it('does NOT render for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u2', role: 'member' }, loading: false, isAdmin: false });
    render(
      <MemoryRouter initialEntries={['/monitoring/agents']}>
        <AdminGuard>
          <AgentMonitorPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(screen.queryByRole('heading', { name: 'Agent Monitor' })).not.toBeInTheDocument();
  });
});

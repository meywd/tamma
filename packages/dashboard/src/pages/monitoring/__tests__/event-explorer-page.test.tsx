// @vitest-environment jsdom
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { EventExplorerPage } from '../EventExplorerPage.js';

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
    type: over.type ?? 'CODE.GENERATED.SUCCESS',
    tags: over.tags ?? { correlationId: 'run-1', userId: 'u1' },
    data: over.data ?? { note: 'ok' },
    createdAt: over.createdAt ?? '2026-07-05T12:00:00.000Z',
    issueNumber: over.issueNumber ?? 42,
    sequenceNumber: over.sequenceNumber ?? 1,
  };
}

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

function page(events: WireEvent[], over: Partial<{ total: number | null; nextCursor: number | null; hasMore: boolean }> = {}): unknown {
  return {
    events,
    total: over.total ?? events.length,
    limit: 50,
    nextCursor: over.nextCursor ?? null,
    hasMore: over.hasMore ?? false,
  };
}

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue(okResponse(page([])));
  vi.stubGlobal('fetch', fetchMock);
  mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/monitoring/events']}>
      <EventExplorerPage />
    </MemoryRouter>,
  );
}

describe('EventExplorerPage', () => {
  it('renders query results returned by the 4-7 query API', async () => {
    fetchMock.mockResolvedValueOnce(
      okResponse(
        page([
          wireEvent({ id: 'id-1', type: 'CODE.GENERATED.SUCCESS', sequenceNumber: 2 }),
          wireEvent({ id: 'id-2', type: 'CODE.GENERATED.FAILED', sequenceNumber: 1 }),
        ]),
      ),
    );

    renderPage();

    expect(await screen.findByText('CODE.GENERATED.SUCCESS')).toBeInTheDocument();
    expect(screen.getByText('CODE.GENERATED.FAILED')).toBeInTheDocument();
    expect(screen.getByTestId('event-footer')).toHaveTextContent('Showing 2 of 2 events');
  });

  it('drives the query from the filter form (type + prefix)', async () => {
    renderPage();
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());

    await userEvent.type(screen.getByLabelText('Event type'), 'AGENT.TASK');
    await userEvent.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => {
      const lastUrl = String(fetchMock.mock.calls[fetchMock.mock.calls.length - 1]?.[0]);
      expect(lastUrl).toContain('type=AGENT.TASK');
      expect(lastUrl).toContain('prefix=true');
    });
  });

  it('appends the next page via cursor pagination (Load more)', async () => {
    fetchMock
      .mockResolvedValueOnce(
        okResponse(
          page([wireEvent({ id: 'id-5', type: 'A.B.SUCCESS', sequenceNumber: 5 })], {
            nextCursor: 5,
            hasMore: true,
          }),
        ),
      )
      .mockResolvedValueOnce(
        okResponse(
          page([wireEvent({ id: 'id-4', type: 'A.B.FAILED', sequenceNumber: 4 })], {
            total: null,
            nextCursor: null,
            hasMore: false,
          }),
        ),
      );

    renderPage();
    const loadMore = await screen.findByRole('button', { name: 'Load more' });
    await userEvent.click(loadMore);

    expect(await screen.findByText('A.B.FAILED')).toBeInTheDocument();
    expect(screen.getByText('A.B.SUCCESS')).toBeInTheDocument();
    const secondUrl = String(fetchMock.mock.calls[1]?.[0]);
    expect(secondUrl).toContain('cursor=5');
  });

  it('opens the detail view when a row is clicked', async () => {
    fetchMock.mockResolvedValueOnce(
      okResponse(page([wireEvent({ id: 'id-9', type: 'CODE.GENERATED.SUCCESS', sequenceNumber: 9 })])),
    );

    renderPage();
    await userEvent.click(await screen.findByText('CODE.GENERATED.SUCCESS'));

    const panel = screen.getByTestId('event-detail-panel');
    expect(within(panel).getByText('id-9')).toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: 'Copy JSON' })).toBeInTheDocument();
  });

  it('shows an empty state when the query returns no events', async () => {
    fetchMock.mockResolvedValue(okResponse(page([])));
    renderPage();
    expect(await screen.findByTestId('empty-state')).toBeInTheDocument();
  });
});

describe('EventExplorerPage RBAC (inherited from the route AdminGuard)', () => {
  it('renders for an admin', async () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
    render(
      <MemoryRouter initialEntries={['/monitoring/events']}>
        <AdminGuard>
          <EventExplorerPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'Event Explorer' })).toBeInTheDocument();
  });

  it('does NOT render for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u2', role: 'member' }, loading: false, isAdmin: false });
    render(
      <MemoryRouter initialEntries={['/monitoring/events']}>
        <AdminGuard>
          <EventExplorerPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(screen.queryByRole('heading', { name: 'Event Explorer' })).not.toBeInTheDocument();
  });
});

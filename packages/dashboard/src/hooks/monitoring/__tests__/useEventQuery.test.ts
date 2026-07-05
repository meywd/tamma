// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { useEventQuery } from '../useEventQuery.js';

interface WireEvent {
  id: string;
  type: string;
  tags: unknown;
  data: unknown;
  createdAt: string;
  issueNumber: number | null;
  sequenceNumber: number;
}

function wireEvent(seq: number): WireEvent {
  return {
    id: `id-${seq}`,
    type: 'AGENT.TASK.SUCCESS',
    tags: { correlationId: 'run-1' },
    data: { seq },
    createdAt: '2026-07-05T12:00:00.000Z',
    issueNumber: seq,
    sequenceNumber: seq,
  };
}

function okResponse(body: unknown): Response {
  return {
    ok: true,
    status: 200,
    json: async () => body,
  } as unknown as Response;
}

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useEventQuery', () => {
  it('runQuery fetches the first page with includeTotal and the requested limit', async () => {
    fetchMock.mockResolvedValueOnce(
      okResponse({ events: [wireEvent(3), wireEvent(2)], total: 2, limit: 25, nextCursor: null, hasMore: false }),
    );

    const { result } = renderHook(() => useEventQuery());
    await act(async () => {
      await result.current.runQuery({ limit: 25, type: 'AGENT.TASK', typeMatch: 'prefix' });
    });

    expect(result.current.events).toHaveLength(2);
    expect(result.current.total).toBe(2);
    expect(result.current.hasMore).toBe(false);

    const url = String(fetchMock.mock.calls[0]?.[0]);
    expect(url).toContain('/api/engine/events/query');
    expect(url).toContain('type=AGENT.TASK');
    expect(url).toContain('prefix=true');
    expect(url).toContain('limit=25');
    expect(url).toContain('includeTotal=true');
  });

  it('loadMore appends the next page using the retained cursor', async () => {
    fetchMock
      .mockResolvedValueOnce(
        okResponse({ events: [wireEvent(5)], total: 3, limit: 1, nextCursor: 5, hasMore: true }),
      )
      .mockResolvedValueOnce(
        okResponse({ events: [wireEvent(4)], total: null, limit: 1, nextCursor: 4, hasMore: true }),
      );

    const { result } = renderHook(() => useEventQuery());
    await act(async () => {
      await result.current.runQuery({ limit: 1 });
    });
    expect(result.current.events).toHaveLength(1);
    expect(result.current.hasMore).toBe(true);

    await act(async () => {
      await result.current.loadMore();
    });

    expect(result.current.events.map((e) => e.sequenceNumber)).toEqual([5, 4]);
    const secondUrl = String(fetchMock.mock.calls[1]?.[0]);
    expect(secondUrl).toContain('cursor=5');
    expect(secondUrl).not.toContain('includeTotal=true');
  });

  it('surfaces an error message on a failed request', async () => {
    fetchMock.mockResolvedValueOnce({
      ok: false,
      status: 400,
      json: async () => ({ error: 'invalid time range' }),
    } as unknown as Response);

    const { result } = renderHook(() => useEventQuery());
    await act(async () => {
      await result.current.runQuery({ limit: 50 });
    });

    await waitFor(() => expect(result.current.error).toBe('invalid time range'));
    expect(result.current.events).toHaveLength(0);
  });
});

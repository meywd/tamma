// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { useTenantConventions } from './useTenantConventions.js';

interface MockResponseInit {
  ok?: boolean;
  status?: number;
  body?: unknown;
}

function mockResponse({ ok = true, status = 200, body = {} }: MockResponseInit = {}): Response {
  return {
    ok,
    status,
    statusText: ok ? 'OK' : 'ERR',
    json: async () => body,
  } as unknown as Response;
}

const SAMPLE_CONVENTIONS = [
  {
    id: '1',
    role: 'developer',
    action: 'implement',
    body: 'Implement conventions here',
    enabled: true,
    version: 1,
    source: 'system',
    isOverride: false,
    updatedAt: '2026-01-01T00:00:00.000Z',
  },
  {
    id: '2',
    role: 'tester',
    action: 'write-tests',
    body: 'Test conventions here',
    enabled: true,
    version: 2,
    source: 'tenant',
    isOverride: true,
    updatedAt: '2026-02-01T00:00:00.000Z',
  },
];

describe('useTenantConventions', () => {
  const originalFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.clearAllMocks();
  });

  it('loads conventions from GET /api/conventions', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      mockResponse({ body: SAMPLE_CONVENTIONS }),
    ) as typeof fetch;

    const { result } = renderHook(() => useTenantConventions());
    await waitFor(() => expect(result.current.loading).toBe(false));

    expect(result.current.conventions).toHaveLength(2);
    expect(result.current.overrideCount).toBe(1);
  });

  it('reports errors when the endpoint fails', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      mockResponse({ ok: false, status: 500, body: { error: 'Server error' } }),
    ) as typeof fetch;

    const { result } = renderHook(() => useTenantConventions());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).not.toBeNull();
  });

  it('upsertOverride PUTs to /api/conventions/:role/:action', async () => {
    globalThis.fetch = vi.fn().mockImplementation((_url: string, init?: RequestInit) => {
      if (init?.method === 'PUT') {
        return Promise.resolve(
          mockResponse({
            body: {
              id: '3',
              role: 'developer',
              action: 'implement',
              body: 'new body',
              enabled: true,
              version: 2,
              source: 'tenant',
              isOverride: true,
              updatedAt: '2026-03-01T00:00:00.000Z',
            },
          }),
        );
      }
      return Promise.resolve(mockResponse({ body: SAMPLE_CONVENTIONS }));
    }) as typeof fetch;

    const { result } = renderHook(() => useTenantConventions());
    await waitFor(() => expect(result.current.loading).toBe(false));

    let saved;
    await act(async () => {
      saved = await result.current.upsertOverride('developer', 'implement', {
        body: 'new body',
        enabled: true,
      });
    });
    expect(saved).toBeDefined();

    const fetchMock = globalThis.fetch as unknown as ReturnType<typeof vi.fn>;
    const putCall = fetchMock.mock.calls.find(
      (c) => (c[1] as RequestInit | undefined)?.method === 'PUT',
    );
    expect(putCall).toBeTruthy();
    expect(putCall![0]).toMatch(/\/api\/conventions\/developer\/implement$/);
  });

  it('deleteOverride DELETEs /api/conventions/:role/:action and returns true', async () => {
    globalThis.fetch = vi.fn().mockImplementation((_url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') {
        return Promise.resolve(mockResponse({ body: { message: 'deleted' } }));
      }
      return Promise.resolve(mockResponse({ body: SAMPLE_CONVENTIONS }));
    }) as typeof fetch;

    const { result } = renderHook(() => useTenantConventions());
    await waitFor(() => expect(result.current.loading).toBe(false));

    let ok = false;
    await act(async () => {
      ok = await result.current.deleteOverride('tester', 'write-tests');
    });
    expect(ok).toBe(true);
  });

  it('overrideCount counts only isOverride=true conventions', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      mockResponse({ body: SAMPLE_CONVENTIONS }),
    ) as typeof fetch;

    const { result } = renderHook(() => useTenantConventions());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.overrideCount).toBe(1);
  });
});

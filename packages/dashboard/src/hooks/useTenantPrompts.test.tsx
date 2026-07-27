// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { useTenantPrompts } from './useTenantPrompts.js';

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

describe('useTenantPrompts', () => {
  const originalFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.clearAllMocks();
  });

  it('loads and merges /api/prompts/system defaults with /api/prompts user overrides', async () => {
    const systemDefaults = {
      roleActionTemplates: [
        {
          role: 'developer',
          action: 'implement',
          template: 'sys impl',
          systemPrompt: 'sys',
          variables: ['role'],
          enableTools: true,
          maxTokens: 4096,
          source: 'system',
        },
        {
          role: 'tester',
          action: 'write-tests',
          template: 'sys tests',
          systemPrompt: 'sys',
          variables: [],
          enableTools: false,
          maxTokens: 4096,
          source: 'system',
        },
      ],
      systemPrompts: {},
    };
    const userOverrides = [
      {
        role: 'developer',
        action: 'implement',
        template: 'user impl',
        systemPrompt: 'sys',
        variables: ['role'],
        enableTools: true,
        maxTokens: 8192,
        source: 'user',
      },
    ];

    globalThis.fetch = vi.fn().mockImplementation((url: string) => {
      if (url.endsWith('/api/prompts/system')) {
        return Promise.resolve(mockResponse({ body: systemDefaults }));
      }
      if (url.endsWith('/api/prompts')) {
        return Promise.resolve(mockResponse({ body: userOverrides }));
      }
      return Promise.resolve(mockResponse({ ok: false, status: 404 }));
    }) as typeof fetch;

    const { result } = renderHook(() => useTenantPrompts());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.prompts).toHaveLength(2);
    const impl = result.current.prompts.find((p) => p.action === 'implement');
    const tests = result.current.prompts.find((p) => p.action === 'write-tests');
    expect(impl?.source).toBe('user');
    expect(impl?.template).toBe('user impl');
    expect(impl?.maxTokens).toBe(8192);
    expect(tests?.source).toBe('system');
    expect(result.current.overrideCount).toBe(1);
  });

  it('reports errors when the system endpoint fails', async () => {
    globalThis.fetch = vi
      .fn()
      .mockResolvedValueOnce(mockResponse({ ok: false, status: 500 })) // system
      .mockResolvedValue(mockResponse({ body: [] })) as typeof fetch;
    const { result } = renderHook(() => useTenantPrompts());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).not.toBeNull();
  });

  it('upsertOverride PUTs to /api/prompts/:role/:action', async () => {
    globalThis.fetch = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (init?.method === 'PUT') {
        return Promise.resolve(
          mockResponse({
            body: {
              role: 'developer',
              action: 'implement',
              template: 'new',
              systemPrompt: 'sys',
              variables: [],
              enableTools: false,
              maxTokens: 4096,
              source: 'user',
            },
          }),
        );
      }
      if (url.endsWith('/api/prompts/system')) {
        return Promise.resolve(
          mockResponse({ body: { roleActionTemplates: [], systemPrompts: {} } }),
        );
      }
      return Promise.resolve(mockResponse({ body: [] }));
    }) as typeof fetch;

    const { result } = renderHook(() => useTenantPrompts());
    await waitFor(() => expect(result.current.loading).toBe(false));
    let saved;
    await act(async () => {
      saved = await result.current.upsertOverride('developer', 'implement', {
        template: 'new',
      });
    });
    expect(saved).toBeDefined();
    const fetchMock = globalThis.fetch as unknown as ReturnType<typeof vi.fn>;
    const putCall = fetchMock.mock.calls.find(
      (c) => (c[1] as RequestInit | undefined)?.method === 'PUT',
    );
    expect(putCall).toBeTruthy();
    expect(putCall![0]).toMatch(/\/api\/prompts\/developer\/implement$/);
  });

  it('deleteOverride DELETEs /api/prompts/:role/:action and returns true', async () => {
    globalThis.fetch = vi.fn().mockImplementation((_url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') {
        return Promise.resolve(mockResponse({ body: { message: 'deleted' } }));
      }
      return Promise.resolve(
        mockResponse({ body: { roleActionTemplates: [], systemPrompts: {} } }),
      );
    }) as typeof fetch;
    const { result } = renderHook(() => useTenantPrompts());
    await waitFor(() => expect(result.current.loading).toBe(false));
    let ok = false;
    await act(async () => {
      ok = await result.current.deleteOverride('developer', 'implement');
    });
    expect(ok).toBe(true);
  });

  it('renderPreview POSTs to /api/prompts/:role/:action/render', async () => {
    globalThis.fetch = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        return Promise.resolve(
          mockResponse({
            body: {
              role: 'developer',
              action: 'implement',
              version: 1,
              renderedTemplate: 'Hello world',
              renderedSystemPrompt: 'sys',
              enableTools: false,
              maxTokens: 4096,
              unresolvedVariables: [],
            },
          }),
        );
      }
      if (url.endsWith('/api/prompts/system')) {
        return Promise.resolve(
          mockResponse({ body: { roleActionTemplates: [], systemPrompts: {} } }),
        );
      }
      return Promise.resolve(mockResponse({ body: [] }));
    }) as typeof fetch;

    const { result } = renderHook(() => useTenantPrompts());
    await waitFor(() => expect(result.current.loading).toBe(false));
    let preview;
    await act(async () => {
      preview = await result.current.renderPreview('developer', 'implement', { name: 'world' });
    });
    expect(preview).toMatchObject({ renderedTemplate: 'Hello world' });
  });
});

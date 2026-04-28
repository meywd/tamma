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
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    vi.clearAllMocks();
  });

  it('loads and merges /api/prompts/system defaults with /api/prompts user overrides', async () => {
    const systemDefaults = {
      RoleActionTemplates: [
        {
          Role: 'developer',
          Action: 'implement',
          Template: 'sys impl',
          SystemPrompt: 'sys',
          Variables: ['role'],
          EnableTools: true,
          MaxTokens: 4096,
          Source: 'system',
        },
        {
          Role: 'tester',
          Action: 'write-tests',
          Template: 'sys tests',
          SystemPrompt: 'sys',
          Variables: [],
          EnableTools: false,
          MaxTokens: 4096,
          Source: 'system',
        },
      ],
      SystemPrompts: {},
      ActionDefaults: {},
    };
    const userOverrides = [
      {
        Role: 'developer',
        Action: 'implement',
        Template: 'user impl',
        SystemPrompt: 'sys',
        Variables: ['role'],
        EnableTools: true,
        MaxTokens: 8192,
        Source: 'user',
      },
    ];

    global.fetch = vi.fn().mockImplementation((url: string) => {
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
    global.fetch = vi
      .fn()
      .mockResolvedValueOnce(mockResponse({ ok: false, status: 500 })) // system
      .mockResolvedValue(mockResponse({ body: [] })) as typeof fetch;
    const { result } = renderHook(() => useTenantPrompts());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).not.toBeNull();
  });

  it('upsertOverride PUTs to /api/prompts/:role/:action', async () => {
    global.fetch = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (init?.method === 'PUT') {
        return Promise.resolve(
          mockResponse({
            body: {
              Role: 'developer',
              Action: 'implement',
              Template: 'new',
              SystemPrompt: 'sys',
              Variables: [],
              EnableTools: false,
              MaxTokens: 4096,
              Source: 'user',
            },
          }),
        );
      }
      if (url.endsWith('/api/prompts/system')) {
        return Promise.resolve(
          mockResponse({ body: { RoleActionTemplates: [], SystemPrompts: {}, ActionDefaults: {} } }),
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
    const fetchMock = global.fetch as unknown as ReturnType<typeof vi.fn>;
    const putCall = fetchMock.mock.calls.find(
      (c) => (c[1] as RequestInit | undefined)?.method === 'PUT',
    );
    expect(putCall).toBeTruthy();
    expect(putCall![0]).toMatch(/\/api\/prompts\/developer\/implement$/);
  });

  it('deleteOverride DELETEs /api/prompts/:role/:action and returns true', async () => {
    global.fetch = vi.fn().mockImplementation((_url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') {
        return Promise.resolve(mockResponse({ body: { message: 'deleted' } }));
      }
      return Promise.resolve(
        mockResponse({ body: { RoleActionTemplates: [], SystemPrompts: {}, ActionDefaults: {} } }),
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
    global.fetch = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        return Promise.resolve(
          mockResponse({
            body: {
              Role: 'developer',
              Action: 'implement',
              Version: 1,
              RenderedTemplate: 'Hello world',
              RenderedSystemPrompt: 'sys',
              EnableTools: false,
              MaxTokens: 4096,
              UnresolvedVariables: [],
            },
          }),
        );
      }
      if (url.endsWith('/api/prompts/system')) {
        return Promise.resolve(
          mockResponse({ body: { RoleActionTemplates: [], SystemPrompts: {}, ActionDefaults: {} } }),
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

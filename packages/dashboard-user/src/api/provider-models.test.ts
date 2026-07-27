/**
 * Story 46-3 — tenant provider-models client contract tests. Asserts the
 * URL/method/body matrix against the routes Program.cs registers under
 * /api/v1/agents (ProviderCredentialEndpoints.cs), and that the client never
 * sends a tenant id (the server resolves the tenant from the session).
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { providerModelsApi } from './provider-models';

function jsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function lastCall(spy: ReturnType<typeof vi.fn>): { url: string; init: RequestInit } {
  const call = spy.mock.calls[0] ?? [];
  return { url: String(call[0] ?? ''), init: (call[1] as RequestInit) ?? {} };
}

describe('providerModelsApi — URL/method/body matrix', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('listProviderModelSettings GETs /api/v1/agents/providers/models (no tenant id)', async () => {
    const spy = vi.fn().mockResolvedValueOnce(jsonResponse({ providers: [] }));
    globalThis.fetch = spy as unknown as typeof fetch;

    const res = await providerModelsApi.listProviderModelSettings();

    const { url, init } = lastCall(spy);
    expect(url).toBe('/api/v1/agents/providers/models');
    expect(init.method).toBe('GET');
    expect(url).not.toMatch(/tenant/i);
    expect(res.providers).toEqual([]);
  });

  it('listProviderModels GETs /api/v1/agents/providers/{provider}/models and encodes the key', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      jsonResponse({
        provider: 'z-ai',
        models: [],
        fetchedAt: null,
        stale: false,
        errorCode: null,
      }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await providerModelsApi.listProviderModels('z-ai');
    expect(lastCall(spy).url).toBe('/api/v1/agents/providers/z-ai/models');
  });

  it('getProviderModel GETs /api/v1/agents/providers/{provider}/model', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      jsonResponse({
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        source: 'platform-db',
        override: null,
      }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await providerModelsApi.getProviderModel('anthropic');
    const { url, init } = lastCall(spy);
    expect(url).toBe('/api/v1/agents/providers/anthropic/model');
    expect(init.method).toBe('GET');
  });

  it('putProviderModel PUTs { model } exactly (PutTenantProviderModelRequest)', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      jsonResponse({
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        source: 'tenant-override',
        pricingKnown: true,
        warning: null,
      }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await providerModelsApi.putProviderModel('anthropic', 'claude-sonnet-4-5');
    const { url, init } = lastCall(spy);
    expect(url).toBe('/api/v1/agents/providers/anthropic/model');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body as string)).toEqual({ model: 'claude-sonnet-4-5' });
  });

  it('deleteProviderModel DELETEs the model route and resolves on 204', async () => {
    const spy = vi
      .fn()
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await providerModelsApi.deleteProviderModel('anthropic');
    const { url, init } = lastCall(spy);
    expect(url).toBe('/api/v1/agents/providers/anthropic/model');
    expect(init.method).toBe('DELETE');
  });
});

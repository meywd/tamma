/**
 * Actions Policy client contract tests. Assert every method builds the right
 * URL/method/body, and that a non-2xx surfaces the API's error/code shape.
 * Mirrors the admin-pricing-client test convention (fetch spy per case).
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { actionsPolicyApi, type ApiError } from './actions-policy-api-client';

function mockJson<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function lastCall(spy: ReturnType<typeof vi.fn>): [string, RequestInit | undefined] {
  const call = spy.mock.calls[0] ?? [];
  return [call[0] as string, call[1] as RequestInit | undefined];
}

describe('actionsPolicyApi — URL/method/body matrix', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('getDial GETs /api/actions/dial', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ min: 1, max: 100, alwaysHuman: 101, default: 70 }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    const dial = await actionsPolicyApi.getDial();

    const [url] = lastCall(spy);
    expect(url).toBe('/api/actions/dial');
    expect(dial.alwaysHuman).toBe(101);
  });

  it('getCatalog GETs /api/actions/catalog', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson([]));
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.getCatalog();

    const [url] = lastCall(spy);
    expect(url).toBe('/api/actions/catalog');
  });

  it('getPolicy GETs /api/actions/policy without a level', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ dial: {}, groups: [], actions: [] }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.getPolicy();

    const [url] = lastCall(spy);
    expect(url).toBe('/api/actions/policy');
  });

  it('getPolicy forwards the what-if level as a query param', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ dial: {}, groups: [], actions: [] }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.getPolicy(85);

    const [url] = lastCall(spy);
    expect(url).toBe('/api/actions/policy?level=85');
  });

  it('setActionThreshold PUTs minAutonomy to /policy/actions/{ns}/{key}/threshold', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ key: 'scw:pr.merge', minAutonomy: 1, dialAtMint: 70 }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.setActionThreshold('scw:pr.merge', 1);

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/actions/scw/pr.merge/threshold');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(init?.body as string)).toEqual({ minAutonomy: 1 });
  });

  it('setActionEnforce PUTs enforce to …/enforce', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ key: 'scw:pr.merge', field: 'enforce', value: true }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.setActionEnforce('scw:pr.merge', true);

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/actions/scw/pr.merge/enforce');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(init?.body as string)).toEqual({ enforce: true });
  });

  it('setActionEnabled PUTs enabled to …/enabled', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ key: 'scw:pr.merge', field: 'enabled', value: false }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.setActionEnabled('scw:pr.merge', false);

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/actions/scw/pr.merge/enabled');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(init?.body as string)).toEqual({ enabled: false });
  });

  it('setActionRoles PUTs allowedRoles to …/roles', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ key: 'scw:pr.merge', field: 'allowedRoles', value: ['tenant_admin'] }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.setActionRoles('scw:pr.merge', ['tenant_admin']);

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/actions/scw/pr.merge/roles');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(init?.body as string)).toEqual({ allowedRoles: ['tenant_admin'] });
  });

  it('deleteActionOverride DELETEs /policy/actions/{ns}/{key}', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ message: 'gone', nowResolvesTo: 90, source: 'shipped', reason: 'the next tier applies' }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.deleteActionOverride('scw:pr.merge');

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/actions/scw/pr.merge');
    expect(init?.method).toBe('DELETE');
  });

  it('splits the wire key at the FIRST colon only (ns vs dotted key)', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ key: 'effect:engine.channel-outbox.enqueue', field: 'enabled', value: false }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.setActionEnabled('effect:engine.channel-outbox.enqueue', false);

    const [url] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/actions/effect/engine.channel-outbox.enqueue/enabled');
  });

  it('setGroupThreshold PUTs minAutonomy to /policy/groups/{group}/threshold', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ group: 'source-control-write', minAutonomy: 95 }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.setGroupThreshold('source-control-write', 95);

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/groups/source-control-write/threshold');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(init?.body as string)).toEqual({ minAutonomy: 95 });
  });

  it('deleteGroupOverride DELETEs /policy/groups/{group}', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ message: 'gone' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.deleteGroupOverride('source-control-write');

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/groups/source-control-write');
    expect(init?.method).toBe('DELETE');
  });

  it('resetPolicy POSTs an empty body for a full reset', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ removed: 3 }));
    globalThis.fetch = spy as unknown as typeof fetch;

    const result = await actionsPolicyApi.resetPolicy();

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/actions/policy/reset');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(init?.body as string)).toEqual({});
    expect(result.removed).toBe(3);
  });

  it('resetPolicy POSTs named targets for a bulk revoke', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ removed: 1, deleted: ['scw:pr.merge'], missing: [], unknown: [] }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.resetPolicy(['scw:pr.merge']);

    const [, init] = lastCall(spy);
    expect(JSON.parse(init?.body as string)).toEqual({ targets: ['scw:pr.merge'] });
  });

  it('listAuthorizations GETs /api/actions/authorizations with the state', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ state: 'pending', count: 0, authorizations: [] }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.listAuthorizations('pending');

    const [url] = lastCall(spy);
    expect(url).toBe('/api/actions/authorizations?state=pending');
  });

  it('decideAuthorization POSTs the decision to …/{id}/decide', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({
        id: 'aaaaaaaa-0000-0000-0000-000000000001',
        state: 'granted',
        correlationId: 'run-1',
        targetKind: 'action',
        targetKey: 'effect:deploy.production',
        decidedAtUtc: '2026-08-21T00:00:00Z',
        decidedByUserId: 'u-1',
        expiresAtUtc: null,
        reason: null,
      }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await actionsPolicyApi.decideAuthorization(
      'aaaaaaaa-0000-0000-0000-000000000001',
      'granted',
      'looks safe',
    );

    const [url, init] = lastCall(spy);
    expect(url).toBe(
      '/api/actions/authorizations/aaaaaaaa-0000-0000-0000-000000000001/decide',
    );
    expect(init?.method).toBe('POST');
    expect(JSON.parse(init?.body as string)).toEqual({
      decision: 'granted',
      reason: 'looks safe',
    });
  });

  it('surfaces the API error message and code on a non-2xx', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson(
        { error: 'already automated at dial 90', code: 'ACTION_POLICY.LEVEL_OWNED' },
        409,
      ),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    let caught: ApiError | null = null;
    try {
      await actionsPolicyApi.setActionThreshold('scw:pr.merge', 1);
    } catch (err) {
      caught = err as ApiError;
    }

    expect(caught).not.toBeNull();
    expect(caught?.message).toBe('already automated at dial 90');
    expect(caught?.status).toBe(409);
    expect(caught?.code).toBe('ACTION_POLICY.LEVEL_OWNED');
  });
});

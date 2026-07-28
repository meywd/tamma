/**
 * Tests for the tenant-scope alerts API module. Focus areas:
 *   - URL shape includes the path tenantId so backend membership gate fires.
 *   - hasPlaintextCredential() catches the common credential field names.
 *   - createTenantChannel throws BEFORE POST when config has a banned field —
 *     server is still authoritative, but clients shouldn't even try.
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  listTenantAlerts,
  getTenantAlert,
  acknowledgeTenantAlert,
  resolveTenantAlert,
  listTenantChannels,
  createTenantChannel,
  updateTenantChannel,
  hasPlaintextCredential,
} from './alerts';

function mockJson<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

describe('alerts API — URL shape', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('listTenantAlerts hits /api/v1/orgs/{tenantId}/alerts', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ items: [], count: 0, limit: 50 }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await listTenantAlerts('tnt-A', { limit: 50 });

    const url = spy.mock.calls[0]?.[0] as string;
    expect(url).toContain('/api/v1/orgs/tnt-A/alerts');
    expect(url).toContain('limit=50');
  });

  // Story 45-0 regression pins: absent filters must NOT leak into the query
  // string (no `status=undefined`), whether the keys are genuinely absent or
  // present-but-undefined (the shape a component builds from optional state).
  it('omits absent filters from the query string', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ items: [], count: 0, limit: 25 }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await listTenantAlerts('tnt-A', { limit: 25 });

    const url = spy.mock.calls[0]?.[0] as string;
    expect(url).toBe('/api/v1/orgs/tnt-A/alerts?limit=25');
  });

  it('omits explicitly-undefined filters exactly like absent ones', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ items: [], count: 0, limit: 25 }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await listTenantAlerts('tnt-A', {
      status: undefined,
      severity: undefined,
      sinceDays: undefined,
      limit: 25,
    });

    const url = spy.mock.calls[0]?.[0] as string;
    expect(url).toBe('/api/v1/orgs/tnt-A/alerts?limit=25');
  });

  it('listTenantAlerts forwards severity + status + since filters', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ items: [], count: 0, limit: 50 }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await listTenantAlerts('tnt-A', {
      severity: 'critical',
      status: 'active',
      sinceDays: 7,
    });

    const url = spy.mock.calls[0]?.[0] as string;
    expect(url).toContain('severity=critical');
    expect(url).toContain('status=active');
    expect(url).toContain('since=');
  });

  it('getTenantAlert hits /alerts/{id} under the tenant path', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ alert: { id: 'A' }, deliveryAttempts: [] }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await getTenantAlert('tnt-A', 'alert-1');

    expect(spy.mock.calls[0]?.[0]).toContain(
      '/api/v1/orgs/tnt-A/alerts/alert-1',
    );
  });

  it('acknowledge POSTs to /alerts/{id}/acknowledge', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ id: 'A' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await acknowledgeTenantAlert('tnt-A', 'alert-1', 'seen');

    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toContain('/api/v1/orgs/tnt-A/alerts/alert-1/acknowledge');
    expect((init as RequestInit).method).toBe('POST');
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.note).toBe('seen');
  });

  it('resolve POSTs to /alerts/{id}/resolve with resolution body', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ id: 'A' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await resolveTenantAlert('tnt-A', 'alert-1', 'fixed it');

    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toContain('/api/v1/orgs/tnt-A/alerts/alert-1/resolve');
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.resolution).toBe('fixed it');
  });

  it('listTenantChannels hits /alert-channels under tenant path', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ items: [], count: 0 }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await listTenantChannels('tnt-A');

    expect(spy.mock.calls[0]?.[0]).toContain(
      '/api/v1/orgs/tnt-A/alert-channels',
    );
  });

  it('updateTenantChannel PATCHes through ApiClient', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ id: 'ch-1' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await updateTenantChannel('tnt-A', 'ch-1', { name: 'renamed' });

    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toContain('/api/v1/orgs/tnt-A/alert-channels/ch-1');
    expect((init as RequestInit).method).toBe('PATCH');
    expect((init as RequestInit).credentials).toBe('include');
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.name).toBe('renamed');
  });

  // Story 45-1 AC8: the PATCH previously used a bare fetch and was the ONE
  // call in the app that missed the single-shot refresh-on-401 retry. This
  // test is what makes the fix visible — the happy path is unchanged.
  it('updateTenantChannel refreshes and retries once on 401', async () => {
    const spy = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 401 })) // original PATCH
      .mockResolvedValueOnce(new Response('', { status: 200 })) // refresh
      .mockResolvedValueOnce(mockJson({ id: 'ch-1', name: 'renamed' })); // retried PATCH
    globalThis.fetch = spy as unknown as typeof fetch;

    const channel = await updateTenantChannel('tnt-A', 'ch-1', { name: 'renamed' });

    expect(spy).toHaveBeenCalledTimes(3);
    expect(spy.mock.calls[0]?.[0]).toContain('/api/v1/orgs/tnt-A/alert-channels/ch-1');
    expect(spy.mock.calls[1]?.[0]).toContain('/api/v1/auth/refresh');
    expect((spy.mock.calls[1]?.[1] as RequestInit).method).toBe('POST');
    expect(spy.mock.calls[2]?.[0]).toContain('/api/v1/orgs/tnt-A/alert-channels/ch-1');
    expect((spy.mock.calls[2]?.[1] as RequestInit).method).toBe('PATCH');
    expect(channel).toMatchObject({ id: 'ch-1', name: 'renamed' });
  });
});

describe('alerts API — credential pre-flight (invariant)', () => {
  it.each([
    ['{"password":"p"}', true],
    ['{"webhookUrl":"https://x"}', true],
    ['{"WEBHOOK_URL":"https://x"}', true],
    ['{"routingKey":"k"}', true],
    ['{"apiKey":"k"}', true],
    ['{"token":"t"}', true],
    ['{"authToken":"t"}', true],
    ['{"secret":"s"}', true],
    ['{}', false],
    ['{"toAddress":"ops@x.dev"}', false],
    ['{"subjectPrefix":"[ALERT] "}', false],
    ['', false],
    ['   ', false],
    ['not json', false],
  ])('hasPlaintextCredential(%j) === %s', (json, expected) => {
    expect(hasPlaintextCredential(json)).toBe(expected);
  });

  it('createTenantChannel throws BEFORE fetch when config leaks a credential', async () => {
    const spy = vi.fn();
    globalThis.fetch = spy as unknown as typeof fetch;

    await expect(
      createTenantChannel('tnt-A', {
        name: 'bad',
        channelType: 'slack',
        config: '{"webhookUrl":"https://hooks.slack.com/x"}',
        credentialsSecretId: 'fake-id',
      }),
    ).rejects.toThrow(/plaintext credentials/i);

    expect(spy).not.toHaveBeenCalled();
  });

  it('createTenantChannel POSTs when config is clean', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ id: 'new' }, 201));
    globalThis.fetch = spy as unknown as typeof fetch;

    await createTenantChannel('tnt-A', {
      name: 'ops',
      channelType: 'email',
      config: '{"toAddress":"ops@tamma.dev"}',
    });

    expect(spy).toHaveBeenCalledTimes(1);
    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toContain('/api/v1/orgs/tnt-A/alert-channels');
    expect((init as RequestInit).method).toBe('POST');
  });
});

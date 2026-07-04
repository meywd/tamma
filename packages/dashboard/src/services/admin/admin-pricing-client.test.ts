/**
 * Story 34-9 — admin pricing client contract tests. Assert every method builds
 * the right URL/method/body, a non-2xx surfaces AdminPricingApiError, and the
 * 409 deprecate path parses affectedTenantCount.
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  adminPricingApi,
  AdminPricingApiError,
  metricKeyLabel,
  METRIC_KEYS,
} from './admin-pricing-client';

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

describe('adminPricingApi — URL/method/body matrix', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('getOverview GETs /api/admin/pricing/overview', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ plans: [], margins: {}, totals: {} }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await adminPricingApi.getOverview();

    const [url] = lastCall(spy);
    expect(url).toBe('/api/admin/pricing/overview');
  });

  it('listPlans forwards status/isCustom/tenantId query params', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ plans: [] }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await adminPricingApi.listPlans({ status: 'active', isCustom: false, tenantId: 'tnt-1' });

    const [url] = lastCall(spy);
    expect(url).toContain('/api/admin/pricing/plans?');
    expect(url).toContain('status=active');
    expect(url).toContain('isCustom=false');
    expect(url).toContain('tenantId=tnt-1');
  });

  it('createPlan POSTs the body to /api/admin/pricing/plans', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ planId: 'p', slug: 'pro' }, 201));
    globalThis.fetch = spy as unknown as typeof fetch;

    await adminPricingApi.createPlan({
      slug: 'pro',
      displayName: 'Pro',
      billingInterval: 'monthly',
      entitlements: [{ metricKey: 'seats', limitValue: 10, period: 'monthly', overageMode: 'block' }],
    });

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/admin/pricing/plans');
    expect(init?.method).toBe('POST');
    const body = JSON.parse(init?.body as string);
    expect(body.slug).toBe('pro');
    expect(body.entitlements[0].metricKey).toBe('seats');
  });

  it('versionPlan PUTs to /api/admin/pricing/plans/{slug}', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ planId: 'p', slug: 'pro', version: 2 }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await adminPricingApi.versionPlan('pro', { displayName: 'Pro v2', entitlements: null });

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/admin/pricing/plans/pro');
    expect(init?.method).toBe('PUT');
    const body = JSON.parse(init?.body as string);
    expect(body.entitlements).toBeNull(); // null ⇒ copy prior version
  });

  it('mintCustomPlan POSTs to /api/admin/pricing/plans/custom', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ planId: 'cp', slug: 'custom-x', isCustom: true }, 201));
    globalThis.fetch = spy as unknown as typeof fetch;

    await adminPricingApi.mintCustomPlan({
      tenantId: 'tnt-9',
      displayName: 'Bespoke',
      billingInterval: 'annual',
    });

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/admin/pricing/plans/custom');
    expect(init?.method).toBe('POST');
    const body = JSON.parse(init?.body as string);
    expect(body.tenantId).toBe('tnt-9');
    expect(body.makePublic).toBeUndefined(); // never asks for public visibility
  });

  it('deprecateVersion DELETEs with force flag and returns deprecated on 204', async () => {
    const spy = vi.fn().mockResolvedValueOnce(new Response(null, { status: 204 }));
    globalThis.fetch = spy as unknown as typeof fetch;

    const result = await adminPricingApi.deprecateVersion('pro', 3, true);

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/admin/pricing/plans/pro/versions/3?force=true');
    expect(init?.method).toBe('DELETE');
    expect(result.deprecated).toBe(true);
  });

  it('deprecateVersion surfaces 409 with affectedTenantCount', async () => {
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson(
        {
          error: 'PLAN.DEPRECATE.HAS_ASSIGNMENTS',
          message: 'has assignments',
          affectedTenantCount: 4,
        },
        409,
      ),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    await expect(adminPricingApi.deprecateVersion('pro', 3, false)).rejects.toMatchObject({
      status: 409,
    });

    // Re-run to inspect the thrown body.
    const spy2 = vi.fn().mockResolvedValueOnce(
      mockJson({ affectedTenantCount: 4 }, 409),
    );
    globalThis.fetch = spy2 as unknown as typeof fetch;
    try {
      await adminPricingApi.deprecateVersion('pro', 3, false);
      throw new Error('should have thrown');
    } catch (err) {
      expect(err).toBeInstanceOf(AdminPricingApiError);
      expect((err as AdminPricingApiError).status).toBe(409);
      expect((err as AdminPricingApiError).body).toMatchObject({ affectedTenantCount: 4 });
    }
  });

  it('listMargins GETs /api/admin/pricing/margins', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ policies: [] }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await adminPricingApi.listMargins();

    const [url] = lastCall(spy);
    expect(url).toBe('/api/admin/pricing/margins');
  });

  it('versionMargin PUTs the policy body', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ policy: {}, supersededPolicyId: null }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await adminPricingApi.versionMargin({ scope: 'global', markupMultiplier: 1.5 });

    const [url, init] = lastCall(spy);
    expect(url).toBe('/api/admin/pricing/margins');
    expect(init?.method).toBe('PUT');
    const body = JSON.parse(init?.body as string);
    expect(body.scope).toBe('global');
    expect(body.markupMultiplier).toBe(1.5);
  });

  it('surfaces a typed AdminPricingApiError on non-2xx', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ error: 'boom' }, 500));
    globalThis.fetch = spy as unknown as typeof fetch;

    await expect(adminPricingApi.getOverview()).rejects.toBeInstanceOf(AdminPricingApiError);
  });
});

describe('metricKeyLabel', () => {
  it('maps numeric ordinals to snake_case keys', () => {
    expect(metricKeyLabel(0)).toBe('agents');
    expect(metricKeyLabel(3)).toBe('seats');
    expect(metricKeyLabel(METRIC_KEYS.length - 1)).toBe('benchmark_retention_days');
  });

  it('passes through a string metric key unchanged', () => {
    expect(metricKeyLabel('llm_tokens')).toBe('llm_tokens');
  });

  it('falls back for an out-of-range ordinal', () => {
    expect(metricKeyLabel(99)).toBe('metric_99');
  });
});

/**
 * Story 34-9 — tenant pricing client contract tests. Asserts the URL/method/body
 * matrix, the estimate query-string assembly, and — critically — that the client
 * derives the tenant from the session (never a URL param) and exposes NO
 * cost/margin field on the estimate response type.
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { tenantPricingApi, metricKeyLabel, METRIC_KEYS } from './pricing';

function jsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function lastUrl(spy: ReturnType<typeof vi.fn>): string {
  return (spy.mock.calls[0]?.[0] as string) ?? '';
}

describe('tenantPricingApi — URL/method/body matrix', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('getEntitlements GETs /api/pricing/entitlements (no tenant id in path)', async () => {
    const spy = vi.fn().mockResolvedValueOnce(jsonResponse({ limits: [] }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await tenantPricingApi.getEntitlements();

    const url = lastUrl(spy);
    expect(url).toBe('/api/pricing/entitlements');
    // No caller-supplied tenant id anywhere in the URL.
    expect(url).not.toMatch(/tenant/i);
  });

  it('listPublicPlans GETs /api/pricing/plans', async () => {
    const spy = vi.fn().mockResolvedValueOnce(jsonResponse({ plans: [] }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await tenantPricingApi.listPublicPlans();
    expect(lastUrl(spy)).toBe('/api/pricing/plans');
  });

  it('getPublicPlan encodes the slug', async () => {
    const spy = vi.fn().mockResolvedValueOnce(jsonResponse({ slug: 'pro' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await tenantPricingApi.getPublicPlan('pro');
    expect(lastUrl(spy)).toBe('/api/pricing/plans/pro');
  });

  it('estimate builds the query string with provider/model/tokens', async () => {
    const spy = vi
      .fn()
      .mockResolvedValueOnce(
        jsonResponse({
          provider: 'anthropic',
          model: 'claude-3-5-sonnet',
          inputTokens: 100,
          outputTokens: 200,
          pricingMode: 'platform_provided',
          sellPriceUsd: 0.01,
          invoice: { sellPriceUsd: 0.01 },
        }),
      );
    globalThis.fetch = spy as unknown as typeof fetch;

    await tenantPricingApi.estimate({
      provider: 'anthropic',
      model: 'claude-3-5-sonnet',
      inputTokens: 100,
      outputTokens: 200,
    });

    const url = lastUrl(spy);
    expect(url).toContain('/api/pricing/estimate?');
    expect(url).toContain('provider=anthropic');
    expect(url).toContain('model=claude-3-5-sonnet');
    expect(url).toContain('inputTokens=100');
    expect(url).toContain('outputTokens=200');
  });

  it('estimate response carries only the sell price — no cost/margin field', async () => {
    const payload = {
      provider: 'anthropic',
      model: 'x',
      inputTokens: 1,
      outputTokens: 1,
      pricingMode: 'byok',
      sellPriceUsd: 0,
      invoice: { sellPriceUsd: 0 },
    };
    const spy = vi.fn().mockResolvedValueOnce(jsonResponse(payload));
    globalThis.fetch = spy as unknown as typeof fetch;

    const result = await tenantPricingApi.estimate({
      provider: 'anthropic',
      model: 'x',
      inputTokens: 1,
      outputTokens: 1,
    });

    expect(result).not.toHaveProperty('costBasisUsd');
    expect(result).not.toHaveProperty('marginUsd');
    expect(result.sellPriceUsd).toBe(0);
  });

  it('subscribe POSTs { planSlug } to /api/pricing/subscribe', async () => {
    const spy = vi.fn().mockResolvedValueOnce(jsonResponse({ tenantId: 't', status: 'ok' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    await tenantPricingApi.subscribe({ planSlug: 'pro' });

    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toBe('/api/pricing/subscribe');
    expect((init as RequestInit).method).toBe('POST');
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.planSlug).toBe('pro');
  });
});

describe('metricKeyLabel', () => {
  it('maps numeric ordinal → snake_case and passes strings through', () => {
    expect(metricKeyLabel(0)).toBe('agents');
    expect(metricKeyLabel(METRIC_KEYS.length - 1)).toBe('benchmark_retention_days');
    expect(metricKeyLabel('seats')).toBe('seats');
  });
});

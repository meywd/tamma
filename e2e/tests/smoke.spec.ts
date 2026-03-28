/**
 * Smoke tests — verify all three Tamma services are reachable.
 *
 * These tests are designed to FAIL CLEARLY when services are down,
 * providing explicit error messages about which service is unreachable.
 */

import { test, expect } from '@playwright/test';
import { API_URL, APP_URL, ELSA_URL } from '../playwright.config';

test.describe('Smoke Tests — Service Reachability', () => {
  test('api.tamma.dev /api/health returns 200 with status ok', async ({
    request,
  }) => {
    const url = `${API_URL}/api/health`;
    let response;
    try {
      response = await request.get(url, { timeout: 15_000 });
    } catch (error) {
      throw new Error(
        `SERVICE DOWN: API server at ${API_URL} is unreachable. ` +
          `Cannot connect to ${url}. Error: ${(error as Error).message}`,
      );
    }

    expect(
      response.status(),
      `API health check at ${url} returned HTTP ${response.status()} instead of 200. ` +
        `The API service may be crashed or misconfigured.`,
    ).toBe(200);

    const body = await response.json();
    expect(
      body.status,
      `API health check returned unexpected body: ${JSON.stringify(body)}. ` +
        `Expected { "status": "ok" }.`,
    ).toBe('ok');
  });

  test('app.tamma.dev loads HTML with Tamma in title', async ({ page }) => {
    const url = APP_URL;
    let loadError: Error | undefined;

    try {
      const response = await page.goto(url, {
        waitUntil: 'domcontentloaded',
        timeout: 20_000,
      });

      if (!response) {
        throw new Error(`No response received from ${url}`);
      }

      expect(
        response.status(),
        `Dashboard at ${url} returned HTTP ${response.status()}. ` +
          `Expected 200. The dashboard service may be down.`,
      ).toBeLessThan(400);
    } catch (error) {
      loadError = error as Error;
      if (loadError.message.includes('ERR_CONNECTION_REFUSED') ||
          loadError.message.includes('ERR_NAME_NOT_RESOLVED') ||
          loadError.message.includes('ERR_CONNECTION_TIMED_OUT') ||
          loadError.message.includes('NS_ERROR_CONNECTION_REFUSED') ||
          loadError.message.includes('No response received')) {
        throw new Error(
          `SERVICE DOWN: Dashboard at ${url} is unreachable. ` +
            `Error: ${loadError.message}`,
        );
      }
      throw loadError;
    }

    const title = await page.title();
    expect(
      title.toLowerCase(),
      `Dashboard page title "${title}" does not contain "tamma". ` +
        `The page may be serving an error page or wrong content.`,
    ).toContain('tamma');
  });

  test('elsa.tamma.dev loads HTML (Blazor WASM app)', async ({ page }) => {
    const url = ELSA_URL;

    try {
      const response = await page.goto(url, {
        waitUntil: 'domcontentloaded',
        timeout: 20_000,
      });

      if (!response) {
        throw new Error(`No response received from ${url}`);
      }

      expect(
        response.status(),
        `ELSA Studio at ${url} returned HTTP ${response.status()}. ` +
          `Expected a success status. The ELSA Studio service may be down.`,
      ).toBeLessThan(400);
    } catch (error) {
      const err = error as Error;
      if (err.message.includes('ERR_CONNECTION_REFUSED') ||
          err.message.includes('ERR_NAME_NOT_RESOLVED') ||
          err.message.includes('ERR_CONNECTION_TIMED_OUT') ||
          err.message.includes('NS_ERROR_CONNECTION_REFUSED') ||
          err.message.includes('No response received')) {
        throw new Error(
          `SERVICE DOWN: ELSA Studio at ${url} is unreachable. ` +
            `Error: ${err.message}`,
        );
      }
      throw err;
    }

    // Blazor WASM apps have a specific structure — check for HTML content
    const html = await page.content();
    expect(
      html.length,
      `ELSA Studio at ${url} returned empty or minimal HTML (${html.length} chars). ` +
        `The Blazor WASM app may not be serving correctly.`,
    ).toBeGreaterThan(100);
  });

  test('elsa.tamma.dev /elsa/api responds (ELSA server API)', async ({
    request,
  }) => {
    const url = `${ELSA_URL}/elsa/api`;
    let response;
    try {
      response = await request.get(url, { timeout: 15_000 });
    } catch (error) {
      throw new Error(
        `SERVICE DOWN: ELSA Server API at ${url} is unreachable. ` +
          `The ELSA backend service may be down. Error: ${(error as Error).message}`,
      );
    }

    // ELSA API root may return various codes (200, 404 for no matching route, etc.)
    // but should NOT return 502/503/504 (proxy errors indicating service is down)
    expect(
      response.status(),
      `ELSA Server API at ${url} returned HTTP ${response.status()}. ` +
        `A 502/503/504 indicates the ELSA server is down behind the proxy.`,
    ).toBeLessThan(500);
  });
});

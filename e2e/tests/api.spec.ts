/**
 * API endpoint tests — verify the Tamma API at api.tamma.dev.
 *
 * Tests health check, config endpoints, and error handling.
 * Designed to fail clearly when the API service is down.
 */

import { test, expect } from '@playwright/test';
import { API_URL } from '../playwright.config';

/**
 * Helper to make an API request with clear error messaging on connection failure.
 */
async function apiRequest(
  request: ReturnType<typeof test['info']> extends never
    ? never
    : Parameters<Parameters<typeof test>[1]>[0]['request'],
  method: 'get' | 'post' | 'put' | 'delete',
  path: string,
  options?: { data?: unknown; headers?: Record<string, string> },
) {
  const url = `${API_URL}${path}`;
  try {
    return await request[method](url, {
      timeout: 15_000,
      data: options?.data,
      headers: options?.headers,
    });
  } catch (error) {
    throw new Error(
      `SERVICE DOWN: API server at ${API_URL} is unreachable. ` +
        `Failed to ${method.toUpperCase()} ${url}. ` +
        `Error: ${(error as Error).message}`,
    );
  }
}

test.describe('API Endpoint Tests', () => {
  test('GET /api/health returns 200', async ({ request }) => {
    const response = await apiRequest(request, 'get', '/api/health');

    expect(
      response.status(),
      `API health endpoint returned HTTP ${response.status()} instead of 200. ` +
        `The API service may be unhealthy.`,
    ).toBe(200);

    const body = await response.json();
    expect(body).toHaveProperty('status', 'ok');
    expect(body).toHaveProperty('timestamp');

    // Verify timestamp is a valid ISO 8601 date
    const timestamp = new Date(body.timestamp);
    expect(
      timestamp.getTime(),
      `API health timestamp "${body.timestamp}" is not a valid date.`,
    ).not.toBeNaN();
  });

  test('GET /api/config/agents returns JSON (may require auth)', async ({
    request,
  }) => {
    const response = await apiRequest(request, 'get', '/api/config/agents');

    // 200 = success, 401 = auth required (both are valid — means API is running)
    const status = response.status();
    expect(
      status === 200 || status === 401 || status === 403,
      `GET /api/config/agents returned HTTP ${status}. ` +
        `Expected 200 (success) or 401/403 (auth required). ` +
        `Status 502/503/504 indicates the API is down.`,
    ).toBeTruthy();

    if (status === 200) {
      const contentType = response.headers()['content-type'] ?? '';
      expect(
        contentType,
        `GET /api/config/agents returned content-type "${contentType}" instead of JSON.`,
      ).toContain('json');
    }
  });

  test('GET /api/config/security returns JSON', async ({ request }) => {
    const response = await apiRequest(request, 'get', '/api/config/security');

    const status = response.status();
    expect(
      status === 200 || status === 401 || status === 403,
      `GET /api/config/security returned HTTP ${status}. ` +
        `Expected 200 (success) or 401/403 (auth required). ` +
        `Status 502/503/504 indicates the API is down.`,
    ).toBeTruthy();

    if (status === 200) {
      const contentType = response.headers()['content-type'] ?? '';
      expect(
        contentType,
        `GET /api/config/security returned content-type "${contentType}" instead of JSON.`,
      ).toContain('json');
    }
  });

  test('POST /api/github/webhooks with invalid payload returns 400 (not 500)', async ({
    request,
  }) => {
    const response = await apiRequest(
      request,
      'post',
      '/api/github/webhooks',
      {
        data: { invalid: 'payload' },
        headers: {
          'Content-Type': 'application/json',
          'X-GitHub-Event': 'ping',
          'X-Hub-Signature-256': 'sha256=invalid',
          'X-GitHub-Delivery': 'test-delivery-id',
        },
      },
    );

    const status = response.status();

    // The webhook handler should reject invalid payloads with 400 or 401,
    // NOT crash with 500. A 404 is also acceptable if the route is not
    // registered (when GitHub App is not configured).
    expect(
      status,
      `POST /api/github/webhooks with invalid payload returned HTTP ${status}. ` +
        `Expected 400 (bad request), 401 (signature mismatch), or 404 (route not configured). ` +
        `A 500 indicates unhandled error; 502/503 indicates service is down.`,
    ).toBeLessThan(500);
  });

  test('GET /api/nonexistent returns 404 (not 500)', async ({ request }) => {
    const response = await apiRequest(request, 'get', '/api/nonexistent');

    expect(
      response.status(),
      `GET /api/nonexistent returned HTTP ${response.status()}. ` +
        `Expected 404. A 500 indicates a server error; 502/503 indicates the service is down.`,
    ).toBe(404);
  });
});

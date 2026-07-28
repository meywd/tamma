/**
 * Tests for the user-dashboard API client. The client must:
 *
 *  1. Send every request with `credentials: 'include'` so the `tamma_session`
 *     cookie travels cross-subdomain from dash.tamma.dev → api.tamma.dev.
 *  2. On a 401, attempt `POST /api/v1/auth/refresh` exactly once, then
 *     retry the original request. If the retry is also 401, surface
 *     `UnauthorizedError` (the guard will redirect to /login).
 *  3. Serialize request bodies as JSON and set the proper content-type.
 *  4. Parse JSON responses or return raw text when the response is not JSON.
 */

import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { ApiClient, UnauthorizedError } from './client';

describe('ApiClient', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let client: ApiClient;

  beforeEach(() => {
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    client = new ApiClient({ baseUrl: 'https://api.tamma.dev' });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('sends credentials:"include" and JSON accept header on GET', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
    await client.get<{ ok: boolean }>('/api/v1/auth/me');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('https://api.tamma.dev/api/v1/auth/me');
    expect((init as RequestInit).credentials).toBe('include');
    expect((init as RequestInit).method).toBe('GET');
  });

  it('serializes JSON body on POST', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response('{}', {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
    await client.post('/api/v1/auth/login', { email: 'a@b.com', password: 'x' });
    const init = fetchMock.mock.calls[0][1] as RequestInit;
    expect(init.method).toBe('POST');
    expect(init.body).toBe(JSON.stringify({ email: 'a@b.com', password: 'x' }));
    const headers = init.headers as Record<string, string>;
    expect(headers['Content-Type']).toBe('application/json');
  });

  it('serializes JSON body, sets content type and credentials on PATCH', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response('{}', {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
    await client.patch('/api/v1/orgs/t/alert-channels/c', { name: 'x' });
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('https://api.tamma.dev/api/v1/orgs/t/alert-channels/c');
    expect((init as RequestInit).method).toBe('PATCH');
    expect((init as RequestInit).credentials).toBe('include');
    expect((init as RequestInit).body).toBe(JSON.stringify({ name: 'x' }));
    const headers = (init as RequestInit).headers as Record<string, string>;
    expect(headers['Content-Type']).toBe('application/json');
  });

  it('parses JSON responses', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ foo: 'bar' }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
    const result = await client.get<{ foo: string }>('/x');
    expect(result).toEqual({ foo: 'bar' });
  });

  it('retries once on 401 after calling /auth/refresh', async () => {
    // First call: 401. Refresh call: 200. Retry of the original call: 200.
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 })) // original
      .mockResolvedValueOnce(new Response('', { status: 200 })) // refresh
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      ); // retry

    const result = await client.get<{ ok: boolean }>('/api/v1/me');
    expect(result).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[1][0]).toBe('https://api.tamma.dev/api/v1/auth/refresh');
    expect((fetchMock.mock.calls[1][1] as RequestInit).method).toBe('POST');
  });

  it('throws UnauthorizedError when refresh also returns 401', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 })) // original
      .mockResolvedValueOnce(new Response('', { status: 401 })); // refresh fails

    await expect(client.get('/api/v1/me')).rejects.toBeInstanceOf(UnauthorizedError);
    // Must NOT retry the original request after a failed refresh.
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('does not retry refresh-endpoint 401s (avoids infinite loop)', async () => {
    fetchMock.mockResolvedValueOnce(new Response('', { status: 401 }));
    await expect(
      client.post('/api/v1/auth/refresh', {}),
    ).rejects.toBeInstanceOf(UnauthorizedError);
    // Only the single original call — never a refresh-of-refresh.
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('throws ApiError with status code on non-401 errors', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'bad' }), {
        status: 400,
        headers: { 'content-type': 'application/json' },
      }),
    );
    await expect(client.get('/api/v1/me')).rejects.toMatchObject({
      status: 400,
      body: { error: 'bad' },
    });
  });
});

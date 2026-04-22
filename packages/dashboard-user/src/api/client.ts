/**
 * User-dashboard API client.
 *
 * Wraps `fetch` with:
 *   - `credentials: 'include'` so the `tamma_session` cookie travels
 *     cross-subdomain (dash.tamma.dev → api.tamma.dev).
 *   - Automatic JSON serialization + parsing.
 *   - Single-shot refresh-on-401: if a request returns 401, call
 *     `POST /api/v1/auth/refresh` once, then retry the original request.
 *     If refresh itself returns 401, throw `UnauthorizedError` for the
 *     caller (the auth guard redirects to /login).
 */

export class UnauthorizedError extends Error {
  constructor() {
    super('Unauthorized');
    this.name = 'UnauthorizedError';
  }
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly body: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export interface ApiClientOptions {
  baseUrl: string;
}

const REFRESH_PATH = '/api/v1/auth/refresh';

export class ApiClient {
  constructor(private readonly options: ApiClientOptions) {}

  get<T>(path: string, init?: RequestInit): Promise<T> {
    return this.request<T>(path, { ...init, method: 'GET' });
  }

  post<T>(path: string, body?: unknown, init?: RequestInit): Promise<T> {
    const requestInit: RequestInit = {
      ...init,
      method: 'POST',
      headers: {
        ...((init?.headers as Record<string, string>) ?? {}),
        'Content-Type': 'application/json',
      },
    };
    if (body !== undefined) requestInit.body = JSON.stringify(body);
    return this.request<T>(path, requestInit);
  }

  put<T>(path: string, body?: unknown, init?: RequestInit): Promise<T> {
    const requestInit: RequestInit = {
      ...init,
      method: 'PUT',
      headers: {
        ...((init?.headers as Record<string, string>) ?? {}),
        'Content-Type': 'application/json',
      },
    };
    if (body !== undefined) requestInit.body = JSON.stringify(body);
    return this.request<T>(path, requestInit);
  }

  delete<T>(path: string, init?: RequestInit): Promise<T> {
    return this.request<T>(path, { ...init, method: 'DELETE' });
  }

  private async request<T>(path: string, init: RequestInit): Promise<T> {
    const url = `${this.options.baseUrl}${path}`;
    const response = await fetch(url, {
      ...init,
      credentials: 'include',
      headers: {
        Accept: 'application/json',
        ...((init.headers as Record<string, string>) ?? {}),
      },
    });

    // Refresh-on-401 — but NEVER attempt to refresh when the failing call
    // IS the refresh endpoint, or we spin forever.
    if (response.status === 401) {
      if (path === REFRESH_PATH) {
        throw new UnauthorizedError();
      }

      const refreshed = await fetch(`${this.options.baseUrl}${REFRESH_PATH}`, {
        method: 'POST',
        credentials: 'include',
        headers: { Accept: 'application/json' },
      });

      if (!refreshed.ok) {
        throw new UnauthorizedError();
      }

      // Retry the original request. No further refresh attempt after this.
      const retry = await fetch(url, {
        ...init,
        credentials: 'include',
        headers: {
          Accept: 'application/json',
          ...((init.headers as Record<string, string>) ?? {}),
        },
      });
      return this.parseResponse<T>(retry);
    }

    return this.parseResponse<T>(response);
  }

  private async parseResponse<T>(response: Response): Promise<T> {
    if (!response.ok) {
      const body = await this.tryParseBody(response);
      throw new ApiError(response.status, `API error: ${response.status}`, body);
    }
    return (await this.tryParseBody(response)) as T;
  }

  private async tryParseBody(response: Response): Promise<unknown> {
    const contentType = response.headers.get('content-type') ?? '';
    if (contentType.includes('application/json')) {
      const text = await response.text();
      if (text.length === 0) return null;
      return JSON.parse(text);
    }
    return response.text();
  }
}

/**
 * Default client used across the app. Points at the API base URL the Vite
 * build resolves via `VITE_API_URL`; falls back to the same origin so the
 * dev-server proxy in `vite.config.ts` can route `/api/*` to the .NET API.
 */
export const apiClient = new ApiClient({
  baseUrl:
    // Vite inlines `import.meta.env` at build time.
    (typeof import.meta !== 'undefined' &&
      (import.meta as { env?: Record<string, string> }).env?.VITE_API_URL) ||
    '',
});

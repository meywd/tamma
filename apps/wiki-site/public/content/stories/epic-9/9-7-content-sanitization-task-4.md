---
title: "Task 4: Implement Secure Fetch with Streaming Body Read and Manual Redirect Handling"
sidebar:
  order: 90
---

**Story:** 9-7-content-sanitization - Content Sanitization
**Epic:** 9

## Task Description

Create `packages/shared/src/security/secure-fetch.ts` with a `secureFetch()` function that wraps the native `fetch()` API with security protections: URL validation against private hosts before the request, **streaming body read** via `response.body` (ReadableStream) with a running byte counter (NOT `response.text()` or `response.arrayBuffer()`), **manual redirect handling** (`redirect: 'manual'`) with re-validation of Location header URLs (max 5 redirect hops), and **Content-Type allowlist** checking before reading the body.

## Acceptance Criteria

- `secureFetch()` validates URLs before making requests using `validateUrl()` from Task 2
- `secureFetch()` uses `redirect: 'manual'` in fetch options to intercept redirects
- `secureFetch()` on 301/302/307/308 extracts `Location` header, re-validates via `validateUrl()` before following
- `secureFetch()` enforces maximum 5 redirect hops
- `secureFetch()` reads body via `response.body` (ReadableStream) with a running byte counter
- `secureFetch()` MUST abort via `AbortController` when byte counter exceeds `maxSizeBytes`
- `secureFetch()` does NOT use `response.text()` or `response.arrayBuffer()` for body reading
- `secureFetch()` checks Content-Type allowlist (default: `['text/', 'application/json', 'application/xml']`) before reading body
- `secureFetch()` rejects non-text responses before reading body
- `secureFetch()` returns a result object with response data or error information
- Private host URLs are rejected before any network request is made
- Redirects to private hosts are rejected
- Function handles network errors gracefully (never throws unhandled)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/shared/src/security/secure-fetch.ts`
- [ ] Import `validateUrl` from `./url-validator.js`
- [ ] Define options interface:

```typescript
export interface SecureFetchOptions {
  maxSizeBytes?: number;           // Default: 10 MB (10 * 1024 * 1024)
  timeoutMs?: number;              // Default: 30000
  allowedProtocols?: string[];     // Default: ['http:', 'https:']
  allowedContentTypes?: string[];  // Default: ['text/', 'application/json', 'application/xml']
  maxRedirects?: number;           // Default: 5
  headers?: Record<string, string>;
}
```

- [ ] Define result interface:

```typescript
export interface SecureFetchResult {
  ok: boolean;
  status?: number;
  body?: string;
  headers?: Record<string, string>;
  error?: string;
  warnings: string[];
}
```

- [ ] Implement `secureFetch()`:

```typescript
export async function secureFetch(
  url: string,
  options?: SecureFetchOptions,
): Promise<SecureFetchResult> {
  const warnings: string[] = [];
  const maxSize = options?.maxSizeBytes ?? 10 * 1024 * 1024; // 10 MB
  const maxRedirects = options?.maxRedirects ?? 5;
  const allowedContentTypes = options?.allowedContentTypes ?? ['text/', 'application/json', 'application/xml'];

  // 1. Pre-validate URL
  const validation = validateUrl(url);
  if (!validation.valid) {
    return { ok: false, error: 'URL validation failed', warnings: validation.warnings };
  }

  // 2. Fetch with redirect: 'manual' to intercept redirects
  let currentUrl = url;
  let redirectCount = 0;
  let response: Response;

  while (true) {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), options?.timeoutMs ?? 30000);

    try {
      response = await fetch(currentUrl, {
        redirect: 'manual',
        signal: controller.signal,
        headers: options?.headers,
      });
    } finally {
      clearTimeout(timeoutId);
    }

    // 3. On 301/302/307/308, extract Location header and re-validate
    if ([301, 302, 307, 308].includes(response.status)) {
      redirectCount++;
      if (redirectCount > maxRedirects) {
        return { ok: false, error: `Too many redirects (max ${maxRedirects})`, warnings };
      }
      const location = response.headers.get('location');
      if (!location) {
        return { ok: false, error: 'Redirect without Location header', warnings };
      }
      const redirectValidation = validateUrl(location);
      if (!redirectValidation.valid) {
        return { ok: false, error: 'Redirect URL validation failed', warnings: [...warnings, ...redirectValidation.warnings] };
      }
      currentUrl = location;
      continue;
    }
    break;
  }

  // 4. Check Content-Type allowlist before reading body
  const contentType = response.headers.get('content-type') ?? '';
  const isAllowedContentType = allowedContentTypes.some(allowed => contentType.toLowerCase().includes(allowed));
  if (!isAllowedContentType) {
    return { ok: false, error: `Blocked content type: ${contentType}`, warnings };
  }

  // 5. Read response body via ReadableStream with running byte counter
  //    MUST use response.body (ReadableStream), NOT response.text() or response.arrayBuffer()
  const reader = response.body?.getReader();
  if (!reader) {
    return { ok: false, error: 'No response body', warnings };
  }

  const chunks: Uint8Array[] = [];
  let totalBytes = 0;
  const bodyController = new AbortController();

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      totalBytes += value.byteLength;
      if (totalBytes > maxSize) {
        await reader.cancel();
        return { ok: false, error: `Response body exceeds max size (${maxSize} bytes)`, warnings };
      }
      chunks.push(value);
    }
  } catch (err) {
    return { ok: false, error: `Error reading response body: ${err}`, warnings };
  }

  const body = new TextDecoder().decode(Buffer.concat(chunks));

  // 6. Return SecureFetchResult
}
```

- [ ] Use `redirect: 'manual'` in fetch options — each redirect hop also uses `redirect: 'manual'`
- [ ] On 301/302/307/308 response, extract `Location` header and re-validate with `validateUrl()`
- [ ] If redirect target is valid, follow it (up to `maxRedirects`, default 5)
- [ ] **Body MUST be read via `response.body` (ReadableStream) with a running byte counter**
- [ ] **MUST abort via `AbortController` / `reader.cancel()` when byte counter exceeds `maxSizeBytes`**
- [ ] **Do NOT use `response.text()` or `response.arrayBuffer()`** — stream only
- [ ] Check Content-Type allowlist (default: `['text/', 'application/json', 'application/xml']`) before reading body
- [ ] Reject non-text responses before reading body
- [ ] Use `AbortController` for timeout enforcement
- [ ] Handle network errors (DNS failure, connection refused, etc.) gracefully

### Files to Modify/Create

- `packages/shared/src/security/secure-fetch.ts` -- **CREATE** -- Secure fetch function

### Dependencies

- [ ] Task 2 must be completed first (`url-validator.ts` must exist)
- [ ] Uses Node.js built-in `fetch()` (available in Node 22 LTS)
- [ ] Uses Node.js built-in `AbortController`

## Testing Strategy

### Unit Tests

Note: Unit tests should mock `globalThis.fetch` to avoid real network calls.

- [ ] Test rejects private host URLs before making any fetch call (fetch not called)
- [ ] Test rejects `http://10.0.0.1/api` -- returns `{ ok: false, error, warnings }`
- [ ] Test rejects `http://localhost:3000` -- returns `{ ok: false, error, warnings }`
- [ ] Test rejects `file:///etc/passwd` -- returns `{ ok: false, error, warnings }`
- [ ] Test accepts and fetches `https://example.com` (mock fetch returns 200 with text content-type)
- [ ] Test enforces max body size via streaming -- mock fetch returns ReadableStream body larger than limit, result has error
- [ ] Test body read uses `response.body` ReadableStream with byte counter (NOT `response.text()`)
- [ ] Test aborts via reader.cancel() when byte counter exceeds maxSizeBytes
- [ ] Test allows responses within size limit
- [ ] Test re-validates redirect URLs on 301:
  - Mock fetch returns 301 with `Location: http://192.168.1.1/secret`
  - Verify redirect is blocked and result has warning
- [ ] Test re-validates redirect URLs on 302:
  - Mock fetch returns 302 with `Location: http://192.168.1.1/secret`
  - Verify redirect is blocked and result has warning
- [ ] Test re-validates redirect URLs on 307 and 308
- [ ] Test redirect-to-private-IP is blocked (public URL redirects to private IP)
- [ ] Test follows valid redirects:
  - Mock fetch returns 302 with `Location: https://example.com/new-path`
  - Verify redirect is followed
- [ ] Test each redirect hop uses `redirect: 'manual'`
- [ ] Test enforces max redirect count of 5 (prevent infinite redirect loops)
- [ ] Test custom `maxRedirects` overrides default
- [ ] Test handles redirect without Location header gracefully
- [ ] Test checks Content-Type allowlist before reading body
- [ ] Test rejects non-text content types (e.g., `application/octet-stream`, `image/png`)
- [ ] Test accepts `text/html`, `text/plain`, `application/json`, `application/xml` content types
- [ ] Test custom `allowedContentTypes` overrides default
- [ ] Test handles network errors gracefully (mock fetch throws)
- [ ] Test timeout enforcement via AbortController (mock slow response)
- [ ] Test default maxSizeBytes is 10 MB when not specified
- [ ] Test custom maxSizeBytes overrides default
- [ ] Test custom headers are passed through to fetch
- [ ] Test returns response headers in result

### Validation Steps

1. [ ] Create secure-fetch.ts with secureFetch function
2. [ ] Verify URL validation happens before fetch call
3. [ ] Verify redirect re-validation logic
4. [ ] Write unit tests in `packages/shared/src/security/secure-fetch.test.ts`
5. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
6. [ ] Run `pnpm vitest run packages/shared/src/security/secure-fetch`

## Notes & Considerations

- Using `redirect: 'manual'` is essential for SSRF protection. The default `redirect: 'follow'` would silently follow redirects to private IPs, bypassing the initial URL validation. Each redirect hop also uses `redirect: 'manual'`.
- **Body MUST be read via `response.body` (ReadableStream) with a running byte counter**. Do NOT use `response.text()` or `response.arrayBuffer()` -- these buffer the entire response in memory before the size check, defeating the purpose of size limiting.
- The `Content-Length` header check can be used as a fast-path rejection only -- it can be spoofed, so streaming body read with byte counter is the authoritative enforcement.
- **Content-Type allowlist** (default: `['text/', 'application/json', 'application/xml']`) is checked before reading the body. This prevents reading binary responses (images, executables, etc.) that would be meaningless as text and could be large.
- The max redirect count (default 5) prevents infinite redirect loops. Each redirect re-validates the URL via `validateUrl()` and increments the counter.
- `AbortController` with `setTimeout` provides timeout enforcement. Remember to clear the timeout on completion.
- This function uses Node.js 22 LTS built-in `fetch()` -- no need for `node-fetch` or similar polyfills.
- DNS rebinding attacks (where a hostname resolves to a private IP) are partially mitigated by the redirect re-validation. Full mitigation would require DNS-level checks, which is out of scope for this story.

## Completion Checklist

- [ ] `packages/shared/src/security/secure-fetch.ts` created
- [ ] `SecureFetchOptions` includes `allowedContentTypes?` and `maxRedirects?`
- [ ] `SecureFetchResult` interface defined
- [ ] `secureFetch()` validates URL before fetching
- [ ] `secureFetch()` uses `redirect: 'manual'` and re-validates Location header on 301/302/307/308
- [ ] `secureFetch()` enforces max 5 redirect hops (configurable)
- [ ] `secureFetch()` reads body via ReadableStream with running byte counter
- [ ] `secureFetch()` does NOT use `response.text()` or `response.arrayBuffer()`
- [ ] `secureFetch()` aborts via AbortController when byte counter exceeds maxSizeBytes
- [ ] `secureFetch()` checks Content-Type allowlist before reading body
- [ ] `secureFetch()` rejects non-text responses
- [ ] `secureFetch()` uses AbortController for timeout
- [ ] `secureFetch()` handles network errors gracefully
- [ ] Unit tests written and passing (with mocked fetch)
- [ ] TypeScript strict mode compilation passes

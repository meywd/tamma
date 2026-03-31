---
title: "Task 2: Implement URL Validator with Numeric Octet Parsing and IPv6 Support"
sidebar:
  order: 90
---

**Story:** 9-7-content-sanitization - Content Sanitization
**Epic:** 9

## Task Description

Create `packages/shared/src/security/url-validator.ts` with `validateUrl()` and `isPrivateHost()` functions. This module generalizes the existing private IP detection logic from `packages/mcp-client/src/security/validator.ts` (`validateTransportUrl`, inline `isPrivateIP`). The critical design constraint is that `isPrivateHost()` uses **numeric octet parsing** (parseInt + integer comparison) for RFC 1918 range detection, NOT regex. This eliminates regex bypass risks and is clearer to audit.

The function also handles **IPv6-mapped IPv4** addresses (`[::ffff:x.x.x.x]`), bracketed IPv6 notation, IPv6 private ranges (fc00::/7, fe80::/10), and blocks cloud metadata hostnames (`metadata.google.internal`, `host.docker.internal`). The `BLOCKED_HOSTS` Set is at **module scope** (not re-allocated per call).

**Migration note**: After this story is complete, `mcp-client/src/security/validator.ts` should be updated to import from `@tamma/shared/security`. That migration is tracked separately and is NOT done in this task.

## Acceptance Criteria

- `isPrivateHost()` uses numeric octet parsing via parseInt and integer comparison (NOT regex)
- `isPrivateHost()` blocks 0.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8, 169.254.0.0/16
- `isPrivateHost()` has a module-scope `BLOCKED_HOSTS` Set (not re-allocated per call) for blocked literals: 0.0.0.0, [::], [::1], 127.0.0.1, localhost, metadata.google.internal, host.docker.internal
- `isPrivateHost()` handles bracketed IPv6: `[::1]`, `[::ffff:x.x.x.x]` (IPv6-mapped IPv4 with recursive check)
- `isPrivateHost()` blocks IPv6 private ranges: fc00::/7 (unique local: fc, fd prefixes), fe80::/10 (link-local)
- `isPrivateHost()` does NOT block non-IPv4 hostnames (e.g. `evil.com` passes through)
- `isPrivateHost()` rejects invalid octets (values > 255, < 0, non-numeric)
- `validateUrl()` allows only http:, https:, ws:, wss: protocols
- `validateUrl()` returns `{ valid: boolean; warnings: string[] }` (never throws)
- `validateUrl()` rejects URLs with private hosts
- `validateUrl()` truncates URL in error messages to max 200 chars (prevent information leakage)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/shared/src/security/url-validator.ts`
- [ ] Define module-scope `BLOCKED_HOSTS` Set:

```typescript
/** Module-scope blocked hostname set (avoids per-call allocation) */
const BLOCKED_HOSTS = new Set([
  '0.0.0.0', '[::]', '[::1]', '127.0.0.1', 'localhost',
  'metadata.google.internal',  // GCP metadata endpoint
  'host.docker.internal',      // Docker host access
]);
```

- [ ] Implement `isPrivateHost(hostname: string): boolean`:

```typescript
/**
 * Check if a hostname is a private/reserved IP address.
 * Uses numeric octet parsing (not regex) for RFC 1918 ranges.
 * Handles IPv6-mapped IPv4 (::ffff:x.x.x.x), bracketed IPv6,
 * and IPv6 private ranges (fc00::/7, fe80::/10).
 */
export function isPrivateHost(hostname: string): boolean {
  // Fast path: known blocked literals
  if (BLOCKED_HOSTS.has(hostname)) return true;

  // Handle bracketed IPv6 (URL notation)
  if (hostname.startsWith('[') && hostname.endsWith(']')) {
    const inner = hostname.slice(1, -1);
    if (inner === '::1' || inner === '::') return true;
    // IPv4-mapped IPv6 (::ffff:x.x.x.x)
    const v4MappedPrefix = '::ffff:';
    if (inner.toLowerCase().startsWith(v4MappedPrefix)) {
      return isPrivateHost(inner.slice(v4MappedPrefix.length));
    }
    // fc00::/7 (unique local) and fe80::/10 (link-local)
    const lc = inner.toLowerCase();
    if (lc.startsWith('fc') || lc.startsWith('fd') || lc.startsWith('fe80')) return true;
    return false;
  }

  // Parse IPv4 octets as integers for range comparison
  const parts = hostname.split('.');
  if (parts.length !== 4) return false;

  const octets = parts.map(p => {
    const n = parseInt(p, 10);
    return Number.isNaN(n) || n < 0 || n > 255 ? -1 : n;
  });
  if (octets.some(o => o === -1)) return false;

  const [a, b] = octets;

  // 0.0.0.0/8 (this network)
  if (a === 0) return true;
  // 10.0.0.0/8
  if (a === 10) return true;
  // 172.16.0.0/12
  if (a === 172 && b >= 16 && b <= 31) return true;
  // 192.168.0.0/16
  if (a === 192 && b === 168) return true;
  // 127.0.0.0/8 (loopback range)
  if (a === 127) return true;
  // 169.254.0.0/16 (link-local)
  if (a === 169 && b === 254) return true;

  return false;
}
```

- [ ] Implement `validateUrl(url: string): { valid: boolean; warnings: string[] }`:

```typescript
export function validateUrl(url: string): { valid: boolean; warnings: string[] } {
  const warnings: string[] = [];
  try {
    const parsed = new URL(url);
    const allowedProtocols = ['http:', 'https:', 'ws:', 'wss:'];
    if (!allowedProtocols.includes(parsed.protocol)) {
      return { valid: false, warnings: [`Blocked protocol: ${parsed.protocol}`] };
    }
    if (isPrivateHost(parsed.hostname)) {
      return { valid: false, warnings: [`Blocked private host: ${parsed.hostname}`] };
    }
    return { valid: true, warnings };
  } catch {
    return { valid: false, warnings: [`Invalid URL: ${url.slice(0, 200)}`] };
  }
}
```

### Files to Modify/Create

- `packages/shared/src/security/url-validator.ts` -- **CREATE** -- URL validation functions

### Dependencies

- None (standalone module, no external dependencies)

### Code Reference

- `packages/mcp-client/src/security/validator.ts` lines 147-187 -- existing `validateTransportUrl()` and inline `isPrivateIP` logic. The new implementation generalizes this with numeric octet parsing instead of regex (`/^172\.(1[6-9]|2[0-9]|3[01])\./`).

## Testing Strategy

### Unit Tests

#### isPrivateHost tests

##### 0.0.0.0/8 range (this network)
- [ ] Test blocks `0.0.0.0` (via BLOCKED_HOSTS Set)
- [ ] Test blocks `0.0.0.1` (0.0.0.0/8 range -- all 0.x.x.x addresses)
- [ ] Test blocks `0.1.2.3` (0.0.0.0/8 range)
- [ ] Test blocks `0.255.255.255` (0.0.0.0/8 end)

##### RFC 1918 private ranges
- [ ] Test blocks `10.0.0.1` (10.0.0.0/8 start)
- [ ] Test blocks `10.255.255.255` (10.0.0.0/8 end)
- [ ] Test blocks `172.16.0.1` (172.16.0.0/12 start)
- [ ] Test blocks `172.31.255.255` (172.16.0.0/12 end)
- [ ] Test does NOT block `172.15.0.1` (just below 172.16)
- [ ] Test does NOT block `172.32.0.1` (just above 172.31)
- [ ] Test blocks `192.168.0.1` (192.168.0.0/16)
- [ ] Test blocks `192.168.255.255` (192.168.0.0/16 end)
- [ ] Test does NOT block `192.169.0.1` (outside range)
- [ ] Test blocks `127.0.0.1` (both via BLOCKED_HOSTS Set and octet parsing)
- [ ] Test blocks `127.255.255.255` (127.0.0.0/8 end)
- [ ] Test blocks `169.254.0.1` (link-local start)
- [ ] Test blocks `169.254.255.255` (link-local end)
- [ ] Test does NOT block `169.255.0.1` (outside link-local)

##### IPv6-mapped IPv4 and bracketed IPv6
- [ ] Test blocks `[::ffff:127.0.0.1]` (IPv6-mapped IPv4 -- recursively checks 127.0.0.1)
- [ ] Test blocks `[::ffff:10.0.0.1]` (IPv6-mapped IPv4 -- recursively checks 10.0.0.1)
- [ ] Test blocks `[::ffff:192.168.1.1]` (IPv6-mapped IPv4 -- recursively checks 192.168.1.1)
- [ ] Test does NOT block `[::ffff:8.8.8.8]` (IPv6-mapped public IP passes through)
- [ ] Test blocks `[::1]` (via BLOCKED_HOSTS Set and inner check)
- [ ] Test blocks `[::]` (via BLOCKED_HOSTS Set and inner check)

##### IPv6 private ranges
- [ ] Test blocks `[fc00::1]` (fc00::/7 unique local)
- [ ] Test blocks `[fd00::1]` (fd prefix -- also fc00::/7)
- [ ] Test blocks `[fe80::1]` (fe80::/10 link-local)
- [ ] Test does NOT block `[2001:db8::1]` (documentation prefix -- not private in our check)

##### Cloud metadata hostnames
- [ ] Test blocks `metadata.google.internal` (GCP metadata endpoint)
- [ ] Test blocks `host.docker.internal` (Docker host access)

##### BLOCKED_HOSTS module scope
- [ ] Test `BLOCKED_HOSTS` is at module scope (verify no per-call Set allocation)

##### Passthrough and edge cases
- [ ] Test blocks `localhost` (via BLOCKED_HOSTS Set)
- [ ] Test does NOT block `evil.com` (non-IPv4 passes through)
- [ ] Test does NOT block `google.com` (non-IPv4 passes through)
- [ ] Test does NOT block `8.8.8.8` (public IP)
- [ ] Test does NOT block `1.1.1.1` (public IP)
- [ ] Test does NOT block `203.0.113.1` (public IP - TEST-NET-3)
- [ ] Test handles invalid octets: `256.0.0.1` returns false (invalid, not blocked -- but not a valid host)
- [ ] Test handles non-numeric octets: `abc.def.ghi.jkl` returns false
- [ ] Test handles too few octets: `10.0.0` returns false
- [ ] Test handles too many octets: `10.0.0.0.0` returns false

#### validateUrl tests

- [ ] Test accepts `https://example.com`
- [ ] Test accepts `http://example.com`
- [ ] Test accepts `ws://example.com`
- [ ] Test accepts `wss://example.com`
- [ ] Test rejects `file:///etc/passwd` with protocol warning
- [ ] Test rejects `ftp://example.com` with protocol warning
- [ ] Test rejects `javascript:alert(1)` with protocol warning
- [ ] Test rejects `https://10.0.0.1/api` with private host warning
- [ ] Test rejects `http://192.168.1.1` with private host warning
- [ ] Test rejects `http://localhost:3000` with private host warning
- [ ] Test returns `{ valid: false, warnings }` for completely invalid URL strings
- [ ] Test truncates URL in error message to max 200 chars for very long invalid URLs
- [ ] Test accepts `https://evil.com` (non-private hostname passes through)

### Validation Steps

1. [ ] Create url-validator.ts with isPrivateHost and validateUrl
2. [ ] Verify isPrivateHost uses parseInt + integer comparison (no regex)
3. [ ] Write unit tests in `packages/shared/src/security/url-validator.test.ts`
4. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
5. [ ] Run `pnpm vitest run packages/shared/src/security/url-validator`

## Notes & Considerations

- The numeric octet parsing approach is preferred over regex because:
  1. It is auditable: `b >= 16 && b <= 31` is clearer than `/^172\.(1[6-9]|2[0-9]|3[01])\./`
  2. It avoids regex bypass risks (octal encoding, leading zeros, etc.)
  3. It handles edge cases like `172.016.0.1` (octal interpretation) correctly since parseInt(p, 10) treats "016" as 16
- The `BLOCKED_HOSTS` Set is at **module scope** to avoid per-call allocation. This is a performance optimization for a hot path.
- Cloud metadata hostnames (`metadata.google.internal`, `host.docker.internal`) are in the literal blocked set because they are hostnames, not IPs -- they cannot be caught by octet parsing.
- IPv6-mapped IPv4 addresses (`[::ffff:x.x.x.x]`) are handled by extracting the embedded IPv4 and recursively calling `isPrivateHost()`. This prevents bypass via alternate IP format encoding.
- IPv6 private ranges are checked by prefix: `fc` and `fd` (fc00::/7 unique local), `fe80` (fe80::/10 link-local).
- Non-IPv4 hostnames (like `evil.com`) pass through `isPrivateHost()` by returning `false` at the `parts.length !== 4` check (after the bracketed IPv6 check). DNS resolution to a private IP is a separate concern (TOCTOU) handled at the fetch layer.
- The `validateUrl` function never throws -- it catches URL parse errors and returns `{ valid: false, warnings }`.
- URL truncation in error messages (max 200 chars) prevents information leakage of potentially sensitive URL content.

## Completion Checklist

- [ ] `packages/shared/src/security/url-validator.ts` created
- [ ] `BLOCKED_HOSTS` Set at module scope with cloud metadata hostnames
- [ ] `isPrivateHost()` implemented with numeric octet parsing (no regex)
- [ ] `isPrivateHost()` covers 0.x, 10.x, 172.16-31.x, 192.168.x, 127.x, 169.254.x
- [ ] `isPrivateHost()` handles IPv6-mapped IPv4, bracketed IPv6, IPv6 private ranges
- [ ] `isPrivateHost()` does NOT block non-IPv4 hostnames
- [ ] `validateUrl()` checks protocol allowlist and private host
- [ ] `validateUrl()` truncates URL in error messages to max 200 chars
- [ ] `validateUrl()` never throws
- [ ] Unit tests written and passing
- [ ] TypeScript strict mode compilation passes

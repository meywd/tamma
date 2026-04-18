# Finding 013: Health API accepts arbitrary `:key` — no regex or length validation

**Scope**: providers
**Severity**: P2 (injection / storage-bloat vector)
**Status**: Incomplete port
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/settings/health-routes.ts`.

- File: `packages/api/src/routes/settings/health-routes.ts:14-22`
- Contract/behavior: Every health route validated the `:key` param against a regex and max length before touching the store.

```typescript
// packages/api/src/routes/settings/health-routes.ts (9e9a57c~1) — lines 14-22
const KEY_PATTERN = /^[a-zA-Z0-9._\-:/]+$/;
const MAX_KEY_LENGTH = 256;

function validateKeyParam(key: string): string | null {
  if (!key || key.length === 0) return 'key must not be empty';
  if (key.length > MAX_KEY_LENGTH) return `key too long (max ${MAX_KEY_LENGTH})`;
  if (!KEY_PATTERN.test(key)) return 'key contains invalid characters';
  return null;
}
```

- Called at the top of `GET /health/providers/:key`, `POST /health/providers/:key/failure`, `POST /health/providers/:key/success`, `POST /health/providers/:key/reset`.
- The same validator was re-applied inside `PgHealthStore.validateKey` (pg-health-store.ts:26-36) — defense-in-depth.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:57-121`
- Contract/behavior: Endpoint handlers accept `string key` directly from the route with **no validation at all**. The only check is inside `CircuitBreakerService.ValidateKey` (CircuitBreakerService.cs:217-223):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs — lines 217-223
private static void ValidateKey(string providerKey)
{
    if (string.IsNullOrWhiteSpace(providerKey))
        throw new ArgumentException("Provider key must not be empty.", nameof(providerKey));
    if (providerKey.Length > 256)
        throw new ArgumentException("Provider key too long (max 256).", nameof(providerKey));
}
```

- Missing: the character-set regex `^[a-zA-Z0-9._\-:/]+$`. C# accepts anything up to 256 chars including whitespace, quotes, control chars, UTF-8 symbols.
- The `ValidateKey` throws `ArgumentException` — ASP.NET maps this to `400` only if the global exception handler catches it. There is a `ExceptionHandlerMiddleware` registered in `Program.cs`, but it returns `500 Internal Server Error` for `ArgumentException` by default. The 400 behaviour is unverified.

## 3. The gap

- Injection vector: a malicious caller can pass `:key = '; DROP TABLE provider_health; --'` — the EF layer parametrizes, so no SQL injection, but the **stored key in `provider_health.provider_key` is arbitrary** and affects downstream log/display rendering. HTML/script payload would render in any dashboard that trusts the DB shape.
- Storage bloat: a misconfigured Elsa workflow passing empty strings or whitespace-only keys can insert 255-char garbage rows as distinct circuit-breaker entries — each with a unique UUID primary key, and `(ProviderKey, TenantId)` uniqueness (`TammaDbContext.cs:282`) prevents duplicates of the same bad string but not bad strings overall.
- Charset inconsistency: TS keys were constrained to `a-zA-Z0-9._-:/`, specifically to fit the `provider:model` convention. C# keys can be `provider with space:model@2024` — breaks downstream parsers that split on `:`.
- For a caller sending `POST /api/providers/health/providers/anthropic%3A%0Ainjected%3Agarbage/failure`:
  - TS: `400 {error:'key contains invalid characters'}`.
  - C#: proceeds, inserts a row with `ProviderKey = "anthropic:\ninjected:garbage"`.

Error paths:
- TS: `400` for any non-conforming key.
- C#: `400` only for empty/over-long; any character passes.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`.
- Story 9-3 AC 3 does not specify key validation. Underspecified.
- TS precedent and the comment above `KEY_PATTERN` in `pg-health-store.ts` establish the intent ("`provider:model` e.g. `openrouter:z-ai/z1-mini`").
- Story alignment:
  - [x] Describes a third behavior — underspecified; both are compliant; TS was safer.
  - [ ] No story — there is a story, it's just ambiguous.

## 5. Status

- **Classification**: Incomplete port (validation weakened).
- **What's needed to finish**:
  1. Add the regex validator to `CircuitBreakerService.ValidateKey` **and** at each endpoint (defense-in-depth, matching TS).
  2. Consider making the regex configurable via `appsettings.json` for tenants that use non-default charsets.
- **Is it "just a stub" or is scope missing?** Scope is understood; validation was simply dropped.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:217-223`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:57-121`
- Tests to add:
  - `ProviderHealth_KeyWithNewline_Returns400`
  - `ProviderHealth_KeyWith256Chars_Accepted`
  - `ProviderHealth_KeyWith257Chars_Returns400`
  - `ProviderHealth_KeyWithInvalidChar_Returns400`
- Estimated effort: 1h.

## References

- TS source: `packages/api/src/routes/settings/health-routes.ts:14-22`, `packages/api/src/services/pg-health-store.ts:26-36` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:217-223`
- Story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`
- Related findings: `012-health-api-response-shape.md`, `022-provider-health-unique-index-positive.md`

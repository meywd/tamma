# Finding 020: Admin `CreateUserApiKey` uses `tamma_uk_` prefix + SHA-256 + base64 (not base64url)

**Scope**: auth
**Severity**: P2 (format drift — not validatable by unified flow)
**Status**: Behavioral drift
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/api-key.ts` and `routes/users/api-key-routes.ts`.

- File: `packages/api/src/auth/api-key.ts:34-42`, `routes/users/api-key-routes.ts:41-54`.
- Contract: **All** API keys — service, user, installation — share the same format: `tamma_sk_<base64url-of-32-random-bytes>`. Hash: scrypt. Prefix for display: first 12 chars.
- Key code:

```typescript
// packages/api/src/auth/api-key.ts:14-17, 34-40 (9e9a57c~1)
const API_KEY_PREFIX = 'tamma_sk_';
const KEY_BYTES = 32;
const DISPLAY_PREFIX_LENGTH = 12;

export function generateApiKey(): string {
  const random = randomBytes(KEY_BYTES).toString('base64url');
  return `${API_KEY_PREFIX}${random}`;
}
```

```typescript
// packages/api/src/routes/users/api-key-routes.ts:41-54
const rawKey = generateApiKey();           // tamma_sk_<base64url>
const keyHash = hashApiKey(rawKey);        // scrypt hex
const keyPrefix = getApiKeyPrefix(rawKey); // first 12 chars → "tamma_sk_xyz"

const record = await apiKeyStore.createApiKey({
  userId: id,
  keyHash,
  keyPrefix,
  label,
});
```

- So user-scope keys are indistinguishable from service-scope keys at the prefix level — both say `tamma_sk_`. Routing is by the `scope` column in `api_keys`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:148-171` (CreateUserApiKey) vs `:17-39` (CreateServiceKey).
- Contract: Service keys use `tamma_sk_`; user keys use `tamma_uk_`. Both hash with SHA-256 (Finding 003). Both encode with plain base64, not base64url. Prefix is first 16 chars.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:154-156 (CreateUserApiKey)
var rawKey = $"tamma_uk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
var prefix = rawKey[..16];
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:22-24 (CreateServiceKey)
var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
var prefix = rawKey[..16];
```

- Differences from TS:
  - **Prefix divergence**: `tamma_uk_` for user keys vs `tamma_sk_` for service keys. TS used one prefix.
  - **Base64 vs base64url**: `Convert.ToBase64String` produces `+`, `/`, `=` characters. These are not URL-safe. A key like `tamma_sk_xYz/abc+123=` will render unsafely in URLs, browser address bars, and may be truncated/escaped. TS used base64url which has only `[A-Za-z0-9_-]`.
  - **Hash algorithm**: SHA-256 vs scrypt (Finding 003).
  - **Prefix length**: 16 vs 12 — means a larger chunk of the key is exposed in logs/lists.

## 3. The gap

Five orthogonal drifts packed into one endpoint.

For a caller creating a user key:
- TS: `tamma_sk_QYJH8xF1p_kXCr0s-aBzLMN3gHeJv...base64url`
- C#: `tamma_uk_QYJH8xF1p/kXCr0s+aBzLMN3gHeJv...base64=`

Consequences:
1. **Prefix ambiguity**: TS clients filtering "does this key look like ours" check `rawKey.startsWith('tamma_sk_')`. For C# user keys with `tamma_uk_`, that check fails. New clients need to check both prefixes or a common root like `tamma_`.
2. **URL-unsafe characters**: A user who copies a C# user-scope key into a curl command with `-H "Authorization: Bearer tamma_uk_.../..."` may have the `/` interpreted by shells/URLs. Unlikely to fail in the `Authorization` header itself (which is opaque), but confusing in log lines, copy-paste, dashboards.
3. **Hash incompatibility with service keys**: all prior service-scope rows in the `api_keys` table were written with scrypt via TS. New user-scope keys (and new service-scope keys) are SHA-256. You now have two eras of hashes in one table. The auth handler only does SHA-256, so old rows are unverifiable (Finding 003). This finding compounds with 003.
4. **Longer visible prefix**: `tamma_sk_1234567` (16 chars) reveals 7 post-prefix characters. That's still cryptographically safe (1/2^56 guessing probability from prefix alone), but more than TS's 3 post-prefix chars.

Additionally, the permissions default is suspicious:

```csharp
// AdminEndpoints.cs:165
Permissions = ["dashboard:view", "workflows:view"],
```

User-scope keys get hardcoded permissions baked into the key creation. TS used role-derived permissions at auth time, not stored-on-key. This is a mild layering regression.

Error paths:
- TS: key generated with no error path.
- C#: same.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md`, `docs/stories/epic-16/16-2-user-management-api.md`.
- Story 16-7 does not specify the prefix format. It refers to "API keys" generically.
- Story 16-2 (not read in detail — referenced via index) likely specifies user-key behavior.
- The `tamma_sk_` prefix is an implementation convention from TS. `tamma_uk_` is a C# invention.
- Story alignment:
  - [x] Matches TS behavior (one-prefix `tamma_sk_`, base64url, scrypt)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — prefix/encoding/hash specifics never written down explicitly

Combined with 003, the user might argue that `tamma_uk_` is a *clarification*: users can tell by inspection which scope a key has. Reasonable UX choice. But it's drift from TS and compat with existing data is broken.

## 5. Status

- **Classification**: Behavioral drift (prefix, encoding, hash all silently changed).
- **What's needed to finish**:
  1. Decide policy: one prefix for all scopes (TS) or scope-distinguished prefixes (C#)? Document in a story or ADR.
  2. Align on encoding: base64url (recommend) or base64 (current C#).
  3. Align on hash: scrypt (TS, pairs with Finding 003) or SHA-256 (current C#).
  4. Apply consistently to `CreateServiceKey` (line 22) AND `CreateUserApiKey` (line 154) AND `RotateServiceKey` (line 51). Centralize in `ApiKeyGenerator` helper class.
  5. Backfill: existing TS-generated keys should either be grandfathered via dual-hash (Finding 003) or invalidated by a migration.
  6. Consider making prefix-length uniform (12 chars like TS, not 16 like C#).
- **Is it "just a stub" or is scope missing?** Scope was implemented but with different conventions. Drift.
- **Blockers**: Finding 003 (hash algorithm alignment).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` (3 key-creation methods).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyGenerator.cs` (centralize `NewKey()` + `Hash(key)` + `Prefix(key)`).
- Tests to add:
  - `ApiKeyGenerator_NewKey_StartsWithTammaSkPrefix` (or chosen prefix).
  - `ApiKeyGenerator_NewKey_UsesBase64UrlCharset` — fails if `+`, `/`, `=` appear.
  - `ApiKeyGenerator_Hash_MatchesScryptParams` (if 003 direction chosen).
  - `CreateServiceKey_UsesGenerator`.
  - `CreateUserApiKey_UsesGenerator`.
- Estimated effort: 0.5h
  - Generator + 3 call-site updates: 20m
  - Tests: 10m

## References

- TS source: `packages/api/src/auth/api-key.ts:14-52`, `packages/api/src/routes/users/api-key-routes.ts:41-54` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:17-39, 49-56, 148-171`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (generic); `docs/stories/epic-16/16-2-user-management-api.md` (referenced, not quoted)
- Related findings: `003-api-key-hash-algorithm.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: AdminEndpoints CreateServiceKey, RotateServiceKey, and CreateUserApiKey all route through ApiKeyHasher (one prefix `tamma_sk_`, base64url, SHA-256, 12-char display prefix).

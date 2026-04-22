# Finding 003: API key hash algorithm incompatibility (scrypt vs SHA-256)

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (persisted hashes unverifiable)
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/api-key.ts`.

- File: `packages/api/src/auth/api-key.ts:1-60`
- Contract: API keys are `tamma_sk_<base64url-of-32-random-bytes>`. The stored `key_hash` column is the hex-encoded output of `scrypt(key, "tamma-api-key-hash-v1", 32, {N:16384, r:8, p:1})`. The fixed salt makes lookups deterministic; the security budget comes from the 256-bit random key.
- Key code:

```typescript
// packages/api/src/auth/api-key.ts:35-52 (9e9a57c~1)
const API_KEY_PREFIX = 'tamma_sk_';
const HASH_SALT = 'tamma-api-key-hash-v1';
const SCRYPT_COST = 16384;

export function generateApiKey(): string {
  const random = randomBytes(KEY_BYTES).toString('base64url');
  return `${API_KEY_PREFIX}${random}`;
}

export function hashApiKey(key: string): string {
  const derived = scryptSync(key, HASH_SALT, SCRYPT_KEY_LENGTH, {
    N: SCRYPT_COST, r: SCRYPT_BLOCK_SIZE, p: SCRYPT_PARALLELIZATION,
  });
  return derived.toString('hex');
}
```

- Dependencies: Node `crypto.scryptSync`.
- Callers in TS: unified-auth middleware (`auth/unified-auth.ts:64`), api-key-auth plugin (`auth/api-key-auth.ts:43`), admin user-key routes (`routes/users/api-key-routes.ts:43`), installation rotation.
- Tests: none visible in the pre-delete tree beyond call-site tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs:31` and `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:22-23, 52-53, 154-155`.
- Contract: API keys are generated as `tamma_sk_<base64-of-32-random-bytes>` (note: plain base64, not base64url — see Finding 020) and hashed with `SHA256(UTF8(key))` → hex.
- Key code (both the auth handler and the key creator use SHA-256):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs:27-35
var rawKey = headerValue["ApiKey ".Length..].Trim();
if (string.IsNullOrEmpty(rawKey))
    return AuthenticateResult.Fail("Empty API key");

var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

using var scope = serviceProvider.CreateScope();
var apiKeyRepo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
var apiKey = await apiKeyRepo.GetByHashAsync(keyHash);
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:22-23 (CreateServiceKey)
var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
```

- Dependencies: `System.Security.Cryptography.SHA256`.
- Tests: No test exercises the handler against a scrypt-format hash.

## 3. The gap

Concrete behavioral difference.

- TS wrote: `key_hash = scryptHex('tamma_sk_...', 'tamma-api-key-hash-v1', {N:16384, r:8, p:1}, 32)` — a 64-character hex string.
- C# writes: `key_hash = lower(hex(SHA256('tamma_sk_...')))` — also a 64-character hex string, but structurally unrelated to the scrypt output for the same input key.
- For a client presenting a TS-generated key (`tamma_sk_XYZ`) to a C# endpoint: C# computes `SHA256("tamma_sk_XYZ")`, queries `api_keys.key_hash = <sha256>`, finds no row, returns 401 "Invalid API key". The same key against the TS code would hash to a completely different value (scrypt output) that was present in the table.
- In production with existing data: every persisted `api_keys` row, every `user_api_keys` row (see Finding 024), every `github_installations.api_key_hash` column (migration 001) contains a scrypt digest. After cutover, every `ApiKey` header auth attempt returns 401. This affects:
  - Every CLI runner authenticating via `ApiKey tamma_sk_...` (installation-scoped keys).
  - Every service-to-service call from Elsa with a stored service key.
  - Every dashboard user with personal API keys for headless tooling.
- Error paths:
  - TS: `ApiKeyStore.findByKeyHash(scryptHex(key))` returns the row → authenticated.
  - C#: `ApiKeyRepository.GetByHashAsync(sha256Hex(key))` returns null → `AuthenticateResult.Fail("Invalid API key")` → 401.

Note the additional asymmetry: C# also reads the `Authorization: ApiKey ...` header scheme (line 24 of the handler), while TS `unified-auth.ts:48` read `Authorization: Bearer ...`. Even if the hashes matched, the header scheme would diverge — covered in Finding 029.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Story AC 2 (line 32): *"All keys are stored as hashed values in `api_keys.key_hash` (scrypt/bcrypt; never plaintext)"*.
- Story §95 of 16-7 and Story §43 of 18-2 do not pin an algorithm for API keys, but 16-7 explicitly says "scrypt/bcrypt" — not SHA-256.
- Story alignment:
  - [x] Matches TS behavior (scrypt)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

C# is a regression. Story explicitly mentions scrypt.

Note: there is a defensible argument for moving API-key hashing to a fast hash (SHA-256) — the entropy lives in the 256-bit random key, not the password, so a memory-hard KDF is overkill. That argument has merit but was never written down as a design change. The regression here is that the format silently changed.

## 5. Status

- **Classification**: Behavioral drift (format change with no data-migration path).
- **What's needed to finish**:
  1. Choose a direction: either (a) restore scrypt for compatibility with persisted hashes, or (b) keep SHA-256 and write a migration that rehashes by re-issuing keys (impossible without access to plaintext — so must be: invalidate all keys and force re-issuance).
  2. If (a): replace `SHA256.HashData(...)` in `ApiKeyAuthHandler.cs:31` and `AdminEndpoints.cs:22-23, 52-53, 154-155` with a scrypt wrapper using Konscious (it ships scrypt alongside argon2 in `Konscious.Security.Cryptography.Scrypt`) or a small inline implementation.
  3. If (b): add deployment runbook step "invalidate all api_keys" and accept the downtime.
- **Is it "just a stub" or is scope missing?** Scope was understood (hash-and-lookup); the hash algorithm was silently simplified. The audit calls this out as drift.
- **Blockers**: Finding 029 (missing suspended-check, X-Tenant-Id) and Finding 020 (`tamma_uk_` prefix for user keys) both touch the same files.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` (5 call sites).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyHasher.cs` (centralize the hash fn so the 5 call sites match).
- Tests to add:
  - `ApiKeyHasherTests.Hash_WithKnownTsFixture_MatchesExpectedHex` — use a real scrypt hash computed via Node to verify compat.
  - `ApiKeyAuthHandlerTests.Authenticate_WithTsGeneratedKey_Succeeds` (fixture).
  - `AdminEndpointsTests.CreateServiceKey_StoresScryptHash` (re-hash check).
- Estimated effort: 2h
  - Central hasher + 5 call-site updates: 1h
  - Fixture tests from Node-generated hashes: 1h

## References

- TS source: `packages/api/src/auth/api-key.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (AC 2)
- Related findings: `001-password-hash-scrypt-vs-argon2.md` (same pattern, different table), `020-admin-create-user-api-key-format.md`, `024-user-api-keys-legacy-table-orphan.md`, `029-unified-auth-missing.md`
- Archived SQL migration: `database/archived-sql-migrations/009_unified_api_keys.sql` (creates `api_keys.key_hash` column)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: ApiKeyHasher centralizes generation/hash. ApiKeyAuthHandler tries SHA-256 first, falls back to legacy scrypt hex via ApiKeyHasher.LegacyScryptHash so persisted TS keys still verify.

# Finding 001: Password hash algorithm incompatibility (scrypt vs Argon2id)

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (wire format incompatible with persisted data)
**Estimated port effort**: 4h (dual-verify fallback only — full migration requires a rehash-on-login path)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/password.ts`.

- File: `packages/api/src/auth/password.ts:1-166`
- Contract: Hashes passwords via Node's native `crypto.scrypt` with OWASP-recommended parameters (N=16384, r=8, p=1, keylen=32, salt=16). The stored hash is the string `scrypt:N:r:p:keylen:saltHex:derivedHex` (7 colon-separated fields). `verifyPassword` parses the stored string, re-derives with the same params, and compares via `timingSafeEqual`.
- Key code:

```typescript
// packages/api/src/auth/password.ts:118-128 (9e9a57c~1)
export async function hashPassword(password: string): Promise<string> {
  const salt = randomBytes(SALT_LENGTH);
  const derived = (await scryptAsync(password, salt, SCRYPT_KEY_LENGTH, {
    N: SCRYPT_N, r: SCRYPT_R, p: SCRYPT_P,
  })) as Buffer;
  return `scrypt:${SCRYPT_N}:${SCRYPT_R}:${SCRYPT_P}:${SCRYPT_KEY_LENGTH}:${salt.toString('hex')}:${derived.toString('hex')}`;
}
```

```typescript
// packages/api/src/auth/password.ts:135-141 (9e9a57c~1)
export async function verifyPassword(password: string, storedHash: string): Promise<boolean> {
  const parts = storedHash.split(':');
  if (parts.length !== 7 || parts[0] !== 'scrypt') {
    return false;
  }
  // ...re-derive and timingSafeEqual...
```

- Dependencies: Node built-in `crypto`. No npm package.
- Tests: `packages/api/src/auth/password.test.ts` (round-trip + strength validation).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Auth/PasswordService.cs:1-57`
- Contract: Hashes via `Konscious.Security.Cryptography.Argon2id` with memory=65536 KiB, iterations=3, parallelism=4, 16-byte salt, 32-byte output. The stored hash is `$argon2id$v=19$m=65536,t=3,p=4$<saltBase64>$<hashBase64>` (6 `$`-separated fields). `VerifyPassword` rejects anything that doesn't start with the exact `$argon2id$` marker.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Auth/PasswordService.cs:20-44
public string HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(SaltLength);
    var hash = ComputeHash(password, salt);
    return $"$argon2id$v=19$m={MemorySize},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
}

public bool VerifyPassword(string password, string hash)
{
    try
    {
        var parts = hash.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id") return false;
        // ...
    }
    catch { return false; }
}
```

- Dependencies: NuGet `Konscious.Security.Cryptography`.
- Tests: No tests directly exercising `VerifyPassword` against a `scrypt:`-prefixed hash — the unit tests hash-then-verify within the same algorithm, so this gap is invisible.

## 3. The gap

Concrete behavioral difference.

- TS did: `hashPassword('secret')` → `"scrypt:16384:8:1:32:<saltHex>:<derivedHex>"`.
- C# does: `HashPassword("secret")` → `"$argon2id$v=19$m=65536,t=3,p=4$<saltB64>$<hashB64>"`.
- For a user whose `users.password_hash` was written by the TS code and who now presents the correct password, `PasswordService.VerifyPassword(pw, row.PasswordHash)` returns `false` because `parts[1] != "argon2id"` — the first branch of the method short-circuits on line 32 before any cryptographic work.
- In production: every email+password user created by the TypeScript API is permanently locked out after cutover. Their `password_hash` column contains a `scrypt:...` string that no C# verify path will ever accept. They see "Invalid email or password" on login and, because they're now also unverified-aware-less (see Finding 006), cannot self-recover — the password-reset flow rewrites the hash with argon2id but the reset email link itself is also affected by Finding 005 (stub verify) and Finding 014 (no rate limit). Net effect: mass login failure on day 1.

Error paths:
- TS `verifyPassword` with a malformed stored hash → returns `false` → login endpoint returns 401 "Invalid email or password".
- C# `VerifyPassword` with a scrypt-format hash → returns `false` → same 401. Indistinguishable from a wrong password; users cannot diagnose.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-1-user-registration-email-verification.md`
- Story AC 2: *"Password hashing uses argon2id with OWASP-recommended parameters (memory: 19 MiB, iterations: 2, parallelism: 1)"*.
- Subtask 2.2: *"Configure argon2id parameters: `{ type: argon2id, memoryCost: 19456, timeCost: 2, parallelism: 1 }`"*.
- Story alignment:
  - [ ] Matches TS behavior
  - [x] Matches C# (algorithm) — but **with different parameters**: story says m=19456 t=2 p=1; C# uses m=65536 t=3 p=4. So C# matches the *algorithm family* but not the *parameter set*.
  - [x] Describes a third behavior (parameters)

The TS implementation went with scrypt against its own story's stated intent (OWASP lists both as acceptable, but the story said argon2id). So the C# choice of argon2id is directionally correct per spec; it's the wire-format incompatibility with all existing data that makes this P0.

## 5. Status

- **Classification**: Behavioral drift (the function works; it just can't read data written by its predecessor).
- **What's needed to finish**:
  1. Add a `scrypt:`-prefix fallback branch to `PasswordService.VerifyPassword` that reproduces the Node scrypt derivation (N=16384, r=8, p=1, keylen=32, hex encoding) and compares with `CryptographicOperations.FixedTimeEquals`. .NET exposes scrypt via `BCrypt.Net.Core` or a manual `BCrypt.Generate` — neither is in the current NuGet set, so a small scrypt implementation or a fresh NuGet dep is required.
  2. On successful scrypt-verify, call `HashPassword` and persist the new argon2id hash (rehash-on-login migration). Wire this in the `Login` endpoint.
  3. Align argon2 parameters with story 18-1 AC 2: m=19456, t=2, p=1 — or update story to reflect the stronger parameters chosen (m=65536, t=3, p=4).
- **Is it "just a stub" or is scope missing?** The scope was understood (login must verify a persisted hash) and implemented — but implemented for a format that has never been written. This is drift.
- **Blockers**: None. Dual-verify ships independently. If argon2 params are tightened, no existing data is invalidated (only newly-hashed rows).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Auth/PasswordService.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (add rehash-on-successful-scrypt-verify).
- Files to create: None; scrypt verification can be inlined using `System.Security.Cryptography.Rfc2898DeriveBytes` is NOT scrypt — need `Konscious.Security.Cryptography.Scrypt` or a fresh dep.
- Tests to add:
  - `PasswordServiceTests.VerifyPassword_WithScryptFormatHash_ReturnsTrue` — fixture hash produced by the TS scrypt params.
  - `PasswordServiceTests.VerifyPassword_WithScryptFormatHash_WrongPassword_ReturnsFalse`.
  - `AuthEndpointsTests.Login_WithScryptHashedUser_RehashesToArgon2`.
- Estimated effort: 4h
  - Add scrypt fallback + NuGet: 2h
  - Rehash-on-login wiring: 1h
  - Tests with real TS-format fixtures: 1h

## References

- TS source: `packages/api/src/auth/password.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Auth/PasswordService.cs`
- Story: `docs/stories/epic-18/18-1-user-registration-email-verification.md` (AC 2, subtask 2.2)
- Related findings: `003-api-key-hash-algorithm.md` (same scrypt→SHA256 shape for API keys), `013-password-strength-validation-missing.md`
- Archived SQL migration: `database/archived-sql-migrations/018_user_auth_fields.sql` (adds `password_hash TEXT` column — format is agnostic at the schema level, so the column itself is portable)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: PasswordService now verifies scrypt-format hashes using BouncyCastle SCrypt. Login transparently rehashes to argon2id via NeedsRehash + UpdatePasswordHashAsync.

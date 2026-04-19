# Finding 012: Login timing oracle (no dummy hash for unknown user)

**Scope**: auth
**Severity**: P3 (correctness / security hardening)
**Status**: Behavioral drift (one conditional branch diverges)
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/login.ts`.

- File: `packages/api/src/routes/auth/login.ts:96-111`.
- Contract: When the `getUserByEmail` lookup returns null (or the user has no `passwordHash`), the handler intentionally runs `verifyPassword` against a constant dummy hash so the request latency matches the known-user branch. This prevents an attacker from enumerating valid accounts by timing a bulk of `POST /login` requests with different emails.
- Key code:

```typescript
// packages/api/src/routes/auth/login.ts:96-111 (9e9a57c~1)
// Look up user
const user = await userStore.getUserByEmail(normalizedEmail);

if (!user || !user.passwordHash) {
  // Constant-time path: always hash something to prevent timing attacks
  await verifyPassword(password, 'scrypt:32768:8:1:64:deadbeef:deadbeef');
  lockoutService.recordFailedAttempt(normalizedEmail);

  request.log.info({
    event: 'USER.LOGIN.FAILED',
    email: normalizedEmail,
    reason: 'user_not_found',
  }, 'Login failed');

  return reply.status(401).send({ error: 'Invalid email or password' });
}
```

- Dependencies: `verifyPassword` accepting arbitrary format strings and returning `false` without exception.
- Tests: Not visible as a named test, but the comment in the code flags the intent ("Constant-time path").

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:177-182`.
- Contract: If the user is null OR has no password hash OR the password doesn't verify, record a failed attempt and return 401. The null-user branch short-circuits BEFORE any hash work — `PasswordService.VerifyPassword` is not called when `user is null`.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:177-182
var user = await userRepo.GetByEmailAsync(req.Email.ToLowerInvariant());
if (user is null || user.PasswordHash is null || !passwordService.VerifyPassword(req.Password, user.PasswordHash))
{
    lockout.RecordFailedAttempt(req.Email);
    return Results.Unauthorized();
}
```

The `||` short-circuit evaluation means if `user is null`, the `VerifyPassword` call on the right-hand side is skipped entirely. Same for `PasswordHash is null`. Only for a known user with a known hash does the actual argon2 derivation execute.

- Dependencies: `PasswordService.VerifyPassword`.
- Tests: No timing-differential test.

## 3. The gap

- TS did: Run the scrypt derivation (N=32768 — higher cost than normal!) for every login attempt regardless of whether the user exists.
- C# does: Skip the argon2 derivation for nonexistent users; only real accounts pay the cost.

For a caller attempting credential-stuffing:
- TS login for `notreal@x.com`: ~200ms (scrypt N=32768 derivation).
- TS login for `real@x.com` (wrong password): ~200ms.
- C# login for `notreal@x.com`: ~5ms (no hash).
- C# login for `real@x.com` (wrong password): ~60ms (argon2 m=64MB, t=3, p=4).

The 55ms differential is clearly distinguishable over a few attempts. An attacker can confirm or deny email-address existence by timing a handful of requests. This enables:
- Enumerating registered users for phishing campaigns.
- Prioritizing which emails to target with password-guess attacks.
- Confirming credential-reuse: the victim's email from an unrelated breach is either in the DB or not.

Production observation: Without rate limiting on the registration endpoint either (Finding 014's sibling), this is a mass-enumeration channel.

Error paths:
- TS: 401 "Invalid email or password" — identical message regardless of which branch hit.
- C#: `Results.Unauthorized()` (bare 401, no body) — identical status but shorter latency for nonexistent accounts.

Severity note: the existing login-lockout service (`LoginLockoutService`) is per-email, which itself IS the normal anti-enumeration defense — lockout-per-email means a nonexistent email can be "locked" after 5 attempts, hiding whether it exists. That plus the timing gap, together, still leak before hitting the lockout.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story AC 3 (line 15): *"Invalid credentials return 401 with generic `'Invalid email or password'` (no enumeration)"*.
- Subtask 3.4 (line 47): *"Look up user by email; return 401 if not found (**constant-time path**)"* — explicit mention.
- Security section line 181: *"Constant-time comparison: Use `timingSafeEqual` for token and password hash comparisons"* — refers to hash equality, not the hash computation itself, but the spirit aligns with the subtask 3.4 constant-time path.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story's subtask 3.4 explicitly says "constant-time path" — C# broke this.

## 5. Status

- **Classification**: Behavioral drift. The story-mandated constant-time path was silently dropped.
- **What's needed to finish**:
  1. Separate the null-user branch: if `user is null || user.PasswordHash is null`, run `passwordService.VerifyPassword(req.Password, DummyHash)` (discard result), then 401.
  2. Provide a `DummyHash` constant — a real argon2id hash of a random throwaway password, stored as a `const string` in `PasswordService.cs`.
  3. The dummy hash must use the same parameters as the current production params (m=65536, t=3, p=4) so the latency matches.
- **Is it "just a stub" or is scope missing?** Specific scope was visible (the subtask explicitly named "constant-time path"); implementation ignored it. Drift.
- **Blockers**: None.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (Login), `apps/tamma-elsa/src/Tamma.Api/Auth/PasswordService.cs` (expose a dummy).
- Files to create: None.
- Tests to add:
  - `Login_UnknownUser_StillCallsVerifyPassword` (mock `IPasswordService`, assert called once).
  - `Login_PerformanceTest_NonexistentVsRealUserLatencyWithinTolerance` — optional but defensible.
- Estimated effort: 0.5h
  - Code change: 15m
  - Unit test with IPasswordService mock: 15m

## References

- TS source: `packages/api/src/routes/auth/login.ts:96-111` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:177-182`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 3, subtask 3.4)
- Related findings: `001-password-hash-scrypt-vs-argon2.md` (the `DummyHash` must share the algorithm)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: Login calls VerifyPassword(req.Password, DummyHash) on the user-not-found branch so the argon2id cost is paid regardless of whether the email exists.

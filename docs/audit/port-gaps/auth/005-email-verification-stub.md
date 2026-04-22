# Finding 005: Email verification endpoint is a no-op stub

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 4h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/register.ts`.

- File: `packages/api/src/routes/auth/register.ts:115-165` (POST /api/v1/auth/verify-email).
- Contract: Accept `{ token }`, SHA-256 hash it, look up a user row whose `email_verification_token_hash` matches, verify expiry, flip `email_verified=true`, clear token fields, emit `USER.EMAIL_VERIFIED.SUCCESS`, return 200.
- Key code:

```typescript
// packages/api/src/routes/auth/register.ts:125-164 (9e9a57c~1)
app.post('/api/v1/auth/verify-email', async (request, reply) => {
  const { token } = request.body ?? {};
  if (!token) {
    return reply.status(400).send({ error: 'token is required' });
  }

  const tokenHash = createHash('sha256').update(token).digest('hex');
  const user = await findUserByVerificationTokenHash(userStore, tokenHash);

  if (!user) {
    return reply.status(400).send({ error: 'Invalid or expired verification token' });
  }
  if (!user.emailVerificationExpiresAt || new Date(user.emailVerificationExpiresAt) < new Date()) {
    return reply.status(400).send({ error: 'Verification token has expired' });
  }
  if (user.emailVerified) {
    return reply.status(400).send({ error: 'Email already verified' });
  }

  await userStore.setEmailVerified(user.id);

  request.log.info({
    event: 'USER.EMAIL_VERIFIED.SUCCESS',
    userId: user.id,
  }, 'Email verified');

  return reply.send({ message: 'Email verified successfully' });
});
```

- Dependencies: `IUserStore.setEmailVerified`, a repository-scanning helper `findUserByVerificationTokenHash` that either walks the in-memory map or runs `SELECT * FROM users WHERE email_verification_token_hash = $1 AND deleted_at IS NULL`.
- Tests: `packages/api/src/routes/auth/register.test.ts` asserts valid-token, expired-token, already-verified, invalid-token paths.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:103-110`.
- Contract: Accepts `{ token }`, hashes it, then returns 200 with a success message — without touching the database.
- Key code (**eight lines including whitespace**):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:103-110
public static async Task<IResult> VerifyEmail(
    VerifyEmailRequest req,
    IUserRepository userRepo)
{
    var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Token))).ToLowerInvariant();
    // We'd need a lookup by verification token hash — for now return OK
    return Results.Ok(new { message = "Email verified successfully" });
}
```

- The comment on line 108 (*"We'd need a lookup by verification token hash — for now return OK"*) is an explicit admission that the implementation is incomplete. The `async` modifier is even unnecessary — the method has no awaits.
- Dependencies: None actually exercised; `IUserRepository` is injected but never called.
- Tests: None exercise this endpoint (the test would pass regardless — it always returns 200).

## 3. The gap

- TS did: Lookup → expiry check → already-verified check → mutate row → emit event.
- C# does: Accept any token, return 200. The user's `email_verified` column remains `false` forever.

For a caller sending `POST /api/v1/auth/verify-email { token: "expired-token-from-2023" }`:
- TS returned `400 { error: "Invalid or expired verification token" }`.
- C# returns `200 { message: "Email verified successfully" }` — a **false-positive confirmation**, which is worse than being broken.

In production:
- Users click the link in their verification email.
- The dashboard displays "Email verified!"
- The user tries to log in — if Finding 006 (login doesn't check `EmailVerified`) is fixed, login says "Please verify your email" (because the DB flag is still false). The user is trapped: the dashboard swears they're verified; the login refuses.
- If Finding 006 is NOT fixed (the current situation), they can log in anyway and never notice — but the `email_verified` column in the DB is perpetually false, so any downstream report / audit / analytics treats them as unverified.

Also note: a malicious user can `POST /api/v1/auth/verify-email { token: "anything-at-all" }` and get a 200 back. They cannot actually verify anyone else's email (because the flag isn't mutated), but the endpoint's response misrepresents the system state.

Error paths:
- TS: 400 "Invalid or expired verification token" / 400 "Verification token has expired" / 400 "Email already verified".
- C#: always 200 regardless of input. No error path exists.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-1-user-registration-email-verification.md`
- Story AC 7 (line 19): *"Verification endpoint `POST /api/v1/auth/verify-email` accepts `{ token }`, marks user as verified, returns success"*.
- Story subtasks 5.1-5.7 (line 65-72):
  > *"Create `POST /api/v1/auth/verify-email` in same route file / Hash incoming token with SHA-256, look up user by hashed token / Check token expiry (24 hours) / Set `emailVerified: true`, clear token fields / Emit `USER.EMAIL_VERIFIED.SUCCESS` event / Return 200 with `{ message: 'Email verified' }` / Write tests for valid token, expired token, already-used token, invalid token"*
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

The story is unambiguous. C# ports the return message but nothing else.

## 5. Status

- **Classification**: Not-yet-implemented (stub). The inline comment `// We'd need a lookup by verification token hash — for now return OK` flags this explicitly.
- **What's needed to finish**:
  1. Add `IUserRepository.GetByEmailVerificationTokenHashAsync(string tokenHash)` and implementation using `FirstOrDefaultAsync(u => u.EmailVerificationTokenHash == tokenHash && u.DeletedAt == null)`.
  2. Add `IUserRepository.SetEmailVerifiedAsync(Guid userId)` — flip `EmailVerified`, null out `EmailVerificationTokenHash` and `EmailVerificationExpiresAt`.
  3. Rewrite the endpoint: hash → lookup → null-check 400 → expiry-check 400 → already-verified-check 400 → set verified → emit event → 200.
  4. Write the four test cases per story subtask 5.7.
- **Is it "just a stub" or is scope missing?** Literally a stub. Scope was fully understood (the comment proves it) — not written.
- **Blockers**: Finding 022 (`IUserRepository` missing methods) covers the repo additions; this endpoint fix depends on that.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (VerifyEmail method), `apps/tamma-elsa/src/Tamma.Data/Repositories/IUserRepository.cs`, `apps/tamma-elsa/src/Tamma.Data/Repositories/UserRepository.cs`.
- Files to create: None.
- Tests to add:
  - `AuthEndpointsTests.VerifyEmail_ValidToken_SetsEmailVerified`.
  - `AuthEndpointsTests.VerifyEmail_ExpiredToken_Returns400`.
  - `AuthEndpointsTests.VerifyEmail_AlreadyVerified_Returns400`.
  - `AuthEndpointsTests.VerifyEmail_InvalidToken_Returns400`.
  - `UserRepositoryTests.GetByEmailVerificationTokenHashAsync_Found_ReturnsUser`.
- Estimated effort: 4h
  - Repo method additions: 1h
  - Endpoint rewrite: 1h
  - Test suite: 1.5h
  - Event emission wiring (if event store path is used): 0.5h

## References

- TS source: `packages/api/src/routes/auth/register.ts:115-165` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:103-110`
- Story: `docs/stories/epic-18/18-1-user-registration-email-verification.md` (AC 7, Task 5)
- Related findings: `006-login-email-verified-check-missing.md`, `022-user-repository-missing-methods.md`
- Archived SQL migration: `database/archived-sql-migrations/018_user_auth_fields.sql` (adds `email_verified`, `email_verification_token_hash`, `email_verification_expires_at` columns)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: VerifyEmail now hashes the token, calls GetByEmailVerificationTokenHashAsync, branches on null/expired/already-verified, and calls SetEmailVerifiedAsync on success.

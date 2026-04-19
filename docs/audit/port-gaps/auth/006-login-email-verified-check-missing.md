# Finding 006: Login does not check EmailVerified

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Incomplete (missing one check block)
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/login.ts`.

- File: `packages/api/src/routes/auth/login.ts:135-137`.
- Contract: After verifying the password, before issuing a JWT, check `user.emailVerified`. If false, return 403 with the message `"Please verify your email"`.
- Key code:

```typescript
// packages/api/src/routes/auth/login.ts:133-138 (9e9a57c~1)
// Check email verification
if (!user.emailVerified) {
  return reply.status(403).send({ error: 'Please verify your email' });
}

// Reset lockout on success
lockoutService.resetAttempts(normalizedEmail);
```

- Dependencies: `user.emailVerified` boolean column.
- Tests: `packages/api/src/routes/auth/login.test.ts` has a test case asserting 403 for an unverified user.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:177-205`.
- Contract: After password verify → lockout reset → tenant resolution → JWT issue. No reference to `user.EmailVerified` anywhere in the login path. The only guard is `IsActive` (line 184).
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:177-187
var user = await userRepo.GetByEmailAsync(req.Email.ToLowerInvariant());
if (user is null || user.PasswordHash is null || !passwordService.VerifyPassword(req.Password, user.PasswordHash))
{
    lockout.RecordFailedAttempt(req.Email);
    return Results.Unauthorized();
}

if (!user.IsActive)
    return Results.Json(new { error = "Account deactivated" }, statusCode: 403);

lockout.ResetAttempts(req.Email);
// ... immediately proceeds to tenant/role/JWT issuance
```

Notice the gap at line 186-187: verify-password → is-active → lockout-reset. Nowhere is `user.EmailVerified` evaluated.

- Dependencies: `User.EmailVerified` column exists on the entity (line 12 of `User.cs`) — it's just never read.
- Tests: No test asserts the 403 path.

## 3. The gap

- TS did: 403 "Please verify your email" before minting a JWT.
- C# does: Mint the JWT anyway. Unverified users are logged in.

For a caller registering at `POST /register` (which creates `emailVerified=false`) and immediately calling `POST /login`:
- TS returns 403 "Please verify your email".
- C# returns 200 with a valid JWT + cookie. The caller has a working session.

Production consequences:
- **Spam/abuse vector**: anyone can register with `fake@fake.com`, skip the email verification step (which is also broken — see Finding 005), and immediately access `/api/*` endpoints as a `member` of their auto-created personal tenant. They can then call any member-level endpoint (`/api/dashboard`, `/api/engine`, `/api/workflows`, etc.).
- **Compliance regression**: GDPR / SOC2 typically require email ownership verification before account usage. The platform cannot demonstrate it.
- **Billing footgun**: If the user is on a metered plan, running LLM calls before email verification produces charges against an unverified email.

Error paths:
- TS: 403 `{ error: "Please verify your email" }`.
- C#: No error path exists; login succeeds.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story AC 2 (line 14): *"Unverified users receive 403 with `'Please verify your email'` on login attempt"*.
- Subtask 3.6 (line 49): *"Check `emailVerified`; return 403 if false"*.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Explicitly mandated by the story. C# just forgot to port it.

## 5. Status

- **Classification**: Incomplete (one 3-line if-block omitted from an otherwise-ported endpoint).
- **What's needed to finish**:
  1. After `lockout.ResetAttempts(req.Email);` on line 187, add:
     ```csharp
     if (!user.EmailVerified)
         return Results.Json(new { error = "Please verify your email" }, statusCode: 403);
     ```
  2. Write the regression test.
- **Is it "just a stub" or is scope missing?** Scope was understood (the column was added to the entity and migration). Just the one check was omitted.
- **Blockers**: None, but closing this before Finding 005 (verify-email stub) is fixed would lock out every registered user permanently (since verify-email cannot flip the flag). Fix 005 first.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (add one if-block in `Login`).
- Files to create: None.
- Tests to add:
  - `AuthEndpointsTests.Login_UnverifiedUser_Returns403`.
  - `AuthEndpointsTests.Login_VerifiedUser_Succeeds` (ensure the happy path still works).
- Estimated effort: 0.5h
  - Code change: 5m
  - Tests: 25m

## References

- TS source: `packages/api/src/routes/auth/login.ts:133-138` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:158-231` (specifically the gap at :186-187)
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 2, subtask 3.6)
- Related findings: `005-email-verification-stub.md` (must be fixed before this, else users can never verify)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: Login returns 403 with `Please verify your email` when EmailVerified is false.

# Finding 014: No rate limit on `/resend-verification` and `/password-reset/request`

**Scope**: auth
**Severity**: P2 (abuse / DoS hardening)
**Status**: Incomplete
**Estimated port effort**: 2h (total across both endpoints)

## 1. What's in TS

Pre-delete snapshots at `git show 9e9a57c~1:packages/api/src/routes/auth/register.ts` and `password-reset.ts`.

- `resend-verification` in `register.ts:176-190, 252-266`:

```typescript
// packages/api/src/routes/auth/register.ts:32-33 (9e9a57c~1)
const resendRateLimit = new Map<string, number[]>();
const RESEND_MAX_PER_HOUR = 3;

// register.ts:176-190 inside the handler
if (isResendRateLimited(normalizedEmail)) {
  return reply.status(429).send({ error: 'Too many requests. Please try again later.' });
}
// ... on success path ...
recordResendAttempt(normalizedEmail);
```

- `password-reset/request` in `password-reset.ts:37-38, 67-70, 167-181`:

```typescript
// packages/api/src/routes/auth/password-reset.ts:37-38
const resetRateLimit = new Map<string, number[]>();
const RESET_MAX_PER_EMAIL_PER_HOUR = 3;

// handler body
if (isResetRateLimited(normalizedEmail)) {
  return reply.status(429).send({ error: 'Too many reset requests. Please try again later.' });
}
// ... on success path ...
recordResetAttempt(normalizedEmail);
```

- Both use an in-process Map keyed by email, keeping a sliding 1-hour window of timestamps, returning 429 when ≥3 entries are present in the last hour.
- The `recordXyzAttempt` is called only after the success branch executes (invalid/unverified emails don't consume quota, to prevent enumeration via rate-limit status).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- `ResendVerification` in `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:112-156`.
- `PasswordResetRequest` in `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:280-323`.
- Neither method references any rate-limit service. Both happily dispatch emails on every call.
- Key code (ResendVerification body — no rate-limit gate):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:119-156 (abridged)
const string CannedResponseMessage =
    "If the email exists, a verification link has been sent";

if (string.IsNullOrWhiteSpace(req.Email))
    return Results.Ok(new { message = CannedResponseMessage });

var email = req.Email.ToLowerInvariant();
var user = await userRepo.GetByEmailAsync(email);

if (user is not null && !user.EmailVerified)
{
    var verificationToken = Guid.NewGuid().ToString("N");
    user.EmailVerificationTokenHash = HashToken(verificationToken);
    user.EmailVerificationExpiresAt = DateTime.UtcNow.AddHours(24);
    await userRepo.UpdateAsync(user);
    // ... sends email ...
}

return Results.Ok(new { message = CannedResponseMessage });
```

- `PasswordResetRequest` (at line 280) is the same shape — it always writes a new reset token and dispatches an email when the user exists.
- Dependencies: `IEmailService.SendAsync` is called unconditionally; no throttle.
- Tests: None.

## 3. The gap

- TS: caller posting `POST /resend-verification { email: "victim@company.com" }` 10 times in 5 minutes gets three emails, then 429s.
- C#: the same caller gets 10 emails.
- For `password-reset/request`: same multiplier.

Production impact:
- **Mail bombing attack**: an attacker sending 1,000 rapid requests to `/resend-verification` with a valid user's email mail-bombs the user and floods the SMTP relay. The user's inbox is rendered unusable; legitimate platform mail lands in the same bucket and gets rate-limited by the MTA.
- **Cost**: every call that reaches the email service counts against the SMTP vendor's quota. At 100,000 abusive calls, this is real money.
- **Token churn**: every `resend-verification` call also overwrites `email_verification_token_hash`, invalidating the previous email. Legitimate users clicking an older link after a flood of resets get "Invalid or expired" errors.
- **Pair with Finding 015**: `password-reset/request` sending to GitHub-only users compounds with no rate limit — an attacker can mail-bomb a GitHub user indefinitely without burning any real "has-email-account" budget.

Error paths:
- TS: 429 `{ error: 'Too many requests. Please try again later.' }`.
- C#: always 200 with the canned message.

## 4. Gap from stories

- Referenced story (resend): `docs/stories/epic-18/18-1-user-registration-email-verification.md`
- Story AC 8 (line 20): *"Resend endpoint `POST /api/v1/auth/resend-verification` accepts `{ email }`, rate-limited to 3 requests per hour per email"*.
- Subtask 6.5 (line 79): *"Rate limit: 3/hour/email"*.
- Referenced story (password reset): `docs/stories/epic-18/story-18-6/18-6-password-reset.md` (Story 18-6).
- Story 18-6 has rate-limit language — TS implemented 3/hour/email.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

AC 8 is explicit.

## 5. Status

- **Classification**: Incomplete (functional baseline present, throttling omitted).
- **What's needed to finish**:
  1. Create an `IRateLimitService` with an in-process sliding-window impl keyed on `(scope, key)` — scope like `resend-verification` or `password-reset-request`, key like the lowercased email.
  2. Register as `Singleton` in DI.
  3. Call `IsLimited(scope, email)` before performing work; return 429 if true. Call `Record(scope, email)` after the work succeeds.
  4. Consider Valkey-backed implementation in Story 16-8 future direction (already tagged `TODO(story-16-8)` in TS — same direction applies here).
  5. In multi-instance production (Hetzner VPS currently runs single instance but horizontal scaling is planned), the in-process map does not sync across pods. Document this limitation.
- **Is it "just a stub" or is scope missing?** Scope explicitly understood in the story; implementation omitted. Drift.
- **Blockers**: None for single-instance. Multi-instance needs Valkey (Story 16-8).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (ResendVerification + PasswordResetRequest), `Program.cs` (register service).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Services/RateLimit/IRateLimitService.cs`, `InMemoryRateLimitService.cs`.
- Tests to add:
  - `InMemoryRateLimitServiceTests.ThreeRequests_NotLimited`.
  - `InMemoryRateLimitServiceTests.FourthRequest_Limited`.
  - `InMemoryRateLimitServiceTests.WindowExpires_ResetsCount`.
  - `ResendVerification_OverLimit_Returns429`.
  - `PasswordResetRequest_OverLimit_Returns429`.
  - `ResendVerification_UnknownEmail_DoesNotConsumeQuota` (enumeration guard).
- Estimated effort: 2h
  - Service + tests: 1h
  - Endpoint wiring: 30m
  - Endpoint tests: 30m

## References

- TS source: `packages/api/src/routes/auth/register.ts:32-33, 176-190, 252-266`; `packages/api/src/routes/auth/password-reset.ts:37-38, 67-70, 167-181` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:112-156, 280-323`
- Story: `docs/stories/epic-18/18-1-user-registration-email-verification.md` (AC 8, subtask 6.5); `docs/stories/epic-18/story-18-6/18-6-password-reset.md`
- Related findings: `015-password-reset-sends-to-github-only-users.md` (compounds with this)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (incl. distributed backend for multi-pod)
- **Commit**: `e56b04d` (initial in-process impl); distributed backend in a follow-up commit.
- **Notes**: `RateLimitService` (3/hour/email) is wired into ResendVerification (`resend-verification` scope) and PasswordResetRequest (`password-reset-request` scope). Quota only consumes on successful work to keep enumeration-safe. **Distributed backend**: `IDistributedRateLimitBackend` abstraction with two impls — `InMemoryDistributedRateLimitBackend` (default, sliding window, exact semantics) and `RedisDistributedRateLimitBackend` (multi-pod, StackExchange.Redis 2.12.14, atomic Lua INCR+EXPIRE script). DI picks Redis when `ConnectionStrings:Redis` is configured, otherwise falls through to the in-process impl. Behavioral contract tests cover both backends (Redis tests use Testcontainers). Valkey-compatible since the protocol is unchanged.

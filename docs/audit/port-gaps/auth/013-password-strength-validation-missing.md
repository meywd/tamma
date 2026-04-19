# Finding 013: Password strength validation missing in register + password-reset/confirm

**Scope**: auth
**Severity**: P2 (correctness / hardening)
**Status**: Incomplete
**Estimated port effort**: 1.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/password.ts`.

- File: `packages/api/src/auth/password.ts:73-112`.
- Contract: `validatePasswordStrength(password)` returns `{ valid: boolean, errors: string[] }`. Checks five criteria and a common-password block-list of 45 entries.
- Key code:

```typescript
// packages/api/src/auth/password.ts:73-112 (9e9a57c~1)
const MIN_PASSWORD_LENGTH = 8;
const MAX_PASSWORD_LENGTH = 128;
const HAS_UPPERCASE = /[A-Z]/;
const HAS_LOWERCASE = /[a-z]/;
const HAS_DIGIT = /\d/;
const COMMON_PASSWORDS = new Set([
  'password', '12345678', '123456789', '1234567890', 'qwerty123',
  // ... 45 total entries
]);

export function validatePasswordStrength(password: string): PasswordValidationResult {
  const errors: string[] = [];
  if (password.length < MIN_PASSWORD_LENGTH) errors.push('Password must be at least 8 characters');
  if (password.length > MAX_PASSWORD_LENGTH) errors.push('Password must be at most 128 characters');
  if (!HAS_UPPERCASE.test(password)) errors.push('Password must contain at least one uppercase letter');
  if (!HAS_LOWERCASE.test(password)) errors.push('Password must contain at least one lowercase letter');
  if (!HAS_DIGIT.test(password)) errors.push('Password must contain at least one digit');
  if (COMMON_PASSWORDS.has(password.toLowerCase())) errors.push('Password is too common');
  return { valid: errors.length === 0, errors };
}
```

- Callers:
  - `packages/api/src/routes/auth/register.ts:62-64` — rejects weak passwords at register with 400 + details.
  - `packages/api/src/routes/auth/password-reset.ts:121-123` — same check for new password on reset.
- Tests: `packages/api/src/auth/password.test.ts` (subtask 2.5 of Story 18-1).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:44-48` (Register); `:325-344` (PasswordResetConfirm).
- Contract: Register enforces only `password.Length >= 8`. PasswordResetConfirm enforces nothing — it hashes whatever the user sends.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:44-48 (Register)
if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
    return Results.BadRequest(new { error = "Email and password are required" });

if (req.Password.Length < 8)
    return Results.BadRequest(new { error = "Password must be at least 8 characters" });
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:325-348 (PasswordResetConfirm)
public static async Task<IResult> PasswordResetConfirm(
    PasswordResetConfirmDto req,
    IPasswordResetRepository resetRepo,
    IPasswordService passwordService,
    IUserRepository userRepo,
    IRefreshTokenRepository refreshTokenRepo)
{
    var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Token))).ToLowerInvariant();
    var token = await resetRepo.GetByTokenHashAsync(tokenHash);
    if (token is null || token.ConsumedAt is not null || token.ExpiresAt < DateTime.UtcNow)
        return Results.BadRequest(new { error = "Invalid or expired reset token" });

    var user = await userRepo.GetByIdAsync(token.UserId);
    if (user is null)
        return Results.BadRequest(new { error = "User not found" });

    user.PasswordHash = passwordService.HashPassword(req.NewPassword);  // no strength check
    await userRepo.UpdateAsync(user);
    // ...
}
```

- Dependencies: none — validation fn doesn't exist.
- Tests: None.

## 3. The gap

- TS rejected: `password123`, `PASSWORD`, `pass1234` (no upper), `ABC12345` (no lower), `Password` (no digit), `p@ssw0rd` (in common list), `short1` (< 8), `"a" * 129` (> 128).
- C# rejects: only `Length < 8`.
- For a caller posting `POST /register { password: "password" }`:
  - TS returns 400 with details including "Password is too common", "Password must contain at least one uppercase letter", "Password must contain at least one digit".
  - C# accepts "password" because it's 8 characters. User created, `password_hash = argon2id(...)`.
- For `POST /password-reset/confirm { newPassword: "1" }`:
  - TS returns 400 "Password too weak".
  - C# accepts `"1"` silently. The user's password is now literally the single digit 1.

Production impact: mass compromise-risk via dictionary attacks on accounts that set `password`, `12345678`, `qwerty12`, etc. No way to enforce organizational password policy.

Error paths:
- TS register: 400 `{ error: 'Password too weak', details: ['Password must contain at least one uppercase letter', ...] }`.
- C# register: 200 (user created).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-1-user-registration-email-verification.md`
- Story AC 4 (line 16): *"Password strength validated: minimum 8 characters, at least one uppercase, one lowercase, one digit; rejects top-1000 common passwords"*.
- Subtask 2.3 (line 39): *"Implement password strength validation function `validatePasswordStrength()`"*.
- Subtask 2.4 (line 40): *"Bundle top-1000 common passwords list for rejection"*.
- Subtask 4.2 (line 53): *"Validate input: email format, password strength, name length (2-100 chars)"*.
- For password-reset: `docs/stories/epic-18/story-18-6/18-6-password-reset.md` subtask 3.3 (line 43): *"Hash new password with argon2id, update user's `passwordHash`"* — does NOT explicitly require strength check (the story is less explicit here), but it would be inconsistent to allow weaker passwords via reset than via register.
- Story alignment:
  - [x] Matches TS behavior (register path)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior (C# allows 4 of 5 criteria through)
  - [ ] No story — for password-reset/confirm specifically, this is a spec gap

For register: regression vs explicit AC 4. For password-reset/confirm: likely spec gap that TS happened to fix ahead of spec.

## 5. Status

- **Classification**: Incomplete (one check out of five + block-list ported).
- **What's needed to finish**:
  1. Port `validatePasswordStrength` to C#: add a `PasswordStrengthValidator` class in `apps/tamma-elsa/src/Tamma.Api/Auth/` with static `Validate(string)` returning `(bool Valid, IReadOnlyList<string> Errors)`.
  2. Include the 45-entry common-password set (or expand to the top-1000 per story AC 4 — TS itself shipped only 45, so there's a matching gap to the story).
  3. Call from `Register` before `HashPassword`.
  4. Call from `PasswordResetConfirm` before `HashPassword`.
  5. Return 400 with `details` array on failure (match TS shape).
- **Is it "just a stub" or is scope missing?** Scope was understood for min-length; the rest dropped. Drift.
- **Blockers**: None.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (Register + PasswordResetConfirm).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/PasswordStrengthValidator.cs`; consider loading the common-password list from an embedded resource file `apps/tamma-elsa/src/Tamma.Api/Auth/common-passwords.txt`.
- Tests to add:
  - `PasswordStrengthValidatorTests.TooShort_Rejected`.
  - `PasswordStrengthValidatorTests.TooLong_Rejected`.
  - `PasswordStrengthValidatorTests.MissingUppercase_Rejected`.
  - `PasswordStrengthValidatorTests.MissingLowercase_Rejected`.
  - `PasswordStrengthValidatorTests.MissingDigit_Rejected`.
  - `PasswordStrengthValidatorTests.CommonPasswordRejected`.
  - `PasswordStrengthValidatorTests.StrongPassword_Accepted`.
  - `Register_WeakPassword_Returns400WithDetails`.
  - `PasswordResetConfirm_WeakPassword_Returns400`.
- Estimated effort: 1.5h
  - Validator + tests: 45m
  - Endpoint wiring: 15m
  - Endpoint tests: 30m

## References

- TS source: `packages/api/src/auth/password.ts:73-112`, `packages/api/src/routes/auth/register.ts:62-64`, `packages/api/src/routes/auth/password-reset.ts:121-123` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:44-48, 325-348`
- Story: `docs/stories/epic-18/18-1-user-registration-email-verification.md` (AC 4, subtask 2.3-2.4); `docs/stories/epic-18/story-18-6/18-6-password-reset.md` (subtask 3.3)
- Related findings: `001-password-hash-scrypt-vs-argon2.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: PasswordStrengthValidator ports the TS criteria + 45-entry common-password set. Wired into Register and PasswordResetConfirm; both return 400 with details on weak input.

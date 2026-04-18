# Finding 015: `password-reset/request` sends to GitHub-only users; silently flips auth_method

**Scope**: auth
**Severity**: P2 (correctness + subtle authz regression)
**Status**: Incomplete
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/password-reset.ts`.

- File: `packages/api/src/routes/auth/password-reset.ts:75-83`.
- Contract: If the looked-up user exists AND has `authMethod === 'github'`, the handler treats them the same as a non-existent user: return the canned "if it exists, a link was sent" message and do NOT generate a reset token, do NOT send an email, do NOT touch any user state.
- Key code:

```typescript
// packages/api/src/routes/auth/password-reset.ts:70-83 (9e9a57c~1)
const successMessage = 'If an account with that email exists, a reset link has been sent';

const user = await userStore.getUserByEmail(normalizedEmail);

// Don't send email if:
// - User doesn't exist
// - User is GitHub-only (no password to reset)
if (!user || user.authMethod === 'github') {
  return reply.send({ message: successMessage });
}

// Generate reset token ...
```

- Rationale (implicit): A GitHub-only user has no password; resetting the "password" would create one where none existed, effectively downgrading their auth posture from "GitHub OAuth only" to "can now log in with a password they chose via a link in email". This is an undocumented account-takeover path: whoever reads the email sent to the user's GitHub-public email sets a password they didn't want.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:280-323`.
- Contract: Any row returned by `GetByEmailAsync` is treated as a password-reset candidate. Token is generated, stored, and an email is dispatched. No check on `user.AuthMethod`.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:294-319
var email = req.Email.ToLowerInvariant();
var user = await userRepo.GetByEmailAsync(email);

if (user is not null)
{
    var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var tokenHash = HashToken(rawToken);
    var expiresAt = DateTime.UtcNow.AddHours(1);

    await resetRepo.CreateAsync(user.Id, tokenHash, expiresAt);

    var resetUrl = BuildResetUrl(config, rawToken);
    var message = EmailTemplates.PasswordResetEmail(user.Email, resetUrl) with
    {
        Template = "password-reset",
        TenantId = user.TenantId,
        UserId = user.Id,
    };
    var txnId = await emailService.SendAsync(message);
    // ...
}
```

- The `PasswordResetConfirm` method (line 325) then writes `user.PasswordHash = passwordService.HashPassword(req.NewPassword)` without any check on `user.AuthMethod`, silently flipping the user from a `github`-authenticated account to one that ALSO accepts password login.

## 3. The gap

Composite behavior:

- TS: request reset on a GitHub-only account → no email, no state change. Confirm cannot happen.
- C#: request reset on a GitHub-only account → email sent → user (or an attacker with access to their inbox) clicks link → can set any password → `users.password_hash` populated → user can now log in with email+password. The `auth_method` column is not updated (still says `'github'`) but `password_hash` is no longer null, so Login will verify the password (`user.PasswordHash is null` check on line 178 passes because the hash is now present).

Attack scenario:
1. Alice signs up via GitHub OAuth at `alice@example.com`. `users.auth_method='github'`, `password_hash=NULL`.
2. Eve learns Alice's email (public on GitHub, or on a data breach).
3. Eve `POST /password-reset/request { email: 'alice@example.com' }`.
4. Alice receives an unsolicited "reset your password" email. She's confused — she doesn't have one.
5. If Alice is inattentive, or her email is already compromised via an unrelated vector, Eve (or Alice, confused) clicks the link and sets a password.
6. Eve can now log in as Alice via `/api/v1/auth/login` with the new password, bypassing GitHub entirely.

Even without Eve actively attacking: any Alice who doesn't recognize why she's getting reset emails is at risk of being socially engineered. The TS branch prevents this vector outright.

Error paths:
- TS: silent "if it exists" canned response (same as nonexistent).
- C#: silent "if it exists" canned response (same as nonexistent) — but the side effect is real.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/story-18-6/18-6-password-reset.md`
- The story does not explicitly say "skip for GitHub-only users". The TS implementation added this guard ahead of the spec — a defense-in-depth step that the story writer did not anticipate.
- Story alignment:
  - [x] Matches TS behavior (TS went beyond spec)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; TS fixed it defensively

This is the rare case where the TS implementation was ahead of its written spec. Classify as "no story" for the specific GitHub-only guard.

The CLAUDE.md "Security Requirements" section says: *"Input validation: Sanitize all user inputs against injection attacks"* — does not cover this business-logic guard.

## 5. Status

- **Classification**: Incomplete (defense-in-depth behavior dropped).
- **What's needed to finish**:
  1. In `PasswordResetRequest`, after fetching `user`, add:
     ```csharp
     if (user is not null && user.AuthMethod == "github")
         return Results.Ok(new { message = CannedResponseMessage });  // no side effects
     ```
  2. For additional hardening (covering even `authMethod='both'` linked accounts), consider: if `user.PasswordHash is null`, treat same as GitHub-only. This is stricter than TS but arguably safer.
  3. Alternative direction: legitimize the flow — allow GitHub users to ADD a password (future story), in which case `PasswordResetConfirm` should explicitly update `auth_method='both'`. Currently neither behavior is spec'd; the TS guard is the conservative default.
- **Is it "just a stub" or is scope missing?** Scope missing (undocumented security best practice from TS).
- **Blockers**: None, but `18-6-password-reset.md` should be updated to document the chosen behavior explicitly before closing this finding.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (PasswordResetRequest).
- Files to create: None.
- Tests to add:
  - `PasswordResetRequest_GitHubOnlyUser_ReturnsCannedMessageWithNoTokenCreated`.
  - `PasswordResetRequest_GitHubOnlyUser_DoesNotSendEmail` (mock `IEmailService`, assert zero calls).
  - `PasswordResetRequest_EmailUser_CreatesTokenAndSendsEmail` (happy path).
- Estimated effort: 0.5h
  - Guard + 3 tests.

## References

- TS source: `packages/api/src/routes/auth/password-reset.ts:70-83` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:280-323, 325-348`
- Story: `docs/stories/epic-18/story-18-6/18-6-password-reset.md` — no explicit mention of GitHub-only handling
- Related findings: `014-no-rate-limit-on-resend-and-reset.md`
- CLAUDE.md section: Security Requirements (doesn't cover this specific case)

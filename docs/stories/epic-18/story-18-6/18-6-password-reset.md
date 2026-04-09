# Story 18-6: Password Reset Flow

Status: planned

## Story

As an **end user who registered with email+password**,
I want to reset my password if I forget it,
so that I can regain access to my Tamma account without contacting support.

## Acceptance Criteria

1. **Request reset** endpoint `POST /api/v1/auth/password-reset/request` accepts `{ email }`, generates a reset token (UUID v7), stores a SHA-256 hash in the DB, and sends a reset email with a link to `dash.tamma.dev/reset-password?token=<token>`
2. **Token expiry**: Reset tokens expire after 1 hour
3. **Token single-use**: Each token can only be used once; after use it is marked as consumed
4. **Confirm reset** endpoint `POST /api/v1/auth/password-reset/confirm` accepts `{ token, newPassword }`, validates the token, hashes the new password with argon2id, and updates the user's `passwordHash`
5. **Password requirements**: New password must meet the same requirements as registration (min 8 chars, max 128 chars)
6. **Revoke sessions**: On password reset, all existing refresh tokens for the user are revoked (forces re-login on all devices)
7. **Rate limiting**: Reset request endpoint limited to 3 requests per email per hour, 10 requests per IP per hour
8. **No enumeration**: Always return 200 with "If an account with that email exists, a reset link has been sent" regardless of whether the email exists
9. **GitHub-only accounts**: If the user registered only via GitHub OAuth (`authMethod: 'github'`), return the same 200 response but do not send an email (no password to reset)
10. **Event emission**: `USER.PASSWORD_RESET_REQUESTED.SUCCESS`, `USER.PASSWORD_RESET.SUCCESS` events emitted
11. **Email template**: Reset email includes the user's name, a reset link, expiry notice ("This link expires in 1 hour"), and a notice that they can ignore the email if they did not request a reset

## Tasks / Subtasks

- [ ] Task 1: Password reset token persistence
  - [ ] Subtask 1.1: Create `PasswordResetToken` model: `{ id, userId, tokenHash, expiresAt, consumedAt, createdAt }`
  - [ ] Subtask 1.2: Add methods to `IUserStore` or create `IPasswordResetStore`: `createResetToken()`, `getResetToken()`, `consumeResetToken()`, `cleanupExpired()`
  - [ ] Subtask 1.3: Create database migration for `password_reset_tokens` table
  - [ ] Subtask 1.4: Write unit tests

- [ ] Task 2: Request reset endpoint
  - [ ] Subtask 2.1: Implement `POST /api/v1/auth/password-reset/request`
  - [ ] Subtask 2.2: Look up user by email; if not found or GitHub-only, return 200 without sending email
  - [ ] Subtask 2.3: Generate token (crypto.randomBytes(32)), hash with SHA-256, store in DB
  - [ ] Subtask 2.4: Send reset email using email service from 18-1
  - [ ] Subtask 2.5: Write integration tests

- [ ] Task 3: Confirm reset endpoint
  - [ ] Subtask 3.1: Implement `POST /api/v1/auth/password-reset/confirm`
  - [ ] Subtask 3.2: Hash incoming token, look up in DB, check expiry and consumed status
  - [ ] Subtask 3.3: Hash new password with argon2id, update user's `passwordHash`
  - [ ] Subtask 3.4: Mark token as consumed
  - [ ] Subtask 3.5: Revoke all refresh tokens for the user
  - [ ] Subtask 3.6: Write integration tests

- [ ] Task 4: Email template
  - [ ] Subtask 4.1: Create `packages/api/src/services/email-templates/password-reset.html`
  - [ ] Subtask 4.2: Create plaintext version
  - [ ] Subtask 4.3: Test email rendering

## Technical Context

### Database Schema

```sql
CREATE TABLE password_reset_tokens (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash TEXT NOT NULL UNIQUE,
  expires_at TIMESTAMPTZ NOT NULL,
  consumed_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_password_reset_tokens_user_id ON password_reset_tokens(user_id);
CREATE INDEX idx_password_reset_tokens_expires_at ON password_reset_tokens(expires_at) WHERE consumed_at IS NULL;
```

### Security Considerations

- Token is generated with `crypto.randomBytes(32)` (256 bits of entropy)
- Only the SHA-256 hash is stored in the DB; the raw token is sent in the email
- Constant-time comparison via `timingSafeEqual` when validating tokens
- Old unused tokens are cleaned up by a periodic job or on-demand during token creation

## Dependencies

- **18-1**: User model with `passwordHash` field, email service
- **18-2**: Refresh token store (for revoking sessions on password reset)

## Estimated Effort

**Medium (3 days)**:
- Day 1: Token persistence + request endpoint + email template
- Day 2: Confirm endpoint + session revocation + tests
- Day 3: Rate limiting, edge cases, security review

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-09 | 1.0.0 | Initial story creation | Cross-epic review |

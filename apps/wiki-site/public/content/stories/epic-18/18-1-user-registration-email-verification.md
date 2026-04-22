---
title: "Story 18-1: User Registration + Email Verification"
sidebar:
  order: 180
---

Status: planned

## Story

As an **end user**,
I want to register for Tamma using my email address and a password,
so that I can create an account and start using the platform without admin intervention.

## Acceptance Criteria

1. **Registration endpoint** `POST /api/v1/auth/register` accepts `{ email, password, name }` and creates an unverified user
2. **Password hashing** uses argon2id with OWASP-recommended parameters (memory: 19 MiB, iterations: 2, parallelism: 1)
3. **Email uniqueness** enforced at the database level; duplicate email returns 409 Conflict
4. **Password strength** validated: minimum 8 characters, at least one uppercase, one lowercase, one digit; rejects top-1000 common passwords
5. **Verification email** sent on successful registration containing a single-use token link (`/verify-email?token=<uuid>`)
6. **Verification token** is a UUID v7, stored hashed (SHA-256), expires after 24 hours
7. **Verification endpoint** `POST /api/v1/auth/verify-email` accepts `{ token }`, marks user as verified, returns success
8. **Resend endpoint** `POST /api/v1/auth/resend-verification` accepts `{ email }`, rate-limited to 3 requests per hour per email
9. **Unverified users** cannot log in; login attempt returns 403 with message "Please verify your email"
10. **Rate limiting**: Registration endpoint limited to 5 requests per IP per hour
11. **Input sanitization**: All inputs validated against injection; email normalized (lowercase, trim)
12. **Event emission**: `USER.REGISTERED.SUCCESS`, `USER.EMAIL_VERIFIED.SUCCESS` events emitted to the event store

## Tasks / Subtasks

- [ ] Task 1: Extend User model and persistence layer
  - [ ] Subtask 1.1: Add fields to `User` interface: `passwordHash: string | null`, `emailVerified: boolean`, `emailVerificationToken: string | null`, `emailVerificationExpiresAt: string | null`, `authMethod: 'email' | 'github' | 'both'`
  - [ ] Subtask 1.2: Create database migration `017_user_auth_fields.sql` adding columns to `users` table
  - [ ] Subtask 1.3: Add unique index on `email` column (case-insensitive using `LOWER(email)`)
  - [ ] Subtask 1.4: Update `IUserStore` interface with new methods: `createUserWithPassword()`, `getUserByEmail()`, `setEmailVerified()`, `updateVerificationToken()`
  - [ ] Subtask 1.5: Implement methods in `InMemoryUserStore` and `PgUserStore`
  - [ ] Subtask 1.6: Write unit tests for all new persistence methods

- [ ] Task 2: Implement password hashing service
  - [ ] Subtask 2.1: Create `packages/api/src/auth/password.ts` with `hashPassword()` and `verifyPassword()` functions
  - [ ] Subtask 2.2: Configure argon2id parameters: `{ type: argon2id, memoryCost: 19456, timeCost: 2, parallelism: 1 }`
  - [ ] Subtask 2.3: Implement password strength validation function `validatePasswordStrength()`
  - [ ] Subtask 2.4: Bundle top-1000 common passwords list for rejection
  - [ ] Subtask 2.5: Write unit tests covering hash/verify round-trip, strength validation edge cases, common password rejection

- [ ] Task 3: Implement email sending service
  - [ ] Subtask 3.1: Create `packages/api/src/services/email.ts` with `IEmailService` interface: `sendVerificationEmail()`, `sendPasswordResetEmail()`, `sendWelcomeEmail()`
  - [ ] Subtask 3.2: Implement `NodemailerEmailService` using nodemailer with SMTP transport
  - [ ] Subtask 3.3: Implement `InMemoryEmailService` for testing (captures sent emails)
  - [ ] Subtask 3.4: Create email templates: verification email (HTML + plaintext fallback)
  - [ ] Subtask 3.5: Configure via environment variables: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM`
  - [ ] Subtask 3.6: Write unit tests for template rendering and service invocation

- [ ] Task 4: Implement registration endpoint
  - [ ] Subtask 4.1: Create `packages/api/src/routes/auth/register.ts` with `POST /api/v1/auth/register`
  - [ ] Subtask 4.2: Validate input: email format, password strength, name length (2-100 chars)
  - [ ] Subtask 4.3: Normalize email (lowercase, trim)
  - [ ] Subtask 4.4: Check email uniqueness, return 409 if exists
  - [ ] Subtask 4.5: Hash password with argon2id
  - [ ] Subtask 4.6: Generate verification token (UUID v7), hash with SHA-256 for storage
  - [ ] Subtask 4.7: Create user record with `emailVerified: false`
  - [ ] Subtask 4.8: Send verification email asynchronously (fire-and-forget with error logging)
  - [ ] Subtask 4.9: Emit `USER.REGISTERED.SUCCESS` event
  - [ ] Subtask 4.10: Return 201 with `{ id, email, message: "Verification email sent" }`
  - [ ] Subtask 4.11: Apply rate limiting: 5/hour/IP
  - [ ] Subtask 4.12: Write integration tests covering happy path, duplicate email, weak password, rate limiting

- [ ] Task 5: Implement email verification endpoint
  - [ ] Subtask 5.1: Create `POST /api/v1/auth/verify-email` in same route file
  - [ ] Subtask 5.2: Hash incoming token with SHA-256, look up user by hashed token
  - [ ] Subtask 5.3: Check token expiry (24 hours)
  - [ ] Subtask 5.4: Set `emailVerified: true`, clear token fields
  - [ ] Subtask 5.5: Emit `USER.EMAIL_VERIFIED.SUCCESS` event
  - [ ] Subtask 5.6: Return 200 with `{ message: "Email verified" }`
  - [ ] Subtask 5.7: Write tests for valid token, expired token, already-used token, invalid token

- [ ] Task 6: Implement resend verification endpoint
  - [ ] Subtask 6.1: Create `POST /api/v1/auth/resend-verification`
  - [ ] Subtask 6.2: Look up user by email; if not found or already verified, return 200 (no leak)
  - [ ] Subtask 6.3: Generate new token, update user record
  - [ ] Subtask 6.4: Send verification email
  - [ ] Subtask 6.5: Rate limit: 3/hour/email
  - [ ] Subtask 6.6: Write tests for rate limiting, already verified, non-existent email

## Technical Context

### Existing Code to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/user-store.ts` | Extend `User` interface, add new methods to `IUserStore` |
| `packages/api/src/persistence/user-store.ts` | Update `InMemoryUserStore` with new methods |
| `packages/api/src/routes/auth/` | Add `register.ts` with three new endpoints |

### New Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/auth/password.ts` | Argon2 hashing + password strength validation |
| `packages/api/src/services/email.ts` | Email service interface + nodemailer implementation |
| `packages/api/src/services/email-templates/` | HTML/text email templates |
| `packages/api/src/routes/auth/register.ts` | Registration + verification + resend endpoints |
| `database/migrations/017_user_auth_fields.sql` | Migration for new user columns |

### Dependencies (npm packages)

| Package | Version | Purpose |
|---------|---------|---------|
| `argon2` | `^0.41` | Password hashing (argon2id) |
| `nodemailer` | `^6.9` | SMTP email sending |
| `@types/nodemailer` | `^6.4` | TypeScript types |

### Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `SMTP_HOST` | Yes (prod) | `localhost` | SMTP server hostname |
| `SMTP_PORT` | No | `587` | SMTP server port |
| `SMTP_USER` | Yes (prod) | - | SMTP authentication username |
| `SMTP_PASS` | Yes (prod) | - | SMTP authentication password |
| `SMTP_FROM` | No | `noreply@tamma.dev` | Sender email address |
| `VERIFY_EMAIL_URL` | No | `https://dash.tamma.dev/verify-email` | Base URL for verification links |

### Database Schema Changes

```sql
ALTER TABLE users
  ADD COLUMN password_hash TEXT,
  ADD COLUMN email_verified BOOLEAN NOT NULL DEFAULT false,
  ADD COLUMN email_verification_token_hash TEXT,
  ADD COLUMN email_verification_expires_at TIMESTAMPTZ,
  ADD COLUMN auth_method TEXT NOT NULL DEFAULT 'github';

CREATE UNIQUE INDEX idx_users_email_lower ON users (LOWER(email)) WHERE email IS NOT NULL;
```

### Security Considerations

- **Timing attacks**: Use constant-time comparison for token verification
- **Enumeration prevention**: Registration and resend endpoints return generic success messages regardless of email existence
- **Token storage**: Only SHA-256 hash of verification token stored in DB; raw token only in email
- **Argon2 parameters**: Follow OWASP 2024 recommendations for argon2id
- **CSRF**: Registration endpoint requires no session (public); standard CSRF protection not needed for API-only routes

## Implementation Notes

- The existing `User` model is GitHub-ID-centric. This story makes `githubId` optional (nullable) to support email-only users.
- The `authMethod` field tracks how the user registered: `'email'` (password-based), `'github'` (OAuth), or `'both'` (linked accounts).
- Email verification is mandatory for email-registered users but skipped for GitHub OAuth users (GitHub verifies emails).
- The verification email should include a deep link to `dash.tamma.dev/verify-email?token=<raw-token>`.
- All new endpoints use the `/api/v1/auth/` prefix to distinguish from the existing `/api/auth/` admin endpoints.

## Dependencies

- **Epic 17 Story 17-1** (Tenant Model): The `users` table must have the `tenant_id` column (nullable — see 17-1 update). New email-registered users are created with `tenant_id = NULL` until they join a tenant via Story 18-3.

## Estimated Effort

**Large (5 days)**:
- Day 1: User model extension + migration + persistence tests
- Day 2: Password hashing service + email service
- Day 3: Registration endpoint + integration tests
- Day 4: Verification + resend endpoints + tests
- Day 5: End-to-end testing, security review, documentation

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0.0 | Initial story creation | Architecture Team |

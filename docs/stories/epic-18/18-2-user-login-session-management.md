# Story 18-2: User Login + Session Management

Status: planned

## Story

As an **end user**,
I want to log in with my email and password or via GitHub OAuth,
so that I can access my Tamma dashboard with a secure session.

## Acceptance Criteria

1. **Email+password login** endpoint `POST /api/v1/auth/login` accepts `{ email, password }`, returns JWT access token + refresh token
2. **Unverified users** receive 403 with `"Please verify your email"` on login attempt
3. **Invalid credentials** return 401 with generic `"Invalid email or password"` (no enumeration)
4. **Account lockout** after 5 failed login attempts within 15 minutes; lockout lasts 30 minutes; locked accounts return 429
5. **GitHub OAuth login** endpoint `GET /api/v1/auth/github` initiates OAuth flow, `GET /api/v1/auth/github/callback` completes it
6. **GitHub OAuth** creates a new user if none exists (with `emailVerified: true`, `authMethod: 'github'`), or links to existing email-matched user
7. **Account linking**: If a GitHub OAuth user's email matches an existing email-registered user, the accounts are linked (`authMethod: 'both'`)
8. **JWT access token** contains claims: `{ id, email, name, orgId, role, authMethod }`; expires in 15 minutes
9. **Refresh token** is an opaque token stored in DB (not a JWT); expires in 7 days; single-use (rotation on refresh)
10. **Token refresh** endpoint `POST /api/v1/auth/refresh` accepts `{ refreshToken }`, returns new access+refresh token pair, invalidates old refresh token
11. **Logout** endpoint `POST /api/v1/auth/logout` clears the session cookie and invalidates the refresh token
12. **Session cookie** `tamma_session` set on `.tamma.dev` domain, `HttpOnly`, `Secure`, `SameSite=Lax`, 15-minute max-age (matches access token)
13. **Rate limiting**: Login endpoint limited to 10 requests per IP per minute
14. **Event emission**: `USER.LOGIN.SUCCESS`, `USER.LOGIN.FAILED`, `USER.LOGOUT.SUCCESS` events emitted

## Tasks / Subtasks

- [ ] Task 1: Implement refresh token persistence
  - [ ] Subtask 1.1: Create `IRefreshTokenStore` interface: `createToken()`, `getToken()`, `revokeToken()`, `revokeAllForUser()`, `cleanupExpired()`
  - [ ] Subtask 1.2: Define `RefreshToken` model: `{ id, userId, tokenHash, expiresAt, createdAt, revokedAt }`
  - [ ] Subtask 1.3: Create `packages/api/src/persistence/refresh-token-store.ts` with `InMemoryRefreshTokenStore` and `PgRefreshTokenStore`
  - [ ] Subtask 1.4: Create database migration `20260402_create_refresh_tokens.sql`
  - [ ] Subtask 1.5: Write unit tests for both implementations

- [ ] Task 2: Implement login lockout service
  - [ ] Subtask 2.1: Create `packages/api/src/auth/login-lockout.ts` with `ILoginLockoutService` interface
  - [ ] Subtask 2.2: Track failed attempts per email in-memory (or Redis in future); 5 failures in 15 min = 30 min lockout
  - [ ] Subtask 2.3: Expose `recordFailedAttempt()`, `isLocked()`, `resetAttempts()` methods
  - [ ] Subtask 2.4: Write unit tests for lockout timing, reset on success, concurrent attempts

- [ ] Task 3: Implement email+password login endpoint
  - [ ] Subtask 3.1: Create `packages/api/src/routes/auth/login.ts` with `POST /api/v1/auth/login`
  - [ ] Subtask 3.2: Validate input: email format, password present
  - [ ] Subtask 3.3: Check lockout status; return 429 if locked
  - [ ] Subtask 3.4: Look up user by email; return 401 if not found (constant-time path)
  - [ ] Subtask 3.5: Verify password with argon2; return 401 if mismatch, record failed attempt
  - [ ] Subtask 3.6: Check `emailVerified`; return 403 if false
  - [ ] Subtask 3.7: Generate JWT access token (15 min expiry) with user claims
  - [ ] Subtask 3.8: Generate opaque refresh token (crypto.randomBytes(32)), hash with SHA-256, store in DB
  - [ ] Subtask 3.9: Set `tamma_session` cookie with access token
  - [ ] Subtask 3.10: Reset lockout counter on success
  - [ ] Subtask 3.11: Update `lastActiveAt` on user
  - [ ] Subtask 3.12: Emit `USER.LOGIN.SUCCESS` event
  - [ ] Subtask 3.13: Return `{ accessToken, refreshToken, user: { id, email, name, role } }`
  - [ ] Subtask 3.14: Write integration tests for all paths

- [ ] Task 4: Implement GitHub OAuth login for end users
  - [ ] Subtask 4.1: Create `GET /api/v1/auth/github` redirect endpoint (separate from admin OAuth at `/api/auth/github`)
  - [ ] Subtask 4.2: Use `state` parameter with CSRF token (stored in short-lived cookie)
  - [ ] Subtask 4.3: Create `GET /api/v1/auth/github/callback` to exchange code for token
  - [ ] Subtask 4.4: Fetch GitHub user profile + verified emails
  - [ ] Subtask 4.5: Check if user exists by `githubId`; if yes, log in
  - [ ] Subtask 4.6: Check if user exists by email; if yes, link GitHub account (`authMethod: 'both'`, set `githubId`)
  - [ ] Subtask 4.7: If no existing user, create new user with `emailVerified: true`, `authMethod: 'github'`
  - [ ] Subtask 4.8: Generate access + refresh tokens, set cookie
  - [ ] Subtask 4.9: Redirect to `dash.tamma.dev` (or onboarding if no org)
  - [ ] Subtask 4.10: Write integration tests for new user, existing user, account linking

- [ ] Task 5: Implement token refresh endpoint
  - [ ] Subtask 5.1: Create `POST /api/v1/auth/refresh` in login routes
  - [ ] Subtask 5.2: Hash incoming refresh token, look up in DB
  - [ ] Subtask 5.3: Check expiry and revocation status
  - [ ] Subtask 5.4: Revoke old refresh token (single-use rotation)
  - [ ] Subtask 5.5: Generate new access + refresh token pair
  - [ ] Subtask 5.6: Return new tokens + set cookie
  - [ ] Subtask 5.7: Write tests for valid refresh, expired, revoked, reuse detection

- [ ] Task 6: Implement logout endpoint
  - [ ] Subtask 6.1: Create `POST /api/v1/auth/logout`
  - [ ] Subtask 6.2: Extract refresh token from request body (or cookie)
  - [ ] Subtask 6.3: Revoke refresh token in DB
  - [ ] Subtask 6.4: Clear `tamma_session` cookie
  - [ ] Subtask 6.5: Emit `USER.LOGOUT.SUCCESS` event
  - [ ] Subtask 6.6: Return 200 `{ ok: true }`
  - [ ] Subtask 6.7: Write tests

## Technical Context

### Existing Code to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/user-store.ts` | Add `getUserByEmail()` if not done in 18-1; add lockout tracking fields |
| `packages/api/src/routes/auth/github-oauth.ts` | Reference implementation; the new v1 OAuth flow is separate but similar |

### New Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/persistence/refresh-token-store.ts` | Refresh token persistence (in-memory + Postgres) |
| `packages/api/src/auth/login-lockout.ts` | Login attempt tracking and lockout logic |
| `packages/api/src/routes/auth/login.ts` | Email+password login, refresh, logout endpoints |
| `packages/api/src/routes/auth/github-oauth-v1.ts` | End-user GitHub OAuth flow (separate from admin) |
| `database/migrations/20260402_create_refresh_tokens.sql` | Refresh token table |

### Refresh Token Table Schema

```sql
CREATE TABLE refresh_tokens (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash TEXT NOT NULL UNIQUE,
  expires_at TIMESTAMPTZ NOT NULL,
  revoked_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX idx_refresh_tokens_expires_at ON refresh_tokens(expires_at) WHERE revoked_at IS NULL;
```

### JWT Claims Structure

```typescript
interface UserJwtPayload {
  id: string;          // User UUID
  email: string;       // User email
  name: string;        // Display name
  orgId: string | null; // Organization ID (null if not yet in an org)
  role: 'member' | 'admin' | 'owner'; // Role within org
  authMethod: 'email' | 'github' | 'both';
  iat: number;         // Issued at
  exp: number;         // Expiry (15 min)
}
```

### Two OAuth Flows

The system will have two separate GitHub OAuth flows:

| Flow | Path | Cookie | Purpose |
|------|------|--------|---------|
| Admin | `/api/auth/github` | `tamma_session` on `.tamma.dev` | Existing admin dashboard auth |
| End-user | `/api/v1/auth/github` | `tamma_session` on `.tamma.dev` | New end-user registration/login |

Both use the same GitHub OAuth App but different callback URLs and different post-auth behavior. The admin flow redirects to `app.tamma.dev`, the end-user flow redirects to `dash.tamma.dev`.

### Security Considerations

- **Refresh token rotation**: Each refresh invalidates the previous token; reuse of an old token revokes ALL tokens for that user (compromise detection)
- **Constant-time comparison**: Use `timingSafeEqual` for token and password hash comparisons
- **CSRF on OAuth**: The `state` parameter includes a random value stored in a short-lived cookie, verified on callback
- **Lockout bypass**: Lockout is per-email, not per-user-ID, to prevent enumeration via lockout status
- **Password not logged**: Never log password values, hashes, or tokens in any log level

## Implementation Notes

- The existing `/api/auth/login` in `packages/api/src/auth/index.ts` is a stub (returns 401 when auth is enabled). The new v1 endpoints replace this for end users.
- Refresh token rotation is critical: if a stolen refresh token is reused, all sessions for that user should be revoked (family rotation pattern).
- The 15-minute access token expiry is short by design -- the frontend refreshes transparently using the refresh token. This limits the window of a stolen access token.
- The GitHub OAuth callback URL must be registered in the GitHub OAuth App settings: `https://api.tamma.dev/api/v1/auth/github/callback`

## Dependencies

- **18-1**: User model with `passwordHash`, `emailVerified`, `authMethod` fields
- **18-1**: Password hashing service (`hashPassword`, `verifyPassword`)

## Estimated Effort

**Large (5 days)**:
- Day 1: Refresh token store + login lockout service + tests
- Day 2: Email+password login endpoint + integration tests
- Day 3: GitHub OAuth v1 flow + account linking logic + tests
- Day 4: Token refresh + logout + cookie management + tests
- Day 5: Security review, edge case testing, documentation

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0.0 | Initial story creation | Architecture Team |

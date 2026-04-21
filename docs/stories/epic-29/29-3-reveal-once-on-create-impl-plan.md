# Story 29-3 Implementation Plan — Reveal-Once-on-Create UX

**Status**: Planned (2026-04-20)
**Story brief**: [`29-3-reveal-once-on-create.md`](./29-3-reveal-once-on-create.md)
**Epic 29 phase**: API layer — after 29-2.
**Branch**: `feat/story-29-3-reveal-once`

---

## 1. Objective

Ship the reveal-once endpoint + token store so secret creation/rotation
returns a short-lived `revealToken` that can be exchanged exactly once
for the plaintext. Turns the cabinet into a "write-only-after-creation"
store for human eyes: re-reads require rotation, which itself emits a
visible audit event.

## 2. Dependencies

Hard blockers:

- **Story 29-2** — the store to create secrets in.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260423000000_SecretRevealTokens.cs` | `secret_reveal_tokens` table. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/SecretEndpoints.cs` | `POST /api/v1/secrets`, `GET /api/v1/secrets/reveal/{token}`, `POST /api/v1/secrets/{id}/rotate`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretRevealService.cs` | Issue/consume token. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/RevealTokenSweeper.cs` | Background 30s sweeper for expired tokens. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/SecretRevealServiceTests.cs` | Unit. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/Secrets/RevealEndpointTests.cs` | E2E. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register reveal service + sweeper + rate limiter. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Add `SecretRevealToken` DbSet. |

## 5. Sequence of changes

### Step 1 — Schema + entity (1h)

- `secret_reveal_tokens (id, token_hash, secret_id, version_number,
  created_by, created_at, expires_at, consumed_at, status)`.
- Partial index on `(status='unused', expires_at)` for sweep.
- **Commit**: `feat(db): secret_reveal_tokens table`.

### Step 2 — SecretRevealService (2h)

- `Issue(secretId, version, userId)` → `{ token, expiresAt }`.
- Token = 32 random bytes, base64url; stored as HMAC-SHA256 hash
  using a KEK-derived key.
- `Consume(token)` → returns plaintext once, then flips `status=consumed`.
- Constant-time hash compare.
- **Commit**: `feat(secrets): reveal service`.

### Step 3 — Sweeper (1h)

- Background `IHostedService` runs every 30s: `UPDATE secret_reveal_tokens SET status='expired' WHERE status='unused' AND expires_at < NOW()`.
- **Commit**: `feat(secrets): expired-token sweeper`.

### Step 4 — Endpoints (3h)

- `POST /api/v1/secrets` (platform: `/api/v1/admin/secrets`):
  creates secret via `ISecretStore.CreateAsync`, issues reveal token.
- `GET /api/v1/secrets/reveal/{token}` with rate limit 10/min/user.
- `POST /api/v1/secrets/{id}/rotate`: same flow for new version.
- Per brief: no other endpoint returns plaintext.
- **Commit**: `feat(secrets): reveal endpoints`.

### Step 5 — Audit choke point (1h)

- `ISecretAccessAuditor.LogReveal(secretId, versionNumber, userId, userAgent, ipHash)`.
- Called from `SecretRevealService.Consume` only — no other writes to
  `SECRET.REVEAL` event type allowed (CI grep guard).
- **Commit**: `feat(secrets): audit choke point`.

### Step 6 — Integration tests (2h)

- Create → reveal succeeds → second reveal 410.
- Wait 61s → reveal returns 410 `expired`.
- Rotate → new token → reveal new value → assert old value still
  retrievable only via rotation.
- **Commit**: `test(secrets): reveal-once E2E`.

## 6. Test strategy

### Unit

- Token format (base64url 43 chars).
- Constant-time hash comparison.
- Rate-limit enforcement (mock time).
- Sweeper idempotency.

### Integration

- Full flow per brief AC8.
- Cross-user reveal attempt → 403 (only issuer can reveal? Plan:
  any authenticated user with the token — tokens are the auth).

## 7. Rollback plan

- **Schema rollback**: drop table.
- **Feature flag**: `Secrets:RevealEnabled=true`. Off disables
  reveal (secrets still createable via direct `ISecretStore` for
  machine consumers via rotation handlers).

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Schema | 1 |
| 2. Reveal service | 2 |
| 3. Sweeper | 1 |
| 4. Endpoints | 3 |
| 5. Audit choke point | 1 |
| 6. Integration | 2 |
| **Total** | **10** (matches brief). |

## 9. Open questions

- **Rate-limit key**: per-user or per-IP? Plan: per-user. Tokens
  are 256-bit so brute force is theoretical.
- **Emergency re-create copy**: brief mentions rotation is the
  safe way. Add a "lost? rotate again" button in 29-4 UI.
- **Token lookup on consume**: HMAC-SHA256 of token vs DB index —
  we HMAC first, then query by hash. No plaintext tokens stored.
- **Multiple tokens per secret/version**: possible if create +
  immediate-rotate. First token still valid until consumed or
  expired; second token overlaps. Documented as OK.

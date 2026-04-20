# Story 29-3: Reveal-Once-on-Create UX + Access Audit

Status: todo (planning brief, 2026-04-20)

## Story

As a **tenant admin (or platform admin)**,
I want to see the newly generated plaintext of an API key, database password, or shared HMAC exactly once — at the moment I create or rotate it — and then never again through any UI or API,
so that the value is auditable (every reveal is logged) but the operational pattern enforces the user's design intent: "tenant admins can generate and edit these passwords, but that means auto-generate and update, since they can't access dbs directly".

## Acceptance Criteria

1. A `POST /api/v1/secrets` (and `POST /api/v1/admin/secrets` for platform scope) creates a secret via `ISecretStore.CreateAsync`. The response includes `revealToken` (32-byte random, base64url), `expiresAt` (60 seconds from creation), and the metadata. The plaintext value is **not** in this response.
2. A follow-up `GET /api/v1/secrets/reveal/{revealToken}` returns `{ name, version, plaintext, expiresAt }` exactly once. Second call returns 410 Gone. Expired token returns 410 with a distinct error code.
3. Reveal tokens live in a separate `secret_reveal_tokens` table with `status ∈ { unused, consumed, expired }` and a partial index for fast expiry sweeps. A background task sweeps expired tokens every 30 s.
4. Every reveal emits `SECRET.REVEAL` with `{ secretId, versionNumber, revealedByUserId, at, userAgent, ipHash }`. The `platform_events` / `domain_events` audit row is idempotent — replaying the reveal endpoint (which already burned the token) does not emit a second event.
5. UI cards in 29-4 / 29-5 render the plaintext in a one-shot copy-to-clipboard modal with a "This value will not be shown again" notice and an explicit "I have saved this value" confirmation before dismiss.
6. Rotation (`POST /api/v1/secrets/{id}/rotate`) follows the same reveal-token flow for the new version. Old versions are **never** revealed — they remain internal state used only by rotation handlers.
7. Rate-limit reveal endpoint at 10 req/min per user to frustrate token-guessing; tokens are 256-bit so brute force is theoretical, but the rate limit makes audit noisier for attackers.
8. Integration test: create, reveal, attempt second reveal (410), attempt reveal after 61 s (410 with `expired` code), assert two `SECRET.REVEAL` or `SECRET.REVEAL.EXPIRED` audit rows as appropriate.
9. Emergency re-create path: if a user loses the one-shot value (browser closed), they can re-rotate (new version, new reveal token); the old version enters `RetiredGrace` per 29-6. Documented in the admin runbook.
10. `ISecretAccessAuditor.LogReveal` is the single choke point — no direct `platform_events` writes from the reveal endpoint; keeps the audit invariant testable.

## Technical Context

### Why reveal-once

A secret cabinet that lets operators re-read a plaintext value at any
time is a honey pot. Every reveal is an attack surface (admin account
compromise, shoulder-surf, screenshot). Reveal-once-on-create turns
the cabinet into a **write-only-after-creation** store for human
eyes: the only way for a human to see a value is to rotate it (and
rotation is itself audited + emits a visible event).

Machine consumers don't use the reveal endpoint at all — they get
values via rotation handlers (29-6) that receive plaintext in-process
and push it directly to the consumer system.

### Token format

```
revealToken = base64url(32 random bytes)  // ~43 chars
```

Stored hashed with HMAC-SHA256 (key = KEK-derived) so a DB dump does
not leak tokens. Lookup: HMAC the incoming token, compare constant-
time against the stored hash.

### Audit event shape

```json
{
  "type": "SECRET.REVEAL",
  "tags": { "secretId": "...", "versionNumber": 3, "userId": "...", "tenantId": "..." },
  "data": {
    "revealedAt": "2026-04-20T14:22:19.331Z",
    "userAgent": "Mozilla/5.0 ...",
    "ipHash": "sha256:ab12cd..."
  }
}
```

## Estimated hours

10 — endpoint + token table + sweeper + rate-limit + tests + docs.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Endpoints/SecretEndpoints.cs` (new)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/20260423000000_SecretRevealTokens.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretRevealService.cs` (new)

## References

- Research notes §3 (saga + grace window)
- Design intent: user quote 2026-04-20
- Pattern: GitHub Actions secrets "you'll only see this once" UX

# GitHub Integration — Port Gap Index

Scope: GitHub webhooks, OAuth login (admin), GitHub App installation flow, secrets provisioning.

TypeScript source reference: commit `9e9a57c~1` (the commit before Epic 19 Phase 3 deleted `packages/api`).

Current C# source: `apps/tamma-elsa/src/Tamma.Api/` on branch `feat/auth-foundation`.

Per-finding documents follow the template at `docs/audit/port-gaps/TEMPLATE.md`.

## Finding summary

| # | Title | Severity | Status | Effort |
|---|-------|----------|--------|-------:|
| [001](./001-webhook-signature-fail-open-no-secret.md) | Webhook signature verification fails open when secret is empty | P0 | Behavioral drift | 1h |
| [002](./002-webhook-event-dispatch-parity.md) | Webhook event dispatch parity across 5 event types (positive finding) | None | Ported faithfully | 0h |
| [003](./003-webhook-idempotency-missing.md) | Webhook idempotency on `X-GitHub-Delivery` header not enforced | P2 | Not-yet-implemented | 6-8h |
| [004](./004-installation-deleted-soft-vs-hard.md) | Installation lifecycle — soft-delete vs hard-delete semantics drift | P3 | Behavioral drift | 2h |
| [005](./005-no-cache-invalidation-hook.md) | Installation lifecycle — no cache invalidation hook on mutate events | P3 | Not-yet-implemented | 1-2h |
| [006](./006-installation-created-no-provisioning.md) | `installation.created` webhook does not provision API key or fetch repos | P0 | Incomplete | 6-8h |
| [007](./007-installation-callback-no-github-api-fetch.md) | Installation callback no longer calls the GitHub API | P0 | Semantic rewrite | 5-6h |
| [008](./008-installation-callback-no-api-key-generation.md) | Installation callback does not generate or provision an API key | P0 | Semantic rewrite | 3-4h |
| [009](./009-oauth-start-no-csrf-state.md) | OAuth start does not include a CSRF `state` parameter | P0 | Behavioral drift | 2-3h |
| [010](./010-oauth-start-missing-read-user-scope.md) | OAuth start requests only `user:email` scope, missing `read:user` | P1 | Behavioral drift | 0.5h |
| [011](./011-oauth-start-no-rd-invite.md) | OAuth start has no `rd` or `invite` token support | P1 | Incomplete | 2-3h |
| [012](./012-oauth-callback-literal-stub.md) | OAuth callback is a literal stub — entire flow not implemented | P0 | Not-yet-implemented | 10-14h |
| [013](./013-secrets-provisioner-libsodium-missing.md) | Secrets provisioner (libsodium sealed-box + GitHub Actions secrets) entirely missing | P0 | Not-yet-implemented | 6-8h |
| [014](./014-no-inbound-rate-limit-webhook-oauth.md) | No inbound rate limiting on `/api/github/webhooks` or `/api/auth/github` | P2 | Not-yet-implemented | 2-3h |
| [015](./015-outbound-github-rate-limit-unhandled.md) | Outbound GitHub API rate-limit handling missing | P2 | Not-yet-implemented | 2-3h |
| [016](./016-installation-router-no-60s-ttl-cache.md) | Installation router has no 60-second TTL cache | P2 | Not-yet-implemented | 2-3h |
| [017](./017-webhook-route-no-rate-limit-plugin.md) | Webhook route has no per-route rate-limit binding equivalent | P2 | Not-yet-implemented | 1h |
| [018](./018-schema-installation-no-apikey-columns.md) | Schema — `github_installations` lacks `ApiKeyHash`/`Prefix`/`Encrypted` columns | P1 | Data-model regression | 2-4h |
| [019](./019-github-webhook-events-table-missing.md) | Schema — `github_webhook_events` idempotency table does not exist | P2 | Not-yet-implemented | 1h |
| [020](./020-github-callback-auth-model-redirect-vs-401.md) | GitHub install callback auth model — redirect to error instead of orphan-persist | P3 | Behavioral drift | 1-2h |
| [021](./021-installation-id-bigint-pk-vs-guid.md) | Schema — `installation_id BIGINT PK` replaced by surrogate `Guid Id` PK | P2 | Data-model regression | 2h |

## By severity

**P0 — cutover-blocking (7 findings)**:
- 001 — Webhook fail-open (security)
- 006, 007, 008 — Installation onboarding broken end-to-end (no API-fetch, no key generation, no per-repo provisioning)
- 009 — OAuth CSRF exposure
- 012 — OAuth callback literal stub (login broken)
- 013 — Secrets provisioner missing (rotation + onboarding blocked)

**P1 — feature broken (3 findings)**:
- 010 — OAuth scope gap
- 011 — OAuth start missing `rd` / `invite`
- 018 — Schema missing encrypted-plaintext column (rotation can't re-push)

**P2 — correctness/observability (7 findings)**:
- 003 — Webhook idempotency
- 014, 015, 017 — Rate limiting (inbound + outbound + per-route)
- 016 — Installation cache
- 019, 021 — Schema (webhook events table, surrogate key)

**P3 — drift/contract (3 findings)**:
- 004 — Soft-delete vs hard-delete
- 005 — Cache invalidation hook
- 020 — Callback auth model redirect

**None — positive finding (1)**:
- 002 — Webhook dispatch parity

## Total effort

Sum of mid-point estimates: **~52h** (matches audit summary's ~42-56h range).

Breakdown by theme:
- **OAuth end-user login**: 009 + 010 + 011 + 012 ≈ 15-20h
- **Install flow (callback + provisioner)**: 007 + 008 + 013 + 018 ≈ 16-22h
- **Webhook hardening (signature + idempotency + rate limits)**: 001 + 003 + 014 + 017 + 019 ≈ 10-13h
- **Installation cache + invalidation**: 005 + 016 ≈ 3-5h
- **Installation lifecycle + callback semantics**: 004 + 006 + 020 + 021 ≈ 6-10h
- **Outbound GitHub resilience**: 015 ≈ 2-3h

## Cross-finding dependencies

Critical path (must land in order):
1. **Finding 007** (GitHub App HTTP client) — enables 008, 013, 015.
2. **Finding 013** (secrets provisioner) — enables 006, 008, 018.
3. **Finding 009** (state param) — enables 012.
4. **Finding 016** (cache) — enables 005.
5. **Finding 014** (rate-limit middleware) — enables 017.
6. **Finding 019** (webhook events table) — enables 003.
7. **Finding 018** (schema update) — enables 008's clean impl.

Recommended landing order:
1. 001 (fail-open) — hot security fix, standalone.
2. 009 + 010 + 011 + 012 (OAuth bundle) — restores login.
3. 014 + 017 (rate limiting) — hardens public surface.
4. 007 (GitHub App client) — unlocks the rest.
5. 013 (provisioner) — depends on 007.
6. 018 (schema encrypted column).
7. 006 + 008 + 020 (install flow completion).
8. 019 + 003 (idempotency).
9. 016 + 005 (cache).
10. 015 (outbound resilience).
11. 004 (soft-delete cleanup), 021 (schema docs) — cosmetic / hardening.

## Stories reference

- `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` — primary story for install flow (Findings 003, 006, 007, 008, 013, 018, 020).
- `docs/stories/epic-18/18-2-user-login-session-management.md` — OAuth login (Findings 009, 010, 011, 012).
- `docs/stories/epic-18/README.md` — Epic overview; notes existing admin OAuth at `/api/auth/github` vs end-user `/api/v1/auth/github`.

## Spec gaps requiring story backfill

The following findings are not covered by any existing story and require new spec work:
- Finding 003 — webhook idempotency policy.
- Finding 013 — API key auto-provisioning to GitHub Actions secrets (proposed as "Story 18-4.1").
- Finding 014 — inbound rate-limiting policy (cross-cutting).
- Finding 015 — outbound GitHub rate-limit handling.
- Finding 018 — key rotation re-push semantics.
- Finding 019 — webhook deliveries table (same gap as 003 from schema angle).

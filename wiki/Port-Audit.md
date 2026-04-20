# TS → C# Port Audit

During the auth-foundation sprint, Tamma audited the gap between the original TypeScript `packages/api/` surface (deleted at commit `9e9a57c`) and its C# replacement in `apps/tamma-elsa/src/Tamma.Api/` on `feat/auth-foundation`.

The audit produced **196 per-finding markdown notes across 8 scopes**, with **118 findings landed** during the sprint. Every finding has:

- A TS source reference (via `git show 9e9a57c~1:<path>`).
- A current C# source reference.
- A severity (P0 cutover-blocking / P1 feature broken / P2 correctness regression / P3 contract drift).
- A concrete remediation (where landed) or an explicit deferral rationale.

## Scope rollup

| Scope | Findings | Status | Notes |
|-------|----------|--------|-------|
| [`admin-db/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/admin-db) | 33 | Phase-1/2 landed | Schema hardening migration, Phase-2 RLS + `tamma_app` role. |
| [`auth/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/auth) | 30 | All P0 landed | scrypt hash compat, JWT shape, API key hash, session cookie, OAuth CSRF state, `/me` cookie read, password strength, rate limit, login lockout, role-check service map. |
| [`orgs/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/orgs) | 27 | Landed | Path-tenant gate, role hierarchy, two-phase delete, audit events, sole-owner guard. |
| [`providers/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/providers) | 26 | P0/P1/P2 landed | Pricing, budget persistence (Postgres), role vocab, sanitizer, clamping, chain, rate limit, diagnostics groupby, health key validation, user-provider CRUD. |
| [`prompts/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/prompts) | 13 | All landed | Resolution order (4-layer), render contract, audit events, tenant-scoped → user-scoped, unique constraint, action-default layer (positive deviation). |
| [`engine/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/engine) | 30 | All landed | execute-task wiring, GitHub-callback service, context store, DTO realignment, cross-tenant guards, SaaS shape parity, idempotent upsert, dashboard rollup, install router cache, hard-delete, key-rotation summary, queue visibility timeout, tenant-scoped task handlers, SSE lifecycle. |
| [`github/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/github) | 21 | All landed | Webhook fail-closed, idempotency, rate limiting, install/rotation provisioner seam, Octokit app client, libsodium secrets. |
| [`kb/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/kb) | 16 | Deferred | RAG pipeline never wired; defaults disable all sources. Composition-root chain tracked — land them together (13-22h) behind Epic 6 finish. |

(Counts reflect the per-finding MD files in each scope directory on this branch. See the per-scope `index.md` for the exact severity breakdown.)

## Finding template

Every finding uses `docs/audit/port-gaps/TEMPLATE.md`:

1. **What's in TS** — source-quote from `9e9a57c~1`.
2. **What's in C#** — current state, honest about stubs.
3. **The gap** — specific behavioural or data-model drift.
4. **Remediation** — what landed / what's deferred and why.
5. **Tests** — which test file locks the fix (or would have caught the gap).

## Audit methodology

- Pre-delete snapshot: commit `9e9a57c~1` (the TS `packages/api/` before deletion).
- Target: `feat/auth-foundation` at sprint start.
- Severity: P0 = data/session-destroying; P1 = feature broken; P2 = correctness/observability; P3 = contract drift.
- Every finding either links to a TS + C# source pair **or** explains why a direct mapping isn't possible (e.g. schema rewrites, CLAUDE.md spec deviations).

## What's next

Deferred work, tracked in each scope's `index.md`:

- **kb scope** (16 findings) — wire the real `@tamma/intelligence` packages (vector store, RAG pipeline, KB recommendations) behind the composition-root gap. Epic 6 finish.
- **admin-db** Phase-3 hardening — some RLS policies have deferred test coverage behind fuller multi-pod integration tests.
- **providers** per-user CRUD — a handful of rarely-used endpoints flagged P3 are still stubbed.

## Related

- [Security](Security)
- [Home → Recent Progress](Home#recent-progress-auth-foundation-sprint)
- Raw audit directory: [`docs/audit/port-gaps/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps)

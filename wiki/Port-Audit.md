# TS → C# Port Audit + Code Review

During the auth-foundation sprint, Tamma audited the gap between the original TypeScript `packages/api/` surface (deleted at commit `9e9a57c`) and its C# replacement in `apps/tamma-elsa/src/Tamma.Api/` on `feat/auth-foundation`, then ran a senior code review over the remediation work before merge.

The audit produced **196 per-finding markdown notes across 8 scopes**, with **118 findings landed** during the sprint. Every finding has:

- A TS source reference (via `git show 9e9a57c~1:<path>`).
- A current C# source reference.
- A severity (P0 cutover-blocking / P1 feature broken / P2 correctness regression / P3 contract drift).
- A concrete remediation (where landed) or an explicit deferral rationale.

The **2026-04-20 senior code review** (range `5ba1e50..e6eb605`, 66 commits) surfaced **18 additional findings** in the remediation work itself. Report: [`docs/review/session-2026-04-20.md`](https://github.com/meywd/tamma/blob/main/docs/review/session-2026-04-20.md).

## Scope rollup

| Scope | Findings | Status | Notes |
|-------|----------|--------|-------|
| [`admin-db/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/admin-db) | 33 | Phase-1/2 landed; Phase-3 scaffold-only | Schema hardening, Phase-2 RLS + `tamma_app` role. Phase-3 markers downgraded to "scaffold only — not live" (commit `c404b51`). |
| [`auth/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/auth) | 30 | All P0 landed | scrypt hash compat, JWT shape, API key hash, session cookie, OAuth CSRF state, `/me` cookie read, password strength, rate limit, login lockout, role-check service map. |
| [`orgs/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/orgs) | 27 | Landed; 002/004 scaffold-only | Path-tenant gate, role hierarchy, two-phase delete, audit events, sole-owner guard. orgs/002 + orgs/004 "Phase-3 fail-closed" marked **scaffold only** pending Story 19-6. |
| [`providers/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/providers) | 26 | P0/P1/P2 landed | Pricing, budget persistence (Postgres), role vocab, sanitizer, clamping, chain, rate limit, diagnostics groupby, health key validation, user-provider CRUD. |
| [`prompts/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/prompts) | 13 | All landed | Resolution order (4-layer), render contract, audit events, tenant-scoped → user-scoped, unique constraint, action-default layer. |
| [`engine/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/engine) | 30 | All landed | execute-task wiring, GitHub-callback service, context store, DTO realignment, cross-tenant guards, SaaS shape parity, idempotent upsert, dashboard rollup, install router cache, hard-delete, key-rotation summary, queue visibility timeout, tenant-scoped task handlers, SSE lifecycle. |
| [`github/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/github) | 21 | All landed | Webhook fail-closed, idempotency, rate limiting, install/rotation provisioner seam, Octokit app client, libsodium secrets. |
| [`kb/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps/kb) | 16 | Deferred | RAG pipeline never wired; defaults disable all sources. Composition-root chain tracked — land them together (13–22h) behind Epic 6 finish. |

(Counts reflect the per-finding MD files in each scope directory on this branch. See the per-scope `index.md` for the exact severity breakdown.)

## Code review (2026-04-20) — 18 findings

Senior-reviewer sweep over the 66-commit remediation range. Full report: `docs/review/session-2026-04-20.md`.

### Merge blockers — closed before merge

| # | Severity | Finding | Closed by |
|---|----------|---------|-----------|
| 1 | P0 | Phase-3 "fail-closed" plane shipped but zero endpoints / repositories inject `TammaAppDbContext` | Audit markers downgraded (`c404b51`); Story 19-6 filed to do real wiring (`b76ea79`) |
| 2 | P1 | RLS policies on `users`, `api_keys`, etc. use `TenantId IS NULL OR …` — leaks NULL-tenant rows once app-role wired | NULL-tenant branch dropped from app-role policies (`aab36e3`) |
| 5 | P1 | `WebhookSignalRegistry` key has no installation id — cross-tenant wake via branch alias | `install:{id}:` prefix on all alias forms (`9160db1`) |
| 6 | P1 | Artifact download unbounded → OOM DoS across tenants | 4 MB cap in `LimitedStream` + string clamps in `ParseResultJson` (`ced59bc`) |

### Follow-up (scheduled, not merge-blocking)

| # | Sev | Finding | Plan |
|---|-----|---------|------|
| 3 | P2 | `prevent_tenant_id_change` trigger vs. query-filter NULL assumption conflict | Align in Story 19-6 or 29-9 |
| 4 | P2 | `tamma_app` role ships with literal `PASSWORD 'changeme'` | **Story 29-9** (rotate on first deploy via Epic 29 cabinet) |
| 7 | P2 | LocalExecutor temp path predictable / symlink-attackable on shared hosts | `TAMMA_AGENT_TMP` env override; reject symlink result paths |
| 9 | P2 | `DefaultProcessRunner.Task.Delay(250)` race for stream drain | Unconditional `Task.WhenAll` after `WaitForExitAsync` |
| 11 | P2 | Installation token cache has no 401 invalidation | Retry-once wrapper around Octokit calls |
| 12 | P2 | Rate-limit handling logs but does not back off | Honor `RetryAfterSeconds`; batch-level gate |
| 13 | P2 | Sealed box ciphertext plaintext not wiped from memory | `CryptographicOperations.ZeroMemory(messageBytes)` after seal |
| 14 | P2 | 8-hex Cranl resource names → ~65k-tenant birthday collision | **Story 30-3** (Cranl refactor to v2 interface); expand to 16 hex or full UUID |
| 15 | P2 | `TAMMA_SHARED_SECRET` written plaintext to Cranl env | **Story 29-8** (Cranl env rotation) + per-tenant minting |
| 16 | P2 | `TenantSecretProtector` HKDF-from-`Cranl:ApiKey` fallback | **Story 29-2** (`ISecretsService`-only KEK path); **Story 29-10** (delete fallback) |
| 18 | smell | `InvalidOperationException` on no-tenant path → 500, should be 404 | Typed `TenantNotFound` + endpoint mapper |

### Things done well (keep doing)

- **Signature verification order**: webhook body buffered, signature verified, then delivery-id idempotency, then JSON parse, then dispatch. No TOCTOU.
- **Interceptor safety**: `TenantContextInterceptor.ApplyTenantBindingAsync` is defensively logged-and-continued with EF query filters + RLS as the safety net.
- **`CranlProvisioningWorkflow` resumability**: each step checks "did the previous step already produce this?" before acting. Re-entrant from any intermediate state.
- **JWT correctness** in `OctokitGitHubAppClient`: 9-minute expiry, 60s clock skew, `CacheSignatureProviders = false` to prevent cross-tenant leak.
- **AES-GCM in `TenantSecretProtector`**: per-call `RandomNumberGenerator.GetBytes(12)` nonce — no key-nonce reuse risk.

## Finding template

Every port-gap finding uses `docs/audit/port-gaps/TEMPLATE.md`:

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

Deferred work, tracked in each scope's `index.md` and mapped into the new scoped epics:

- **kb scope** (16 findings) — wire the real `@tamma/intelligence` packages (vector store, RAG pipeline, KB recommendations) behind the composition-root gap. Epic 6 finish.
- **admin-db / orgs Phase-3 hardening** — Story 19-6 wires `TammaAppDbContext` into endpoints and repositories; Epic 30 Story 30-8 closes per-tenant endpoint routing.
- **Secret-management follow-ups (findings 4, 15, 16)** — rolled into [Epic 29](Secret-Management) (cabinet + rotation workflows + migrate stopgaps).
- **Cranl resource-name collisions (finding 14)** — Epic 30 Story 30-3 refactor.
- **providers per-user CRUD** — a handful of rarely-used endpoints flagged P3 are still stubbed.

## Related

- [Security](Security)
- [Agent Dispatch](Agent-Dispatch) — webhook signal + artifact cap fixes
- [Secret Management](Secret-Management) — Epic 29 closes findings 4, 15, 16
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) — Epic 30 closes finding 14
- [Home → Recent Progress](Home#recent-progress-auth-foundation-sprint-2026-04-18--2026-04-21)
- Raw audit directory: [`docs/audit/port-gaps/`](https://github.com/meywd/tamma/tree/main/docs/audit/port-gaps)
- Review report: [`docs/review/session-2026-04-20.md`](https://github.com/meywd/tamma/blob/main/docs/review/session-2026-04-20.md)

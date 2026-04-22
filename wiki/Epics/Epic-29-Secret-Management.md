# Epic 29: Platform Secret Management

**Status:** Planning (briefs + impl plans authored 2026-04-20)
**Stories:** 10 (29-1 through 29-10), ~166h
**Layer:** Layer 4 (integration/UI)
**Depends on:** Epic 28 Phase A (28-3 DbContext factory), Epic 19 Story 19-6 (real per-tenant `TammaAppDbContext` wiring)

> **Overview**: [Secret Management](Secret-Management) — root-level topic page with the data model, crypto pipeline, rotation patterns, and tenant/platform UI surfaces.

## Purpose

Today the platform has **three stopgap secret stores** and none of them is managed:

1. `TenantSecretProtector` — a direct-AES-GCM helper whose key is read from `Cranl:EncryptionKey` or falls back to HKDF-from-`Cranl:ApiKey`.
2. `tenants.cranl_database_url_encrypted` — a `bytea` column that holds the ciphertext of each tenant's Cranl DATABASE_URL, bound to the `TenantSecretProtector` above.
3. Plaintext env vars baked into deployment: `TAMMA_SHARED_SECRET`, `ConnectionStrings:TammaAppDb` (with a literal `changeme` password set by migration `20260419021119_Phase2RlsAndTriggers`), plus the `Cranl:ApiKey` and GitHub App private key.

The user's design intent (2026-04-20):

> Tenant DB passwords will be generated and saved in tenant secret store. Tenant admins can generate and edit these passwords, but that means auto-generate and update, since they can't access dbs directly. Platform works the same for admin. Secret management UI tells what this key is, where it's used and so on.

This epic ships a **typed secret cabinet** with two UIs (platform admin, tenant admin), rotation workflows that push the new value into the consumer (database, Cranl env, engine config), an auditable reveal-once-on-create flow, and a migration of every stopgap secret listed above into the cabinet.

## Current state

- Epic 1.5 secret-management track ships the LLM-safe ops path (1.5-16 onwards: vault store, crypto primitives, LLM-safe rotation activities)
- Epic 29 reuses Epic 1.5's primitives and adds the **operator-facing cabinet** on top
- All three stopgaps still live in production; closed by Story 29-9 (migrate) and Story 29-10 (delete)

## Stories

| # | Title | Effort | Depends on | Blocks | Status |
|---|-------|--------|------------|--------|--------|
| 29-1 | Secret store abstraction + typed data model | 16h | 1.5-16 | 29-2..29-10 | Planned |
| 29-2 | Postgres-backed envelope-encrypted store (KEK from env) | 22h | 29-1, 28-3 | 29-3..29-10 | Planned |
| 29-3 | Reveal-once-on-create UX + access audit events | 10h | 29-2 | 29-4, 29-5 | Planned |
| 29-4 | Platform-admin secret management UI | 24h | 29-3 | 29-9 | Planned |
| 29-5 | Tenant-admin secret management UI | 20h | 29-3, 28-9, 18-5 | 29-9 | Planned |
| 29-6 | Generic rotation workflow primitive (Elsa activity set) | 16h | 29-2, 1.5-30 | 29-7, 29-8 | Planned |
| 29-7 | Postgres role-password rotation workflow | 14h | 29-6, 19-6 | 29-9 | Planned |
| 29-8 | Cranl env-var rotation workflow (push + restart) | 16h | 29-6 | 29-9 | Planned |
| 29-9 | Migrate `tamma_app`, Cranl API key, shared HMAC, DB URLs | 20h | 29-4, 29-5, 29-7, 29-8 | 29-10 | Planned |
| 29-10 | Delete `TenantSecretProtector` + encrypted columns | 8h | 29-9 | — | Planned |

**Total**: 166h.

## Architecture / key decisions

1. **Typed secret kinds**: `database_credential`, `api_key`, `hmac_secret`, `generic`. Each kind binds to a rotation handler that knows how to push the new value into the consumer.
2. **Envelope encryption**: KEK (key-encryption key) wraps DEK (data-encryption key) per secret version. Story 29-2 ships KEK-from-env; Story 28-13 swaps to OpenBao-backed KEK only when triggered.
3. **Reveal-once-on-create**: secrets are shown to the operator exactly once at creation. After that, only the rotation workflow can read the plaintext. Loss of the only copy = emergency re-create + rotate.
4. **Rotation = create new version + push to consumer + retire old**. Old version stays `retired_grace` for N minutes; connection pool drains on rotate. Failed push compensates by reverting the active version pointer.
5. **No KMS on roadmap today**. `ISecretsService` seam stays intact; OpenBao is the planned backend driver if a trigger fires.
6. **Tenant-admin UI tenant-scoped twice**: backend tenant filter + RLS on `secret_versions` table (depends on Story 19-6).

## Dependencies

**Upstream**:
- [Epic 28](Epic-28-DB-Per-Tenant.md) Phase A — DbContext factory (28-3)
- [Epic 1.5](Epic-1.5-Infrastructure.md) — secret-management track (1.5-16, 1.5-30) for crypto primitives + LLM-safe rotation activities
- [Epic 19](Epic-19-Agent-Dispatch.md) Story 19-6 — real per-tenant `TammaAppDbContext` wiring

**Downstream**:
- [Epic 30](Epic-30-Pluggable-Provisioning.md) Stories 30-4..30-6 — each provisioning backend registers a rotation handler with the cabinet
- [Epic 31](Epic-31-Multi-Git-Platform.md) Story 31-8 — `ICiSecretsProvisioner` consumes the cabinet's per-tenant credentials

## Review findings closed

- **Finding 4** (code review 2026-04-20 §2.4) — `Cranl:EncryptionKey` HKDF-from-API-key fallback bypasses `ISecretsService`. Closed by 29-2 (all KEK material flows through the service).
- **Finding 15** (§2.15) — `tamma_app` password hard-coded `changeme` in Phase-2 migration. Closed by 29-9 (rotation on first deploy).
- **Finding 16** (§2.16) — `TAMMA_SHARED_SECRET` plaintext env var. Closed by 29-9 (moved into the cabinet, rotated with HMAC probe).
- **Partial close on Finding 1** (per-tenant wiring) via the rotation-aware `TammaAppDbContext` password pipeline in 29-7 + 29-9.

## Non-goals

- Does not introduce OpenBao. The `ISecretsService` seam is preserved; Story 28-13 remains the adoption path when triggers fire.
- Does not mirror secrets into GitHub / GitLab / Gitea Actions stores. Epic 1.5-23..1.5-26 own that surface.
- Does not change how the GitHub App private key is loaded — that's a separate hardening item.

## Risks

| Risk | Mitigation |
|------|------------|
| Rotation mid-request breaks in-flight calls | Grace window: old version stays `retired_grace` for N minutes; connection pool drains on rotate. |
| Reveal-once UX loses the only copy if browser closes | Documented explicitly in 29-3. Operator may request emergency re-create which generates a new value and rotates; old is revoked. |
| KEK rotation takes downtime | 29-2 AC: re-wrap DEKs without touching plaintext; operator can dual-run `PRIMARY` + `SECONDARY` KEKs for the rotation window. |
| Tenant-admin UI leaks other tenants' secrets | Enforced twice: backend tenant filter + RLS on `secret_versions` table (depends on 19-6). |

## Open questions

1. **GitHub App private key migration**: should it move into the cabinet too? Currently loaded from disk / env at boot. Out of scope for v1; flagged as a follow-up hardening item.
2. **Per-tenant vs platform-level KEK**: today both share one env-var KEK. A future hardening could give each tenant its own KEK derived from a master KMS — gated on the same triggers as Story 28-13.

## Sources

- User design intent: 2026-04-20 planning session
- Research notes: `docs/stories/research/secret-management-and-multi-backend-provisioning-2026.md`
- KEK decision memory: `~/.claude/projects/-home-meywd-tamma/memory/project_epic28_kek_decision.md`
- Epic 1.5 overlap: `docs/stories/plans/secret-management-track.md`
- Current stopgaps: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs`, `apps/tamma-elsa/src/Tamma.Data/Migrations/20260419021119_Phase2RlsAndTriggers.cs`

## Story files

[Epic 29 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-29)

---

_Last updated: 2026-04-21_

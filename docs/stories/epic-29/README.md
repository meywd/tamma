# Epic 29: Platform Secret Management

**Status**: planning (briefs only, 2026-04-20)
**Layer**: Layer 4 (integration/UI) — see
[`plans/epic-29-30-placement.md`](../plans/epic-29-30-placement.md)
**Depends on**: Epic 28 Phase A (28-3 DbContext factory), Epic 19 Story
19-6 (real per-tenant routing via `TammaAppDbContext`)
**Related**: Epic 1.5 secret-management track (LLM-safe ops) — Epic 29
reuses Epic 1.5's crypto primitives + vault store and adds the
operator-facing cabinet on top.

## Why this epic exists

Today the platform has three stopgap secret stores and none of them is
managed:

1. `TenantSecretProtector` — a direct-AES-GCM helper whose key is read
   from `Cranl:EncryptionKey` or falls back to HKDF-from-`Cranl:ApiKey`.
2. `tenants.cranl_database_url_encrypted` — a bytea column that holds
   the ciphertext of each tenant's Cranl DATABASE_URL, bound to the
   `TenantSecretProtector` above.
3. Plaintext env vars baked into deployment: `TAMMA_SHARED_SECRET`,
   `ConnectionStrings:TammaAppDb` (with a literal `changeme` password
   set by migration `20260419021119_Phase2RlsAndTriggers`), plus the
   `Cranl:ApiKey` and GitHub App private key.

The user's design intent (2026-04-20):

> Tenant DB passwords will be generated and saved in tenant secret
> store. Tenant admins can generate and edit these passwords, but that
> means auto-generate and update, since they can't access dbs
> directly. Platform works the same for admin. Secret management UI
> tells what this key is, where it's used and so on.

This epic ships a **typed secret cabinet** with two UIs (platform
admin, tenant admin), rotation workflows that push the new value into
the consumer (database, Cranl env, engine config), an auditable reveal-
once-on-create flow, and a migration of every stopgap secret listed
above into the cabinet.

## Scope

- **In-scope**: the control-plane cabinet (data model, crypto, UX,
  rotation primitives), migrating the six named stopgap secrets,
  deleting the stopgap code.
- **Out-of-scope**: LLM-facing workflow ops for secrets (Epic 1.5
  covers those), platform-native mirrors to GitHub/GitLab/Gitea CI
  variable stores (Epic 1.5-23 through 1.5-26), KMS/OpenBao backend
  adoption (gated on Story 28-13 triggers — the `ISecretsService` seam
  stays intact so the switch is a driver swap later).

## Story map

| # | Title | Est. hours | Depends on | Blocks |
|---|---|---|---|---|
| [29-1](./29-1-secret-store-abstraction.md) | Secret store abstraction + typed data model | 16 | 1.5-16 | 29-2 .. 29-10 |
| [29-2](./29-2-postgres-backed-store.md) | Postgres-backed envelope-encrypted store (KEK from env) | 22 | 29-1, 28-3 | 29-3 .. 29-10 |
| [29-3](./29-3-reveal-once-on-create.md) | Reveal-once-on-create UX + access audit events | 10 | 29-2 | 29-4, 29-5 |
| [29-4](./29-4-platform-admin-ui.md) | Platform-admin secret management UI | 24 | 29-3 | 29-9 |
| [29-5](./29-5-tenant-admin-ui.md) | Tenant-admin secret management UI | 20 | 29-3, 28-9, 18-5 | 29-9 |
| [29-6](./29-6-rotation-workflow-primitive.md) | Generic rotation workflow primitive (Elsa activity set) | 16 | 29-2, 1.5-30 | 29-7, 29-8 |
| [29-7](./29-7-db-credential-rotation.md) | Postgres role-password rotation workflow | 14 | 29-6, 19-6 | 29-9 |
| [29-8](./29-8-cranl-env-rotation.md) | Cranl env-var rotation workflow (push + restart) | 16 | 29-6 | 29-9 |
| [29-9](./29-9-migrate-stopgap-secrets.md) | Migrate tamma_app, Cranl API key, shared HMAC, DB URLs | 20 | 29-4, 29-5, 29-7, 29-8 | 29-10 |
| [29-10](./29-10-delete-stopgaps.md) | Delete `TenantSecretProtector` + encrypted columns | 8 | 29-9 | — |
| **Total** | | **166** | | |

## Review findings this epic closes

- **Finding 4** (code review 2026-04-20 §2.4) — `Cranl:EncryptionKey`
  HKDF-from-API-key fallback bypasses `ISecretsService`. Closed by
  29-2 (all KEK material flows through the service).
- **Finding 15** (§2.15) — `tamma_app` password hard-coded `changeme`
  in Phase-2 migration. Closed by 29-9 (rotation on first deploy).
- **Finding 16** (§2.16) — `TAMMA_SHARED_SECRET` plaintext env var.
  Closed by 29-9 (moved into the cabinet, rotated with HMAC probe).
- **Partial close on Finding 1** (per-tenant wiring) via the rotation-
  aware `TammaAppDbContext` password pipeline in 29-7 + 29-9.

## Non-goals

- Does not introduce OpenBao. The `ISecretsService` seam is preserved;
  Story 28-13 remains the adoption path when triggers fire.
- Does not mirror secrets into GitHub / GitLab / Gitea Actions stores.
  Epic 1.5-23 through 1.5-26 own that surface.
- Does not change how the GitHub App private key is loaded — that's a
  separate hardening item.

## Risks

| Risk | Mitigation |
|---|---|
| Rotation mid-request breaks in-flight calls | Grace window: old version stays `retired_grace` for N minutes; connection pool drains on rotate. |
| Reveal-once UX loses the only copy if browser closes | Accept it — documented explicitly in 29-3. Operator may request an emergency re-create which generates a new value and rotates; the old is revoked. |
| KEK rotation takes downtime | 29-2 AC: re-wrap DEKs without touching plaintext; operator can dual-run `PRIMARY` + `SECONDARY` KEKs for the rotation window. |
| Tenant-admin UI leaks other tenants' secrets | Enforced twice: backend tenant filter + RLS on `secret_versions` table (depends on 19-6). |

## Sources

- User design intent: 2026-04-20 planning session
- Research notes: [`../research/secret-management-and-multi-backend-provisioning-2026.md`](../research/secret-management-and-multi-backend-provisioning-2026.md)
- KEK decision memory: `~/.claude/projects/-home-meywd-tamma/memory/project_epic28_kek_decision.md`
- Epic 1.5 overlap: [`../epic-1.5/README.md`](../epic-1.5/README.md), [`../plans/secret-management-track.md`](../plans/secret-management-track.md)
- Current stopgaps: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs`, `apps/tamma-elsa/src/Tamma.Data/Migrations/20260419021119_Phase2RlsAndTriggers.cs`

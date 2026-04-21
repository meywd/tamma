# Secret Management (Epic 29)

**Status**: planning (briefs + impl plans authored 2026-04-20). 10 stories, 166h, Layer 4.
**Depends on**: Epic 28 Phase A (DbContext factory), Story 19-6 (real per-tenant `TammaAppDbContext` wiring).
**Source**: `docs/stories/epic-29/` (10 briefs + 10 impl plans + README).

## Why this epic exists

The platform currently has **three stopgap secret stores** and none of them is managed:

1. `TenantSecretProtector` — a direct-AES-GCM helper whose key is read from `Cranl:EncryptionKey` or falls back to HKDF-from-`Cranl:ApiKey`.
2. `tenants.cranl_database_url_encrypted` — a `bytea` column that holds each tenant's Cranl DATABASE_URL ciphertext, bound to the `TenantSecretProtector` above.
3. Plaintext env vars baked into deployment: `TAMMA_SHARED_SECRET`, `ConnectionStrings:TammaAppDb` (with a literal `changeme` password set by migration `20260419021119_Phase2RlsAndTriggers`), plus the `Cranl:ApiKey` and GitHub App private key.

The 2026-04-20 code review surfaced findings 4, 15, 16 that all trace back to these stopgaps. Epic 29 ships the unified **typed secret cabinet** that replaces them.

## Design intent

> Tenant DB passwords will be generated and saved in tenant secret store. Tenant admins can generate and edit these passwords, but that means auto-generate and update, since they can't access DBs directly. Platform works the same for admin. Secret management UI tells what this key is, where it's used and so on.

— User design intent, 2026-04-20 planning session.

## Data model

```
secret_kinds (enum):
  - database_credential           (e.g. tamma_app role password)
  - api_key                       (e.g. Cranl API key; user-scoped API keys stay in their own table)
  - hmac_secret                   (e.g. TAMMA_SHARED_SECRET)
  - generic                       (operator-entered with display name + description)

secrets
  ├─ id  (UUID)
  ├─ tenant_id  (UUID, nullable — platform-level secrets are NULL)
  ├─ display_name
  ├─ kind
  ├─ consumer_metadata  (JSONB — tells the rotation handler which consumer to push to)
  ├─ current_version_id  (FK → secret_versions)
  ├─ created_at / updated_at

secret_versions
  ├─ id  (UUID)
  ├─ secret_id  (FK)
  ├─ ciphertext  (bytea, envelope-encrypted)
  ├─ dek_wrapped  (bytea — DEK wrapped by KEK)
  ├─ status  ('active' | 'retired_grace' | 'revoked')
  ├─ created_at
  ├─ retired_at

secret_access_events
  ├─ secret_id, user_id, action ('reveal'|'create'|'rotate'|'revoke'), timestamp
```

- **Envelope encryption**: DEK per secret version wraps the ciphertext; KEK wraps each DEK. KEK lives in env var on ship (Epic 28 §8.2 KEK decision); OpenBao adoption deferred to Story 28-13 until trigger conditions fire.
- **Reveal-once-on-create**: the plaintext is shown to the operator once at creation time and never again. After that point, only the rotation workflow can read it.
- **Versioned rotations**: a retired secret stays `retired_grace` for a configurable window (default 15 min) so in-flight connections drain without breaking.
- **RLS + tenant filter**: `secret_versions` are RLS-scoped to the tenant; tenant admins see their own only. Four-layer defense (RBAC + app-role connection filter + RLS + store-level assertion). Full defense depends on Story 19-6.

## Rotation workflow primitive

`RotationWorkflowActivity` (Story 29-6, Elsa) implements the shared shape of any rotation:

```
1. Mint new value (cryptographically random or user-supplied)
2. Write as new secret_version, status='active', old version → 'retired_grace'
3. Dispatch to consumer via IRotationHandler<TConsumer>
4. Wait for consumer ack (or timeout → compensate)
5. Drain grace window
6. Mark old version 'revoked'
```

Each backend (Postgres, Cranl env, GitHub Actions secret, BYO) registers its own `IRotationHandler` implementation. The workflow is resumable and idempotent per `(secretId, version)`.

## Concrete rotation handlers

| Story | Handler | Consumer | Behaviour |
|-------|---------|----------|-----------|
| 29-7 | `PostgresRolePasswordRotationHandler` | PG role (e.g. `tamma_app`) | `ALTER ROLE … PASSWORD …`; signal connection-pool drainer to recycle connections |
| 29-8 | `CranlEnvVarRotationHandler` | Cranl per-tenant env | PUT new env value via Cranl API; wait for app restart; probe new value |
| (future) | `GitHubActionsSecretRotationHandler` | GitHub Actions repo secret | Libsodium seal + PUT via Octokit |

## Migration of existing stopgaps

Story 29-9 migrates the six named stopgap secrets into the cabinet:

1. `tamma_app` role password — auto-rotate from `changeme` to a high-entropy value on import (closes review finding 4 + 15).
2. `TAMMA_SHARED_SECRET` — import existing env value, schedule 30-day rotation (closes review finding 16).
3. `Cranl:ApiKey` — import, add to rotation-handler registry.
4. `Cranl:EncryptionKey` — hard-require after import; HKDF-from-ApiKey fallback removed (closes half of finding 4).
5. `tenants.cranl_database_url_encrypted` — re-encrypt under the cabinet's envelope; delete the bespoke column in Story 29-10.
6. GitHub App private key — imported; rotation deferred to a separate hardening pass.

Story 29-10 deletes `TenantSecretProtector.cs` and the `cranl_database_url_encrypted` column once 29-9 completes.

## UIs

### Platform admin UI (Story 29-4)

- List all platform secrets (kind, display name, rotation status, last-rotated-at).
- Rotate / reveal-once-on-create (operator confirms they've captured the new value).
- View per-secret access audit log (`secret_access_events`).
- Trigger migration of individual stopgap secrets (transition from "pending" to "managed").

### Tenant admin UI (Story 29-5)

Same shape, scoped to the tenant's own secrets. Depends on 18-5 dashboard shell and 28-9 `switch-org` JWT claim.

## Story map

| # | Title | Est. hours | Depends on |
|---|---|---|---|
| 29-1 | Secret store abstraction + typed data model | 16 | 1.5-16 |
| 29-2 | Postgres-backed envelope-encrypted store (KEK from env) | 22 | 29-1, 28-3 |
| 29-3 | Reveal-once-on-create UX + access audit events | 10 | 29-2 |
| 29-4 | Platform-admin secret management UI | 24 | 29-3 |
| 29-5 | Tenant-admin secret management UI | 20 | 29-3, 28-9, 18-5 |
| 29-6 | Generic rotation workflow primitive (Elsa activity set) | 16 | 29-2, 1.5-30 |
| 29-7 | Postgres role-password rotation workflow | 14 | 29-6, 19-6 |
| 29-8 | Cranl env-var rotation workflow (push + restart) | 16 | 29-6 |
| 29-9 | Migrate `tamma_app`, Cranl API key, shared HMAC, DB URLs | 20 | 29-4, 29-5, 29-7, 29-8 |
| 29-10 | Delete `TenantSecretProtector` + encrypted columns | 8 | 29-9 |
| **Total** |  | **166h** | |

## Review findings closed

| Finding | Severity | Closes via |
|---------|----------|------------|
| #4 `Cranl:EncryptionKey` HKDF-from-ApiKey fallback | P1 | 29-2 (all KEK material flows through `ISecretsService`) + 29-10 (delete fallback) |
| #15 `tamma_app` `PASSWORD 'changeme'` literal | P0 | 29-9 (auto-rotate on import) + 29-10 (safety-net migration asserts rotation) |
| #16 `TAMMA_SHARED_SECRET` plaintext env var | P1 | 29-9 (import + rotate) + 29-6 (primitive) + 29-8 (Cranl handler applies new value to consumer) |

## Non-goals

- Does not introduce OpenBao. `ISecretsService` seam is preserved; [Story 28-13](https://github.com/meywd/tamma/blob/main/docs/stories/epic-28/story-28-13/28-13-openbao-kms-backend-planning-blocker.md) remains the adoption path when triggers fire.
- Does not mirror secrets into GitHub / GitLab / Gitea Actions stores. [Epic 1.5-23..1.5-26](https://github.com/meywd/tamma/blob/main/docs/stories/plans/secret-management-track.md) owns the LLM-safe-ops surface.
- Does not change how the GitHub App private key is loaded — separate hardening item.

## Related

- [Security](Security)
- [Port Audit](Port-Audit) — review findings 4, 15, 16
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) — Epic 30 backends each register rotation handlers
- [Architecture → Tenancy & Data Isolation](Architecture#tenancy--data-isolation)
- Source: [`docs/stories/epic-29/README.md`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-29)
- Layer placement: [`docs/stories/plans/epic-29-30-placement.md`](https://github.com/meywd/tamma/blob/main/docs/stories/plans/epic-29-30-placement.md)
- Research: [`docs/stories/research/secret-management-and-multi-backend-provisioning-2026.md`](https://github.com/meywd/tamma/blob/main/docs/stories/research/secret-management-and-multi-backend-provisioning-2026.md)

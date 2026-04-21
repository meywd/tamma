# Story 28-12 Implementation Plan — Postgres Roles + KEK Rotation

**Status**: Planned (2026-04-20)
**Story brief**: [`28-12-postgres-roles-kek-rotation.md`](./28-12-postgres-roles-kek-rotation.md)
**Epic 28 phase**: Ops stream
**Branch**: `feat/story-28-12-roles-kek`

---

## 1. Objective

Establish three distinct Postgres cluster roles (`tamma_admin`,
`tamma_provisioner`, `tamma_app`) enforced at cluster bootstrap, plus
AES-256-GCM envelope encryption of every per-tenant connection string
with a master KEK. KEK rotates via a primary+secondary overlap scheme
and a background re-encrypt task with a documented 90-minute rotation
runbook that never interrupts live tenant traffic.

## 2. Dependencies

Hard blockers:

- Postgres 17 cluster bootstrap access.
- Hetzner sealed-secrets path for `TAMMA_TENANT_KEK_PRIMARY` /
  `TAMMA_TENANT_KEK_SECONDARY`.
- **Story 28-5** encrypts new tenant creds via `ISecretsService`.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/scripts/postgres-roles.sql` | Cluster-bootstrap idempotent role script. |
| `/home/meywd/tamma/apps/tamma-elsa/docker-entrypoint-db.sh` | Wraps Postgres entrypoint + applies role script. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretsService.cs` | Contract (also used by 28-4 + 28-5). |
| `.../Services/Secrets/EnvKekSecretsService.cs` | Impl reading `TAMMA_TENANT_KEK_PRIMARY/SECONDARY` env vars. |
| `.../Services/Secrets/AesGcmEnvelope.cs` | AES-256-GCM encrypt/decrypt primitive + ciphertext framing (`v1|iv|ciphertext|tag`). |
| `.../Services/Secrets/KekRotationWorker.cs` | Background service that re-encrypts `tenants.DbConnectionCiphertext` entries under the new KEK. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/KekRotationEndpoints.cs` | `POST /admin/kek/rotate/start`, `GET /admin/kek/rotate/status`. |
| `/home/meywd/tamma/docs/runbooks/kek-rotation.md` | 90-minute operator runbook. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/AesGcmEnvelopeTests.cs` | Round-trip, tamper detection, version-prefix validation. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/KekRotationWorkerTests.cs` | Rotation semantics, concurrency, mid-rotate read. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/docker/docker-compose.yml` | Mount `postgres-roles.sql` into Postgres container at `/docker-entrypoint-initdb.d/` so it applies on bootstrap. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register `ISecretsService`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` | Confirm `DbConnectionCiphertext` column (from 28-5) is `text` with envelope framing. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | Apply `TAMMA_PROVISIONER_DB_URL` — uses `tamma_provisioner` role. |

## 5. Sequence of changes

### Step 1 — Cluster roles script (3h)

- `postgres-roles.sql` creates `tamma_admin`, `tamma_provisioner`,
  `tamma_app`. Idempotent via `DO $$ IF NOT EXISTS ... $$`.
- Grants per Doc 01 §7.1 matrix.
- Entrypoint script wraps.
- **Commit**: `ops(postgres): cluster-bootstrap role script`.

### Step 2 — AES-256-GCM envelope (3h)

- `AesGcmEnvelope.Encrypt(plaintext, kek)` returns
  `v1|<b64iv>|<b64ct>|<b64tag>` (4 fields).
- `Decrypt(ciphertext, [kekPrimary, kekSecondary])` tries primary
  first, falls back to secondary on MAC failure (constant-time check).
- Property-based tests: 100k random plaintexts round-trip correctly.
- Tamper tests: flipping any byte fails decrypt.
- **Commit**: `feat(secrets): AES-256-GCM envelope primitive`.

### Step 3 — Env-backed SecretsService (2h)

- Reads `TAMMA_TENANT_KEK_PRIMARY` + optional
  `TAMMA_TENANT_KEK_SECONDARY`.
- Rejects KEKs shorter than 32 bytes (post-base64 decode).
- `EncryptConnectionString` always uses primary; `DecryptConnectionString`
  tries both.
- Emits structured log on fallback-to-secondary (signal for rotation progress).
- **Commit**: `feat(secrets): env-backed KEK SecretsService`.

### Step 4 — Rotation worker (5h)

- `KekRotationWorker`:
  - On `POST /admin/kek/rotate/start` → enqueues a queue task per tenant.
  - Per tenant: decrypt with primary+secondary, re-encrypt with
    primary, update `tenants.DbConnectionCiphertext`, emit
    `SECRETS.KEK.ROTATE.STEP.SUCCESS` event.
  - Concurrency: 4 tenants at a time.
  - On completion: emit `SECRETS.KEK.ROTATE.COMPLETED` with count +
    duration.
- Status endpoint reports progress: `{ total, completed, inFlight, failed }`.
- **Commit**: `feat(secrets): KEK rotation worker + endpoints`.

### Step 5 — Runbook (3h)

- `docs/runbooks/kek-rotation.md` — 90-min procedure:
  1. Generate new 32-byte key via `openssl rand -base64 32`.
  2. Deploy with `TAMMA_TENANT_KEK_PRIMARY=<new>` and
     `TAMMA_TENANT_KEK_SECONDARY=<old>`.
  3. Wait for steady-state (5 min).
  4. `POST /admin/kek/rotate/start`.
  5. Poll status until `completed`.
  6. Deploy with secondary unset.
- **Commit**: `docs(runbooks): 90-minute KEK rotation`.

### Step 6 — Integration tests + deploy (3h)

- Integration test: create encrypted row with primary, flip to
  new primary + old as secondary, assert read works; trigger rotation,
  assert re-encrypted; assert secondary dropped.
- Deploy gate: run all 3 roles on staging; verify pg_stat_activity
  shows expected role per connection.
- **Commit**: `test(secrets): KEK rotation E2E`.

## 6. Test strategy

### Unit

- `AesGcmEnvelopeTests` — round-trip, tamper, version prefix.
- `SecretsServiceTests` — primary/secondary fallback, null handling.
- `KekRotationWorkerTests` — progress counters, concurrency, failure.

### Integration

- Staging deploy: roles enforce (try `CREATE DATABASE` as `tamma_app`
  → permission denied).
- Rotation end-to-end on 3 tenants; assert zero downtime in
  concurrent traffic simulation.

### Security

- Verify no KEK bytes ever appear in logs (grep test).
- Verify `EnvKekSecretsService` refuses to start without primary.

## 7. Rollback plan

- **Roles**: non-reversible at Postgres level (revoke → can't revert
  without service restart). Runbook documents recovery.
- **Rotation rollback**: if primary is set to a bad key, secondary
  still decrypts. Worker fails safe (re-encrypt won't run without
  successful decrypt).
- **KEK loss**: catastrophic. Runbook mandates storing KEKs in
  Hetzner sealed secrets with 3 trusted human custodians.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Roles script | 3 |
| 2. AES-GCM envelope | 3 |
| 3. SecretsService | 2 |
| 4. Rotation worker | 5 |
| 5. Runbook | 3 |
| 6. Integration + deploy | 3 |
| **Total** | **19** (brief 20). |

## 9. Open questions

- **KEK length**: 32 bytes (256 bits). Confirmed with AES-256-GCM spec.
- **Nonce / IV handling**: per-message random 12-byte IV. Counter-based
  would be faster but risks reuse. Random is safe.
- **Secondary KEK persistence during rotation**: must survive pod
  restart. Documented in runbook.
- **Future OpenBao swap**: Story 28-13 (deferred). `ISecretsService`
  interface is the seam.
- **RBAC for rotation endpoints**: platform-admin only (28-9 policy).
- **Re-encrypt failures**: per-tenant row marked `RotationError` with
  message; admin retries manually. Count surfaced in endpoint status.

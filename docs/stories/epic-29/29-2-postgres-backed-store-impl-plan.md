# Story 29-2 Implementation Plan — Postgres-Backed Envelope-Encrypted Secret Store

**Status**: Planned (2026-04-20)
**Story brief**: [`29-2-postgres-backed-store.md`](./29-2-postgres-backed-store.md)
**Epic 29 phase**: Foundation — after 29-1.
**Branch**: `feat/story-29-2-postgres-secret-store`

---

## 1. Objective

Ship `PostgresSecretStoreBackend` implementing `ISecretStoreBackend`
from 29-1. Two metadata tables (`platform_secrets`,
`tenant_secrets` with RLS) + a shared `secret_versions` table
storing AES-256-GCM envelopes per the spec (version byte + kek_id
byte + wrap nonce + wrapped DEK + value nonce + value ct + value
tag). Env-sourced KEK with primary/secondary overlap for rotation.
Plaintext never touches an EF-tracked entity or an `ILogger` call.

## 2. Dependencies

Hard blockers:

- **Story 29-1** — interfaces and types.
- **Story 28-3** — per-tenant DbContext so tenant_secrets routes.
- **Story 19-6** — app-role wiring so the RLS policy actually
  enforces isolation.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260422000000_SecretStoreSchema.cs` | 3 tables + RLS. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/PostgresSecretStoreBackend.cs` | Main impl. |
| `.../Services/Secrets/SecretEnvelope.cs` | Encode/decode + format version check. |
| `.../Services/Secrets/KekLoader.cs` | Reads `TAMMA_SECRET_STORE_KEK_PRIMARY/SECONDARY`. |
| `.../Services/Secrets/PostgresSecretStore.cs` | Implements `ISecretStore` on top of backend + auditor. |
| `.../Services/Secrets/PostgresSecretAccessAuditor.cs` | Writes `SECRET.*` to `platform_events` / `domain_events`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/PostgresSecretStoreBackendTests.cs` | Testcontainers integration. |
| `.../Secrets/SecretEnvelopeTests.cs` | Round-trip, tamper, version. |
| `.../Secrets/KekLoaderTests.cs` | Startup health, length validation. |
| `.../Secrets/RewrapOperationTests.cs` | KEK rotation semantics. |
| `/home/meywd/tamma/docs/runbooks/kek-rotation-secret-store.md` | 5-step operator procedure. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Add `PlatformSecret` + `SecretVersion` + `SecretAccessAudit` DbSets. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Add `TenantSecret` DbSet (RLS enforced). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register real `ISecretStore` + `ISecretAccessAuditor` behind `SecretStore:Backend=postgres`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | Add `SecretStore:Backend=postgres`, env-var docs. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlTenantProvisioner.cs` | Warn on `Cranl:EncryptionKey` fallback; ignore. |

## 5. Sequence of changes

### Step 1 — Schema (3h)

- Migration creates `platform_secrets`, `tenant_secrets`,
  `secret_versions`, `secret_access_audit`.
- RLS policy `secret_isolation_policy` on `tenant_secrets`
  matches `current_setting('app.current_tenant_id', true)::uuid`.
- `tamma_app` grants: SELECT/INSERT/UPDATE/DELETE on tenant_secrets;
  no access on platform_secrets.
- **Commit**: `feat(db): secret-store schema + RLS`.

### Step 2 — Envelope primitive (3h)

- `SecretEnvelope.Encrypt(plaintext, kek)` returns bytea per spec.
- `Decrypt(envelope, [primary, secondary?])`:
  - Version byte check → unknown throws `SecretEnvelopeFormatException`.
  - Try KEK with matching `kek_id` first; fallback to secondary.
- Constant-time tag compare.
- Unit tests: 100k round-trips; tamper detection; format-version mismatch.
- **Commit**: `feat(secrets): AES-GCM envelope`.

### Step 3 — KEK loader (2h)

- Read + base64-decode env vars; validate 32-byte length.
- Startup health check throws if `_PRIMARY` missing.
- Unit tests: missing, too short, bad base64.
- **Commit**: `feat(secrets): KEK loader + startup health`.

### Step 4 — Backend + store (4h)

- `PostgresSecretStoreBackend.PutVersionAsync` / `GetVersionPlaintextAsync` / `DeleteVersionAsync`.
- `PostgresSecretStore` wraps backend + auditor.
- Every backend call emits exactly one audit event.
- **Commit**: `feat(secrets): postgres backend + store`.

### Step 5 — Rewrap operation (3h)

- `RewrapAllAsync(oldKekId, newKekId, ct)`:
  - Batches of 100 rows.
  - For each: decrypt DEK with old, re-encrypt DEK with new, update
    `wrapped_dek` + `kek_id` byte. Plaintext value unchanged.
  - Emits progress events.
- Unit + integration tests.
- **Commit**: `feat(secrets): KEK rewrap operation`.

### Step 6 — Integration tests (3h)

- Create/read/rotate/retire full path.
- RLS enforcement: tenant A cannot read tenant B's rows via app connection.
- KEK rotation E2E: encrypt under A, swap to B+A, decrypt, rewrap,
  remove A, decrypt.
- **Commit**: `test(secrets): Testcontainers integration`.

### Step 7 — Runbook + deprecation (2h)

- `kek-rotation-secret-store.md`.
- `Cranl:EncryptionKey` deprecation warning path.
- **Commit**: `docs(secrets): KEK rotation runbook + deprecation`.

## 6. Test strategy

### Unit

- Envelope (round-trip, tamper, format version).
- KEK loader (edge cases).
- Store + backend (mocked DbContext).

### Integration

- Testcontainers Postgres 17: full lifecycle, RLS, KEK rotation.
- Property test: 1000 random plaintexts round-trip without loss.

### Security

- Grep test: no plaintext bytes appear in any log output.
- Timing test: constant-time decrypt comparison.

## 7. Rollback plan

- **Feature flag**: `SecretStore:Backend=env` disables Postgres
  backend; code path that doesn't exist yet falls back to env
  (handled by 29-9's resolver).
- **Schema rollback**: migration drop order: audit → versions →
  tenant_secrets → platform_secrets.
- **KEK loss**: catastrophic; documented in runbook with 3-custodian
  recovery.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Schema | 3 |
| 2. Envelope | 3 |
| 3. KEK loader | 2 |
| 4. Backend + store | 4 |
| 5. Rewrap | 3 |
| 6. Integration tests | 3 |
| 7. Runbook | 2 |
| **Total** | **20** (brief 22). |

## 9. Open questions

- **DEK size**: 32 bytes (per AC3). Standard for AES-256.
- **`kek_id` byte exhaustion**: 256 values. Rotation every few
  months = centuries until exhaustion.
- **Platform-admin bypass via superuser connection**: documented
  pattern; every read still audited (AC8).
- **`secret_access_audit` vs. `platform_events`**: separate table
  for cross-tenant reporting; `platform_events` keeps the broader
  audit trail. 28-6 covers the latter.
- **RLS in CI tests**: must connect as `tamma_app` role. Test
  fixture creates role + grants per test database.

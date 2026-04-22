# Story 29-2: Postgres-Backed Envelope-Encrypted Secret Store

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform operator**,
I want the default `ISecretStoreBackend` to persist envelope-encrypted secret versions in Postgres using a KEK sourced from an operator-supplied env var,
so that the platform can manage DB passwords, API keys, and shared HMACs without adopting OpenBao today (per the 2026-04-17 decision recorded in `project_epic28_kek_decision.md`), while keeping the path open to swap in OpenBao or a cloud KMS later via the `ISecretStoreBackend` port from Story 29-1.

## Acceptance Criteria

1. A new migration `20260422000000_SecretStoreSchema.cs` creates two tables: `platform_secrets` (metadata, platform-scoped) and `tenant_secrets` (metadata, tenant-scoped; RLS-enforced). Both reference a shared `secret_versions` table keyed on `(secret_id, version_number)` storing the envelope bytea.
2. Migration also creates `secret_access_audit` (platform-scoped) referenced by `ISecretAccessAuditor` for cross-tenant reporting. RLS policy grants `tamma_app` read on its own tenant's rows only; platform admin reads via the admin context.
3. `secret_versions.envelope` is a `bytea` with layout `version(1) ‖ kek_id(1) ‖ wrap_nonce(12) ‖ wrapped_dek(48) ‖ value_nonce(12) ‖ value_ct(var) ‖ value_tag(16)`. The `version` byte starts at `0x01`; unknown versions raise `SecretEnvelopeFormatException` on read.
4. `PostgresSecretStoreBackend` implements `ISecretStoreBackend` with `PutVersionAsync` / `GetVersionPlaintextAsync` / `DeleteVersionAsync`. Plaintext bytes never touch an EF-tracked entity or an `ILogger` call.
5. KEK material is loaded from `TAMMA_SECRET_STORE_KEK_PRIMARY` (required, base64, 32 bytes) and optionally `TAMMA_SECRET_STORE_KEK_SECONDARY` (for rotation). `_PRIMARY` wraps new DEKs; reads try `_PRIMARY` first and fall back to `_SECONDARY` when `kek_id` mismatches. A startup health check fails the process if `_PRIMARY` is missing or not 32 bytes.
6. A `RewrapAllAsync(oldKekId, newKekId, ct)` operation re-wraps every DEK under the new KEK in batches. Emits progress events (`SECRET.KEK.REWRAP.STARTED|PROGRESS|COMPLETED|FAILED`). Does not touch plaintext values — only re-wraps the DEK.
7. Uses `AesGcm` (System.Security.Cryptography) for both the wrap step and the value step. Both steps use fresh nonces from `RandomNumberGenerator.GetBytes(12)`. 16-byte auth tag; tag-mismatch on read throws and audits a `SECRET.DECRYPT.FAILED` event.
8. Per-tenant `tenant_secrets` rows are isolated by RLS policy `secret_isolation_policy` matching `current_setting('app.current_tenant_id', true)::uuid`. Platform admin reads through the admin connection (superuser bypass) with every read audited.
9. Integration test (Testcontainers Postgres 17): create secret, read it back, rotate to a new value, read old version (returns `RetiredGrace` payload during grace window), then `Revoke` and verify read throws `SecretVersionRevokedException`.
10. KEK rotation test: put a version under `KEK_A`, swap env to `KEK_B` primary + `KEK_A` secondary, read succeeds; run `RewrapAllAsync`; remove `KEK_A`; read still succeeds.
11. `ISecretsService` (the existing seam per Doc 01 §8.2) is updated to route to `PostgresSecretStoreBackend` when a feature flag `SecretStore:Backend=postgres` is set; the feature-flag path is the only supported value in this story but the plumbing accepts `openbao`, `kms-aws`, `kms-gcp`, `kms-azure` as future values for the driver swap.
12. No `Cranl:EncryptionKey` fallback. If the config key is present, startup warns with a `"Cranl:EncryptionKey is deprecated; see Story 29-10"` message and the value is ignored.

## Technical Context

### Why per-secret DEKs instead of direct KEK-encrypt

The Epic 28 direct-encrypt design (`project_epic28_kek_decision.md`)
was picked for simplicity when the encrypted payload was a single
connection string per tenant. Epic 29 stores dozens of secrets per
tenant; rotating the KEK under direct-encrypt means decrypt-and-
re-encrypt every payload. Per-secret DEK re-wrap keeps KEK rotation
O(rows-in-secret-versions) × 32 bytes of I/O instead of
O(total-plaintext-bytes). Envelope adds 61 bytes per version — trivial
versus the rotation-cost savings.

### Row shape

```
byte 0:    envelope format version (0x01)
byte 1:    kek_id (0x01 = PRIMARY at write-time, 0x02 = SECONDARY)
bytes 2-13:  wrap_nonce (12 bytes, AES-GCM nonce for wrapping DEK)
bytes 14-61: wrapped_dek (32-byte DEK + 16-byte AES-GCM tag)
bytes 62-73: value_nonce (12 bytes)
bytes 74-N-17: value_ct (variable)
bytes N-16-N-1: value_tag (16 bytes)
```

### Env-var schema

```
TAMMA_SECRET_STORE_KEK_PRIMARY=<base64 32 bytes>
TAMMA_SECRET_STORE_KEK_SECONDARY=<optional; base64 32 bytes>
TAMMA_SECRET_STORE_KEK_PRIMARY_ID=01                   # default
TAMMA_SECRET_STORE_KEK_SECONDARY_ID=02                 # default
```

Rotation procedure (documented in 29-2's runbook):
1. Generate new 32-byte key; put it as `_SECONDARY` with id `0x02`.
2. Restart process. Reads now accept both.
3. Promote: swap env vars so `_PRIMARY` is the new key with id `0x02`, `_SECONDARY` is the old key with id `0x01`. Restart.
4. Call `RewrapAllAsync(0x01, 0x02)` via an admin endpoint / one-shot task.
5. Remove `_SECONDARY` when re-wrap finishes. Restart.

### Out-of-scope

- OpenBao driver (Story 28-13).
- AWS/GCP/Azure KMS drivers (future, gated on Story 28-13 triggers).
- UI (29-4, 29-5).

## Estimated hours

22 — schema + driver + KEK loader + re-wrap operation + integration
tests + rollout runbook.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Data/Migrations/20260422000000_SecretStoreSchema.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/PostgresSecretStoreBackend.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretEnvelope.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/KekLoader.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/ISecretsService.cs` (extend — routing switch)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/PostgresSecretStoreBackendTests.cs` (new)

## References

- [Envelope Encryption — Google Cloud KMS](https://docs.cloud.google.com/kms/docs/envelope-encryption)
- Epic 28 KEK decision: `~/.claude/projects/-home-meywd-tamma/memory/project_epic28_kek_decision.md`
- Story 28-12 (current env-var KEK home for tenant DB URLs) — will be superseded by 29-9.
- Research notes §4

# Story 28.12: Postgres Roles (`admin` / `provisioner` / `app`) + KEK Rotation

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Operations
**Status**: MOSTLY DONE — AC1+AC2 role-split runtime enforcement CLOSED by the 2026-05-30 follow-up (see section below). Remaining residuals: `RekeyTenantConnectionStringsWorkflow` location (coordinator-instead-of-workflow may be the new architecture) and AC5 KEK-rotation items — see audit `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md`. AC1/AC2 startup `current_user` check + distinct compose role-URL slots are now in place.
**Priority**: High (the three-role privilege split and per-tenant KEK
encryption are the hard security floor of the DB-per-tenant model;
shipping without them lets the runtime API `CREATE DATABASE` and
stores per-tenant passwords in cleartext-adjacent form, contradicting
Epic 28's SOC 2 / ISO 27001 unlocks)
**Estimated Effort**: L (20h)

## User Story

As a **security engineer**, I want **three distinct Postgres cluster
roles (`tamma_admin`, `tamma_provisioner`, `tamma_app`) enforced at
cluster bootstrap, plus AES-256-GCM envelope-encryption of every
per-tenant connection string with a master KEK rotatable via a
secondary-key overlap and a background re-encrypt task**, so that
**the runtime API cannot provision arbitrary databases, a single
leaked secret compromises only one privilege level's blast radius,
and we have a documented 90-minute KEK-rotation runbook that never
interrupts live tenant traffic**.

## Acceptance Criteria

### AC1: Three Postgres roles with least-privilege grants

Per Doc 01 §7.1 exact matrix:

- [ ] **`tamma_admin`** — `SUPERUSER`. Used only for manual
      migrations, disaster recovery, ad-hoc investigations by a
      human operator. Secret stored in Hetzner sealed secrets and
      sourced into the operator's shell at the moment it is needed
      (see Doc 01 §7.2). **Never** set as an env var on any
      running host.
- [ ] **`tamma_provisioner`** — `CREATEDB`, `CREATEROLE`. **No
      `SUPERUSER`, no `BYPASSRLS`.** Used by
      `CreateTenantWorkflow` + `DeleteTenantWorkflow` (Story 28-5)
      running on the global-Elsa host. Secret at env var
      `TAMMA_PROVISIONER_DB_URL` on that pod only.
- [ ] **`tamma_app`** — `LOGIN`, no `CREATE` privileges, `SELECT /
      INSERT / UPDATE / DELETE` on CP tables only. Used by the
      control-plane API runtime. Secret at env var
      `TAMMA_CONTROL_DB_URL` on the API pod.
- [ ] Per-tenant roles `tamma_tenant_<guid32hex>` are owners of
      their own tenant DB (created by `CreateTenantWorkflow` step 2
      per Story 28-5 AC2). Not in scope for this story except the
      `tamma_provisioner` grant to CREATE them.

### AC2: Cluster-bootstrap role script (not EF)

- [ ] Script at `scripts/postgres-roles.sql` runs at **Postgres
      cluster bootstrap** (Hetzner VPS Docker compose entrypoint
      `apps/tamma-elsa/docker-entrypoint-db.sh` is extended to
      apply it before the API container starts).
- [ ] The script is **idempotent** via `DO $$ BEGIN IF NOT EXISTS
      ... CREATE ROLE ... END IF; END $$;` for every role and grant.
      Re-running the script on an initialised cluster is a no-op
      with zero warnings.
- [ ] Script is kept **out of EF migrations** — EF migrations run
      as `tamma_app` which has no privilege to `CREATE ROLE`.
      Attempting to put role creation in EF migrations would either
      demand elevating the migration runner to `SUPERUSER` (bad) or
      fail at run time (worse). The split keeps EF migrations
      privilege-minimal.
- [ ] A CI job `postgres-roles-lint.yml` runs on every PR touching
      `scripts/postgres-roles.sql` and asserts the script parses
      with `psql --set ON_ERROR_STOP=on --dry-run` on a throwaway
      container.
- [ ] Connection-string configs have distinct slot names so an ops
      typo cannot point the API pod at the provisioner URL:
      `ConnectionStrings:ControlPlane` (reads
      `TAMMA_CONTROL_DB_URL`, user `tamma_app`),
      `ConnectionStrings:Provisioner` (reads
      `TAMMA_PROVISIONER_DB_URL`, user `tamma_provisioner`). Loaded
      in `apps/tamma-elsa/src/Tamma.Api/Program.cs` and the
      global-Elsa `Program.cs` respectively. The API pod asserts at
      startup that it is NOT running as `tamma_provisioner`
      (`SELECT current_user` check) — fails fast if misconfigured.

### Closed by 2026-05-30 follow-up (AC1+AC2 runtime enforcement)

The 2026-05-30 Epic 28 residual-verification report flagged that the
three-role split existed in `scripts/db/postgres-roles.sql` but was
**not enforced at runtime**: `docker-compose.{yml,prod.yml}` did not
slot distinct DB-role URLs, and no `SELECT current_user` startup
assertion caught a regression (an API pod accidentally configured with
the provisioner/admin URL would silently run with escalated
privileges). Closed as follows:

- **Startup least-privilege assertion** —
  `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/DbRoleLeastPrivilegeCheck.cs`
  is an `IHealthCheck` (mirrors the existing `KekCabinetHealthCheck`
  idiom) registered in `Program.cs` on the `"ready"` probe alongside
  the KEK cabinet check. On the `/health/ready` probe it opens the
  app connection (`ConnectionStrings:TammaAppDb`), runs
  `SELECT current_user`, and asserts the result is **not**
  `tamma_provisioner` and **not** `tamma_admin`.
- **Dev-warning / prod-fail gating** — the pure decision core
  (`IsForbiddenAppUser` + `Evaluate`, unit-tested in
  `Epic28/DbRoleLeastPrivilegeCheckTests.cs`) returns `Fail` only when
  `ASPNETCORE_ENVIRONMENT=Production` **and** the role is privileged;
  outside Production a privileged role is `WarnOnly` (logs a WARN,
  reports Healthy). This is required so the full test suite — which
  runs under `UseEnvironment("Development")` on a single default
  Postgres role with no split — stays green. A missing app connection
  string or an unreachable Postgres degrades rather than hard-fails.
- **Compose role-URL slots** — `docker-compose.prod.yml` now carries
  three documented, distinct connection-string slots for the api
  service: `ConnectionStrings__TammaAppDb` (`tamma_app`, the runtime
  least-privilege role), `ConnectionStrings__TammaDb` (admin /
  migration runner, `tamma`→`tamma_admin` via `${TAMMA_ADMIN_DB_USER}`),
  and `ConnectionStrings__Provisioner` (`tamma_provisioner`, used by
  the provisioning workflow on the global-Elsa host — slotted for a
  single-`.env` carry; the API never reads it). All use `${VAR}`
  interpolation with per-role `*_DB_PASSWORD` placeholders consistent
  with the existing `.env` secret pattern — no hardcoded secrets.
  `docker-compose.yml` (dev) mirrors the shape (adds the `Provisioner`
  slot + role-user env overrides) so the dev logs' "TammaAppDb is not
  configured" warning goes away and dev matches prod; dev keeps a
  single underlying role and relies on the WarnOnly gating.

### AC3: `ISecretsService.EncryptTenantConnectionString` / `Decrypt`

- [ ] Interface at
      `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretsService.cs`
      already exists from Story 28-4. This story extends it with
      per-tenant connection-string helpers:

      ```csharp
      byte[] EncryptTenantConnectionString(Guid tenantId,
          string connectionString);
      string DecryptTenantConnectionString(Guid tenantId,
          byte[] ciphertext);
      ```

- [ ] Implementation at
      `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/AesGcmSecretsService.cs`
      (also from Story 28-4; extend, do not rewrite):
  - **The KEK directly encrypts the connection string** — no DEK
    derivation, no HKDF, no per-tenant key material. Per Doc 01
    §8.2: "Derived per-tenant DEKs are not used — the KEK encrypts
    the connection string directly. Acceptable because the
    connection string is the only thing being encrypted and the
    data volume is tiny."
  - Encrypt with AES-256-GCM using the KEK fetched from
    `KekProvider` for the requested slot, 12-byte random nonce per
    operation.
  - Ciphertext envelope format (per Doc 01 §8.1):

        [1 byte version=0x01]
        [1 byte kek_slot]          // 0x01=primary, 0x02=secondary
        [12 bytes nonce]
        [ciphertext N bytes]
        [16 bytes GCM tag]

  - **Version byte `0x01`** is reserved for the direct-KEK scheme.
    Future format changes (e.g. OpenBao/KMS-backed per Story
    28-13, or any DEK layer added later) bump the version byte and
    the decrypt path switches on it. Version-byte routing lives in
    `DecryptTenantConnectionString` so the envelope can evolve
    without a breaking change. Reserving the byte today costs
    nothing.
- [ ] Encrypt path: fetches `KekVersion=1` from primary slot unless
      overridden by the caller (the re-encrypt task in AC5 passes
      `KekVersion=2` to force encryption under the new key).
- [ ] Decrypt path: reads the `kek_slot` byte from the envelope,
      pulls the matching KEK from the secrets service, runs
      AES-GCM. On auth-tag failure, **does NOT reveal** whether the
      slot byte was wrong or the data was tampered — returns
      `TammaError(code="SECRETS_DECRYPT_FAILED")` with no context
      leakage.

### AC4: Primary + secondary KEK slots

- [ ] Two env vars on the API pod + global-Elsa pod:
  - `TAMMA_TENANT_KEK_PRIMARY` — 32-byte base64, required.
  - `TAMMA_TENANT_KEK_SECONDARY` — 32-byte base64, optional
    during steady state, **required during a rotation window**.
- [ ] Both values loaded via
      `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/KekProvider.cs`
      (new) which:
  - Validates each value is exactly 32 bytes after base64 decode.
  - Stores in a `Memory<byte>` region zeroed at process shutdown
    (ASP.NET Core `IHostApplicationLifetime.ApplicationStopping`).
  - Exposes a `GetKek(byte slotByte)` method; returns primary on
    `0x01`, secondary on `0x02`, throws on unknown slots.
- [ ] **Fallback chain** on decrypt: read the envelope's
      `kek_slot` byte, try that key first. On auth-tag failure
      **and only if the envelope says primary (0x01)**, retry with
      secondary. Rationale: this covers the rotation window where
      some rows are still primary-encrypted and some are
      secondary-encrypted; the fallback is time-bounded and logged
      as a WARN metric `tamma_kek_fallback_decrypt_total`.
- [ ] At startup, the app scans `tenants.KekVersion` distribution
      and logs `log.Info("kek.version_distribution", primary=<n>,
      secondary=<n>, other=<n>)`. A non-zero `other` count
      triggers an ERROR — the app refuses to start because the
      envelope-format version is ahead of the code version.

### AC5: `REKEY_TENANT_CONNECTION_STRINGS` scheduled task

- [ ] New scheduled task at
      `apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/RekeyTenantConnectionStringsWorkflow.cs`
      — kicks off manually via `POST /api/admin/secrets/rekey`
      (gated by the `PlatformAdmin` policy from Story 28-9 AC5),
      not on a cron.
- [ ] Preconditions checked at start: both KEK slots populated,
      secondary != primary, at least one `tenants` row has
      `KekVersion=1`. Violating any precondition returns 400 with
      a specific error.
- [ ] Per-tenant re-encrypt loop:
  1. `SELECT Id, EncryptedConnectionString, EncryptedElsaConnectionString,
     KekVersion FROM tenants WHERE KekVersion=1 AND Status IN
     ('active', 'failed') LIMIT 100 FOR UPDATE SKIP LOCKED`.
  2. For each row: decrypt with primary (slot 0x01 per envelope),
     re-encrypt under secondary (slot 0x02) — same direct-KEK
     AES-256-GCM, fresh random nonce, write back
     `EncryptedConnectionString` + `EncryptedElsaConnectionString`
     + `KekVersion=2`.
  3. Commit the CP transaction. Loop until zero rows remain.
- [ ] Idempotency: a failure mid-loop leaves some rows at
      `KekVersion=1` and some at `KekVersion=2` — restarting the
      workflow picks up the remaining `KekVersion=1` rows. No
      double-encryption hazard because the envelope's `kek_slot`
      byte authoritatively says which key was used.
- [ ] Live-request safety: during the re-encrypt window, every
      decrypt uses the envelope's `kek_slot` byte → no live request
      fails. The resolver's pool cache (Story 28-4) keeps warm
      connections going; cold-miss decrypts still succeed because
      the envelope self-describes.
- [ ] Progress metric `tamma_kek_rotation_remaining_gauge` reports
      the count of `tenants WHERE KekVersion=1`. Admin UI (Story
      28-11 as a follow-up panel or Grafana) monitors it.
- [ ] After the loop completes, the **operator manually**:
  - Promotes secondary → primary: `TAMMA_TENANT_KEK_PRIMARY`
    takes the old secondary value.
  - Removes secondary (or populates with a fresh key for the next
    rotation).
  - Resets `KekVersion=1` for every row via a second workflow
    run under the new key pair (i.e., the next rotation starts
    fresh). See the runbook in AC6 for step-by-step.

### AC6: Runbook `.dev/runbooks/kek-rotation.md`

- [ ] New runbook at `.dev/runbooks/kek-rotation.md` documenting
      the 90-minute rotation window, step by step:
  1. **Pre-flight** — generate new KEK via `openssl rand -base64
     32`, update the sealed secret, deploy as
     `TAMMA_TENANT_KEK_SECONDARY`. Roll pods.
  2. **Invoke rekey** — `POST /api/admin/secrets/rekey`. Watch
     `tamma_kek_rotation_remaining_gauge`.
  3. **Verify** — `tamma_kek_fallback_decrypt_total` must be
     non-zero during the window (proves the fallback works) and
     **zero after** (proves no rows are stuck on the old key).
     Run a consistency check: `SELECT COUNT(*) FROM tenants
     WHERE KekVersion=1 AND Status NOT IN ('deleted',
     'pending_verification')` must return 0.
  4. **Promote** — swap the env vars (secondary becomes primary,
     primary becomes secondary or is cleared). Roll pods one at
     a time for zero-downtime.
  5. **Reset** — second rekey run to set all rows back to
     `KekVersion=1` under the now-promoted key (so the next
     rotation starts from the same invariant).
  6. **Rollback** — if the workflow fails catastrophically
     mid-rotation, the secondary key stays populated; live
     traffic continues via the self-describing envelope. Operator
     investigates, fixes, re-runs.
- [ ] Runbook is linked from `.dev/README.md` under the "security
      runbooks" section.
- [ ] Runbook references the specific Grafana dashboards to watch
      (`tamma_kek_*` metrics) and the on-call rotation to notify
      before starting.

### AC7: CI secret separation + gitleaks gate

- [ ] CI secrets (GitHub Actions secrets) for the test suite use
      **completely distinct** KEK values from production. The test
      values are hard-coded in `.github/workflows/test.yml` as
      `TAMMA_TENANT_KEK_PRIMARY_TEST` / `..._SECONDARY_TEST` and
      are derived from a `test-only` string — these can be committed
      because they only encrypt test fixtures.
- [ ] Gitleaks runs on every PR with a custom rule detecting the
      pattern `TAMMA_TENANT_KEK` in any file not matching
      `**/*.test.{ts,cs}` or `.github/workflows/test.yml`.
      Violations fail the PR check.
- [ ] A runtime startup check compares the primary KEK against a
      list of known-bad constants (all-zero, all-one, the
      test-only value) and **refuses to start** if a match is
      found. Returns exit code 78 with the message "refusing to
      start with default or test KEK in production environment" if
      `ASPNETCORE_ENVIRONMENT=Production`.

### AC8: Negative integration tests

Per Epic 28 success-metric #4 ("12 cross-tenant leak scenarios"),
this story ships these specific negative tests:

- [ ] **T_DROP_TABLE**: connect as `tamma_app`, attempt `DROP TABLE
      tenants` → Postgres returns `42501: permission denied`.
      Assert the error class, not the specific message.
- [ ] **T_CREATE_DATABASE**: connect as `tamma_app`, attempt
      `CREATE DATABASE tamma_tenant_test` → `42501`.
- [ ] **T_CREATE_ROLE**: connect as `tamma_app`, attempt `CREATE
      ROLE evil` → `42501`.
- [ ] **T_BYPASSRLS**: connect as `tamma_app` + `tamma_provisioner`,
      attempt `SET SESSION AUTHORIZATION` to another role → both
      fail (only `tamma_admin` can).
- [ ] **T_MAX_CONNECTIONS**: assert `SHOW max_connections` is at
      least `pool_max_size × max_cached_tenants + 100` (per Doc 01
      §9.2 arithmetic — the 10k connection ceiling at 500 warm
      pools × 20 conns each requires `max_connections ≥ 10100`).
      If not, startup health check fails and ops is paged.
- [ ] **T_WRONG_KEK_DECRYPT**: encrypt with primary, attempt
      decrypt with secondary only (simulate primary loss by
      clearing the slot) → returns `SECRETS_DECRYPT_FAILED` with
      no sensitive context in the error payload.
- [ ] **T_ENVELOPE_VERSION_MISMATCH**: craft an envelope with
      version byte `0x99` → decrypt rejects it at the version
      check before attempting AES-GCM.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §7 (three
    roles + rotation) and §8 (connection-string encryption
    envelope format) — source of truth for the role matrix,
    envelope format, and rotation flow.
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §4
    (encryption Approach A — stored ciphertext with KEK from env;
    this story implements Approach A with the two-slot overlap),
    §4.2 (recommendation table: Phase 1 uses Approach A, Phase 2
    migrates to KMS behind the `IConnectionStringDecryptor`
    interface — version byte `0x01` is this story, future KMS
    work is `0x02`).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §5.1
    (naming scheme — role names `tamma_tenant_<guid32hex>` fit
    the 63-byte Postgres identifier limit by construction).
- **File layout**:
  - `scripts/postgres-roles.sql` — new; idempotent role creation.
  - `apps/tamma-elsa/docker-entrypoint-db.sh` — new or modified;
    runs `psql -f postgres-roles.sql` before releasing the
    API container.
  - `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretsService.cs`
    — modified; add tenant-string methods.
  - `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/AesGcmSecretsService.cs`
    — modified; envelope format (version/slot/nonce/ct/tag) +
    direct-KEK AES-256-GCM encrypt/decrypt.
  - `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/KekProvider.cs`
    — new; two-slot KEK loader + zeroing.
  - `apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/RekeyTenantConnectionStringsWorkflow.cs`
    — new; re-encrypt loop.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/SecretsEndpoints.cs`
    — new; `POST /api/admin/secrets/rekey` handler.
  - `.dev/runbooks/kek-rotation.md` — new; 90-min runbook.
  - `.github/workflows/gitleaks.yml` — modified; custom KEK rule.
- **Interaction with Story 28-4**: `ITenantConnectionResolver`
  already calls `ISecretsService.DecryptTenantConnectionString` on
  pool-cache miss (Story 28-4 AC3). This story changes the
  encryption scheme but the interface is unchanged, so 28-4 needs
  **no code changes** — the envelope-version byte is internal to
  the secrets service.
- **Interaction with Story 28-5**: `CreateTenantWorkflow` step 8
  (`EncryptConnectionStringActivity` per 28-5 AC2) uses
  `ISecretsService.EncryptTenantConnectionString`. This story
  makes that activity work under the two-slot envelope format —
  no signature change.

## Dependencies

- **Blocks**: none in Epic 28 — this story is the last of Phase 2
  per `00-sequencing.md` and unlocks the Stream C stories in
  Phase 3 only indirectly (they don't depend on rotation
  capability, just on the envelope format being in place from
  Story 28-4).
- **Blocked by**: 28-1 (`tenants.EncryptedConnectionString` +
  `tenants.EncryptedElsaConnectionString` + `tenants.KekVersion`
  columns must exist in the CP schema), 28-4 (`ISecretsService`
  interface + `AesGcmSecretsService` initial scaffold).
- **External**: Hetzner sealed secrets (or dev-env `.env` files),
  gitleaks action (existing CI surface).

## Test Plan

### Unit tests

- `KekProviderTests`:
  - Valid base64 → loaded successfully.
  - Invalid length (31 bytes) → startup fails with specific error.
  - Secondary absent → `GetKek(0x02)` throws (caller handles
    missing secondary in the fallback path).
  - Process shutdown → memory zeroed (verified via
    `byte[]` reflection inspection immediately after
    `ApplicationStopping`).
- `AesGcmSecretsServiceTests`:
  - Encrypt → decrypt round-trip for 100 different tenant ids
    → asserts ciphertexts differ per encryption (distinct random
    nonces produce distinct ciphertexts even for the same
    plaintext + tenant).
  - Tamper with one byte of the ciphertext → decrypt fails at
    the GCM auth tag.
  - Decrypt envelope with `kek_slot=0x02` when secondary is
    populated → succeeds.
  - Decrypt envelope with `kek_slot=0x99` → fails at the slot
    check.
  - Decrypt envelope with `version=0x99` → fails at the version
    check.
- `RekeyTenantConnectionStringsWorkflowTests`:
  - Seed 50 `KekVersion=1` tenants, run workflow, verify all 50
    flipped to `KekVersion=2` with secondary-encrypted envelopes.
  - Mid-loop failure (inject at row 30) → 30 rows flipped, 20
    remain; re-run completes the remaining 20.
  - Precondition check: secondary missing → 400 error.

### Integration tests (Testcontainers.PostgreSQL)

- **T1 Role-separation negative tests** (AC8 full suite): run
  each negative test against a bootstrapped cluster, assert the
  expected Postgres error class per test.
- **T2 End-to-end encrypt → store → decrypt**: encrypt a
  connection string via `AesGcmSecretsService`, store in
  `tenants.EncryptedConnectionString`, read back via the
  resolver (Story 28-4), assert the decrypted string matches
  byte-for-byte.
- **T3 Rotation-window live traffic**: spin up a test server with
  both KEKs populated, seed 10 tenants at `KekVersion=1`,
  concurrently (a) run the rekey workflow and (b) fire 100
  tenant-scoped requests per second → zero request failures.
- **T4 Primary rollback**: corrupt the primary KEK env var at
  runtime (simulated via DI reconfiguration), assert that
  tenants at `KekVersion=1` fail decrypt **cleanly** with no
  data leak in the error message.
- **T5 CI-prod KEK separation**: assert the test suite refuses
  to run with an env var matching the production KEK's known
  shape (added as a guard in the test `conftest` equivalent).
- **T6 Envelope version forward-compat**: a version-byte `0x02`
  envelope returns `UNSUPPORTED_ENVELOPE_VERSION` instead of
  `DECRYPT_FAILED` (so future KMS migration has a clean signal).

### Manual verification

- Local dev: bootstrap the cluster with `docker compose up`,
  confirm all three roles exist via `\du` in `psql`. Attempt
  `CREATE DATABASE foo` as each role; only `tamma_admin` and
  `tamma_provisioner` succeed.
- Generate a new secondary KEK, deploy, invoke rekey via
  `/api/admin/secrets/rekey`, watch the gauge drop to zero,
  verify ops has observed `fallback_decrypt_total` climb then
  settle.

## Definition of Done

- [ ] AC all green
- [ ] Unit + integration tests added, suite passes
- [ ] Negative suite in T1 passes against a real bootstrapped
      Postgres 17 cluster
- [ ] Runbook `.dev/runbooks/kek-rotation.md` reviewed by a
      security engineer + operations engineer
- [ ] No new CodeQL alerts (audit every `byte[]` allocation
      around KEKs — must not survive past the scope of one call)
- [ ] Gitleaks CI gate passes with the new custom rule
- [ ] Design-doc references updated if the impl deviated
      (expected: confirm Doc 01 §8.1 envelope format + §8.2
      direct-KEK-encrypt match the implemented bytes exactly —
      no DEK layer, no HKDF)
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Direct-KEK design is intentional.** An earlier draft of this
  story added HKDF-derived per-tenant DEKs. That was reverted on
  2026-04-17 to match Doc 01 §8.2 exactly: the KEK encrypts the
  connection string directly. Rationale: no KMS on the roadmap
  today, so the DEK layer's main benefit (clean KMS-migration
  seam) doesn't justify its complexity cost. If/when Tamma adopts
  a KMS, **Story 28-13** (OpenBao backend, deferred) swaps the
  backend via the `ISecretsService` seam — envelope version byte
  `0x01` is reserved for today's direct-KEK scheme and a future
  `0x02` can introduce a DEK layer or Transit-wrapped ciphertext
  without a breaking migration.
- **`max_connections` threshold is ops-sensitive.** T_MAX_CONNECTIONS
  in AC8 enforces `max_connections ≥ 10100` at startup. On a dev
  laptop with a shared Postgres 17 default of 100, this would
  fail. The check is gated by `ASPNETCORE_ENVIRONMENT=Production
  || Staging` so dev runs unaffected; production Postgres config
  must raise it. Ops runbook updated.
- **Rotation assumes a single API pod at a time has the
  secondary KEK.** In a multi-pod deploy the rolling restart has
  a window where pod A has only primary and pod B has both. This
  is fine because: (a) A can still decrypt `KekVersion=1` rows,
  (b) A cannot decrypt `KekVersion=2` rows — which don't exist
  yet at rollout time, (c) after the rekey workflow runs (on the
  global-Elsa host which is on the new config), every row is
  `KekVersion=2` and pod A suddenly can't decrypt. Therefore the
  runbook step order is: deploy secondary to ALL pods, THEN run
  the rekey, THEN promote. AC6's runbook makes this ordering
  explicit.

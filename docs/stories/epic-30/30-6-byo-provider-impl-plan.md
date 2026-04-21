# Story 30-6 Implementation Plan — BYO Provider

**Status**: Planned (2026-04-20)
**Story brief**: [`30-6-byo-provider.md`](./30-6-byo-provider.md)
**Epic 30 phase**: Provider drivers — parallel with 30-4, 30-5.
**Branch**: `feat/story-30-6-byo-provider`

---

## 1. Objective

Ship `BringYourOwnTenantProvider` for compliance-heavy enterprise
tenants who operate their own Postgres + engine. Tamma validates
connectivity + schema parity + engine health, stores endpoints,
routes traffic — but never creates or deletes customer infrastructure.
Adds `ManagedSecretRotationHandler` for the rotation workflow so
customer-owned secrets can still be audited.

## 2. Dependencies

Hard blockers:

- **Story 30-1** — v2 interface + `Managed` topology.
- **Story 30-2** — dispatch workflow.
- **Story 29-6** — rotation handler registry.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Byo/BringYourOwnTenantProvider.cs` | v2 provider. |
| `.../Provisioning/Byo/ByoValidationHarness.cs` | AC2 validation checks. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/ManagedSecretRotationHandler.cs` | No-op push, real probe. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/Byo/ByoCertificationTests.cs` | Smoke workflow against real Postgres + engine container pair. |
| `/home/meywd/tamma/docs/runbooks/byo-tenant-offboarding.md` | Non-destructive offboarding runbook. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Keyed singleton `"byo"` + handler `"managed"`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | Register handler. |

## 5. Sequence of changes

### Step 1 — Validation harness (4h)

- `ValidateDbAsync(connUrl, allowPlaintext=false)`:
  - Open connection; assert version ≥ 15; assert permissions.
  - Refuse if `sslmode=disable` unless `--allow-plaintext-db` flag set.
- `ValidateEngineAsync(engineUrl)`:
  - `GET /health` → 200 with expected shape.
- `ApplyMigrationsAsync(connUrl)`:
  - Run `DbContext.Database.Migrate()` against tenant DB.
  - Fails with clear error listing missing extensions if any.
- Unit tests: invalid URL, wrong version, missing permission, migration failure.
- **Commit**: `feat(byo): validation harness`.

### Step 2 — Provider implementation (4h)

- `ProvisionAsync`:
  - Validate DB + engine.
  - Apply migrations.
  - Write endpoints to `provider_resource_ids`:
    `{ byo_db_url_ref: "tenant:db/byo-connection", byo_engine_url: "..." }`.
  - Cabinet row created by 30-2's `RegisterSecretsActivity` with
    user-supplied DB URL.
- `DeprovisionAsync`:
  - **Does not touch external infra.**
  - Purge cabinet rows + clear routing.
  - Emits `TENANT.BYO.OFFBOARDED` with summary.
- `ResolveEndpointsAsync`:
  - Cabinet read for current DB URL + stored engine URL.
- `GetStatusAsync`: DB `SELECT 1` + engine `/health`.
- **Commit**: `feat(byo): provider implementation`.

### Step 3 — Capability declaration (1h)

- `SupportedTopologies = Managed` only.
- `Features = DedicatedDb` (customer's); others false.
- `CostUnitsPerMonth = 0` (customer pays their own infra).
- **Commit**: `feat(byo): capabilities`.

### Step 4 — ManagedSecretRotationHandler (3h)

- `PushAsync` — no-op (customer already rotated externally).
- `ProbeAsync` — opens fresh connection with new value from cabinet.
- `RollbackAsync` — reverts cabinet version only; no external action.
- Unit tests.
- **Commit**: `feat(secrets): managed rotation handler`.

### Step 5 — PII-safe logging (1h)

- Customer URL hashed for audit (hostname only visible, rest hashed).
- `TENANT.BYO.VALIDATION.<OUTCOME>` event emits hash not URL.
- **Commit**: `feat(byo): PII-safe URL logging`.

### Step 6 — Certification suite (4h)

- Fixture: spin up a real Postgres + tamma-engine container pair
  via Testcontainers.
- Register via BYO provider.
- Run a smoke workflow (agent dispatch, one LLM call, event
  emission).
- Assert identical outcomes to the Cranl test.
- **Commit**: `test(byo): certification suite`.

### Step 7 — Runbook (1h)

- `byo-tenant-offboarding.md`: explicit note that customer data is
  retained; Tamma clears routing only.
- **Commit**: `docs(runbooks): byo tenant offboarding`.

## 6. Test strategy

### Unit

- Validation harness: each check independently.
- Rotation handler: no-op push; probe uses new value.

### Integration

- Certification suite — real Postgres + engine.

### Security

- Customer URL never appears verbatim in logs.

## 7. Rollback plan

- **Feature flag**: `Providers:Byo:Enabled`.
- **Non-destructive**: BYO never deletes customer infra; rollback
  affects only platform state.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Validation harness | 4 |
| 2. Provider | 4 |
| 3. Capabilities | 1 |
| 4. Rotation handler | 3 |
| 5. PII logging | 1 |
| 6. Certification suite | 4 |
| 7. Runbook | 1 |
| **Total** | **18** (matches brief). |

## 9. Open questions

- **Migration failure recovery**: if tenant's DB lacks an
  extension, validation fails. Does Tamma retry after the customer
  installs it? Plan: customer re-runs the onboarding form.
- **Engine health-check shape**: `{ engine: "tamma-engine", ... }`.
  Ships contract in the runbook so customers know what to expose.
- **Cabinet row creation sequencing**: `RegisterSecretsActivity`
  (30-2) creates the row with user-provided initial value. For BYO,
  that value comes from the onboarding form.
- **Customer's DB credentials**: stored in the cabinet like any
  other tenant secret. Accessible via the `tk_u_` user key of the
  customer's admin.
- **Compliance claims**: BYO does not imply SOC 2 / ISO 27001 on its
  own; Tamma can make those claims only for the platform surface it
  operates. Documented in the enterprise sales materials, not
  promised by this story.

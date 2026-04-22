# Story 29-9 Implementation Plan — Migrate Stopgap Secrets Into the Cabinet

**Status**: Planned (2026-04-20)
**Story brief**: [`29-9-migrate-stopgap-secrets.md`](./29-9-migrate-stopgap-secrets.md)
**Epic 29 phase**: Migration — after 29-1..29-7.
**Branch**: `feat/story-29-9-migrate-stopgap-secrets`

---

## 1. Objective

One-shot command that imports every secret currently stored in env
vars / config / DB columns / `changeme` literals into the Epic 29
cabinet. Idempotent. Auto-rotates known-bad values (`changeme`) via
29-7 during import so `tamma_app` never exposes the literal outside
the migration's own flow. Introduces `IRuntimeSecretResolver` as the
new runtime read path with env-var fallback during the grace window.

## 2. Dependencies

Hard blockers:

- **Stories 29-1 through 29-7** — the cabinet, rotation workflow,
  and Postgres rotation handler.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Commands/MigrateSecretsCommand.cs` | One-shot `dotnet run -- migrate-secrets`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/RuntimeSecretResolver.cs` | Runtime read-through with env-var fallback. |
| `.../Services/Secrets/ISecretChangeListener.cs` | Event subscription for `SECRET.ROTATE.ACTIVATED`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/Secrets/MigrateSecretsCommandTests.cs` | Testcontainers E2E. |
| `/home/meywd/tamma/docs/runbooks/migrate-secrets.md` | Step-by-step rollout. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register `IRuntimeSecretResolver` + subscribe to rotation events. |
| All consumers of `ConnectionStrings:TammaAppDb`, `TAMMA_SHARED_SECRET`, `Cranl:ApiKey`, `GitHub:WebhookSecret` | Inject `IRuntimeSecretResolver` instead of reading config directly. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlTenantProvisioner.cs` | Stop reading `Cranl:EncryptionKey`; warn + ignore. |

## 5. Sequence of changes

### Step 1 — RuntimeSecretResolver (3h)

- `GetAsync(name)`:
  1. Try `ISecretStore.GetAsync`.
  2. If present, fetch latest version plaintext via handler path.
  3. Cache for 60s.
  4. On cache miss → try env-var fallback (with deprecation warn log).
- `OnRotated` event subscription refreshes cache + fires local event.
- Unit tests: fallback warn, cache refresh, event propagation.
- **Commit**: `feat(secrets): runtime resolver with env fallback`.

### Step 2 — Migrate command (5h)

- `MigrateSecretsCommand.ExecuteAsync`:
  1. Read env / config sources.
  2. For each source row:
     - If existing cabinet row `(scope, tenantId?, name)` → skip.
     - Else `ISecretStore.CreateAsync(metadata, initialValue)`.
     - Emit `SECRET.IMPORTED` with source + previousLocation.
  3. For tenants with `cranl_database_url_encrypted`: import per
     row.
  4. After import: if value was `changeme` or other known-bad,
     immediately dispatch `RotateSecretWorkflow` via 29-7; wait for
     activation; update runtime resolver cache.
- Idempotent: `ON CONFLICT DO NOTHING` on name-unique.
- Rollback: reverse order delete on partial failure.
- **Commit**: `feat(migrate): migrate-secrets command`.

### Step 3 — Consumer wiring (4h)

- Replace direct env/config reads with `IRuntimeSecretResolver.GetAsync`.
- Audit pass via grep: `IConfiguration["ConnectionStrings:TammaAppDb"]`, etc.
- Each replaced call gets a unit test.
- **Commit**: `refactor(secrets): consumers use RuntimeSecretResolver`.

### Step 4 — Rotation event subscription (2h)

- `ISecretChangeListener` subscribes to `SECRET.ROTATE.ACTIVATED`
  via RabbitMQ or in-process event bus (reuse 28-6 dispatcher's
  pub/sub).
- On event: refresh cache for affected name; emit local `OnRotated` event.
- Pool drainer (29-7) subscribes to `OnRotated` for postgres secrets.
- **Commit**: `feat(secrets): rotation event subscription`.

### Step 5 — `Cranl:EncryptionKey` ignore (1h)

- `CranlTenantProvisioner` logs warning and does not read the config.
- All Cranl-encrypted data reads route through `ISecretStore`.
- **Commit**: `fix(cranl): ignore deprecated Cranl:EncryptionKey`.

### Step 6 — Integration tests (3h)

- Seed Postgres with pre-migration schema + `changeme` role +
  sample tenants.
- Run command.
- Assert cabinet populated, `changeme` rotated, app connects.
- Run command twice — assert idempotent.
- **Commit**: `test(migrate): migrate-secrets E2E`.

### Step 7 — Runbook (2h)

- `migrate-secrets.md` with backup → deploy → run → verify →
  remove-fallback sequence.
- **Commit**: `docs(runbooks): migrate-secrets rollout`.

## 6. Test strategy

### Unit

- `RuntimeSecretResolver` cache + fallback.
- Command idempotency (run twice, assert no duplicates).

### Integration

- Full migration against seeded Postgres; assert row counts + hash
  rotation happened.
- Partial-failure: inject error after N imports; assert rollback.

### Security

- Post-migration grep: `changeme` nowhere in runtime config or DB.

## 7. Rollback plan

- **Partial-failure rollback**: command deletes imported rows on
  fatal error. Env-var fallback keeps the app alive.
- **Full rollback**: run delete-imported-rows query (documented in
  runbook); app falls back to env.
- **Non-reversible**: `changeme` password rotation is destructive —
  once rotated, the original is unrecoverable. Acceptable because
  `changeme` is a known-public value by definition.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. RuntimeSecretResolver | 3 |
| 2. Migrate command | 5 |
| 3. Consumer wiring | 4 |
| 4. Rotation event subscription | 2 |
| 5. Cranl key ignore | 1 |
| 6. Integration tests | 3 |
| 7. Runbook | 2 |
| **Total** | **20** (matches brief). |

## 9. Open questions

- **Migrate command deploy location**: CLI in the API container?
  Separate one-shot Docker image? Plan: CLI subcommand on the
  existing API binary (no extra image).
- **Coexistence window duration**: one release cycle (per brief).
  Revisit if any tenant needs longer.
- **Tenant-scoped migration ordering**: all platform secrets first,
  then per-tenant. Parallelism=1 for tenant loop to avoid
  rate-limit pile-up.
- **Auto-rotate side-effects**: 29-7 handler alters a live role.
  Timing: run migration during maintenance window or after blue
  deploy. Runbook specifies.
- **Event-bus coupling**: RabbitMQ for cross-process events;
  in-process event bus for single-process. Choose at wiring time.

# Story 29-8 Implementation Plan — Cranl Env-Var Rotation

**Status**: Planned (2026-04-20)
**Story brief**: [`29-8-cranl-env-rotation.md`](./29-8-cranl-env-rotation.md)
**Epic 29 phase**: Handlers — after 29-6.
**Branch**: `feat/story-29-8-cranl-env-rotation`

---

## 1. Objective

Ship `CranlEnvVarRotationHandler` plugging into 29-6's workflow.
Pushes a new env-var value into a tenant's Cranl app, reloads (or
redeploys), probes the engine's `/health` returns 200, rolls back on
probe failure. Wires one-click rotation of `TAMMA_SHARED_SECRET` and
per-tenant Cranl API keys through the admin UI.

## 2. Dependencies

Hard blockers:

- **Story 29-6** — workflow contract.

Soft:

- Cranl API client in `Services/Provisioning/Cranl/` — extend if
  reload/deploy endpoints absent.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/CranlEnvVarRotationHandler.cs` | Handler impl. |
| `.../Services/Secrets/Handlers/CranlEnvText.cs` | Env-text parse/modify/serialize helper. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Secrets/CranlEnvTextTests.cs` | Parse + modify edge cases. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/Secrets/CranlEnvRotationTests.cs` | Fake Cranl client end-to-end. |
| `/home/meywd/tamma/docs/vendors/cranl/rotation-integration.md` | Integration notes. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/CranlApiClient.cs` | Ensure `GetEnvironmentAsync`, `PutEnvironmentAsync`, `ReloadAsync`, `DeployAsync`, `GetAppStatusAsync` methods exist. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | Register keyed handler. |

## 5. Sequence of changes

### Step 1 — CranlEnvText helper (3h)

- `Parse(text)` → `IDictionary<string, string>` preserving order.
- `Merge(current, updates)` → new text with order preserved.
- `DiffKeys(current, next)` → `+/~/-` key list for logging.
- Unit tests: edge cases (trailing newline, empty value, `=` in value).
- **Commit**: `feat(secrets): cranl env text helper`.

### Step 2 — Cranl client surface audit (2h)

- Inspect existing `CranlApiClient`; add missing methods
  (reload, app status) with retry-on-429.
- **Commit**: `feat(cranl): rotation-facing API methods`.

### Step 3 — Handler PushAsync (3h)

- Fetch current env → merge new value → PUT env.
- Trigger reload (default) or redeploy (per secret's
  `RotationOptions.CranlMode`).
- 3× retry on 5xx with 10s/30s/90s backoff.
- **Commit**: `feat(secrets): cranl push + reload`.

### Step 4 — ProbeAsync (3h)

- Poll `GetAppStatusAsync` until `running` or 5-min timeout.
- Call `GET <tenant.CranlAppUrl>/health`; expect 200.
- Return `Healthy`/`Unhealthy(reason)`.
- **Commit**: `feat(secrets): cranl probe`.

### Step 5 — RollbackAsync (2h)

- Fetch previous version plaintext.
- PUT env with previous value; reload.
- Emit `SECRET.ROTATE.ROLLBACK.CRANL_ENV` event.
- **Commit**: `feat(secrets): cranl rollback`.

### Step 6 — PII-safe logging (1h)

- Env text never logged — only diff of key names via
  `CranlEnvText.DiffKeys`.
- Unit test: log output scan asserts no values leak.
- **Commit**: `feat(secrets): PII-safe env logging`.

### Step 7 — Integration tests (2h)

- Fake Cranl client; full flow; retry behaviour.
- Rollback E2E.
- **Commit**: `test(secrets): cranl rotation E2E`.

## 6. Test strategy

### Unit

- `CranlEnvText` edge cases.
- Handler with mocked `ICranlApiClient` (push, probe, rollback).
- Rate-limit handling via fake 429.

### Integration

- Tests use a wiremock-style fake Cranl client.
- Probe-retry: 503 twice, then `running` → handler activates.
- Rollback verifies PUT contains previous value.

### Security

- Log grep: no env values or Cranl keys leaked.

## 7. Rollback plan

- **Handler disable**: remove keyed registration.
- **Compensation**: 29-6 invokes `RollbackAsync` on probe fail.
- **Non-reversible**: Cranl deploys are slow to undo; redeploy mode
  incurs a second deploy on rollback.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Env text helper | 3 |
| 2. Cranl client surface | 2 |
| 3. Push + reload | 3 |
| 4. Probe | 3 |
| 5. Rollback | 2 |
| 6. Logging | 1 |
| 7. Integration | 2 |
| **Total** | **16** (matches brief). |

## 9. Open questions

- **Cranl rate limit**: 60 req/min per account (per research).
  With 29-6's 3× push retry + probe polls every 15s, a single
  rotation consumes ~10 requests. 6 parallel rotations hit the limit
  — worker concurrency=4 keeps us safe. Documented in runbook.
- **Reload vs. redeploy default**: reload (faster). Redeploy for
  build-time env vars; secret metadata flag.
- **Per-tenant Cranl API key** (future): `RotationContext.CranlApiKey`
  is passed in explicitly, already generalised.
- **Probe's `/health` endpoint**: tenant's engine — may not exist on
  all tenants. Plan: fall back to app-status-only if `/health` 404s.
- **Cranl deploy timeouts**: 5-min probe timeout matches Cranl's
  typical redeploy duration. Configurable per-secret.

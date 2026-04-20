# Story 29-8: Cranl Env-Var Rotation Workflow

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform administrator**,
I want a `CranlEnvVarRotationHandler` that plugs into the generic rotation workflow from Story 29-6 and pushes a new env-var value into a tenant's Cranl application, redeploys (or reloads, where supported), and probes that the tenant engine came back healthy,
so that rotating `TAMMA_SHARED_SECRET`, a tenant's `Cranl:ApiKey` (when we split it per tenant), or the tenant's `DATABASE_URL` is a one-click flow from the admin UI with full saga-shaped rollback on probe failure.

## Acceptance Criteria

1. `CranlEnvVarRotationHandler : IRotationHandler` registered with `System = "cranl"`. Resolved when a secret's first `ConsumerRef` is `{ system: "cranl", identifier: "app=<appId>;env=<VAR_NAME>" }`.
2. `PushAsync`:
   - Fetches current env via `GET /api/applications/:id/environment` (returns `{ env: "K=V\n..." }`).
   - Replaces exactly one line matching `^<VAR_NAME>=`; adds a new line if absent; preserves all other vars (Cranl `PUT /environment` replaces the entire set, so we reconstruct).
   - Calls `PUT /api/applications/:id/environment` with the new env text.
   - Triggers `POST /api/applications/:id/lifecycle { action: "reload" }` (falls back to `deploy` if the handler options specify `redeploy-on-rotate = true` for the secret).
3. `ProbeAsync`:
   - Polls the app until its Cranl status returns to `running` (5-minute timeout).
   - Then opens a short-lived HTTP call to the tenant's `cranl_app_url` `/health` endpoint (from `Tenant.CranlAppUrl`) and checks for 200.
   - Returns `ProbeResult.Healthy` if both steps pass; `Unhealthy(reason)` otherwise.
4. `RollbackAsync`:
   - Reconstructs the env text with the **previous** value (from the secret store's previous Active version).
   - `PUT` the env.
   - `POST lifecycle { action: "reload" }`.
   - Does not probe on rollback — the activity that triggered rollback already knows something is broken; rely on the workflow-level compensation event for escalation.
5. Handler respects Cranl rate limit (120 req/min per API key) — uses the existing `ICranlApiClient` infra (if present) or implements a token-bucket on the handler. Emits `CRANL.RATE_LIMIT.HIT` on 429.
6. The handler must **not** send the entire env text through any log line. A helper serializer strips values and logs only the env key names diff (`+ TAMMA_SHARED_SECRET`, `~ TAMMA_SHARED_SECRET`, `- OLD_VAR`). Unit-tested.
7. Cranl 5xx handling: retry push 3× with exponential backoff (10s, 30s, 90s — Cranl's operations can be slow); on persistent failure, return `PushFailed` which drives compensation per 29-6.
8. Integration test uses a fake `ICranlApiClient` (wiremock-style) to simulate full flow: push succeeds, reload succeeds, probe succeeds → activate. Then a second test: push succeeds, reload succeeds, probe returns 503 twice then `running` → probe activity retries twice, activates on third attempt.
9. Integration test for rollback: push succeeds, reload returns 200, probe fails 3× → workflow invokes `RollbackAsync`; assert the env text posted on rollback contains the previous value; assert reload called again; assert secret store's new version is in `Revoked` state.
10. Handler emits `CRANL.ENV.PUSHED` and `CRANL.ENV.RELOAD.TRIGGERED` events into `platform_events` (platform-scoped) or the tenant's `domain_events` (tenant-scoped) so operators can see the rotation's timing in the same feed as their other platform activity.

## Technical Context

### Why reload vs redeploy

Cranl's `lifecycle { action: "reload" }` restarts the running app
without a full deploy (faster, no image rebuild). But some env vars
are baked into build-time (e.g. ones consumed by a nixpacks build
step); for those the handler's secret metadata sets
`RotationOptions.CranlMode = "redeploy"` and triggers
`POST /api/applications/:id/deploy` instead.

Default is `reload` because most Tamma env vars are consumed at
runtime.

### Cranl env text format

Cranl stores env as a single string `K=V\n...`. We parse it into a
dictionary, replace/insert the target key, re-serialize preserving
original order for unchanged keys. A helper `CranlEnvText` handles
this; tested with edge cases (values containing `=`, trailing newline,
empty file).

### Per-tenant Cranl API key future

Today Tamma uses one platform-wide `Cranl:ApiKey`. When Epic 30
introduces BYO tenant Cranl accounts, each tenant will have its own
Cranl API key (stored as a tenant-scoped secret). This handler is
written with `RotationContext.CranlApiKey` passed explicitly rather
than resolved from static config so the per-tenant switch is a
wiring change only.

## Estimated hours

16 — handler + env text helper + probe + rollback + integration tests
(fake Cranl client) + event emission.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/CranlEnvVarRotationHandler.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/CranlEnvText.cs` (new helper)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cranl/*` (may add `ReloadAsync` / `DeployAsync` to the existing client if absent)

## References

- Story 29-6 workflow primitive
- Cranl API reference: `docs/vendors/cranl/README.md`
- Research notes §3

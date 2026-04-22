# Story 30-5 Implementation Plan — Cloudflare Provider (D1 + Workers + KV)

**Status**: Planned (2026-04-20)
**Story brief**: [`30-5-cloudflare-provider.md`](./30-5-cloudflare-provider.md)
**Epic 30 phase**: Provider drivers — parallel with 30-4.
**Branch**: `feat/story-30-5-cloudflare-provider`

---

## 1. Objective

Ship `CloudflareTenantProvider` that creates a D1 database + a
dispatch-namespace Worker + a KV namespace per tenant via Cloudflare
for Platforms API. Enables the "edge tier" — cheapest-per-tenant,
globally distributed, sub-second provisioning. Research confirms:
**50,000 D1 databases per account on Workers Paid**, **10 GB
per-database hard cap**, and the **Upload Worker Module endpoint is
`PUT /accounts/{aid}/workers/dispatch/namespaces/{ns}/scripts/{name}`**
(first-time uploads are now synchronous with 200 OK = ready).

## 2. Dependencies

Hard blockers:

- **Story 30-1** — v2 interface.
- **Story 30-2** — dispatch workflow.
- **Story 29-6** — rotation workflow (for Worker secrets handler).
- Cloudflare API token with "Workers for Platforms:Edit",
  "D1:Edit", "KV:Edit", "DNS:Edit" scopes.
- Pre-built `packages/engine-worker/dist/engine-worker.js` bundle
  (bundle production is a separate post-Epic-30 story).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cloudflare/CloudflareTenantProvider.cs` | v2 provider. |
| `.../Provisioning/Cloudflare/CloudflareApiClient.cs` | Typed client. |
| `.../Provisioning/Cloudflare/D1MigrationApplier.cs` | Applies SQLite migrations to a fresh D1 DB. |
| `.../Services/Secrets/Handlers/CloudflareWorkerSecretsRotationHandler.cs` | Rotation handler for Worker secrets. |
| `/home/meywd/tamma/packages/engine-worker-sqlite-migrations/` | New package: D1-compatible migration set. |
| `/home/meywd/tamma/packages/engine-worker-sqlite-migrations/migrations/0001_initial.sql` | Pgsql-to-sqlite port of tenant schema. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/Cloudflare/CloudflareProviderTests.cs` | WireMock integration. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Keyed singleton `"cloudflare"`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | `Cloudflare:ApiToken`, `Cloudflare:AccountId`, `Cloudflare:DispatchNamespace`, `Cloudflare:ZoneId`, `Cloudflare:EdgeDomain`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | Register rotation handler with key `"cloudflare-worker-secrets"`. |

## 5. Sequence of changes

### Step 1 — Cloudflare API client (4h)

- Typed methods:
  - `CreateD1DatabaseAsync(name)` → uuid.
  - `DeleteD1DatabaseAsync(uuid)`.
  - `CreateKvNamespaceAsync(name)` → id.
  - `DeleteKvNamespaceAsync(id)`.
  - `UploadDispatchScriptAsync(namespace, scriptName, moduleContent, bindings, tags)` (PUT).
  - `PutWorkerSecretsAsync(scriptName, secrets)`.
  - `CreateWorkerRouteAsync(zoneId, pattern, dispatchNamespace)`.
  - `CreateDnsRecordAsync(zoneId, name, content)`.
- Rate-limit aware: 1200 req/5min default — back off on 429.
- **Commit**: `feat(cloudflare): typed API client`.

### Step 2 — D1 migration applier (3h)

- `D1MigrationApplier.ApplyAsync(d1Uuid, migrationsFolder)`:
  - Reads each `.sql` file in order.
  - Splits on statement boundary.
  - POSTs batch to D1 `/query` endpoint.
- Tracks applied migrations in a `__migrations` table on D1.
- **Commit**: `feat(cloudflare): D1 migration applier`.

### Step 3 — SQLite migration port (4h)

- Port Postgres tenant schema to SQLite:
  - `JSONB` → `TEXT` with `json_*` functions.
  - `SERIAL` → `INTEGER PRIMARY KEY AUTOINCREMENT`.
  - `PARTIAL INDEX` supported in SQLite 3.8+.
  - `pgvector` explicitly disabled; secrets metadata flags
    `SupportsVectorSearch=false` for Cloudflare tenants.
- Document incompatibilities in-package README.
- **Commit**: `feat(engine-worker): SQLite migration set`.

### Step 4 — ProvisionAsync (DedicatedCompute) (5h)

- Sequence:
  1. Create D1 DB → capture uuid.
  2. Apply SQLite migrations.
  3. Create KV namespace → capture id.
  4. Read bundled `engine-worker.js`.
  5. `PUT dispatch/namespaces/{ns}/scripts/tamma-tenant-{tenantId}`
     with bindings + tags.
  6. `POST workers/scripts/{name}/secrets` for each cabinet row.
  7. Create DNS record `<slug>.tamma-edge.net` → Worker route.
  8. Probe `/health`.
- **Commit**: `feat(cloudflare): provisionAsync (DedicatedCompute)`.

### Step 5 — DatabaseOnly topology (2h)

- Skip Worker + KV creation.
- `ResolveEndpointsAsync` returns shared engine URL + tenant's D1 URL.
- Shared engine uses a Cloudflare API token to talk to tenant's D1
  directly over the REST `/query` interface.
- **Commit**: `feat(cloudflare): DatabaseOnly topology`.

### Step 6 — DeprovisionAsync (2h)

- Reverse order: DNS record → Worker script → KV → D1.
- Each step 404-safe.
- **Commit**: `feat(cloudflare): deprovisionAsync`.

### Step 7 — GetStatusAsync (2h)

- Probe `/health` on Worker + D1 status query.
- Both green = Ready.
- **Commit**: `feat(cloudflare): getStatusAsync`.

### Step 8 — Rotation handler (4h)

- `CloudflareWorkerSecretsRotationHandler`:
  - Push: `PATCH workers/scripts/{name}/secrets` with new value.
  - Probe: `GET /health` returns 200.
  - Rollback: push previous value.
- Registered with 29-6.
- **Commit**: `feat(secrets): cloudflare worker secrets handler`.

### Step 9 — Integration tests (4h)

- WireMock Cloudflare API: full provision success.
- Partial-fail: Worker upload succeeds + D1 create times out → compensation removes Worker.
- **Commit**: `test(cloudflare): provider integration`.

## 6. Test strategy

### Unit

- API client: each method's happy path + 429 + 404.
- D1 migration applier: statement splitting, idempotent re-run.

### Integration

- WireMock end-to-end provision + deprovision.
- Topology switch (DedicatedCompute vs. DatabaseOnly) renders
  different bindings.

### Regression

- Rotation handler full flow against fake Cloudflare.

## 7. Rollback plan

- **Feature flag**: `Providers:Cloudflare:Enabled=false`.
- **Compensation**: 30-2 invokes `DeprovisionAsync`; each sub-step
  404-safe.
- **D1 data loss on deprovision**: irrecoverable. Tenant warned in
  the offboarding runbook.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. API client | 4 |
| 2. D1 migration applier | 3 |
| 3. SQLite migration port | 4 |
| 4. ProvisionAsync | 5 |
| 5. DatabaseOnly topology | 2 |
| 6. DeprovisionAsync | 2 |
| 7. GetStatusAsync | 2 |
| 8. Rotation handler | 4 |
| 9. Integration tests | 4 |
| **Total** | **30** (matches brief). |

## 9. Open questions

- **Upload endpoint verb**: research confirms **PUT** (not POST).
  Plan uses PUT. Brief says "POST" — correct at implementation time;
  align with current Cloudflare docs.
- **Bindings format**: multipart/form-data with JSON metadata.
  Example from Cloudflare docs; verify at implementation.
- **Dispatch-namespace pre-existence**: operator must create the
  dispatch namespace one-time (manual ops). Document in runbook.
- **First-time upload synchronicity (2026 change)**: 200 OK means
  script is ready. Plan skips the "wait for ready" poll on first
  upload.
- **D1 10 GB cap per DB**: tenant quota in 30-10's dashboard alerts
  at 8 GB.
- **50k D1 databases per account**: enough for 50k Cloudflare-tier
  tenants. Document the cap.
- **pgvector absence**: onboarding UI (30-7) warns if tenant needs
  vector search.
- **Engine-worker bundle production**: out of scope here; requires
  Worker-compatible build of the TS engine. Tracked as post-Epic-30.

Sources:
- [Cloudflare D1 limits](https://developers.cloudflare.com/d1/platform/limits/)
- [Workers for Platforms dispatch API](https://developers.cloudflare.com/api/resources/workers_for_platforms/subresources/dispatch/subresources/namespaces/subresources/scripts/methods/update/)
- [Cloudflare Workers for Platforms docs](https://developers.cloudflare.com/cloudflare-for-platforms/workers-for-platforms/)
- [Workers for Platforms example repo](https://github.com/cloudflare/workers-for-platforms-example)

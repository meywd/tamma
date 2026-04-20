# Story 30-5: Cloudflare Provider — D1 + Workers + KV

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform administrator**,
I want a `CloudflareTenantProvider` that provisions a Cloudflare D1 database + a Cloudflare Worker (from the tamma-engine TS bundle) + a KV namespace for bookmark state per tenant, via the Cloudflare for Platforms API,
so that Tamma can offer an "edge tier" that is dirt-cheap per tenant, instantly provisioned, and globally distributed — matching the industry pattern in the research notes where Cloudflare for Platforms is the closest analogue to "per-tenant infra on demand".

## Acceptance Criteria

1. `CloudflareTenantProvider : ITenantInfrastructureProvider` with `ProviderKey = "cloudflare"`. `GetCapabilities`:
   - `SupportedTopologies = DatabaseOnly | DedicatedCompute` (DedicatedCompute = D1 + Worker; DatabaseOnly = D1 only with shared Worker).
   - `Regions = ["global"]` (Cloudflare is edge, no region selection — locale-tagged only).
   - `Features = CustomDomains | BackupManagement (via D1 time-travel) | AutoscaleCompute (intrinsic)`.
   - `MaxTenantsPerOrg = 50000` (D1 limit per account is "thousands" — cite research notes; we set a conservative cap).
2. `ProvisionAsync` (DedicatedCompute):
   - Calls `POST /accounts/{account_id}/d1/database` to create a D1 database named `tamma-tenant-<tenantId>`. Captures the D1 `uuid`.
   - Calls `POST /accounts/{account_id}/workers/dispatch/namespaces/{dispatch_ns}/scripts/{script_name}` (Workers for Platforms dispatch-namespace upload) with the pre-bundled `tamma-engine-worker.js` + bindings `{ D1: d1_uuid, KV: kv_namespace_id, SECRETS: <wrangler-secret-refs> }`.
   - Calls `POST /accounts/{account_id}/storage/kv/namespaces` to create a per-tenant KV namespace for bookmarks / idempotency keys.
   - Configures a custom domain route (`<tenant-slug>.tamma-edge.net`) via `POST /zones/{zone}/dns_records` + `POST /accounts/{acct}/workers/domains`.
   - Probes the Worker at `/health`.
3. `ProvisionAsync` (DatabaseOnly) skips the Worker + KV — engine runs on shared Tamma control-plane infrastructure and holds a Cloudflare API token to talk to the tenant's D1 as an external DB.
4. D1's SQL flavour is SQLite with the "smart-placement-style" autoscaling — this story notes the following data-model compatibility constraints (documented for 30-7 / 30-8):
   - Tamma's EF migrations target Postgres. A D1-compatible migration set lives in `packages/engine-worker-sqlite-migrations/` (new package, minimal) produced by running the Postgres migrations through `pgsql-to-sqlite` + manual fixups.
   - Features not available on D1: `JSONB` columns (use TEXT with JSON helpers), `SERIAL` (use `INTEGER PRIMARY KEY AUTOINCREMENT`), `PARTIAL INDEX` (supported in recent SQLite — verify), Postgres-only extensions (pgvector — disabled on the edge tier for this epic).
   - These constraints are called out in the onboarding UI so operators don't pick Cloudflare for a tenant that needs vector search.
5. Initial secrets provisioning: the Worker's secrets are set via `POST /accounts/{account_id}/workers/scripts/{script}/secrets` — each Epic 29 cabinet row for the tenant is pushed as a Worker secret. Rotation handler for Cloudflare secrets ("cloudflare-worker-secrets") is added to Story 29-6's handler set in this story.
6. `ResolveEndpointsAsync` returns `{ EngineUrl: "https://<slug>.tamma-edge.net", DbUrl: "d1://<uuid>", BookmarkStorageRef: "kv://<ns_id>" }`. The `DbUrl` d1:// scheme is a Tamma convention — the Npgsql-equivalent client on the Worker side parses it to the right Cloudflare binding.
7. `DeprovisionAsync` reverses: delete Worker script → delete KV namespace → delete D1 database → remove DNS. Each step is individually idempotent (404 treated as success).
8. `GetStatusAsync` probes `https://<slug>.tamma-edge.net/health` and `GET /accounts/{a}/d1/database/{uuid}` for DB status. Both green = Ready.
9. Cloudflare API rate-limit handling: token bucket at 1200 req/min per API token (Cloudflare default 1200/5min = 240/min but varies by product; per 2026 docs). Emits `CLOUDFLARE.RATE_LIMIT.HIT` on 429.
10. Integration test with WireMock-faked Cloudflare API: provision success path, partial-fail (Worker uploads but D1 create times out) → compensation runs (delete Worker + KV that did get created; 404-safe).

## Technical Context

### Workers for Platforms vs stock Workers

Stock Cloudflare Workers don't have a per-tenant isolation primitive
(all your Workers share your account). Workers for Platforms
(formerly "for SaaS") introduces **dispatch namespaces** — scripts
uploaded to a namespace are routed by your host Worker, giving you
per-tenant isolation within one Cloudflare account. Research notes
§2 confirmed this is the idiomatic shape.

### Why D1 rather than a provider-managed Postgres like Neon

D1 is bundled per-account with no per-DB cost (1000+ free per
account); Neon is per-project cost. For the "cheapest tier" this
story builds, D1 wins. Neon would be a future 4th backend for "serverless
Postgres per tenant with pgvector" (skipped here; filed as a future
story).

### Engine bundle

The Cloudflare provider assumes an already-built TS Worker bundle at
`packages/engine-worker/dist/engine-worker.js` — producing the bundle
is not in scope here (existing orchestration engine needs adaptation,
but the foundation is in the TS packages). This story only handles the
"take the bundle and deploy it per tenant" step. The engine adaptation
is a separate follow-up story (flagged as "Cloudflare edge engine
build" in the placement plan, deferred post-Epic-30).

### Out-of-scope

- Building the Worker bundle from existing TS engine (separate story,
  post-Epic-30).
- Neon / PlanetScale backends (future epic).
- R2 storage for tenants (future — when a tenant needs blob storage
  for generated artifacts).
- Durable Objects for coordination (future — currently RabbitMQ does
  the control-plane job).

## Estimated hours

30 — provider class + Cloudflare API client + bindings orchestration +
KV namespace management + custom-domain routing + rotation handler +
integration tests.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cloudflare/CloudflareTenantProvider.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Cloudflare/CloudflareApiClient.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/CloudflareWorkerSecretsRotationHandler.cs` (new)
- `packages/engine-worker-sqlite-migrations/` (new package — D1-compatible schema)

## References

- [Cloudflare D1 docs](https://developers.cloudflare.com/d1/)
- [Workers for Platforms solutions page](https://workers.cloudflare.com/solutions/platforms)
- [Cloudflare Workers API reference](https://developers.cloudflare.com/api/operations/worker-script-upload-worker-module)
- Research notes §2

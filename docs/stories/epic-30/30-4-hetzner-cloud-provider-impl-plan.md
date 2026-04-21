# Story 30-4 Implementation Plan — Hetzner Cloud Provider

**Status**: Planned (2026-04-20)
**Story brief**: [`30-4-hetzner-cloud-provider.md`](./30-4-hetzner-cloud-provider.md)
**Epic 30 phase**: Provider drivers — parallel with 30-5.
**Branch**: `feat/story-30-4-hetzner-cloud-provider`

---

## 1. Objective

Ship `HetznerCloudTenantProvider` that provisions a dedicated VPS per
tenant via Hetzner Cloud API, boots it with a cloud-init template
that installs Docker + launches engine + Postgres containers. Enables
the "dedicated" tier for compliance-heavy tenants + cheaper-than-
Cranl sizing. Research confirms Hetzner Cloud API rate limit is
**3600 req/hour** (shared across all API tokens on an account); plan
documents a parallel-provisioning cap to stay under.

## 2. Dependencies

Hard blockers:

- **Story 30-1** — v2 interface.
- **Story 30-2** — dispatch workflow.
- **Story 29-2 / 29-7** — secret store + DB rotation (for env push).
- Hetzner Cloud API token (operator-supplied).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/Hetzner/HetznerCloudTenantProvider.cs` | v2 provider impl. |
| `.../Provisioning/Hetzner/HetznerCloudApiClient.cs` | Typed client over `api.hetzner.cloud/v1`. |
| `.../Provisioning/Hetzner/CloudInitRenderer.cs` | Scriban template renderer. |
| `.../Provisioning/Hetzner/cloud-init-tenant.yaml.tmpl` | cloud-init template. |
| `.../Provisioning/Hetzner/HetznerSshTenantHook.cs` | SSH push for rotation. |
| `.../Provisioning/Hetzner/HetznerRateLimiter.cs` | Token-bucket for 3600/h shared limit. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/Hetzner/HetznerProviderTests.cs` | WireMock-based integration. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/Hetzner/CloudInitRendererTests.cs` | Template rendering edge cases. |
| `/home/meywd/tamma/docs/runbooks/hetzner-operator-key-rotation.md` | SSH key rotation. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | `AddKeyedSingleton<ITenantInfrastructureProvider, HetznerCloudTenantProvider>("hetzner")`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | `HetznerCloud:ApiToken`, `HetznerCloud:OperatorSshKeyId`, `HetznerCloud:DefaultRegion`, engine image tag. |

## 5. Sequence of changes

### Step 1 — API client + rate limiter (4h)

- Typed `HetznerCloudApiClient` over the REST API:
  - `CreateServerAsync(createServerRequest)` → 201 with server JSON.
  - `GetServerAsync(id)`.
  - `DeleteServerAsync(id)`.
  - `CreateFirewallAsync`, `DeleteFirewallAsync`.
- `HetznerRateLimiter`: shared token bucket at 3600/h (leaky-bucket refill per-second).
- Each client call passes through the limiter.
- Unit tests: 429 handling, retry-after respect, bucket refill.
- **Commit**: `feat(hetzner): typed API client + rate limiter`.

### Step 2 — cloud-init template + renderer (4h)

- Scriban template with placeholders: `tenantId`, `secrets`,
  `engineImageTag`, `postgresVersion`, `operatorSshPublicKey`.
- Template writes `.env`, `docker-compose.yml`, installs Docker,
  disables password SSH, runs `docker compose up -d`.
- Unit tests: render with each topology variant (DedicatedCompute,
  DatabaseOnly), assert YAML valid + secrets interpolated.
- **Commit**: `feat(hetzner): cloud-init renderer + template`.

### Step 3 — ProvisionAsync (6h)

- Resolve secrets from `ISecretStore` (created by 30-2's
  `RegisterSecretsActivity` before this runs).
- Render cloud-init → base64.
- `POST /v1/servers` with `server_type`, `image`, `location`,
  `ssh_keys=[<operatorKeyId>]`, `user_data=<b64>`.
- Poll `GET /v1/servers/{id}` until `status=running` (max 90s).
- Poll `/health` on returned IP until 200 (max 5 min).
- Returns `ProvisioningResult` with `hetzner_server_id`.
- **Commit**: `feat(hetzner): provisionAsync`.

### Step 4 — DatabaseOnly short-circuit (3h)

- If topology is `DatabaseOnly`, render a slimmer cloud-init that
  installs only Postgres.
- `EngineHost` returned as shared Tamma engine host; `DbUrl` is
  `postgres://...@<hetzner-ip>:5432/tamma_tenant`.
- Firewall rule: 5432 open only to shared Tamma control-plane IP range.
- **Commit**: `feat(hetzner): DatabaseOnly topology`.

### Step 5 — DeprovisionAsync (2h)

- `DELETE /v1/servers/{id}`; 404 = already gone.
- Delete firewall rule; release floating IPs.
- Idempotent.
- **Commit**: `feat(hetzner): deprovisionAsync`.

### Step 6 — GetStatusAsync (2h)

- Combines Hetzner server status + engine `/health` probe.
- 5 consecutive failures → `Unhealthy`.
- **Commit**: `feat(hetzner): getStatusAsync`.

### Step 7 — SSH rotation hook (4h)

- `HetznerSshTenantHook.UpdateTenantEnvAsync(tenantId, envDict)`:
  - SSHes to server using operator key.
  - Updates `.env` + `docker compose restart tamma-engine`.
- Called by 29-7 / 29-8 rotation handlers for Hetzner-backed secrets.
- Uses `SSH.NET` NuGet.
- Secrets never logged.
- **Commit**: `feat(hetzner): SSH rotation hook`.

### Step 8 — Integration tests + runbook (5h)

- WireMock fake `IHetznerCloudApiClient`:
  - Provision success (end-to-end).
  - Server-running timeout → compensation.
  - Deprovision idempotent on 404.
  - Rotation hook pushes new env.
- `hetzner-operator-key-rotation.md` runbook.
- **Commit**: `test(hetzner): provider integration + runbook`.

## 6. Test strategy

### Unit

- API client with mocked HTTP.
- cloud-init renderer edge cases (empty secrets, unicode).
- Rate limiter bucket semantics.

### Integration

- WireMock full lifecycle per AC1-AC10.
- cloud-init YAML validation with `cloud-init schema` tool.

### Security

- Secrets never appear in logs (grep test).
- SSH key stored in cabinet (29-2); rotated via 29-6/29-7 path.

## 7. Rollback plan

- **Feature flag**: provider registration gated by
  `Providers:Hetzner:Enabled=false` initially. Opt-in per tenant via
  onboarding.
- **Deprovision compensation**: 30-2's compensation calls
  `DeprovisionAsync`; firewall + floating IPs also cleaned.
- **Non-reversible**: server data is destroyed on deprovision. No
  recovery after delete.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. API client + rate limiter | 4 |
| 2. cloud-init renderer | 4 |
| 3. ProvisionAsync | 6 |
| 4. DatabaseOnly topology | 3 |
| 5. DeprovisionAsync | 2 |
| 6. GetStatusAsync | 2 |
| 7. SSH rotation hook | 4 |
| 8. Integration + runbook | 5 |
| **Total** | **30** (brief 32). |

## 9. Open questions

- **Rate limit 3600/h sharing**: Hetzner's limit is per-account,
  shared across all API tokens. Parallel provisioning must cap
  concurrency. Plan: `SemaphoreSlim(8)` on provisioning + all
  non-provision calls (probes, status) share the same bucket.
  Documented in runbook. Research confirms 2026 limit unchanged.
- **SSH rotation alternatives**: brief suggests cloud-init
  Configurator mode as Epic 1.5 follow-up. Current implementation
  uses SSH; acceptable but operator-key rotation is manual via the
  runbook.
- **cloud-init validation**: cloud-init schema check runs in CI.
- **Region list accuracy**: `nbg1`, `fsn1`, `hel1`, `ash`, `hil`
  current as of 2026-04. Re-verify before ship.
- **Image choice**: `docker-ce` image vs. `ubuntu-24.04` + user-data
  Docker install? Plan: `ubuntu-24.04` + user-data for broader
  compatibility. Document trade-off.
- **Floating IP vs. server IP**: server IP is ephemeral on re-create.
  Plan: pin a floating IP per tenant. Adds ~EUR 1/month per tenant.
  Not in MVP; documented as future enhancement.
- **SSH.NET library**: widely used, MIT. Confirm version at
  implementation.

Sources:
- [Hetzner Cloud API](https://docs.hetzner.cloud/)
- [Hetzner Cloud basic cloud-config](https://community.hetzner.com/tutorials/basic-cloud-config/)
- Rate-limit reference: 3600 req/hour confirmed on
  [Hitting the Hetzner API limits](https://github.com/hetznercloud/hcloud-go/issues/79).

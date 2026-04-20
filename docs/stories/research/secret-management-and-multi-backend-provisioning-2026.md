# Research Notes — Secret Management + Multi-Backend Tenant Provisioning (2026)

**Author**: planning sweep, 2026-04-20
**Purpose**: ground Epic 29 (Platform Secret Management) and Epic 30
(Pluggable Tenant Infrastructure Provisioning) in current (2025-2026)
guidance, not training memory. Each section: findings + chosen direction.

## 1. Control-plane secret store backend

Candidates scored on: self-hosted footprint, per-request lease auth,
dynamic DB credentials, rotation workflow primitives, audit log, UI,
governance / vendor-lock risk.

| Backend | Self-host | Dynamic DB | Rotation | Audit | UI | Governance | Notes |
|---|---|---|---|---|---|---|---|
| **OpenBao 2.5** (Feb 2026) | Yes (MPL 2.0) | Yes | Yes | Yes | Yes (built-in) | Linux Foundation (not yet graduated) | Free namespaces for multi-tenancy; horizontal read scalability added 2026-02-04 [(Swain, Medium 2026-02-13)](https://lalatenduswain.medium.com/openbao-vs-hashicorp-vault-the-secrets-management-showdown-every-devops-team-needs-to-read-in-2026-458ae0d9a408). |
| HashiCorp Vault OSS | Yes (BSL 1.1) | Yes | Yes | Yes | Yes | IBM (post-acquisition) | Namespaces Enterprise-only; BSL prohibits "Vault-as-a-service" [(Digitalis)](https://digitalis.io/post/choosing-a-secrets-storage-hashicorp-vault-vs-openbao). |
| Infisical | Yes (cloud + self-host) | Beta (DB engines) | Yes (auto-rotate) | Yes | Yes (excellent DX) | Company-backed OSS | Strongest developer UX in the self-host tier [(Infisical blog 2026)](https://infisical.com/blog/best-secret-management-tools). |
| Bitwarden Secrets Manager | Yes | **No** dynamic secrets | Manual | Yes | Yes | Bitwarden Inc. | Disqualified for the DB-password-per-tenant use case [(NebulaGG DEV 2026)](https://dev.to/nebulagg/top-6-secrets-management-tools-for-devs-in-2026-4ahe). |
| Doppler | **No** self-host | Beta | Enterprise tier only | Yes | Yes | SaaS only | Disqualified — we need self-host on the Hetzner VPS. |
| Cloud KMS (AWS / GCP / Azure) | N/A | via separate service | via separate service | Yes | Limited | Cloud vendor | Lock-in; not appropriate as the primary store (fine as a future KEK backend per Story 28-13 when a trigger fires). |

**Recommendation**: **OpenBao** for the KEK + dynamic-DB-credential
backend when Epic 28-13 triggers fire. Until then, **Postgres-backed
envelope-encrypted secrets table** with KEK from env var (keeping
`ISecretsService` as the seam per the 2026-04-17 user decision recorded
in `project_epic28_kek_decision.md`). Rationale: OpenBao is truly open
(MPL 2.0), namespaces are free, dynamic DB credentials are production-
mature, and the LF governance removes the HashiCorp/IBM vendor-lock
concern; but it is not yet LF-graduated and we have no paying tenant
with a breach clause forcing the adoption cost. Epic 29 ships on
Postgres + env KEK with a pluggable `ISecretStoreBackend` so swapping to
OpenBao later is a driver swap, not a re-architecture.

## 2. Multi-backend tenant infrastructure patterns (2026)

Looked at **Supabase / Neon / PlanetScale / Cloudflare Workers for
Platforms / Northflank BYOC** for how they shape the "provision a DB
(or VM) per tenant on demand across multiple backends" problem.

Key findings:

- **Cloudflare Workers for Platforms** is the closest match to the
  user's ask: a host platform provisions per-tenant D1 databases,
  Workers scripts, KV namespaces, and R2 buckets through a single
  control-plane API. D1 is designed for "thousands of small (10 GB)
  databases per account" as a per-tenant unit, not one giant DB
  [(Cloudflare D1 overview)](https://developers.cloudflare.com/d1/),
  [(Workers for Platforms)](https://workers.cloudflare.com/solutions/platforms).
- **Neon's model**: separate storage from compute; instant provisioning;
  per-agent/per-tenant branching up to 100 projects per account —
  "purpose-built for agentic workloads" [(DevToolReviews 2026)](https://www.devtoolreviews.com/reviews/supabase-vs-planetscale-vs-neon).
- **PlanetScale**: launched PostgreSQL support in September 2025
  ("PlanetScale for Postgres"); Project Neki brings Vitess-style
  horizontal sharding to PG; Supabase has Multigres (Sougoumarane) in
  parallel [(DataFormatHub 2025)](https://dev.to/dataformathub/serverless-postgresql-2025-the-truth-about-supabase-neon-and-planetscale-7lf).
- **Northflank BYOC** generalises the "bring your own cloud" shape
  across AWS/GCP/Azure/Civo/CoreWeave/Oracle/bare-metal from one
  control plane — the exact pattern Epic 30 wants [(Northflank 2026)](https://northflank.com/blog/multi-tenant-cloud-deployment).
- **Hetzner Cloud**: server-create API accepts `user_data` for
  cloud-init; idiomatic bootstrap is cloud-init → Docker install →
  pull image → run. CX23+ supports user_data; Cluster API provider
  (CAPH) exists for Kubernetes but is overkill for our per-tenant VM
  case [(Hetzner Basic Cloud Config)](https://community.hetzner.com/tutorials/basic-cloud-config/),
  [(Hetzner Docker CE app)](https://docs.hetzner.com/cloud/apps/list/docker-ce/).

**Idiomatic abstraction shape** (what Epic 30 should target):

```
ITenantInfrastructureProvider
├─ ProvisionAsync(tenant, topology, ct) → { endpoints, state, cost-meta }
├─ DeprovisionAsync(tenant, ct)          → compensating saga
├─ GetStatusAsync(tenant, ct)             → probe health
└─ RotateConnectionAsync(tenant, ct)      → secret-store handoff

ProvisioningTopology:
  DatabaseOnly      // just the tenant DB; engine runs shared
  DedicatedCompute  // VM + engine + DB
  Managed           // tenant owns infra; we register endpoints
```

Each provider registers a *capability matrix* (supports DatabaseOnly?
DedicatedCompute? Managed?) so the onboarding UI can filter. This is
the Northflank control-plane + Cloudflare-for-Platforms multi-resource
pattern collapsed onto a single C# interface.

## 3. Atomic rotation when the consumer lives in a separate system

The hard case: rotate `tamma_app` password (consumer is the Tamma API
process) or rotate `Cranl:ApiKey` (consumer is Cranl's running
container). Atomicity cannot be ACID — it has to be a **saga** with
compensation.

Current guidance (2025-2026):

- Treat rotation as an orchestrated saga (Temporal, Elsa, Azure
  Durable Functions). Each step is a local transaction with a named
  compensation. [(Temporal blog)](https://temporal.io/blog/mastering-saga-patterns-for-distributed-transactions-in-microservices),
  [(Azure Architecture Center)](https://learn.microsoft.com/en-us/azure/architecture/patterns/saga).
- Canonical sequence for a password-style secret pushed to a consumer:
  1. Mint new value in the secret store (status = `pending_rollout`).
  2. Call consumer's API to accept the new value (idempotent
     upsert, correlation id).
  3. Health-probe the consumer (does it still work?).
  4. On success: flip the store row to `active`, mark previous
     version as `retired_grace` for N minutes.
  5. On failure: compensating transaction — delete the new version in
     the consumer, leave the previous value `active`, emit
     `SECRET.ROTATION.FAILED`.
- Both the "new" and "old" value should be retrievable during a
  **grace window** so in-flight requests don't fail mid-rotation.
- Probes + compensation must be **idempotent and retryable** — webhook
  / API retries will re-deliver [(microservices.io Saga pattern)](https://microservices.io/patterns/data/saga.html).
- Epic 1.5-30 (`RotationCascadeWorkflow`) already prescribes this
  shape for the LLM-safe secret track. Epic 29's rotation primitives
  should reuse that Elsa activity set rather than invent a second one;
  Epic 29-6/7/8 call out the reuse.

**Fallback if push fails midway**: the store keeps a `versions` table
with `status ∈ { pending, active, retired_grace, revoked }`. The
compensating transaction revokes `pending` and retries the push with
backoff up to N attempts, then alerts a human (Story 29-6). The
previous `active` version remains serving until either the rotation
succeeds or an operator forces a retire.

## 4. Envelope encryption shape for the Postgres-backed store

Standard KEK/DEK envelope per [(Google Cloud KMS)](https://docs.cloud.google.com/kms/docs/envelope-encryption):

- **KEK**: single operator-supplied key (env var in our case —
  `TAMMA_TENANT_KEK_PRIMARY`, with `_SECONDARY` for rotation). Never
  leaves the process.
- **DEK**: per-tenant, per-secret 32-byte AES-GCM key, generated via
  `RandomNumberGenerator.GetBytes(32)`.
- **Row format** (bytea column): `version(1) ‖ nonce(12) ‖ wrapped_dek(ct+tag) ‖ value_nonce(12) ‖ value_ct ‖ value_tag(16)`.
- **KEK rotation**: re-wrap all DEKs under the new KEK without
  touching plaintext values; keeps rotation cheap.
- This matches Epic 28's direct-encrypt design but with a per-secret
  DEK rather than encrypting values with the KEK directly — lets us
  rotate the KEK without re-encrypting gigabytes of values.

## 5. What changes vs. existing Epic 1.5 secret-management track

Epic 1.5 (stories 1.5-16 through 1.5-45) already covers **LLM-safe
secret operations** — commitment hashes, multi-platform mirrors, OIDC,
probes, rotation cascade. Its focus is workflow-driven secret lifecycle
where the LLM never sees plaintext.

Epic 29 is orthogonal: it's the **control-plane secret cabinet** — the
Postgres-backed store + UX + admin/tenant-admin management of
platform-scoped secrets (DB passwords, platform API keys, HMAC shared
secrets, per-tenant DB URLs). Epic 29 depends on the Epic 1.5 crypto
primitives (1.5-16) and vault storage (1.5-17) but adds:

- A typed-secret data model (name, scope, purpose, consumers, owner,
  rotation schedule, last-rotated-at) that Epic 1.5-16 does not
  require (1.5-16's interface is byte-oriented).
- Platform-admin and tenant-admin UIs — user confirmed they want
  "Secret management UI tells what this key is, where it's used".
- Migration of today's stopgaps (`TenantSecretProtector`,
  `cranl_database_url_encrypted`, `changeme` tamma_app password,
  `TAMMA_SHARED_SECRET`, `Cranl:EncryptionKey` HKDF fallback).

Epic 1.5 remains the **workflow / platform-mirror / LLM-safety** track;
Epic 29 is the **operator-facing cabinet** that ties today's ad-hoc
secrets into it. A later retrospective may merge them, but in this
planning round they run as separate epics with explicit reuse points.

## Sources

- [OpenBao vs HashiCorp Vault 2026 (Swain, Medium)](https://lalatenduswain.medium.com/openbao-vs-hashicorp-vault-the-secrets-management-showdown-every-devops-team-needs-to-read-in-2026-458ae0d9a408)
- [OpenBao vs Vault (Digitalis, 2026)](https://digitalis.io/post/choosing-a-secrets-storage-hashicorp-vault-vs-openbao)
- [Open Source Secrets Management for DevOps in 2026 (Infisical)](https://infisical.com/blog/open-source-secrets-management-devops)
- [Top 6 Secrets Management Tools for Devs in 2026 (NebulaGG, dev.to)](https://dev.to/nebulagg/top-6-secrets-management-tools-for-devs-in-2026-4ahe)
- [Best Secrets Management Tools 2026 (Infisical)](https://infisical.com/blog/best-secret-management-tools)
- [Cloudflare D1 Docs](https://developers.cloudflare.com/d1/)
- [Cloudflare Workers for Platforms](https://workers.cloudflare.com/solutions/platforms)
- [Supabase vs Neon vs PlanetScale 2026 (DevToolReviews)](https://www.devtoolreviews.com/reviews/supabase-vs-planetscale-vs-neon)
- [Serverless Postgres 2025 (DataFormatHub, dev.to)](https://dev.to/dataformathub/serverless-postgresql-2025-the-truth-about-supabase-neon-and-planetscale-7lf)
- [Northflank — Multi-tenant cloud deployment 2026](https://northflank.com/blog/multi-tenant-cloud-deployment)
- [Hetzner Basic Cloud Config Tutorial](https://community.hetzner.com/tutorials/basic-cloud-config/)
- [Hetzner Docker CE App Docs](https://docs.hetzner.com/cloud/apps/list/docker-ce/)
- [Saga Pattern (microservices.io)](https://microservices.io/patterns/data/saga.html)
- [Saga Pattern in Microservices (Temporal)](https://temporal.io/blog/mastering-saga-patterns-for-distributed-transactions-in-microservices)
- [Saga Design Pattern (Azure Architecture Center)](https://learn.microsoft.com/en-us/azure/architecture/patterns/saga)
- [Envelope Encryption (Google Cloud KMS)](https://docs.cloud.google.com/kms/docs/envelope-encryption)

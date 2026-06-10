# Story 30-1 — `ITenantInfrastructureProvider` v2 design decisions

**Date**: 2026-04-27
**Status**: locked-in (interface, records, registry, null seam, DI, and unit tests landed in `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/` on branch `story-30-1-provisioner-interface-v2`).

The Story 30-1 brief (`docs/stories/epic-30/30-1-provisioner-interface-v2.md`) is the foundation interface for Epic 30 — every other story (30-2 dispatch workflow, 30-3 Cranl refactor, 30-4 Hetzner, 30-5 Cloudflare, 30-6 BYO, 30-7 onboarding UI, 30-8 routing, 30-9 deprovisioning saga, 30-10 cost dashboard) consumes this contract. Designing it wrong forces 30 hours of rework downstream. Six substantive design calls were made; this ADR records each so reviewers and the authors of 30-2..30-10 know what was deliberate vs. what is open.

The decisions fall into two buckets: **shape of the contract** (#1, #2, #3, #4) and **how the contract sits in the existing codebase** (#5, #6).

## Decisions

### #1 — Backend identity: string-keyed registry, **not** a `BackendType` enum

**Context**: Story brief mentions four named backends today (`cranl`, `hetzner`, `cloudflare`, `byo`) plus the `null` seam. The choice was between (a) an enum that hard-codes those values, (b) a string key registered via DI, (c) a `Type`-based key.

**Decision**: **string key** (`ITenantInfrastructureProvider.ProviderKey`). Convention: lowercase snake_case. Reserved keys: `null`, `cranl`, `hetzner`, `cloudflare`, `byo`. The registry (`TenantProviderRegistry`) is a thin `Dictionary<string, ITenantInfrastructureProvider>` populated from `IEnumerable<ITenantInfrastructureProvider>` at DI time.

**Rationale**:
- AC10 demands "no switch statement on provider key outside the registry itself." An enum invites switch statements; strings + a registry don't.
- New providers (a future `aws-rds`, `digitalocean`, `kubernetes-operator`) plug in by calling a fresh `services.AddTenantProvider*` extension method without touching this story's enum and migration. That is the whole point of the v2 abstraction.
- `tenants.provider_key` (the column 30-3 will introduce) maps 1:1 to the string key. No enum-to-text round-trip churn in EF Core or psql.

**Alternatives considered**:
- `BackendType` enum — rejected: every Epic 30 follow-up that adds a backend would have to extend the enum (or worse, edit the migration that wrote the column). Strings keep the open-set boundary clean.
- `Type`-based key — rejected: forces every consumer of the registry to know the concrete provider type, defeating the abstraction.

### #2 — Provisioning state machine: reuse the **v1** enum, wrap in a snapshot record

**Context**: Story 28-1 already shipped `Tamma.Api.Services.Provisioning.ProvisioningState` (a 10-value enum: `None → Pending → DatabaseProvisioning → DatabaseReady → AppProvisioning → AppDeploying → Ready | Failed`, plus `Deprovisioning → Deprovisioned`). Open call: should v2 mint a fresh state vocabulary, or reuse v1's enum?

**Decision**: **reuse v1's enum**. The new `ProvisioningStatusSnapshot` record wraps `ProvisioningState` + a free-text `Detail` + a structured `FailureReason` short-code + `UpdatedAt`. Persistence of the state machine stays on the existing `tenants.provisioning_state` column (snake_case storage, see `ProvisioningStateExtensions.ToStorageString()`).

**Rationale**:
- The v1 vocabulary covers every transition every Epic 30 backend needs. Cranl, Hetzner Cloud, Cloudflare D1+Workers, and BYO all walk roughly the same arc — even Managed/BYO is essentially "validate → register → Ready" which maps onto the existing enum (Pending → Ready) without strain.
- Forking a parallel enum would mean tools that read `tenants.provisioning_state` (admin endpoints, dashboards, the `provisioning_events` audit table) need translation on every read, which is rework with no upside.
- The new `FailureReason` short-code (e.g. `"unsupported_topology"`, `"cranl_db_create_failed"`, `"byo_db_unreachable"`) is the new structured field — v1's `ProvisioningStatus` only had a free-text `Detail`. The dispatch workflow (30-2) keys retry-vs-surface decisions off `FailureReason`.

**Alternatives considered**:
- New enum (`InfraProvisioningState` with v2 prefixes) — rejected: writes 10 entries of work and demands a translation layer at the storage boundary forever.
- Free-text state with a validation list — rejected: every dashboard / admin endpoint would have to validate independently.

### #3 — Capability negotiation: provider declares topologies + features as bit-flags; registry exposes the matrix

**Context**: AC1 says capabilities include "which topologies supported; region list; optional features like `SupportsCustomDomains`". Open call: bit-flags on a single enum, separate enums per axis, or a list-of-strings shape?

**Decision**:
- **`ProvisioningTopology` is `[Flags]`** with three values (`DatabaseOnly`, `DedicatedCompute`, `Managed`). A provider composes the flags it supports (e.g. Hetzner = `DatabaseOnly | DedicatedCompute`).
- **`ProviderFeatures` is a separate `[Flags]` enum** with `CustomDomains`, `AutoscaleCompute`, `DedicatedDb`, `BackupManagement`. Orthogonal to topology.
- **Regions are free-form strings** (`IReadOnlyList<string>`), not an enum. Each provider mints its own region vocabulary (`germany-1` for Cranl, `nbg1` for Hetzner, `auto` for Cloudflare).
- **`ProviderCapabilities.SupportsTopology(topology)`** is the predicate the dispatch workflow calls before routing — `(SupportedTopologies & topology) == topology` semantics.
- **`TenantProviderRegistry.ListCapabilities()`** returns the full matrix; the onboarding UI (30-7) consumes it directly.

**Rationale**:
- Bit-flags compose naturally: a provider that supports two topologies is `A | B`, not "two providers". The dispatch workflow's predicate is a single bitwise check.
- Free-form region strings avoid a platform-wide region enum that would need editing for every provider — the user prompt explicitly warned against widening scope, and a region enum is exactly that kind of cross-cutting maintenance debt.
- Features as a separate flags enum keeps the matrix two-dimensional in the UI ("backend × topology" with feature checkboxes per row) instead of three-dimensional.

**Alternatives considered**:
- One enum `Capability { CranlDedicatedCompute, HetznerDatabaseOnly, ... }` — rejected: explodes combinatorially as backends are added.
- `IReadOnlySet<ProvisioningTopology>` — rejected: heavier to allocate, and bit-flags is the .NET idiom.

### #4 — Provider scoping: **platform-scoped, not tenant-scoped**

**Context**: Implementation question — does each tenant get its own `ITenantInfrastructureProvider` instance (lifetime = scoped per request, keyed by tenant), or does one platform-wide provider serve every tenant of its kind?

**Decision**: **platform-scoped singletons**. One `CranlTenantProvider` instance serves every Cranl-backed tenant in the process. Tenants do NOT bring their own provider implementation — providers are wired by the platform operator at startup via DI extension methods. The registry is also a singleton.

**Rationale**:
- A real provider (Cranl, Hetzner, Cloudflare) needs an authenticated API client, a rate-limit token bucket, and a circuit breaker. Allocating those per-tenant per-request would be expensive and would defeat rate-limiting (each per-tenant client would have its own bucket, and the API would 429).
- Registering a singleton matches the existing project pattern (`PlatformTaskWorker`, `EngineRegistryHeartbeatService`, `OutboxSmtpSender` are all singletons).
- The `ITenantInfrastructureProvider` does NOT capture tenant state in fields — it takes `tenantId` on every method, so a singleton is thread-safe by construction.
- "Tenants bring their own provider" sounds nice but has no real use case in Epic 30: BYO is a backend, not a "tenant runs Tamma's provider code." If a tenant hand-rolls a backend, a platform-owner registers it.

**Idempotency requirement** falls out of this: because a singleton serves every tenant, every method MUST be idempotent (concurrent calls with the same `(tenantId, request)` must coalesce / no-op the second). The `ProvisionAsync` doc-comment makes this explicit. The Elsa workflow retry semantics in 30-2 depend on it.

**Alternatives considered**:
- Scoped-per-tenant with `IServiceProviderIsService` keyed lookup — rejected: no use case justifies the complexity, and rate-limit fragmentation is a real cost.

### #5 — V1 / v2 coexistence: **keep v1 fully intact**, do NOT shim CranlTenantProvisioner here

**Context**: The plan (`30-1-provisioner-interface-v2-impl-plan.md` Step 5) says "Mark `ITenantProvisioner` + `CranlTenantProvisioner` `[Obsolete]`. Shim delegates to `TenantProviderRegistry.GetProvider("cranl")`." But the user prompt (and the natural reading of "30-3 refactors Cranl") says **DO NOT refactor `CranlTenantProvisioner` — that's 30-3**.

**Decision**: **30-1 does NOT touch `ITenantProvisioner`, `CranlTenantProvisioner`, `NullTenantProvisioner`, or any v1 caller.** The v2 surface is added in a parallel namespace `Tamma.Api.Services.Provisioning.V2.*`. DI registers both:
- v1 (`ITenantProvisioner` → Cranl or Null per existing logic)
- v2 (`ITenantInfrastructureProvider` → `NullTenantProvider` only, plus the registry)

Existing admin endpoints, the `TenantProvisioningTaskHandler`, and every test that depends on the v1 surface keep working unchanged. Story 30-3 will:
1. Refactor Cranl into a v2 provider.
2. Mark v1 `[Obsolete]` and shim it to delegate to `registry.GetProvider("cranl")`.
3. Migrate the admin endpoints to call the v2 surface via the registry.

**Rationale**:
- Deviation from the plan is justified per CLAUDE.md ("the user prompt is newer + authoritative" — the user prompt explicitly forbade refactoring `CranlTenantProvisioner` as out-of-scope for this story).
- Following the plan as written would mean editing CranlTenantProvisioner's constructor + every test that mocks `IPlatformQueuedTaskRepository`, which is the very work 30-3 is sized for. Doing it here doubles the diff size and forces a rebase against 30-3.
- The cost of coexistence is small: two interfaces side-by-side until 30-3 lands. No runtime behaviour change. No data shape change.

**Defer to 30-3**: the entity columns `provider_key TEXT` and `provider_resource_ids JSONB`, the EF migration that adds them, and the backfill of existing Cranl rows. The plan put them in 30-1; the decision is to move them with the Cranl refactor in 30-3 because they are consumed only when Cranl writes to them. Until 30-3 lands, `ProviderResourceIds` lives on the `ProvisioningResult` record and travels in-memory only.

### #6 — Operating-mode behaviour: single-user wires only the null seam; SaaS layers in real backends

**Context**: CLAUDE.md §"Operating Modes" (newly authoritative as of the wave-b changes) requires every tenant-aware feature to "answer 'in single-user mode, who owns this?' AND 'in SaaS mode, who owns this?' separately."

**Decision**:
- **Single-user mode** (`tamma start` / `tamma server`): `NullTenantProvider` is the only provider in the registry. `ProvisionAsync` / `DeprovisionAsync` / `ResolveEndpointsAsync` throw `NotSupportedException`. Provisioning is genuinely never invoked in single-user mode — the sole user runs on the central / shared Postgres, owns everything, and the onboarding UI (Story 30-7) is SaaS-only. The throw is the loudest possible signal that a code path got misrouted.
- **SaaS mode** (`tamma api`): `NullTenantProvider` registers as a baseline + each real backend's DI extension (`AddCranlProvider()`, `AddHetznerProvider()`, ...) layers in their provider keyed by `ProviderKey`. Tenant principals never call provisioning directly — only platform-owner-scoped admin endpoints do (gated by `OwnerAccess` policy).
- `GetStatusAsync` on the null seam returns a stable `None` snapshot (rather than throwing) so health-check / diagnostic endpoints that enumerate every provider don't have to special-case the null seam.

**Rationale**:
- The loud-throw on `ProvisionAsync` is deliberate: in single-user mode a caller hitting the null provider has a configuration bug, not a "graceful degradation" scenario. The exception makes that visible immediately rather than silently flipping rows to "Ready" the way v1's `NullTenantProvisioner` does.
- The v1 `NullTenantProvisioner` keeps its "fake Ready" behaviour because v1 is the surface admin endpoints currently call, and breaking that breaks single-user dev. Story 30-3's refactor will reconcile the two by removing v1.

**Alternatives considered**:
- Have v2 null seam mirror v1's "fake Ready" — rejected: v2 is for SaaS-mode SaaS-style provisioning. The null seam is for type-system completeness, not for faking a backend.

## Open questions (deferred)

These were called out in the plan's §9 and remain unresolved here — every one is owned by a downstream story:

- **Cost-hint schema** (`ProviderCostHint`) — currently a minimal `(decimal UnitsPerMonth, string Currency)`. Story 30-10 will replace with a richer record (per-tier breakdown, projected monthly cost, units consumed YTD). Locked here as a record so the replacement is a non-breaking field addition.
- **`MaxTenantsPerOrg`** — nullable int per-tier. Per-tier breakdown (Cloudflare 50k, Cranl TBD) deferred to the per-provider stories (30-3..30-6).
- **Provisioning event-stream emission** — every provider call should emit a DCB event (`PROVISIONING.*`). The interface deliberately doesn't mandate this in 30-1; Story 30-2's dispatch workflow will own the emission boundary so providers stay pure.
- **Per-tenant provider override** — currently the registry resolves provider by request, not by tenant. The `tenants.provider_key` column (30-3) makes per-tenant resolution possible but the lookup helper lives in 30-3 / 30-8.

## What landed in 30-1

```
apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/
├── ITenantInfrastructureProvider.cs    (5-method interface, doc-commented)
├── ProvisioningTopology.cs              ([Flags] enum, 3 real values + None)
├── ProviderFeatures.cs                  ([Flags] enum, 4 optional features)
├── ProviderCapabilities.cs              (record + ProviderCostHint sub-record)
├── ProvisioningRequest.cs               (request record, 7 fields)
├── ProvisioningResult.cs                (result record, 4 fields)
├── ProvisioningStatusSnapshot.cs        (snapshot record, 4 fields)
├── TenantEndpoints.cs                   (DB url + engine host/url + custom domain)
├── DeprovisioningRequest.cs             (cleanup-mode + reason)
├── TenantProviderRegistry.cs            (Dictionary-backed, throws on dup/missing)
└── NullTenantProvider.cs                (always-registered seam)

apps/tamma-elsa/src/Tamma.Api/Extensions/
└── ProvisioningServiceCollectionExtensions.cs  (modified — appends v2 wiring)

apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/V2/
├── NullTenantProviderTests.cs           (6 tests — contract on the seam)
├── TenantProviderRegistryTests.cs       (8 tests — lookup, dup, blank, listing)
├── ProviderCapabilitiesTests.cs         (5 tests — bit-flags, defaults)
└── TopologyCompatibilityTests.cs        (13 tests — AC9 matrix)
```

32 new tests, 0 regressions. Full suite: 2187 passed / 0 failed / 8 skipped.

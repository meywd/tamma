# Story 30-7: Admin UI — Onboarding Backend + Topology Picker

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform admin (and later: a tenant admin on self-service plans)**,
I want a tenant-onboarding UI that lets me pick a backend (Cranl / Hetzner / Cloudflare / BYO) and topology (DatabaseOnly / DedicatedCompute / Managed) from the capability matrix, configure region and tier, and trigger provisioning with real-time progress via SSE,
so that the onboarding flow is one page, every pair of (backend, topology) works or is explicitly disabled with a reason, and the operator has a live feed of the saga from Story 30-2.

## Acceptance Criteria

1. Route `app.tamma.dev/admin/tenants/new` (platform-admin) renders a 3-step flow:
   - Step 1: Tenant basics — name, slug, owner user, plan tier, tags.
   - Step 2: Infrastructure — backend picker + topology picker + region selector + sizing. The picker is driven by `GET /api/v1/admin/providers/capabilities` (new endpoint) which aggregates each registered provider's `GetCapabilities()`. Unsupported combinations are disabled with a tooltip explaining why.
   - Step 3: Review + Provision — summary of choices; estimated cost (from `ProviderCapabilities.CostUnitsPerMonth`); "Provision" button triggers `POST /api/v1/admin/tenants`.
2. `POST /api/v1/admin/tenants` body: `{ name, slug, ownerUserId, plan, provisioning: { providerKey, topology, region, tier, extraTags } }`. Response: 202 with `{ tenantId, workflowId, statusStreamUrl }`.
3. `GET /api/v1/admin/tenants/{tenantId}/provision-stream` — Server-Sent Events streaming each `TENANT.PROVISION.*` event from Story 30-2. UI renders a step-by-step progress panel (ResolveProvider → Preflight → Reserve → Execute → Persist → RegisterSecrets → Probe → Activate) with a per-step duration + outcome.
4. On failure, UI shows the compensation log with per-step outcome + orphan resource warning if compensation failed. A "View events" button links to `/admin/audit?filter=tenant=<id>&type=TENANT.PROVISION.*`.
5. Tenant-admin variant at `dash.tamma.dev/tenants/new` (for self-service plans — gated by a feature flag `Tenants:AllowSelfServiceProvisioning = false` by default; operator opens it per-plan). Restricted to backends the plan allows (basic tier = cloudflare only; pro tier = cranl or cloudflare; enterprise = all).
6. Capability-matrix endpoint (`/api/v1/admin/providers/capabilities`) returns the same shape the picker consumes: `[{ key, label, iconUrl, supportedTopologies, regions, features, costHint }]`. Cached for 5 min server-side; invalidated on process restart only (capabilities are compile-time).
7. BYO flow has its own separate wizard with a validation-preview step between Step 2 and Step 3 that displays the AC 2 results from Story 30-6 (DB reachable? migrations applicable? engine healthy?).
8. Region + tier choices feed the provider's cost hint so the UI can display a "$X / month estimated" badge. Cost hint is a rough annotation not a commitment — documented in-UI.
9. Accessibility: keyboard-navigable picker; screen-reader reads each capability tooltip; axe clean.
10. E2E test (Playwright): onboard a tenant on each of the 4 backends through the full flow, assert the SSE feed finishes in `Ready`, assert the tenant appears in the admin tenant list with the correct provider_key.

## Technical Context

### Component layout

```
packages/dashboard/src/admin/tenants/onboarding/
  ├─ OnboardingWizard.tsx       — 3-step container
  ├─ BasicsStep.tsx
  ├─ InfrastructureStep.tsx     — backend+topology+region+tier picker
  ├─ ByoValidationStep.tsx      — only shown when backend = byo
  ├─ ReviewStep.tsx
  ├─ ProvisionProgress.tsx      — SSE renderer (shared with tenant dashboard)
  └─ CapabilityTooltip.tsx
```

### Capability rendering example

```
Backend: Cloudflare
  ✔ DatabaseOnly
  ✔ DedicatedCompute
  ✘ Managed — [tooltip: "Cloudflare requires platform-operated infra; BYO not supported"]
Regions: global
Features: Custom domains ✓, Autoscale ✓, Dedicated DB ✓, pgvector ✗
Cost hint: $0.80-$12 / tenant / month
```

### Feature flag for self-service

`Tenants:AllowSelfServiceProvisioning` per-plan-tier dictionary so
operators gate self-service by subscription level. Off by default;
opt-in per paying customer.

### Out-of-scope

- Billing integration (separate epic).
- Plan / quota enforcement beyond the provider's own capability limits
  (Story 30-10 covers the quota dashboard but not enforcement logic
  at onboarding).

## Estimated hours

24 — two wizard surfaces (admin + tenant), capability endpoint, SSE
renderer (shared), BYO validation preview, a11y pass, Playwright E2E.

## Files to touch

- `packages/dashboard/src/admin/tenants/onboarding/` (new folder)
- `packages/dashboard-user/src/tenants/onboarding/` (new, thin wrapper)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminProviderEndpoints.cs` (new — capability endpoint)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantOnboardingEndpoints.cs` (extend — POST/tenants, provision-stream SSE)

## References

- Story 30-1 interface + capability matrix
- Story 30-2 workflow events
- Story 30-3..30-6 providers
- Epic 18 dashboard shells
- Research notes §2

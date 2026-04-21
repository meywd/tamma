# Story 30-7 Implementation Plan — Onboarding Backend + Topology Picker UI

**Status**: Planned (2026-04-20)
**Story brief**: [`30-7-onboarding-ui.md`](./30-7-onboarding-ui.md)
**Epic 30 phase**: UI — after 30-2..30-6.
**Branch**: `feat/story-30-7-onboarding-ui`

---

## 1. Objective

Ship a 3-step tenant onboarding wizard at `app.tamma.dev/admin/tenants/new`
that lets platform admins pick a backend + topology + region + tier
from the capability matrix, configure the tenant, and watch
provisioning progress via SSE streamed from 30-2's workflow events.
Tenant-admin variant at `dash.tamma.dev/tenants/new` — gated by
plan tier via a feature flag. BYO gets a separate validation preview.

## 2. Dependencies

Hard blockers:

- **Stories 30-1..30-6** — all providers registered.
- **Story 18-5** — dashboard-user shell.
- **Story 28-11** — admin dashboard patterns to reuse.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/dashboard/src/admin/tenants/onboarding/OnboardingWizard.tsx` | Admin 3-step container. |
| `.../admin/tenants/onboarding/BasicsStep.tsx` | Step 1. |
| `.../admin/tenants/onboarding/InfrastructureStep.tsx` | Step 2 picker. |
| `.../admin/tenants/onboarding/ByoValidationStep.tsx` | BYO preview step. |
| `.../admin/tenants/onboarding/ReviewStep.tsx` | Step 3. |
| `.../admin/tenants/onboarding/ProvisionProgress.tsx` | SSE renderer (shared with tenant). |
| `.../admin/tenants/onboarding/CapabilityTooltip.tsx` | Tooltip component. |
| `/home/meywd/tamma/packages/dashboard-user/src/tenants/onboarding/OnboardingWizard.tsx` | Tenant variant. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminProviderEndpoints.cs` | `GET /api/v1/admin/providers/capabilities`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantOnboardingEndpoints.cs` | `POST /api/v1/admin/tenants`, `GET /api/v1/admin/tenants/:id/provision-stream`. |
| `/home/meywd/tamma/packages/dashboard/e2e/onboarding-backends.spec.ts` | Playwright for all 4 backends. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/packages/dashboard/src/router.tsx` | Add route. |
| `/home/meywd/tamma/packages/dashboard-user/src/router.tsx` | Add route (gated). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | `Tenants:AllowSelfServiceProvisioning` per-plan dict. |

## 5. Sequence of changes

### Step 1 — Capability endpoint (3h)

- `GET /api/v1/admin/providers/capabilities` aggregates each
  registered provider's `GetCapabilities()`.
- Cached in-memory 5 min (capabilities are compile-time).
- Response shape matches brief AC6.
- **Commit**: `feat(api): provider capabilities endpoint`.

### Step 2 — Onboard endpoint + SSE (4h)

- `POST /api/v1/admin/tenants` validates + enqueues 30-2 workflow.
- Response 202 with `{ tenantId, workflowId, statusStreamUrl }`.
- `GET /provision-stream` streams `TENANT.PROVISION.*` events via SSE.
- **Commit**: `feat(api): tenant onboarding endpoints + SSE`.

### Step 3 — Basics step (2h)

- Form: name, slug, owner, plan, tags.
- Slug validator + async uniqueness check.
- **Commit**: `feat(onboarding): basics step`.

### Step 4 — Infrastructure picker (4h)

- Backend grid with icon + support grid.
- Topology, region, tier selectors reactive to backend selection.
- Unsupported combos disabled with tooltip (per `CapabilityTooltip`).
- Cost hint badge.
- **Commit**: `feat(onboarding): infrastructure picker`.

### Step 5 — BYO validation step (3h)

- Conditional on `backend='byo'`.
- Form: DB URL + engine URL.
- "Validate" button → server calls `ByoValidationHarness.PreviewAsync`
  (new endpoint that runs checks without creating a tenant).
- Green/red indicators per check.
- **Commit**: `feat(onboarding): BYO validation preview`.

### Step 6 — Review + Provision (2h)

- Summary display.
- "Provision" → POST → SSE stream → show progress.
- **Commit**: `feat(onboarding): review + provision flow`.

### Step 7 — Provision progress (3h)

- SSE renderer: step indicator, per-step duration, error details.
- "View events" link to audit.
- **Commit**: `feat(onboarding): progress panel`.

### Step 8 — Tenant variant (2h)

- Thin wrapper for `dash.tamma.dev/tenants/new`.
- Filter capabilities by plan.
- **Commit**: `feat(onboarding): tenant self-service variant`.

### Step 9 — E2E + a11y (3h)

- Playwright: 4 backends end-to-end (fakes for cloud APIs).
- axe-clean on every step.
- **Commit**: `test(onboarding): E2E per backend`.

## 6. Test strategy

### Unit (Vitest)

- Capability matrix rendering (all compatible vs. unsupported).
- Form validation for each step.
- SSE event parsing.

### Integration

- Capability endpoint returns the right shape.
- SSE streams expected events.

### E2E

- Per brief AC10.
- Accessibility per AC9.

## 7. Rollback plan

- **Feature flag**: `AdminUI:Onboarding=true` gates the new flow.
  Fallback to existing admin tenant-create flow if disabled.
- **Tenant self-service flag**: `Tenants:AllowSelfServiceProvisioning`
  per plan — disabled by default. Safe.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Capability endpoint | 3 |
| 2. Onboard + SSE | 4 |
| 3. Basics | 2 |
| 4. Infrastructure picker | 4 |
| 5. BYO validation | 3 |
| 6. Review + provision | 2 |
| 7. Progress | 3 |
| 8. Tenant variant | 2 |
| 9. E2E + a11y | 3 |
| **Total** | **26** (brief 24). |

## 9. Open questions

- **Cost hint display granularity**: integer USD or "~$5"? Plan:
  locale-formatted currency range per `CostHint`.
- **Per-plan capability filter**: plan tiers map to allowed backends.
  Plan: starter=cloudflare; pro=cranl|cloudflare; enterprise=all.
  Configurable via env var.
- **SSE reconnect**: UI reconnects on drop with last-event-id. Fall
  back to polling every 2s if SSE blocked.
- **Tenant self-service security**: feature flag gates; tenant admin
  can only create under their org.
- **Onboarding abandonment**: if user closes tab mid-provision, the
  workflow continues. Next login surfaces progress.

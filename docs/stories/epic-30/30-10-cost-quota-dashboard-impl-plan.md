# Story 30-10 Implementation Plan — Cost + Quota Dashboard

**Status**: Planned (2026-04-20)
**Story brief**: [`30-10-cost-quota-dashboard.md`](./30-10-cost-quota-dashboard.md)
**Epic 30 phase**: Observability — Epic 30 closeout.
**Branch**: `feat/story-30-10-cost-quota-dashboard`

---

## 1. Objective

Ship `app.tamma.dev/admin/infrastructure` — per-tenant cost + quota
dashboard across all backends. One pane of glass over Cranl /
Hetzner / Cloudflare / BYO footprints. Alerts on cost / event /
workflow / health thresholds. Tenant-admin variant at
`dash.tamma.dev/infrastructure`. CSV export for vendor-invoice
reconciliation. Closes Epic 30.

## 2. Dependencies

Hard blockers:

- **Stories 30-1..30-9** — providers + routing + deprovision for full
  observability.
- **Story 28-10** — `platform_analytics_hourly` rollup.
- **Epic 1.5-37** — notification channels.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260601000000_AnalyticsCostColumns.cs` | Extend `platform_analytics_hourly` with per-backend cost fact columns. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminInfrastructureEndpoints.cs` | List, drill-in, CSV export, comparison estimator, alert config. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Analytics/CostEstimator.cs` | Per-backend cost formulae. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Analytics/QuotaAlertTrigger.cs` | Alert firing logic. |
| `/home/meywd/tamma/packages/dashboard/src/admin/infrastructure/InfrastructureDashboardPage.tsx` | Admin UI. |
| `.../admin/infrastructure/TenantDrawer.tsx` | Drill-in. |
| `.../admin/infrastructure/BackendComparison.tsx` | Estimator. |
| `/home/meywd/tamma/packages/dashboard-user/src/infrastructure/UserInfrastructurePage.tsx` | Tenant variant. |
| `/home/meywd/tamma/packages/dashboard/e2e/infrastructure-dashboard.spec.ts` | E2E. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/AnalyticsRollupWorkflow.cs` (from 28-10) | Emit cost rows per tenant. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ProviderCapabilities.cs` (from 30-1) | Add structured `CostHint` record. |
| `/home/meywd/tamma/packages/dashboard/src/router.tsx` | Add `/admin/infrastructure`. |
| `/home/meywd/tamma/packages/dashboard-user/src/router.tsx` | Add `/infrastructure`. |

## 5. Sequence of changes

### Step 1 — CostHint + provider-level declaration (2h)

- Expand `CostHint` record per brief:
  `{ BaseMonthly, PerGbStorage, PerMillionApiCalls, PerGbBandwidth }`.
- Each provider declares its hint (Cranl ~$5/tenant, Hetzner ~$4.50
  cx22, Cloudflare ~$0.80 base + usage, BYO = 0).
- **Commit**: `feat(provisioning): CostHint record + declarations`.

### Step 2 — Analytics schema extension (2h)

- Migration adds columns: `cost_base_usd`, `cost_storage_usd`,
  `cost_api_calls_usd`, `cost_total_usd` to `platform_analytics_hourly`.
- Keyed by `(HourBucket, TenantId, ProviderKey)`.
- **Commit**: `migration(analytics): cost fact columns`.

### Step 3 — Rollup extension (3h)

- `AnalyticsRollupWorkflow` computes cost per hour per tenant:
  `base/720 × hours_in_month` + usage × rate.
- Writes to new columns.
- **Commit**: `feat(analytics): per-backend cost rollup`.

### Step 4 — Endpoints (4h)

- `GET /admin/infrastructure` — aggregated per-backend view.
- `GET /admin/infrastructure/tenants/:id` — drill-in.
- `GET /admin/infrastructure/export?from=&to=` — CSV streaming.
- `POST /admin/infrastructure/compare` body
  `{ fromProvider, toProvider, tenantId }` → delta estimate.
- RBAC: platform-admin.
- **Commit**: `feat(api): infrastructure endpoints`.

### Step 5 — Alert trigger (3h)

- `QuotaAlertTrigger` runs on rollup completion:
  - Check cost / event / workflow thresholds per plan tier.
  - Check health-probe failure rate > 5% / 24h.
  - Emit `TENANT.QUOTA.ALERT` event + notification channel call.
- **Commit**: `feat(analytics): quota alerts`.

### Step 6 — Admin UI (5h)

- Header cards + per-backend sections.
- Tenant table with filters.
- Drill-in drawer with resources + cost timeline + quota bars.
- Link to vendor-native dashboard (deep link via vendor URLs).
- **Commit**: `feat(ui): infrastructure admin dashboard`.

### Step 7 — Backend comparison + CSV (2h)

- Comparison estimator renders estimated cost delta.
- CSV export triggers a download stream.
- **Commit**: `feat(ui): comparison + CSV export`.

### Step 8 — Tenant variant (2h)

- `dash.tamma.dev/infrastructure` — own tenant only.
- Consumption bar chart vs. plan limits.
- **Commit**: `feat(ui): tenant infrastructure view`.

### Step 9 — E2E + a11y (3h)

- Playwright: seed 3 tenants × 3 backends; verify view + drill-in + alerts.
- axe-clean.
- **Commit**: `test(ui): infrastructure dashboard E2E + a11y`.

## 6. Test strategy

### Unit

- Cost estimator math per provider.
- Alert trigger threshold logic.

### Integration

- Seeded tenants + rollup events → dashboard values match.
- CSV export content matches in-memory dataset.

### E2E

- Per brief AC9.
- Tenant variant RBAC.

## 7. Rollback plan

- **Feature flag**: `AdminUI:Infrastructure=true`.
- **Non-reversible**: analytics rows accumulate; purge via 28-10's
  retention.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. CostHint | 2 |
| 2. Schema extension | 2 |
| 3. Rollup extension | 3 |
| 4. Endpoints | 4 |
| 5. Alert trigger | 3 |
| 6. Admin UI | 5 |
| 7. Comparison + CSV | 2 |
| 8. Tenant variant | 2 |
| 9. E2E + a11y | 3 |
| **Total** | **26** (brief 22). |

## 9. Open questions

- **Cost hints vs. reality**: estimates are annotations, not
  commitments. UI labels "Estimated — verify with provider invoice".
- **Cloudflare Workers cost is usage-based**: requires per-request
  metric. Covered by `platform_events API.REQUEST.*` counts.
- **BYO cost = $0 from Tamma's perspective**: customer pays their
  own. Display $0 + note "customer-operated".
- **CSV export RBAC**: platform-admin only. Tenant variant has no
  export.
- **Comparison estimator accuracy**: rough — doesn't account for
  migration cost. UI labels "move estimate only".
- **Alert channel integration**: via Epic 1.5-37 notification ports.
  If not shipped, fall back to email.
- **Plan limits source**: `plans` table (from 28-1). Extend columns
  if needed.

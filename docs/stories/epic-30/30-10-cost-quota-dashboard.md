# Story 30-10: Cost + Quota Dashboard Per Tenant

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform administrator**,
I want a per-tenant cost + quota dashboard that shows which backend each tenant runs on, an estimated monthly spend (from per-provider cost hints + observed usage), quota consumption against plan limits, and alerts for abnormal spikes,
so that running multiple backends doesn't mean I need to juggle four vendor dashboards — Tamma is the one pane of glass over every tenant's infra footprint.

## Acceptance Criteria

1. Route `app.tamma.dev/admin/infrastructure` renders an aggregated view across all tenants:
   - **Header cards**: total tenants per backend, total monthly spend estimate, quota-over-threshold count.
   - **Per-backend breakdown**: one section per registered provider showing tenants on that backend with their individual cost estimate, provisioned date, last-health-check, topology.
   - **Per-tenant drill-in**: clicking a tenant row opens a drawer with a breakdown of resources (e.g. Hetzner → "cx42 Nuremberg, 40GB SSD, running 32d"), cost timeline (30-day + projected month-end), quota consumption (events stored, workflows run, API calls), and a link to the provider's native dashboard.
2. Cost estimate is a **hint**, not a commitment. Computed as `ProviderCapabilities.CostUnitsPerMonth` × `tier multiplier` × `days-in-month-so-far ÷ 30`. For Cloudflare (pay-per-use), add observed API call count × per-call cost. Labeled "Estimated — verify with provider invoice".
3. Quota metrics collected from:
   - `domain_events` rowcount per tenant (from a materialised view rebuilt hourly via a platform workflow — reuses Story 28-10's analytics rollup).
   - `workflow_instances` completion count per tenant per day.
   - API call count per tenant per day (from `platform_events` of type `API.REQUEST.*`).
4. Alerts fire when any of the following cross a threshold:
   - Cost estimate > plan-tier limit.
   - Event store rowcount > plan-tier limit.
   - Workflow run count > plan-tier limit.
   - Health probe failure rate > 5% over 24h.
   Alerts emit `TENANT.QUOTA.ALERT` platform events + (optional) email to tenant owner + webhook to operator's preferred channel.
5. Per-tenant view for tenant admins at `dash.tamma.dev/infrastructure` shows only their tenant, with the same breakdown + a "consumption vs plan limit" bar chart.
6. Backend comparison view (admin only): side-by-side "move this tenant to X backend" estimator that computes the cost difference if a tenant moved from Cranl → Cloudflare (for example). Does not actually migrate — just shows the delta.
7. Export: CSV of tenant × backend × cost × quota for any date range. Used by ops to reconcile with vendor invoices.
8. RBAC: platform-admin sees all; tenant-admin sees only their own tenant; tenant-member is 403'd.
9. E2E test (Playwright): seed 3 tenants across 3 backends; view the dashboard; assert the per-backend totals match expectations; drill into one tenant; assert the alert fires when the seeded event count exceeds the plan limit.
10. Closes Epic 30 — the operator's tooling is complete across provisioning, routing, and ongoing visibility.

## Technical Context

### Cost-hint schema

`ProviderCapabilities.CostUnitsPerMonth`:

```csharp
public record CostHint(
    decimal BaseMonthly,       // fixed per-tenant e.g. $4.50 for cx22
    decimal PerGbStorage,       // 0 for Cranl, 0.05 for Hetzner
    decimal PerMillionApiCalls, // 0.15 for Cloudflare Workers
    decimal PerGbBandwidth      // 0 / included
);
```

UI renders the "currently provisioned" cost + adds observed usage
from the metrics view.

### Observability

Reuses Story 28-10's `platform_analytics_hourly` rollup. This story
extends the rollup schema with per-backend cost columns rather than
introducing a parallel analytics pipeline.

### Alerts channel

Integrates with the existing notification infrastructure from Epic
1.5-37 (notification channels for Slack / email / PagerDuty /
webhook). No new channel logic here — just uses the existing port to
dispatch `TENANT.QUOTA.ALERT` events.

### Plan limits

Plan tiers (starter / pro / enterprise) define quota ceilings. Stored
in `plans` table (extend Epic 18's plan model if needed). Enforcement
is outside this epic — this story only surfaces consumption visibility.

## Estimated hours

22 — aggregation view + drill-in + alerts integration + tenant-admin
variant + CSV export + comparison estimator + Playwright E2E.

## Files to touch

- `packages/dashboard/src/admin/infrastructure/` (new folder)
- `packages/dashboard-user/src/infrastructure/` (new folder, thin variant)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminInfrastructureEndpoints.cs` (new)
- Schema extension to `platform_analytics_hourly` (migration)

## References

- Story 28-10 analytics rollup
- Epic 1.5-37 notification channels
- Story 30-1 capability matrix (source of cost hints)
- Story 30-8 routing resolver (source of per-tenant backend lookup)

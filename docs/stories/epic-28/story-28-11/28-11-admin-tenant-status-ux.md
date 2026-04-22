# Story 28.11: Admin UX for `tenants.Status` State Machine

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Operations
**Status**: Draft
**Priority**: Medium (without this UX, platform admins investigate
stuck tenants via `psql` and Elsa Studio — workable but slow; this
is the first-class observability surface for the workflow-driven
state machine Story 28-5 ships)
**Estimated Effort**: L (22h)

## User Story

As a **platform operations engineer**, I want **an admin dashboard
section that lists every tenant with its current `tenants.Status`,
offers a detail view with a live workflow step ladder during
provisioning and deletion, surfaces the recent `platform_events`
timeline, and gates destructive actions (retry / delete /
force-delete / impersonate) by the state machine**, so that **I can
investigate and resolve stuck or failed tenants without SSHing into
the DB, and platform admin actions are predictable, auditable, and
typo-proof**.

## Acceptance Criteria

### AC1: `GET /api/v1/admin/tenants` list endpoint

- [ ] New endpoint under `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/
      AdminTenantsEndpoints.cs`, gated by the `PlatformAdmin` policy
      from Story 28-9 AC5.
- [ ] Query params:
  - `status` — filter by `tenants.Status` (CSV-list, e.g.
    `?status=provisioning,failed`).
  - `plan` — filter by `tenants.Plan`.
  - `search` — case-insensitive `ILIKE %...%` on `tenants.Slug` OR
    `tenants.Name` OR owning user's email (the latter requires a
    JOIN to `users`).
  - `createdAfter`, `createdBefore` — ISO-8601 date bounds on
    `tenants.CreatedAt`.
  - `page` (1-indexed) + `pageSize` (default 50, max 200).
- [ ] Response body: `{ items: [...], total: N, page, pageSize }`.
      Each item is `{ id, slug, name, status, plan, createdAt,
      provisionedAt, deleteRequestedAt, ownerEmail,
      requiresManualCleanup, latestEventType, latestEventAt }` —
      shaped for the list view; detail fields fetched in AC2.
- [ ] Pagination uses keyset pagination internally (`CreatedAt DESC,
      Id DESC`) — offset pagination on a multi-tenant table with
      occasional inserts is fine at the current scale but the keyset
      path is ready for growth.
- [ ] p95 response budget: 200ms for a 50-row page at 10k tenants.
      Verified against a seeded benchmark DB.

### AC2: `GET /api/v1/admin/tenants/{id}` detail endpoint

- [ ] Returns a richer DTO:
      `{ ...list-item..., failureReason, lastError, currentStep,
      correlationId, memberships: [...], recentEvents: [...10 most
      recent from platform_events filtered by tenant],
      resourceSummary: {...from platform_analytics_hourly last
      24h...} }`.
- [ ] `resourceSummary` keys: `workflowsLast24h`, `llmCostLast24h`,
      `apiRequestsLast24h`, `errorsLast24h`. Pulled from
      `platform_analytics_hourly` (Story 28-10 AC2 metrics) via the
      shared `AnalyticsBucketDto` from 28-10.
- [ ] For tenants in `provisioning` or `dropping`, the response
      includes a `stepLadder` field shaped like Doc 03 §6.3 — the
      response from `/api/v1/tenants/{id}/provisioning-status`
      (Story 28-5 AC6) is inlined here so the admin view has one
      round trip.
- [ ] 404 if no such tenant (including permanently purged
      `deleted` tenants that have been retained per Doc 04 §6.4 —
      return the `deleted` row, not 404, so the audit trail is
      visible).

### AC3: `GET /api/v1/admin/tenants/{id}/events/stream` SSE

- [ ] Server-Sent Events endpoint emitting every new
      `platform_events` row where `tags.tenantId = <id>` in
      real time. Used by the detail page for live workflow
      progress.
- [ ] Stream format: `event: platform_event\ndata:
      {...event-dto...}\n\n` per the existing Tamma SSE
      conventions.
- [ ] Initial payload: the 10 most recent events (same as AC2
      `recentEvents`) so the client can render a complete ladder
      without also hitting AC2.
- [ ] Heartbeat: `event: ping\ndata: {}\n\n` every 15s so
      intermediate proxies don't drop the connection.
- [ ] **SSE fallback**: if the client cannot open the stream
      (proxy incompatibility), the dashboard falls back to
      polling AC2 every 2 seconds. A query param
      `?fallback=poll` lets the client explicitly request polling
      mode.
- [ ] Per Story 28-8, the SSE handler subscribes to the
      `tenant.deleted` signal and terminates the stream with a
      final `event: tenant_deleted` when a tenant's
      `DeleteTenantWorkflow` reaches Step F. The dashboard handles
      the close gracefully (shows "this tenant no longer exists").

### AC4: Admin tenants list page

Build in the existing React dashboard at `packages/dashboard/`
(Vite + React 18 + Tailwind + TypeScript + Vitest). The admin area
already lives at `packages/dashboard/src/pages/admin/` as a tabbed
layout (`AdminLayout.tsx` wraps `UsersTab`, `HealthTab`,
`AuditLogTab`, `ApiKeysTab`, `QuickLinksTab`) — this story adds a
new **Tenants tab** alongside them plus a full-page tenant detail
route.

**Framework note**: The Blazor `Tamma.Studio` project under
`apps/tamma-elsa/src/Tamma.Studio/` remains the internal /
developer-facing studio for Elsa workflow design and is **not**
where admin tenant UX lives. End-user and platform-admin UI is
the React dashboard; this story follows that convention.

- [ ] New component `packages/dashboard/src/pages/admin/TenantsTab.tsx`
      mounted inside `AdminLayout` at `/admin/tenants`, gated by the
      same `PlatformAdmin` role check the other admin tabs use
      (via the `useCurrentUser()` hook — reject with "Not
      authorized" view if the user lacks the platform-admin
      claim).
- [ ] Table view with sortable columns: `Slug`, `Name`, `Status`
      (badge), `Plan`, `Owner`, `CreatedAt`, `Provisioned /
      Requested` (relative time), `Actions`. Table markup uses the
      existing common patterns from `UsersTab.tsx` (semantic
      `<table>`, role-based cell rendering, relative-time
      formatter).
- [ ] Filters above the table: status multi-select, plan
      multi-select, search input (debounced 300ms via
      `useDebounce` hook), date range pickers. Filter state is
      bound to URL query params via `useSearchParams` from
      `react-router-dom` v7 so a page reload preserves the view
      and admins can share links.
- [ ] Pagination controls at the bottom. Page size picker (10 / 50
      / 100 / 200). Page number is also in the URL query string.
- [ ] Each row has a click-through to the detail page (AC5) via
      `<Link>` and a trailing action-menu button
      (`components/common/DropdownMenu.tsx` if it exists, else a
      small new one) with state-machine-gated actions (AC6).
- [ ] Data fetching: new hook
      `packages/dashboard/src/hooks/admin/useTenants.ts` wrapping
      the AC1 endpoint, modelled on the existing `useUsers` hook.
      Supports the filter/page query-param inputs and returns
      `{ data, isLoading, error, refetch }`.

### AC5: Admin tenant detail page

- [ ] Page `packages/dashboard/src/pages/admin/TenantDetailPage.tsx`
      at route `/admin/tenants/:id` (full page outside the tabbed
      `AdminLayout`, with its own back-to-list breadcrumb).
- [ ] Layout, top to bottom:
  1. **Header** — slug + name + status badge + owner email.
  2. **Actions bar** — state-machine-gated buttons per AC6.
  3. **Workflow step ladder** — visible only when status is
     `provisioning`, `dropping`, or `failed`. Pulled from AC2's
     `stepLadder`. Live-updates via the AC3 SSE stream. Each step
     shows status (pending / in-progress / completed / failed),
     attempts count, duration. Matches the shape of Doc 03 §6.3.
  4. **Recent events panel** — paginated, 10 per page, filterable
     by event-type prefix. Rendered from AC2 + live-appended from
     AC3 SSE.
  5. **Resource summary** — "Workflows executed (24h)", "LLM
     cost (24h)", "API requests (24h)", "5xx errors (24h)" cards.
     Pulled from AC2's `resourceSummary`.
  6. **Memberships** — table of `tenant_memberships` with role,
     invited-by, joined-at.
- [ ] Live updates are unobtrusive: new events slide in at the top
      of the events panel with a 200ms highlight animation. Step
      ladder transitions between states with a CSS fade. The page
      never auto-scrolls.

### AC6: State-machine-gated destructive actions

All actions are visible only when the tenant's `Status` makes them
valid. Each action requires a confirmation dialog.

- [ ] **Retry provisioning** — visible only when `Status='failed'`.
      `POST /api/v1/admin/tenants/{id}/reprovision` (new endpoint
      in this story; signals `CreateTenantWorkflow` correlation).
      Confirmation: "Retry provisioning for tenant <slug>?" with a
      "Type the slug to confirm" field.
- [ ] **Delete tenant** — visible when `Status ∈ {active, failed}`.
      `DELETE /api/v1/admin/tenants/{id}` (Story 28-5 AC4). The
      confirmation dialog requires the admin to **type the tenant
      slug** to enable the submit button — no typo-delete is
      possible. Body includes a `reason` field (required).
- [ ] **Cancel deletion** — visible only during the 5-minute
      cooling-off window (`Status='deleting'` and
      `DeleteRequestedAt < now() - 5min`). `POST
      /api/v1/admin/tenants/{id}/cancel-delete`.
- [ ] **Force-delete** — visible only when `Status='deleting'` or
      `Status='dropping'` AND `DeleteRequestedAt < now() - 10min`
      (i.e. the workflow has been stuck for > 10 minutes). `POST
      /api/v1/admin/tenants/{id}/force-delete` (new endpoint), body
      requires both the slug and a `reason` explaining why the
      normal workflow is being bypassed. Surfaces `DROP DATABASE
      ... WITH (FORCE)` directly per Doc 04 §10.3. Writes
      `PLATFORM_ADMIN.FORCE_DELETE_INVOKED` to `platform_events`
      with the reason text.
- [ ] **Impersonate (switch into tenant view)** — visible when
      `Status='active'`. Calls `POST /api/admin/impersonate/{id}`
      (Story 28-8 AC5), stashes the impersonation id in
      session storage, sets `X-Impersonate-Tenant-Id` on subsequent
      tenant-scoped requests. Ends via a persistent "End
      impersonation" banner.
- [ ] **Clean up failed tenant (manual-cleanup workflow)** — visible
      when `Status='failed' AND RequiresManualCleanup=true`.
      Triggers `POST /api/v1/admin/tenants/{id}/cleanup` (Story 28-5
      AC7).

### AC7: Accessibility + resilience

- [ ] **WCAG AA** colour contrast on all status badges. Validated
      via `jest-axe` (add to `packages/dashboard/package.json`
      devDeps if not already present) asserting zero axe-core
      violations on rendered pages in Vitest + React Testing
      Library. Status badges carry a text label alongside the
      color (not color-only).
- [ ] Keyboard navigation: every action button reachable with Tab;
      confirmation dialogs trap focus per ARIA guidelines.
- [ ] Screen-reader: each row announces "Tenant <slug>, status
      <status>, owner <email>". Live event-panel updates use
      `aria-live="polite"` so a screen reader announces each new
      event without interrupting the admin's current focus.
- [ ] SSE fallback (AC3 last bullet) covers the case where the
      admin's browser or corporate proxy blocks SSE.
- [ ] On network failure, the list view surfaces a "retry" banner
      rather than a blank page. The detail view preserves the
      last-loaded state and shows a stale-indicator.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §10 (tenant
    deletion state machine — `active → delete_requested → deleting
    → deleted` plus the cancellation branch) — this story's
    action-gating mirrors this diagram.
  - `plans/db-per-tenant/03-async-tenant-provisioning.md` §6.3
    (provisioning-status API contract — the stepLadder DTO shape
    reused in AC2 / AC5 panel 3) and §6.4 (status projection fold
    rules).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §8.1
    (full state-machine → HTTP table — the action-gating from AC6
    mirrors the admin-side of this table) and §10.2 (startup
    stuck-tenant scan — the "force-delete after 10 min stuck" in
    AC6 is the admin-initiated counterpart of the automated alert
    in that section).
  - Epic 28 README conflict resolution #1 — `pending_verification`
    is displayed as its own status badge on the list (it's a real
    state users can be stuck in if they never click verify-email).
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs`
    — new; hosts all admin tenant endpoints listed in AC1–6.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsSseEndpoint.cs`
    — new; SSE handler for AC3.
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Admin/AdminTenantDtos.cs` —
    modified; new `AdminTenantListItemDto`, `AdminTenantDetailDto`,
    `ForceDeleteRequestDto`, `ReprovisionRequestDto`.
  - `packages/dashboard/src/pages/admin/TenantsTab.tsx` — new;
    list view mounted inside `AdminLayout` at `/admin/tenants`.
  - `packages/dashboard/src/pages/admin/TenantDetailPage.tsx` —
    new; full-page detail route at `/admin/tenants/:id`.
  - `packages/dashboard/src/components/admin/TenantStatusBadge.tsx`
    — new; WCAG-AA badge component reused on both pages.
  - `packages/dashboard/src/components/admin/ConfirmBySlugDialog.tsx`
    — new; the "type the slug" confirmation modal (extends
    existing `ConfirmDialog` pattern from `components/common/`).
  - `packages/dashboard/src/components/admin/StepLadder.tsx` —
    new; reusable workflow-step-ladder component (shared with
    Story 28-5's user-facing status page if ever promoted).
  - `packages/dashboard/src/hooks/admin/useTenants.ts` — new;
    list-query hook modelled on `useUsers.ts`.
  - `packages/dashboard/src/hooks/admin/useTenant.ts` — new;
    detail-query hook with SSE subscription + polling fallback
    (AC3).
  - `packages/dashboard/src/services/admin/admin-tenants-api-client.ts`
    — new; typed fetch wrapper around the AC1–6 endpoints,
    sibling of the existing `admin-api-client.ts`.
  - `packages/dashboard/src/router.tsx` — modified; add
    `/admin/tenants/:id` route entry.

## Dependencies

- **Blocks**: none — this is the terminal story in Stream C.
- **Blocked by**: 28-5 (workflow emits the events this UX renders),
  28-6 (`platform_events` table backs the AC2 `recentEvents` and
  AC3 SSE), 28-9 (the `PlatformAdmin` policy + impersonation), 28-10
  (`platform_analytics_hourly` powers the AC5 resource summary).
- **External**: existing React dashboard stack at
  `packages/dashboard/` (Vite + React 18 + Tailwind + TypeScript +
  Vitest + React Testing Library), the existing SSE conventions
  in the codebase, `jest-axe` for accessibility validation (add
  to dashboard devDeps if not already present).

## Test Plan

### Unit tests

- `AdminTenantsEndpointsTests` (using `WebApplicationFactory`):
  - AC1 filters: each query param independently + combined.
  - AC1 pagination: 100 seeded tenants, page 2 of 50 returns rows
    51–100, `total=100`.
  - AC2 tenant detail: seed events, assert `stepLadder`
    reflects the fold rules from Doc 03 §6.4.
  - AC6 action gating: for each `Status` value, assert the set of
    actions returned in the detail payload matches the table in
    AC6.
- `admin-tenants-api-client.test.ts` (Vitest + `msw` or fetch
  mock): typed client serialisation + error-mapping round-trips.
- `ConfirmBySlugDialog.test.tsx` (Vitest + React Testing Library
  + user-event): submit button disabled until typed slug matches
  exactly (case-sensitive); whitespace is rejected; Escape
  dismisses.
- `StepLadder.test.tsx`: given a fold-rule trace from Doc 03
  §6.4 as props, renders the expected step states and
  transitions between them when props update.
- `TenantStatusBadge.test.tsx`: every status value renders with
  the expected Tailwind color class + text label.
- `useTenants.test.ts`: hook correctly encodes filter params,
  handles loading/error/success states, `refetch` works.

### Integration tests (Testcontainers.PostgreSQL + RabbitMQ)

- **T1 Happy-path provisioning view**: start a tenant in
  `provisioning` → open detail page → SSE streams the step events
  → page updates live → tenant flips to `active` → "Impersonate"
  action appears.
- **T2 Failed tenant + retry**: simulate a `TENANT.PROVISION.FAILED`
  event → detail page shows the failed step + `failureReason` →
  click Retry → POST fires → new events stream in.
- **T3 Delete flow with grace window**: click Delete → confirm by
  typing slug → `Status='deleting'` → Cancel button visible for
  5 minutes → click Cancel → tenant returns to `active` → event
  log shows `TENANT.DELETE_CANCELLED`.
- **T4 Force-delete stuck tenant**: flip tenant to `deleting`,
  manually set `DeleteRequestedAt = NOW() - 11 min` → Force-delete
  button appears → requires slug + reason → `DROP DATABASE WITH
  FORCE` fires via Story 28-5 endpoint.
- **T5 SSE termination on tenant deletion**: open SSE stream for
  tenant X → delete tenant X → stream receives final
  `tenant_deleted` event and closes cleanly.
- **T6 Impersonation flow**: click Impersonate on active tenant →
  session storage has impersonation id → navigate to `/issues` →
  tenant-scoped data loads from the target tenant → click "End
  impersonation" banner → returns to admin view.
- **T7 PlatformAdmin gating**: non-admin user hits
  `/admin/tenants` → 403. Admin hits → 200.
- **T8 Accessibility snapshot**: `jest-axe` runs on each page
  variant (empty list, populated list, provisioning detail,
  failed detail) rendered via React Testing Library → zero
  violations at WCAG AA level.
- **T9 Fallback polling**: open detail page with
  `?fallback=poll` → AC2 is called every 2s → no SSE request
  is made.

### Manual verification

- Local dev: seed 20 tenants across every status value, open
  `/admin/tenants`, filter by each status, page through, click
  through to detail. Trigger each action against a seeded
  `failed` tenant. Verify the confirm-by-slug dialog rejects
  typos.
- Use `axe DevTools` browser extension on both pages — zero
  violations at AA level.

## Definition of Done

- [ ] AC all green
- [ ] Unit + integration tests added, suite passes
- [ ] Accessibility audit passes (T8) with zero WCAG-AA
      violations
- [ ] No new CodeQL alerts (confirm the SSE endpoint handles
      client disconnects without leaking connections)
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream), including
      one review pass by a UX-minded reviewer for the confirm-
      by-slug dialog wording

## Risks / Open Questions

- **Dashboard location resolution.** The Epic 28 README /
  sequencing plan drafts occasionally referenced a hypothetical
  `apps/tamma-dashboard/`. The **canonical React dashboard lives
  at `packages/dashboard/`** (monorepo package, not an
  app-directory project). This story targets that path. The
  Blazor `apps/tamma-elsa/src/Tamma.Studio/` project is kept for
  Elsa workflow design / internal developer tooling only and is
  not extended here. API surface in AC1–3 is framework-agnostic
  — if the frontend ever moves again, the backend endpoints are
  reusable unchanged.
- **SSE scale at 100+ admins monitoring different tenants.** Each
  SSE connection holds a DB listener (the CP listens on `LISTEN
  platform_events` and fans out to subscribers). At 100 admins
  with 10 open detail pages each, that's 1000 in-process
  subscribers — well within in-process bus capacity but worth
  monitoring. Metric `tamma_admin_sse_subscribers_gauge`.
- **Force-delete is a privileged escape hatch.** It bypasses the
  normal workflow's retry ladder and runs `DROP DATABASE WITH
  FORCE` directly. The 10-minute-stuck precondition prevents
  accidental use, and the audit event (`PLATFORM_ADMIN
  .FORCE_DELETE_INVOKED`) is a compliance artefact. Consider
  requiring two platform admin approvals before enabling in
  production — deferred to a follow-up if the first production
  use shows one admin is insufficient.

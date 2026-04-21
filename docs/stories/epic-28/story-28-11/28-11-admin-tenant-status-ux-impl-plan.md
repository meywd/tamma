# Story 28-11 Implementation Plan — Admin UX for `tenants.Status`

**Status**: Planned (2026-04-20)
**Story brief**: [`28-11-admin-tenant-status-ux.md`](./28-11-admin-tenant-status-ux.md)
**Epic 28 phase**: Ops stream (parallel with Phase D)
**Branch**: `feat/story-28-11-admin-tenant-status-ux`

---

## 1. Objective

Ship the admin-dashboard section that lists every tenant with its
`tenants.Status`, offers a detail view with a live workflow step
ladder during provisioning/deletion, surfaces the recent
`platform_events` timeline, and gates destructive actions (retry /
delete / force-delete / impersonate) by the state machine. Removes
the current SSH-into-psql workflow for stuck-tenant investigation.

## 2. Dependencies

Hard blockers:

- **Story 28-5** — provisioning workflow to observe.
- **Story 28-6** — `platform_events` timeline.
- **Story 28-9** — `PlatformAdmin` policy.
- **Story 28-10** — analytics for the detail view.
- Dashboard package exists (admin dashboard at `app.tamma.dev`).

## 3. Files to create

### API (C#)

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` | List + filter + detail + actions. |
| `.../Endpoints/Admin/AdminTenantActionsEndpoints.cs` | POST retry / force-delete / impersonate. |
| `.../Services/Admin/WorkflowLadderService.cs` | Parses Elsa workflow state → step ladder for a tenant. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Admin/AdminTenantsTests.cs` | Listing + filters + pagination. |

### UI (React)

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/dashboard/src/pages/admin/tenants/TenantsListPage.tsx` | List with filters. |
| `.../admin/tenants/TenantDetailPage.tsx` | Detail view. |
| `.../admin/tenants/components/StateLadder.tsx` | Live step-ladder during provisioning. |
| `.../admin/tenants/components/EventsTimeline.tsx` | `platform_events` feed with SSE. |
| `.../admin/tenants/components/DestructiveActions.tsx` | Retry / delete / force-delete / impersonate buttons with state gating. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/SseEndpoints.cs` | Add `GET /admin/tenants/:tenantId/events/stream`. |
| `/home/meywd/tamma/packages/dashboard/src/router.tsx` | Add admin tenant routes. |
| `/home/meywd/tamma/packages/dashboard/src/api/adminClient.ts` | Add tenant endpoints. |

## 5. Sequence of changes

### Step 1 — List endpoint (3h)

- `GET /api/v1/admin/tenants` with filters (status, plan, search,
  date range, page, pageSize).
- JOIN `users` for owner email search.
- RBAC: `PlatformAdmin`.
- Unit + integration tests.
- **Commit**: `feat(admin): tenants list endpoint`.

### Step 2 — Detail endpoint (3h)

- `GET /api/v1/admin/tenants/:id` returns tenant row + recent
  `platform_events` (last 100) + current workflow state (if
  provisioning/deleting) + analytics snapshot.
- **Commit**: `feat(admin): tenant detail endpoint`.

### Step 3 — Action endpoints (4h)

- `POST /admin/tenants/:id/actions/retry` — re-dispatches
  `CreateTenantWorkflow` (only if `Status='failed'`).
- `POST /admin/tenants/:id/actions/delete` — enqueues
  `DeleteTenantWorkflow` (only if `Status='active'`).
- `POST /admin/tenants/:id/actions/force-delete` — same but for
  stuck states; requires 2-factor confirmation header.
- `POST /admin/tenants/:id/actions/impersonate` — issues a
  short-lived impersonation JWT; emits audit event.
- Each action gated by state machine; 409 if illegal.
- **Commit**: `feat(admin): tenant action endpoints with state gate`.

### Step 4 — SSE stream for events (3h)

- `GET /admin/tenants/:id/events/stream` tails `platform_events`
  for the tenant via a Postgres LISTEN/NOTIFY channel.
- Falls back to polling if LISTEN unavailable.
- **Commit**: `feat(admin): tenant events SSE stream`.

### Step 5 — UI list + detail (5h)

- `TenantsListPage` with URL-synced filters, sortable table,
  pagination.
- `TenantDetailPage` composes the components.
- **Commit**: `feat(admin-ui): tenants list + detail`.

### Step 6 — Live components (4h)

- `StateLadder` renders workflow step progress from detail API +
  SSE.
- `EventsTimeline` streams from SSE, renders filterable.
- **Commit**: `feat(admin-ui): live state ladder + events timeline`.

### Step 7 — Destructive actions UI (3h)

- Buttons disabled based on state.
- Confirmation modals with friction (typed slug for delete).
- Toasts on success/failure.
- **Commit**: `feat(admin-ui): destructive actions with state gate`.

## 6. Test strategy

### Unit (C#)

- Endpoint policy + state-gate tests.
- `WorkflowLadderService` parses various Elsa states.

### Unit (TS)

- Component tests for `StateLadder`, `EventsTimeline`, modal gating.

### Integration

- E2E Playwright: stuck tenant surfaced, retry action recovers.
- RBAC: non-admin user gets 403.

### Accessibility

- axe-core on every admin page.

## 7. Rollback plan

- **Feature flag**: `AdminUI:TenantStatus=true` gates the UI +
  endpoints. Off hides the section.
- **Action endpoint disable**: each action has its own flag to
  permit read-only rollout.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. List endpoint | 3 |
| 2. Detail endpoint | 3 |
| 3. Action endpoints | 4 |
| 4. SSE stream | 3 |
| 5. UI list + detail | 5 |
| 6. Live components | 4 |
| 7. Destructive UI | 3 |
| **Total** | **25** (brief 22; +3 for SSE fallback + 2FA
confirmation). |

## 9. Open questions

- **2FA confirmation for force-delete**: sent via email OTP or
  re-entered password? Plan: password re-entry (no new dependency).
- **LISTEN/NOTIFY vs. polling**: production uses NOTIFY; local dev
  uses 2s polling.
- **Impersonation duration**: 15 min matches JWT `exp`. Renewal
  requires another audit event.
- **Retry action idempotency**: workflow is idempotent (28-5), so
  double-click is safe.
- **Pagination**: max pageSize=200 per brief AC1; sufficient for
  current scale.

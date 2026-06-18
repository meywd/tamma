# Story 37-12: Admin & Tenant Audit Dashboard UI

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **platform owner** (and, separately, as a **tenant admin/owner**),
I want operator-facing audit & compliance pages that render the Epic 37 audit query/export/chain-verification/legal-hold/DSAR APIs,
So that I can investigate sensitive actions, prove tamper-evidence, satisfy GDPR data-subject requests, and manage retention/legal holds — each principal seeing only the audit they are entitled to, gated by per-mode RBAC.

## Priority

P1 — The audit/compliance APIs (37-2..37-9, 37-11) are useless to operators without a UI; this story is the human surface of the entire epic.

## Architecture Note (read first — deviates from the raw spec)

The story spec's `primaryComponents` named the Blazor `apps/tamma-elsa/src/Tamma.Studio` app for the
admin surface. **This story deliberately re-targets the admin audit UI onto the React
`packages/dashboard` app** for three verified reasons:

1. `packages/dashboard` is the live admin/platform dashboard (deployed at app.tamma.dev) and already
   ships an **`AuditLogTab` stub** (`packages/dashboard/src/pages/admin/AuditLogTab.tsx`) explicitly
   marked "Full implementation will be wired when the audit-log API lands" — this story lands it.
2. The tenant audit surface lives in the React `packages/dashboard-user` app (per the spec and the
   `feat/wave-b` architecture); keeping admin in React too means one component library, one test
   stack (Vitest + Testing Library), and one API-client pattern across both surfaces.
3. The C# `Tamma.Studio` Blazor app has no audit pages today and is the Elsa-engine operator console,
   not the customer-facing audit product.

`packages/api` is **deleted** and is NOT a target of this story. The audit/compliance HTTP APIs this
UI consumes are owned by the C# control-plane (`apps/tamma-elsa/src/Tamma.Api`), shipped by the
dependency stories (37-2..37-9, 37-11). This story ships **only** the two React dashboards plus their
typed API clients and route/guard wiring.

## Per-Mode Ownership (mandatory two-scoping-model answer, per CLAUDE.md)

Every audit surface must answer "in single-user mode, who owns this?" AND "in SaaS mode, who owns
this?" The UI mirrors the server-side scoping; it never decides authorization (the server is
authoritative) but it must not render controls the caller can't use, and must never request
cross-tenant data.

| Surface | single-user mode | SaaS mode |
|---|---|---|
| **Tenant audit page** (`packages/dashboard-user`) | The sole user sees their own audit records (their instance). Owner-only actions are always available (they are the owner). | `tenant_admin`/`tenant_owner` see their tenant's audit; `member` sees a read-only audit view (or no nav entry). Cross-tenant reads 404 server-side; the UI only ever sends the caller's active `tenantId`. |
| **Platform audit page** (`packages/dashboard`) | The sole user IS the platform owner — sees the platform audit log + impersonation/chain/evidence controls. | `PlatformOwnerAccess` only. Tenant users have no admin nav entry; a deep-linked route renders the existing 403 `ForbiddenPage`. |
| **Retention editor / legal holds (owner-only)** | The user (they are owner). | `tenant_owner` mutates; `tenant_admin` read-only; `member` 403. |
| **DSAR / erasure request** | The user. | `tenant_admin`+ submits; `member` 403 on submit. |

Mode is process-stable server-side; the UI does not branch on mode. It branches on the caller's
**role** (`user.role` from `/api/auth/me`) and tenant (`user.tenantId`). In single-user mode the sole
user's role resolves to owner-equivalent, so owner-only controls appear — exactly the desired
single-user behavior with zero mode-specific UI code.

## Acceptance Criteria

1. **Tenant audit page exists and is reachable.** `packages/dashboard-user` gains an `/audit` route
   (under `AuthGuard` → `AppLayout`) rendering a `TenantAuditPage` that lists the caller's own audit
   records via `GET /api/v1/orgs/{tenantId}/audit` (37-3). A nav entry "Audit" is added to
   `AppLayout`. The page only ever sends `user.tenantId`; it has no UI affordance to query another
   tenant.

2. **Tenant audit table: filters, facets, pagination.** The audit table supports the 37-3 filter set
   (actor, action/event-type, category, severity, resource id, date range) plus severity/category
   facet chips, and **keyset (cursor) pagination** ("Load more" / next-cursor) — never offset
   pagination — so a 100k+ row trail pages without a slow `COUNT(*)`. Active filters are reflected in
   the request and round-trip on refresh.

3. **Tenant audit export.** An "Export" button submits the current filter set to `POST
   /api/v1/orgs/{tenantId}/audit/export` (37-4) and the client downloads via the returned
   **time-limited signed URL** (37-4). The export request carries exactly the filters shown, so the
   exported view matches the on-screen view. No raw artifact/object-store URLs are ever rendered or
   logged — only the signed, expiring export URL is used, and it is opened, not persisted in app
   state beyond the click.

4. **Chain-verification status badge.** The tenant audit page shows a chain-verification status badge
   sourced from `GET /api/v1/orgs/{tenantId}/audit/chain/verify` (37-2): `verified`
   (green), `tampered`/`broken` (red, with the first broken sequence/range surfaced), or `pending`
   (neutral) while the check runs. A manual "Re-verify" action re-requests it.

5. **Tenant Compliance sub-pages.** `packages/dashboard-user` settings gain Compliance controls:
   - **Retention policy editor** (37-5) at `/settings/compliance/retention` — read for admin, edit
     for owner; a `member` sees 403.
   - **Legal Holds** (37-6) at `/settings/compliance/legal-holds` — list + place + release; place/
     release are owner-only; the list is read-only for admin.
   - **DSAR + Erasure requests** (37-7/37-8) at `/settings/compliance/data-requests` — submit a DSAR
     export or a right-to-erasure request and poll job status (`pending → running → completed/failed`)
     with the completed DSAR download served via a signed URL (37-4 pattern).
   - **Consent history** (37-9) at `/settings/compliance/consent` — read-only timeline of consent
     grant/revoke events for the tenant's data subjects.

6. **Platform audit page exists (PlatformOwnerAccess).** `packages/dashboard` gains a platform Audit &
   Compliance surface by wiring the existing `AuditLogTab` (today a "Coming soon" stub) to `GET
   /api/admin/audit` (37-3): the platform-wide audit log with the same filter/facet/keyset-pagination
   capabilities as the tenant view. The whole admin route is already behind `AdminGuard`
   (`PlatformOwnerAccess` server-side); a non-owner deep-linking the route hits `ForbiddenPage`.

7. **Platform impersonation log.** The platform Audit page surfaces active + historical impersonation
   sessions by reusing the existing `AdminImpersonationsEndpoints`: active sessions from `GET
   /api/admin/impersonations/active`, history filtered from the platform audit log
   (`IMPERSONATION.STARTED`/`IMPERSONATION.ENDED` events). Each row shows impersonator → target,
   reason, started/ended timestamps, and the `impersonationId` that joins the event stream to the
   `admin_impersonations` audit row.

8. **Platform chain verification + evidence pack.** The platform Audit page exposes (a) a chain-verify
   action over the platform audit log (37-2) with the same badge semantics as AC4, and (b) an
   **evidence-pack generation** control (37-11): submit a date range + scope, kick off the pack job,
   poll status, and download the completed pack via its signed URL (37-4 pattern). No raw artifact
   URLs are exposed.

9. **Platform legal-hold management.** The platform Audit page lists all legal holds across tenants
   (37-6) and lets the platform owner place/release a hold scoped to a tenant or resource. (Tenant-
   scoped place/release also lives in the tenant Compliance page per AC5; this is the cross-tenant
   admin view.)

10. **RBAC enforced in the UI per mode — no cross-tenant, no over-privilege.** SaaS `member` users get
    no admin nav entry and a 403 on any direct admin route; on the tenant side a `member` either gets
    a read-only audit view or no nav entry per the table above, and owner-only mutations (retention
    edit, legal-hold place/release) are hidden/disabled for non-owners. The single-user variant shows
    the user-owned surface with owner-level controls. **Every tenant request is keyed to the caller's
    active `tenantId` only** — the UI never constructs a URL for another tenant, and relies on the
    server returning 404 for cross-tenant attempts (defense in depth).

11. **Meta-audit + signed-URL contract honored.** Every audit read from the UI is a normal `GET` that
    the server records as the `AUDIT.QUERIED` meta-audit (37-3) — the UI adds no header to suppress
    it. All downloads (export, DSAR, evidence pack) use the **time-limited signed-export URLs** (37-4);
    the client never receives, renders, persists, or logs a raw object-store/artifact URL.

12. **Loading / empty / error / large-table states.** Each surface renders distinct loading, empty,
    and error states (error states are `role="alert"`). The audit table virtualizes rows (windowed
    rendering) so a 100k+ row trail scrolls without freezing the main thread; keyset pagination caps
    the working set per page. A failed signed-URL fetch or expired URL surfaces a retryable error, not
    a broken download.

13. **Tests: component + RBAC-per-mode + isolation + e2e.** Vitest + Testing Library cover the audit
    table (rows/filters/facets/keyset "load more"), the chain badge states, the export trigger
    (asserts the request body equals the shown filters and that the signed URL — not a raw URL — is
    used), and the retention/legal-hold/DSAR/consent forms (validation, owner-only gating, job-status
    polling). RBAC guard tests assert: SaaS `member` → no nav + read-only/403; `tenant_admin` →
    read-only where owner-only actions exist; owner → full controls; single-user → user-owned variant.
    An **isolation test** asserts the tenant client never builds a URL for a tenant other than
    `user.tenantId`. An e2e/integration test (mocked API) walks tenant-audit → filter → export and
    admin-audit → chain-verify → evidence-pack.

## Technical Design

### Surface map

```
packages/dashboard-user/  (tenant audit — dash.tamma.dev)
  src/api/
    audit.ts                      # NEW — tenant audit query/export/chain client (37-3/37-4/37-2)
    compliance.ts                 # NEW — retention/legal-hold/DSAR/erasure/consent client (37-5..37-9)
    audit.test.ts                 # NEW
    compliance.test.ts            # NEW
  src/pages/audit/
    TenantAuditPage.tsx           # NEW — table + filters + facets + keyset paging + chain badge + export
    TenantAuditPage.test.tsx      # NEW
  src/pages/settings/compliance/
    RetentionPolicyPage.tsx       # NEW (37-5)
    LegalHoldsPage.tsx            # NEW (37-6)
    DataRequestsPage.tsx          # NEW (37-7/37-8: DSAR + erasure + job status)
    ConsentHistoryPage.tsx        # NEW (37-9)
    *.test.tsx                    # NEW (one per page)
  src/components/audit/
    AuditTable.tsx                # NEW — virtualized table (shared by tenant page)
    AuditFilters.tsx              # NEW — filter bar + facet chips
    ChainStatusBadge.tsx          # NEW
    JobStatusPoller.tsx           # NEW — shared poll-until-terminal helper component/hook
    *.test.tsx                    # NEW
  src/App.tsx                     # MODIFY — add /audit + /settings/compliance/* routes + guards
  src/layouts/AppLayout.tsx       # MODIFY — add "Audit" + "Compliance" nav entries (role-aware)

packages/dashboard/  (platform audit — app.tamma.dev admin panel)
  src/services/admin/
    audit-api-client.ts           # NEW — platform audit + chain + evidence + impersonation client
    compliance-api-client.ts      # NEW — cross-tenant legal-hold client (37-6)
    audit-api-client.test.ts      # NEW
  src/pages/admin/
    AuditLogTab.tsx               # MODIFY — replace stub viewer with the wired platform audit page
    AuditLogTab.test.tsx          # MODIFY — extend existing test
    audit/
      PlatformAuditPanel.tsx      # NEW — audit table + filters + chain verify
      ImpersonationPanel.tsx      # NEW — active + history (AdminImpersonationsEndpoints)
      EvidencePackPanel.tsx       # NEW — generate + poll + signed-URL download (37-11)
      LegalHoldAdminPanel.tsx     # NEW — cross-tenant legal holds (37-6)
      *.test.tsx                  # NEW
  src/pages/admin/AdminLayout.tsx # MODIFY — relabel "Audit Log" tab → "Audit & Compliance" (optional)
```

The `AuditTable`, `AuditFilters`, and `ChainStatusBadge` are authored in `dashboard-user` first (the
tenant page is the simpler instance) and the platform panel re-implements the same shape against the
admin client. (Cross-package component sharing is out of scope — the two apps don't import each other;
duplication of ~150 lines of presentational table code is the accepted trade-off, matching how
`alerts.ts` is duplicated between the two apps today.)

### API clients (NEW — consume dependency-story endpoints)

The audit/compliance HTTP endpoints are owned by `apps/tamma-elsa/src/Tamma.Api` and shipped by
37-2..37-9 / 37-11. These clients are the only new "API" code; they follow the existing client
patterns exactly: `dashboard-user` uses the shared `ApiClient` (`src/api/client.ts`, refresh-on-401
built in); `dashboard` uses the `fetchJSON` helper in `services/admin/`.

Tenant client (`packages/dashboard-user/src/api/audit.ts`), mirroring `api/alerts.ts`:

```typescript
import { apiClient } from './client';

export type AuditSeverity = 'critical' | 'high' | 'medium' | 'low' | 'info';
export interface AuditRecordDto {
  id: string;
  sequence: number;            // chain position (37-2)
  eventType: string;           // AGGREGATE.ACTION.STATUS
  category: string;
  severity: AuditSeverity;
  actor: string;
  actorId: string | null;
  resourceType: string | null;
  resourceId: string | null;
  tenantId: string | null;
  occurredAt: string;          // ISO 8601
  summary: string;
}
export interface AuditQuery {
  actor?: string;
  eventType?: string;
  category?: string;
  severity?: AuditSeverity;
  resourceId?: string;
  from?: string;               // ISO 8601
  to?: string;                 // ISO 8601
  cursor?: string;             // keyset cursor — NOT a page number
  limit?: number;              // default 100
}
export interface AuditPage {
  items: AuditRecordDto[];
  nextCursor: string | null;   // null ⇒ end of trail
}
export interface ChainVerifyResult {
  status: 'verified' | 'tampered' | 'pending';
  verifiedThrough: number | null;
  firstBrokenSequence: number | null;
  checkedAt: string;
}
export interface ExportTicket { downloadUrl: string; expiresAt: string; format: string; }

export async function queryTenantAudit(tenantId: string, q: AuditQuery): Promise<AuditPage> { /* GET /api/v1/orgs/{tenantId}/audit?... */ }
export async function verifyTenantChain(tenantId: string): Promise<ChainVerifyResult> { /* GET .../audit/chain/verify */ }
export async function exportTenantAudit(tenantId: string, q: AuditQuery, format: 'csv' | 'jsonl'): Promise<ExportTicket> { /* POST .../audit/export */ }
```

`compliance.ts` adds `getRetentionPolicy`/`putRetentionPolicy` (37-5), `listLegalHolds`/
`placeLegalHold`/`releaseLegalHold` (37-6), `submitDsarRequest`/`submitErasureRequest`/`getJobStatus`
(37-7/37-8), `listConsentHistory` (37-9) — all keyed to `tenantId`, all over `apiClient`.

Admin client (`packages/dashboard/src/services/admin/audit-api-client.ts`), mirroring
`admin-api-client.ts` (`fetchJSON`, `credentials: 'include'`): `queryPlatformAudit`,
`verifyPlatformChain`, `listActiveImpersonations` (reusing `GET /api/admin/impersonations/active`),
`generateEvidencePack` + `getEvidencePackStatus` (37-11), and cross-tenant `listLegalHolds`/
`placeLegalHold`/`releaseLegalHold` (37-6).

### Keyset pagination & virtualization

- The table requests `limit` rows + an opaque `cursor`; the server returns `nextCursor`. "Load more"
  appends the next page; the working set is the accumulated rows. This avoids `OFFSET`/`COUNT(*)` on a
  100k+ trail.
- Row virtualization: render only the visible window (a lightweight windowing approach — e.g. a fixed
  row height + `IntersectionObserver` sentinel that triggers the next keyset fetch). No new heavy
  dependency is required; if `@tanstack/react-virtual` is already in the dashboard dep tree it may be
  used, otherwise a hand-rolled windowing hook keeps the bundle lean. **Verify the dep tree before
  adding a library.**

### Signed-URL download contract (37-4)

Export / DSAR / evidence-pack downloads NEVER expose a raw object-store URL. The flow is: POST the
job/export request → receive `{ downloadUrl, expiresAt }` where `downloadUrl` is a short-lived signed
URL → trigger the browser download (anchor click / `window.open`) → discard the URL. The client must
not store the signed URL in long-lived state, must not log it, and must surface an "expired — retry"
error if the signed URL 403s.

### Routing & guards

`dashboard-user/src/App.tsx` adds:

```
/audit                              AuthGuard → AppLayout → TenantAuditPage          (members read-only)
/settings/compliance/retention      AuthGuard → TenantAdminGuard → AppLayout → RetentionPolicyPage
/settings/compliance/legal-holds    AuthGuard → TenantAdminGuard → AppLayout → LegalHoldsPage
/settings/compliance/data-requests  AuthGuard → TenantAdminGuard → AppLayout → DataRequestsPage
/settings/compliance/consent        AuthGuard → TenantAdminGuard → AppLayout → ConsentHistoryPage
```

Owner-only controls inside admin-guarded pages are gated by an inline `role === 'owner'` check
(retention edit, legal-hold place/release) — `TenantAdminGuard` admits admin+owner; the finer
owner-only split is done in-page exactly as `TenantAlertFeed` gates ack/resolve by role.

`dashboard` admin routes are already inside `AdminGuard`; no new route is needed — the existing
`audit-log` tab in `AdminLayout` becomes the entry point.

### Component behavior cribbed from existing exemplars

- Filter bar + table + modal patterns follow `dashboard-user/src/pages/alerts/TenantAlertFeed.tsx`
  (filters, `role`-gated action buttons, `Modal`, `SeverityPill`, `role="alert"` error banner).
- The tab-wiring and feature-flag fallback follow `dashboard/src/pages/admin/AuditLogTab.tsx`
  (the `VITE_FEATURE_ADMIN_AUDIT_LOG` flag may stay as the kill-switch; flip its default to enabled
  once the 37-3 admin endpoint is confirmed live).

## Dependencies

- **Prerequisite (hard): Story 37-3** — Audit query/search API (`GET /api/v1/orgs/{tenantId}/audit`,
  `GET /api/admin/audit`, filters, keyset pagination, the `AUDIT.QUERIED` meta-audit). The entire
  audit table renders this. Until 37-3 lands the tenant page and `AuditLogTab` stay behind their
  feature flags / show the "API pending" placeholder.
- **Prerequisite (hard): Story 37-4** — Export API + time-limited signed URLs. All downloads (export,
  DSAR, evidence pack) go through the signed-URL contract.
- **Prerequisite: Story 37-2** — Chain verification API. Powers the chain status badge + re-verify.
- **Prerequisite: Story 37-5** — Retention policy API (retention editor page).
- **Prerequisite: Story 37-6** — Legal hold API (tenant + cross-tenant legal-hold panels).
- **Prerequisite: Story 37-7 / 37-8** — DSAR export + right-to-erasure APIs + job status (data-requests
  page).
- **Related: Story 37-9** — Consent logging API (consent history page).
- **Prerequisite: Story 37-11** — Evidence-pack generation API (admin evidence-pack panel).
- **Reuses (already shipped): `AdminImpersonationsEndpoints`** —
  `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminImpersonationsEndpoints.cs`
  (`GET /api/admin/impersonations/active`, `PlatformOwnerAccess`).
- **Builds on (already shipped):** `packages/dashboard` admin shell (`AdminLayout`, `AdminGuard`,
  `services/admin/admin-api-client.ts`, the `AuditLogTab` stub); `packages/dashboard-user` shell
  (`App.tsx`, `AppLayout`, `AuthGuard`, `TenantAdminGuard`, `api/client.ts`, `api/alerts.ts`,
  `useAuth`).

**Sequencing note:** This is a UI-only story and can be drafted/scaffolded in parallel with its
dependencies behind feature flags, but it cannot be marked done until 37-3 and 37-4 are live (the two
hard prerequisites every page depends on). The chain/legal-hold/DSAR/evidence sub-features go live as
their respective dependency endpoints land.

## Testing Strategy

1. **Component tests (Vitest + Testing Library), colocated `*.test.tsx`:**
   - `AuditTable`: renders rows from a mocked `AuditPage`; applying a filter re-queries with the right
     params; "Load more" appends the next keyset page and stops when `nextCursor` is null; empty +
     error states.
   - `ChainStatusBadge`: `verified`/`tampered`/`pending` render distinct, accessible states; "Re-verify"
     re-requests.
   - `AuditFilters`: facet chips toggle category/severity; date-range round-trips into the query.
   - Export: clicking Export posts the **current** filter set (assert request body equals shown
     filters) and downloads via the returned signed URL; an expired/403 signed URL shows a retry error.
   - Retention/legal-hold/DSAR/consent pages: form validation, owner-only gating (admin sees
     read-only), job-status polling transitions (`pending → running → completed`) drive the download
     button enable.
2. **RBAC-per-mode guard tests:** mock `useAuth`/`useCurrentUser` to return owner / tenant_admin /
   member / single-user; assert nav entries, route guards (member → no admin nav + 403 on admin route;
   member → read-only or no tenant-audit nav), and owner-only control visibility. Mirror existing
   `guards/__tests__/` and `AdminLayout.test.tsx` patterns.
3. **Isolation test:** spy on `apiClient`/`fetch`; assert the tenant client only ever issues URLs
   containing `user.tenantId` — feeding a different tenant id must be impossible from the UI (no input
   that accepts a foreign tenant id), and the client surfaces a server 404 cleanly when one is forced
   in a test.
4. **e2e/integration (mocked API, MSW or fetch-mock):** tenant flow audit → filter → export →
   signed-URL download; admin flow audit → chain-verify badge → evidence-pack generate/poll/download;
   assert no raw artifact URL ever appears in the DOM or in any logged value.
5. **Large-table behavior:** render a mocked 100k-row source through the virtualized table; assert only
   a windowed subset is in the DOM and scrolling/"Load more" does not block (smoke-level — full perf
   profiling is out of scope, but the windowing assertion guards regressions).
6. **Run:** `pnpm test --filter @tamma/dashboard-user` and `pnpm test --filter @tamma/dashboard` green;
   `pnpm typecheck` clean for both; no new ESLint errors.

## Estimated Effort

6-7 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/dashboard-user/src/api/audit.ts` | Create |
| `packages/dashboard-user/src/api/audit.test.ts` | Create |
| `packages/dashboard-user/src/api/compliance.ts` | Create |
| `packages/dashboard-user/src/api/compliance.test.ts` | Create |
| `packages/dashboard-user/src/components/audit/AuditTable.tsx` | Create |
| `packages/dashboard-user/src/components/audit/AuditFilters.tsx` | Create |
| `packages/dashboard-user/src/components/audit/ChainStatusBadge.tsx` | Create |
| `packages/dashboard-user/src/components/audit/JobStatusPoller.tsx` | Create |
| `packages/dashboard-user/src/components/audit/AuditTable.test.tsx` | Create |
| `packages/dashboard-user/src/components/audit/AuditFilters.test.tsx` | Create |
| `packages/dashboard-user/src/components/audit/ChainStatusBadge.test.tsx` | Create |
| `packages/dashboard-user/src/pages/audit/TenantAuditPage.tsx` | Create |
| `packages/dashboard-user/src/pages/audit/TenantAuditPage.test.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/RetentionPolicyPage.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/LegalHoldsPage.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/DataRequestsPage.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/ConsentHistoryPage.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/RetentionPolicyPage.test.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/LegalHoldsPage.test.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/DataRequestsPage.test.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/compliance/ConsentHistoryPage.test.tsx` | Create |
| `packages/dashboard-user/src/App.tsx` | Modify (routes + guards) |
| `packages/dashboard-user/src/layouts/AppLayout.tsx` | Modify (nav entries) |
| `packages/dashboard/src/services/admin/audit-api-client.ts` | Create |
| `packages/dashboard/src/services/admin/audit-api-client.test.ts` | Create |
| `packages/dashboard/src/services/admin/compliance-api-client.ts` | Create |
| `packages/dashboard/src/pages/admin/audit/PlatformAuditPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/audit/ImpersonationPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/audit/EvidencePackPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/audit/LegalHoldAdminPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/audit/PlatformAuditPanel.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/audit/ImpersonationPanel.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/audit/EvidencePackPanel.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/AuditLogTab.tsx` | Modify (wire viewer) |
| `packages/dashboard/src/pages/admin/__tests__/AuditLogTab.test.tsx` | Modify (extend) |
| `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Modify (relabel tab — optional) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. anything on audit/DCB,
   signed URLs, or tenant isolation in the dashboards).
3. Confirmed the 37-3 / 37-4 endpoint shapes against the as-built `apps/tamma-elsa/src/Tamma.Api`
   audit endpoints — **the DTOs sketched above are the contract this story expects; reconcile with the
   real endpoint responses before coding the clients.** If a field name differs, the client is the
   single place it's mapped.
4. Verified whether a virtualization library is already in the dashboard dep tree before adding one
   (`pnpm why @tanstack/react-virtual` in each package).
5. Planned the TDD approach (Red-Green-Refactor) — these are pure React components with a fetch seam;
   mock the client, write the component test first.

### Why React, not Tamma.Studio

The spec named `Tamma.Studio` (Blazor). This story targets the React `packages/dashboard` instead
because that is the live admin product with the existing `AuditLogTab` stub waiting for exactly this
API, and it keeps both audit surfaces on one component/test stack. See the Architecture Note above.
`packages/api` is deleted and is never a target.

### Tenant isolation is the load-bearing invariant

The server is authoritative (cross-tenant reads 404 behind `RequireTenantMembershipFilter`), but the
UI must never even *attempt* a cross-tenant request: the tenant client takes `tenantId` only from
`user.tenantId` (`useAuth`), there is no free-text tenant-id input on tenant pages, and the isolation
test pins this. The platform (cross-tenant) view lives exclusively in `packages/dashboard` behind
`AdminGuard`/`PlatformOwnerAccess`.

### Signed URLs only

Treat any raw artifact/object-store URL as a leak. Downloads always go through the 37-4 signed-URL
ticket; never store it past the click, never log it, and handle expiry as a retryable error.

### Meta-audit is automatic

Audit reads are plain `GET`s; the server records `AUDIT.QUERIED` (37-3) on its side. The UI does
nothing special — and must NOT send any "skip audit" header. Do not add request de-duplication that
would hide reads from the meta-audit.

### Feature flags as kill-switches

`AuditLogTab` already gates on `VITE_FEATURE_ADMIN_AUDIT_LOG`. Keep a flag per surface as a deploy
kill-switch so the UI can ship ahead of, or be disabled independently of, the backing endpoints.

## Logging Requirements

(Browser/dashboard logging — `console`/structured client logger; no server-side Pino here.)

- **INFO:** audit query issued (surface, filter summary — never PII values, never the signed URL),
  chain verification requested + result status, export/DSAR/evidence-pack job submitted + terminal
  status.
- **DEBUG:** keyset page fetched (cursor in/out, row count), job-status poll tick (job id + state),
  filter changes.
- **WARN:** signed-URL download expired/403 (retry surfaced), job ended `failed`, audit query returned
  an error the page recovered from.
- **ERROR:** unrecoverable load failure for a surface (renders the `role="alert"` error state).
- **Structured context:** include `{ surface, tenantId?, jobId?, eventType?, cursor? }` where
  applicable.
- **Credential / PII safety:** NEVER log signed URLs, raw artifact URLs, actor PII (emails/ids beyond
  what the row already shows), or any export payload. The signed URL is treated as a secret.
- **Cross-tenant safety:** the tenant surface logs only the caller's own `tenantId`; it can never log
  another tenant's id because it can never request one.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |

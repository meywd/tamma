# Story 37-12 — Admin & Tenant Audit Dashboard UI (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes the
> component/client test before implementation.

**Story file:** `docs/stories/epic-37/story-37-12/37-12-admin-and-tenant-audit-dashboard-ui.md`
(read it first — it carries the full ACs, the per-mode ownership matrix, and the
why-React-not-Tamma.Studio architecture note).

**Goal:** Ship the operator-facing UI of the Epic 37 audit product on the two React dashboards.
`packages/dashboard-user` gets a tenant audit page (own records, filters, facets, keyset paging,
chain badge, export) plus Compliance settings (retention, legal holds, DSAR/erasure, consent).
`packages/dashboard` (admin panel) gets a platform Audit & Compliance surface (platform audit log,
impersonation active+history, chain verify, evidence-pack, cross-tenant legal holds). RBAC is enforced
per mode; a tenant never sees another tenant's audit; downloads always go through 37-4 signed URLs.

**Tech stack:** React 19 + Vite 8 + Tailwind 4 + react-router-dom 7, tested with Vitest 4 +
@testing-library/react. Two SPAs: `packages/dashboard` (admin, `fetchJSON` admin clients,
`AdminGuard`/`PlatformOwnerAccess`) and `packages/dashboard-user` (tenant, shared `ApiClient` with
refresh-on-401, `AuthGuard`/`TenantAdminGuard`). The backing audit/compliance HTTP API is owned by
`apps/tamma-elsa/src/Tamma.Api` and shipped by the dependency stories — this plan touches **only** the
two React packages.

---

## Current-state findings (verified 2026-06-17, repo @ main)

| Thing | State today |
|---|---|
| `packages/dashboard/src/pages/admin/AuditLogTab.tsx` | **Stub.** Feature-flagged on `VITE_FEATURE_ADMIN_AUDIT_LOG`; `AuditLogViewer` says "API integration pending". This story wires it. |
| `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Tabbed admin shell; `AdminTab` union + `TABS` array already include `audit-log`. Whole admin panel is behind `AdminGuard`. |
| `packages/dashboard/src/guards/AdminGuard.tsx` | Redirects non-admin to `/account`; exports `ForbiddenPage` (403). Role from `useCurrentUser().isAdmin`. Server is `PlatformOwnerAccess`. |
| `packages/dashboard/src/services/admin/admin-api-client.ts` | `fetchJSON` helper, `credentials: 'include'`, `API_BASE = VITE_API_BASE_URL ?? '/api'`. New admin clients mirror this. |
| `packages/dashboard-user/src/App.tsx` | Router; routes under `AuthGuard → AppLayout`; admin-only routes wrap `TenantAdminGuard`. Add `/audit` + `/settings/compliance/*` here. |
| `packages/dashboard-user/src/layouts/AppLayout.tsx` | Sidebar nav (Dashboard/Repositories/Runs/Settings). Add "Audit" + "Compliance" entries. |
| `packages/dashboard-user/src/guards/TenantAdminGuard.tsx` | Admits `admin`+`owner`; renders inline "Admin-only" for `member`. Owner-only split is done in-page (see `TenantAlertFeed`). |
| `packages/dashboard-user/src/api/client.ts` | Shared `ApiClient` (get/post/put/delete) with refresh-on-401; `apiClient` singleton. |
| `packages/dashboard-user/src/api/alerts.ts` | **The structural exemplar** for the new tenant clients: typed DTOs, `tenantId`-keyed `/api/v1/orgs/{tenantId}/...` calls over `apiClient`, plaintext-credential pre-flight guard. |
| `packages/dashboard-user/src/pages/alerts/TenantAlertFeed.tsx` | **The UX exemplar**: filter bar, `role`-gated action buttons, `Modal`, `SeverityPill`, `role="alert"` error banner, `useCallback` refresh. Crib heavily. |
| `packages/dashboard-user/src/hooks/useAuth.tsx` | `AuthUser` = `{ id, email, displayName, tenantId?, role? }` from `/api/auth/me`. The only source of `tenantId` — load-bearing for isolation. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminImpersonationsEndpoints.cs` | **Shipped.** `GET /api/admin/impersonations/active` (`PlatformOwnerAccess`); `IMPERSONATION.STARTED/ENDED` events carry impersonator+target+`impersonationId` in tags+data. Reuse for AC7. |
| Audit/export/chain/retention/legal-hold/DSAR/evidence endpoints | **NOT built yet** — owned by 37-2..37-9 / 37-11. The clients here are written against the contract in the story file and reconciled when those land. |

**Two cross-package facts that shape the plan:** (1) the two SPAs do not import each other — the
shared table/filter/badge components are authored in `dashboard-user` and re-implemented in
`dashboard` (≈150 lines duplicated, same as `alerts.ts` is duplicated today); (2) feature flags
(`VITE_FEATURE_*`) are the deploy kill-switch so the UI ships ahead of the backing endpoints.

---

## Non-goals (YAGNI guard)

- NO changes to `apps/tamma-elsa` (the C# API) — endpoints are dependency-story scope. If a client
  needs a field the endpoint doesn't return, that is a 37-3..37-11 gap, not work for this story.
- NO `Tamma.Studio` (Blazor) pages — admin audit is React `packages/dashboard` (see story
  architecture note). `packages/api` is deleted and never a target.
- NO cross-package shared component library / new shared package — duplicate the ~150 lines.
- NO new heavy charting/data-grid dependency unless one is already in the dep tree (verify with
  `pnpm why`); hand-roll windowing otherwise.
- NO real-time SSE audit stream — polling/refresh is enough for v1 (SSE can be a follow-up).
- NO per-user audit personalization, saved-search persistence, or column customization in v1.
- NO offset pagination / `COUNT(*)` — keyset only (the 100k-row AC forbids it).
- NO mode-branching UI code — branch on `role`/`tenantId`, never on SaaS-vs-single-user.

---

## Architecture (UI shape)

```
TENANT (packages/dashboard-user)                ADMIN (packages/dashboard)
  /audit  → TenantAuditPage                        admin panel → "Audit & Compliance" tab
    AuditFilters ─ AuditTable ─ ChainStatusBadge     PlatformAuditPanel (table + filters + chain)
    [Export] → exportTenantAudit → signed URL        ImpersonationPanel (active + history)
                                                      EvidencePackPanel (generate/poll/signed URL)
  /settings/compliance/                              LegalHoldAdminPanel (cross-tenant)
    retention      → RetentionPolicyPage (37-5)
    legal-holds    → LegalHoldsPage      (37-6)     services/admin/audit-api-client.ts
    data-requests  → DataRequestsPage    (37-7/8)   services/admin/compliance-api-client.ts
    consent        → ConsentHistoryPage  (37-9)
  api/audit.ts (37-3/37-4/37-2)
  api/compliance.ts (37-5..37-9)
```

Data flow per surface: client (typed, `tenantId`/admin-scoped) → page (filters/state/poll) →
presentational table/badge/form. The fetch seam is the test boundary — mock the client, never `fetch`.

---

## Task breakdown

### T1 — Tenant audit API client (`api/audit.ts`) + tests  [foundation]

**Scope:** Typed client for 37-3 query, 37-2 chain verify, 37-4 export, over the shared `apiClient`,
all keyed to `tenantId`. DTOs per the story-file contract (`AuditRecordDto`, `AuditQuery` with
`cursor`, `AuditPage` with `nextCursor`, `ChainVerifyResult`, `ExportTicket`).

**Files:** New `packages/dashboard-user/src/api/audit.ts`, `audit.test.ts`.

**Tests first:** query builds the right `/api/v1/orgs/{tenantId}/audit?...` querystring from filters
(omits empty); `verifyTenantChain` hits `.../audit/chain/verify`; `exportTenantAudit` POSTs the filter
set and returns the `ExportTicket`; the client never accepts a `tenantId` other than the one passed by
the caller (it's a parameter, and the page only ever passes `user.tenantId`).

**Done when:** `pnpm test --filter @tamma/dashboard-user` green for the new file; typecheck clean.

- [ ] Write `audit.test.ts` (querystring, endpoints, export ticket).
- [ ] Implement `audit.ts` mirroring `api/alerts.ts`.
- [ ] Tests green; typecheck clean.

### T2 — Audit presentational components: `AuditTable`, `AuditFilters`, `ChainStatusBadge`

**Scope:** The shared presentational pieces (authored in dashboard-user). `AuditTable` = virtualized,
keyset "Load more", loading/empty/error states. `AuditFilters` = filter bar + severity/category facet
chips + date range. `ChainStatusBadge` = verified/tampered/pending + "Re-verify".

**Files:** New `components/audit/AuditTable.tsx`, `AuditFilters.tsx`, `ChainStatusBadge.tsx` + tests.
`JobStatusPoller.tsx` (shared poll-until-terminal hook/component) added here too.

**Decisions to make in-task:** virtualization approach — `pnpm why @tanstack/react-virtual` in the
dashboard packages; if present, use it; else hand-roll a windowing hook (fixed row height +
`IntersectionObserver` sentinel that drives the next keyset fetch). Keep the bundle lean.

**Tests first:** table renders mocked rows; "Load more" appends next page, stops on `null` nextCursor;
windowed render keeps DOM small for a 100k-row source; filters emit the right query object; facet chips
toggle; badge renders three states accessibly and re-verify re-requests; poller transitions
`pending→running→completed` and flips a callback.

- [ ] `pnpm why` the virtualization lib in both packages; decide approach.
- [ ] Write component tests (table/filters/badge/poller).
- [ ] Implement components (crib `TenantAlertFeed` for table/filter/error idioms).
- [ ] Tests green; typecheck clean.

### T3 — `TenantAuditPage` + route + nav (AC1-4, AC10-12)

**Scope:** Compose T1+T2 into the tenant audit page: load `user.tenantId` from `useAuth`, wire
filters→query→table, chain badge, Export button (signed-URL download), member read-only behavior. Add
`/audit` route in `App.tsx` (under `AuthGuard → AppLayout`) and an "Audit" nav entry in `AppLayout`.

**Files:** New `pages/audit/TenantAuditPage.tsx` + test. Modify `App.tsx`, `AppLayout.tsx`.

**Signed-URL contract:** Export → `exportTenantAudit` → `{ downloadUrl }` → anchor-click/`window.open`
→ discard. Never store/log the URL; expired/403 → retryable error. Assert in test the export request
body equals the shown filters and that no raw artifact URL is rendered.

**Isolation:** the page passes ONLY `user.tenantId`; there is no foreign-tenant input. Add the
isolation test here (spy `apiClient`/`fetch`; every URL contains `user.tenantId`).

**Tests first:** renders rows for the active tenant; filter change re-queries; chain badge reflects
result; Export posts current filters + downloads via signed URL; member sees read-only (no export? —
decide: members CAN read+export own-tenant audit? Per AC10 members get a read-only audit view; export
of own-tenant data is a read — allow it, but no mutate controls exist on this page anyway); no-tenant
state renders the "no active organization" message (crib `TenantAlertFeed`).

- [ ] Write `TenantAuditPage.test.tsx` + isolation test.
- [ ] Implement page; wire route + nav.
- [ ] Tests green; typecheck clean.

### T4 — Compliance client (`api/compliance.ts`) + tests (37-5..37-9)

**Scope:** Tenant-keyed client: retention get/put (37-5), legal-hold list/place/release (37-6), DSAR
submit + erasure submit + job status (37-7/37-8), consent history list (37-9). All over `apiClient`,
all `/api/v1/orgs/{tenantId}/...`.

**Files:** New `packages/dashboard-user/src/api/compliance.ts`, `compliance.test.ts`.

**Tests first:** each call hits the right endpoint with the right method/body; job-status returns the
terminal-state shape; everything is `tenantId`-keyed.

- [ ] Write `compliance.test.ts`.
- [ ] Implement `compliance.ts`.
- [ ] Tests green; typecheck clean.

### T5 — Tenant Compliance pages + routes (AC5, AC10)

**Scope:** Four pages under `/settings/compliance/*`, all behind `TenantAdminGuard`, with owner-only
splits in-page (retention edit, legal-hold place/release):
- `RetentionPolicyPage` (37-5) — read for admin, edit for owner.
- `LegalHoldsPage` (37-6) — list + place + release; place/release owner-only.
- `DataRequestsPage` (37-7/37-8) — DSAR export + erasure request forms + `JobStatusPoller` + signed-URL
  download of the completed DSAR.
- `ConsentHistoryPage` (37-9) — read-only consent timeline.

**Files:** New four pages + four tests under `pages/settings/compliance/`. Modify `App.tsx` (4 routes),
`AppLayout.tsx` (a "Compliance" nav group/entry).

**Tests first:** form validation; owner-only gating (admin → read-only, no place/release/edit; member →
the guard's "Admin-only" screen); DSAR/erasure submit kicks a job and polling enables the download;
download uses signed URL; consent timeline renders.

- [ ] Write the four page tests.
- [ ] Implement the four pages + routes + nav (crib `TenantAlertFeed` Modal + role gating).
- [ ] Tests green; typecheck clean.

### T6 — Admin audit + compliance clients (`services/admin/audit-api-client.ts`, `compliance-api-client.ts`)

**Scope:** Platform-scoped clients over `fetchJSON` (admin pattern): `queryPlatformAudit`,
`verifyPlatformChain` (37-2), `listActiveImpersonations` (reuse `GET /api/admin/impersonations/active`),
`generateEvidencePack`+`getEvidencePackStatus` (37-11); cross-tenant `listLegalHolds`/`placeLegalHold`/
`releaseLegalHold` (37-6).

**Files:** New `packages/dashboard/src/services/admin/audit-api-client.ts`,
`compliance-api-client.ts`, `audit-api-client.test.ts`.

**Tests first:** each call hits the right `/api/admin/...` endpoint; evidence-pack status returns the
terminal shape; impersonation-active maps to the existing endpoint's DTO.

- [ ] Write `audit-api-client.test.ts`.
- [ ] Implement both admin clients (mirror `admin-api-client.ts` `fetchJSON`).
- [ ] Tests green; typecheck clean.

### T7 — Platform audit panels + wire `AuditLogTab` (AC6-9, AC10-12)

**Scope:** Build the four admin panels and replace the `AuditLogViewer` stub with a real
`PlatformAuditPanel` composition:
- `PlatformAuditPanel` — platform audit table + filters + chain verify (re-implements T2's shape
  against the admin client).
- `ImpersonationPanel` — active sessions (`listActiveImpersonations`) + history filtered from the audit
  log (`IMPERSONATION.STARTED/ENDED`); impersonator→target, reason, timestamps, `impersonationId`.
- `EvidencePackPanel` — date-range/scope form → generate → `JobStatusPoller` → signed-URL download.
- `LegalHoldAdminPanel` — cross-tenant legal-hold list + place/release.

**Files:** New `pages/admin/audit/PlatformAuditPanel.tsx`, `ImpersonationPanel.tsx`,
`EvidencePackPanel.tsx`, `LegalHoldAdminPanel.tsx` + tests. Modify `AuditLogTab.tsx` (compose panels;
keep `VITE_FEATURE_ADMIN_AUDIT_LOG` as kill-switch, flip default to enabled once 37-3 admin endpoint
confirmed live), extend `AuditLogTab.test.tsx`, optionally relabel the tab in `AdminLayout.tsx`.

**RBAC:** the whole admin route is `AdminGuard`/`PlatformOwnerAccess`; the test asserts a non-admin
deep-link renders `ForbiddenPage` / redirect (existing guard behavior — just assert it still holds with
the new content).

**Tests first:** panels render mocked data; chain badge; impersonation active+history rows; evidence
pack generate→poll→signed-URL download (no raw URL in DOM); legal-hold place/release; `AuditLogTab`
renders panels when flag on, "Coming soon" when off.

- [ ] Write panel tests + extend `AuditLogTab.test.tsx`.
- [ ] Implement panels; wire `AuditLogTab`; relabel tab.
- [ ] Tests green; typecheck clean.

### T8 — RBAC-per-mode + isolation + e2e tests, polish

**Scope:** The cross-cutting test sweep + final polish. (Much is co-located in T3/T5/T7; T8 fills the
matrix gaps and the e2e walks.)

**Tests:**
- RBAC-per-mode: mock `useAuth`/`useCurrentUser` for owner / tenant_admin / member / single-user;
  assert nav entries, route guards (member → no admin nav + 403; member tenant → read-only/no nav),
  owner-only control visibility.
- Isolation: tenant client only ever issues `user.tenantId` URLs; forced cross-tenant 404 surfaces
  cleanly.
- e2e (mocked API — MSW or fetch-mock): tenant audit→filter→export→signed download; admin
  audit→chain→evidence generate/poll/download; assert no raw artifact URL appears in DOM or logs.
- Large-table: 100k-row mocked source → windowed DOM, non-blocking "Load more".

**Done when:** `pnpm test --filter @tamma/dashboard-user` AND `pnpm test --filter @tamma/dashboard`
green; `pnpm typecheck` clean for both; no new ESLint errors; the story's 13 ACs each map to a passing
test.

- [ ] Fill the RBAC matrix + isolation + e2e + large-table tests.
- [ ] Map each AC → a test; close gaps.
- [ ] Both packages green; typecheck + lint clean.

---

## Task order & dependencies

T1 → T2 → T3 (tenant audit MVP) ; T4 → T5 (tenant compliance) ; T6 → T7 (admin) ; T8 last.
T1/T2 are prerequisites for T3 and (shape-wise) T7. T4 precedes T5. T6 precedes T7. T8 is the
cross-cutting sweep. T3, T5, T7 are independently shippable behind feature flags.

External: hard-blocked on **37-3** (audit query) and **37-4** (export/signed URLs) being live before
the story is *done*; can be fully scaffolded + unit-tested against mocks before then. Chain (37-2),
retention (37-5), legal hold (37-6), DSAR/erasure (37-7/8), consent (37-9), evidence (37-11) gate their
respective sub-features only.

## Risks

- **Endpoint contract drift:** the client DTOs are written ahead of the real 37-3..37-11 endpoints.
  Mitigation: keep all field-mapping in the clients (single place to fix); reconcile against the
  as-built `apps/tamma-elsa` endpoints as each lands; the feature flags keep an unfinished surface
  dark.
- **Tenant isolation regression:** the headline invariant. Mitigation: `tenantId` comes only from
  `useAuth`, no foreign-tenant input on tenant pages, an explicit isolation test, and server-side 404
  as the real boundary. Never relax this for "admin convenience" — the cross-tenant view is
  admin-package-only behind `PlatformOwnerAccess`.
- **Signed-URL leakage:** a raw artifact URL in the DOM/logs is a compliance defect. Mitigation: only
  ever use the 37-4 ticket URL, discard after click, never log it, e2e asserts no raw URL appears.
- **Virtualization vs bundle size:** adding a data-grid lib bloats the SPA. Mitigation: `pnpm why`
  first; hand-roll windowing if nothing's already there.
- **Keyset paging mismatch:** if 37-3 ships offset paging instead of keyset, the 100k-row AC is at
  risk. Mitigation: this story's client assumes `cursor`/`nextCursor`; flag a contract conflict to the
  37-3 owner early rather than fall back to offset.
- **Meta-audit accidental suppression:** request de-dup/caching could hide reads from `AUDIT.QUERIED`.
  Mitigation: don't cache audit GETs in a way that skips the network; the meta-audit is a feature, not
  overhead.
- **Two-package duplication drift:** the table/badge shape exists twice. Mitigation: accept the
  duplication (precedent: `alerts.ts`); if it grows, a future shared package is a clean follow-up — out
  of scope here.

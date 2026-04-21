# Story 29-4 Implementation Plan — Platform-Admin Secret Management UI

**Status**: Planned (2026-04-20)
**Story brief**: [`29-4-platform-admin-ui.md`](./29-4-platform-admin-ui.md)
**Epic 29 phase**: UI layer — after 29-3.
**Branch**: `feat/story-29-4-platform-admin-secrets-ui`

---

## 1. Objective

Ship `app.tamma.dev/admin/secrets` — the platform admin's single pane
for operational secrets. Lists with purpose/consumers/schedule/status;
detail drawer with version history + audit; create/rotate/retire with
reveal-modal one-shot UX; consumer map renders typed links per system
(postgres → RLS runbook, cranl → tenant page, github_webhook →
installation, hmac → consumer).

## 2. Dependencies

Hard blockers:

- **Story 29-3** — reveal endpoint + create/rotate endpoints.
- **Story 29-6** — rotation workflow (SSE progress feed).
- **Epic 16** — RBAC (`platform_admin` role).
- **Story 16-3** — admin dashboard shell merged.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/dashboard/src/admin/secrets/SecretsPage.tsx` | List + filters. |
| `.../admin/secrets/SecretDetailDrawer.tsx` | Metadata + versions + audit. |
| `.../admin/secrets/CreateSecretForm.tsx` | Create modal. |
| `.../admin/secrets/RotateSecretDialog.tsx` | Rotate trigger + SSE progress. |
| `.../admin/secrets/RevealModal.tsx` | One-shot copy-to-clipboard (shared with 29-5). |
| `.../admin/secrets/ConsumerLink.tsx` | Typed consumer renderer. |
| `.../api-client/secrets.ts` | API hooks. |
| `/home/meywd/tamma/packages/dashboard/src/admin/secrets/__tests__/*.test.tsx` | Component tests. |
| `/home/meywd/tamma/packages/dashboard/e2e/admin-secrets.spec.ts` | Playwright E2E. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/packages/dashboard/src/router.tsx` | Add `/admin/secrets` routes. |
| `/home/meywd/tamma/packages/dashboard/src/admin/AdminSidebar.tsx` | Add "Secrets" nav link (gated by role). |

## 5. Sequence of changes

### Step 1 — API client (2h)

- `secrets.ts`: React Query hooks for list/detail/versions/audit/create/rotate/retire/reveal.
- **Commit**: `feat(admin-ui): secrets API client`.

### Step 2 — Consumer link + reveal modal (3h)

- `ConsumerLink` renders per system type using the lookup table.
- `RevealModal` — one-shot, clipboard copy, explicit confirm before dismiss; zeroes ref on dismiss.
- Component tests.
- **Commit**: `feat(admin-ui): consumer link + reveal modal`.

### Step 3 — Create form (3h)

- Slug validator for Name.
- Purpose dropdown.
- Consumer multi-select from lookup.
- Rotation schedule picker (None/Days/Cron).
- Initial value: auto-generate toggle + length.
- Submit → server → reveal modal.
- **Commit**: `feat(admin-ui): create secret form`.

### Step 4 — Rotate dialog + SSE (4h)

- Rotate button on drawer.
- SSE subscribe to `/admin/secrets/{id}/rotation-progress?workflowId=...`.
- Step indicator: started → push → probe → activated → retired.
- Reveal modal on activated for new value.
- **Commit**: `feat(admin-ui): rotate dialog with SSE progress`.

### Step 5 — Detail drawer (4h)

- Metadata pane.
- Version history table.
- Audit feed (last 20 with pagination).
- Retire-version action (gated per AC5).
- **Commit**: `feat(admin-ui): secret detail drawer`.

### Step 6 — List page (3h)

- Table with filters (status, purpose, overdue).
- Keyboard shortcuts: `c` create, `r` rotate focused row.
- URL-synced filters for shareable links.
- **Commit**: `feat(admin-ui): secrets list page`.

### Step 7 — E2E + a11y (3h)

- Playwright: create → reveal → rotate → reveal → audit feed visible.
- axe-core on page + drawer + modal.
- **Commit**: `test(admin-ui): secrets E2E + a11y`.

### Step 8 — Navigation + RBAC (2h)

- Admin sidebar link; hidden for non-platform-admins.
- Route guard.
- **Commit**: `feat(admin-ui): secrets navigation + RBAC`.

## 6. Test strategy

### Unit (Vitest + RTL)

- Each component with MSW-mocked API.
- Focus management in modals.

### Integration

- React Query hooks against MSW mocks replicating 29-3 server shapes.

### E2E (Playwright)

- Happy path per AC9.
- Retire version flow.
- RBAC: non-admin → 403 page.

### Accessibility

- axe clean on every path.

## 7. Rollback plan

- **Feature flag**: `AdminUI:Secrets=true` hides the page + nav.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. API client | 2 |
| 2. Consumer + reveal | 3 |
| 3. Create form | 3 |
| 4. Rotate + SSE | 4 |
| 5. Detail drawer | 4 |
| 6. List page | 3 |
| 7. E2E + a11y | 3 |
| 8. Nav + RBAC | 2 |
| **Total** | **24** (matches brief). |

## 9. Open questions

- **Pagination cursor vs. page-index** for audit feed: server
  returns cursor (opaque); client passes back. Consistent with 28-11
  events pagination.
- **SSE failure fallback**: if SSE breaks, fall back to 2s polling
  on rotation-progress endpoint.
- **Keyboard shortcut scoping**: `c` / `r` must not fire in form
  inputs. Standard shortcut library usage.
- **Copy-to-clipboard on iOS**: `navigator.clipboard.writeText`
  requires secure context. Verified — dash.tamma.dev is HTTPS.
- **Banner design for "save this value"**: matches GitHub Actions
  secrets UX. Copy available.

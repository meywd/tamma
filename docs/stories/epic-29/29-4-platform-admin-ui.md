# Story 29-4: Platform-Admin Secret Management UI

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform administrator**,
I want a dedicated secret-management page at `app.tamma.dev/admin/secrets` that lists every platform-scoped secret with its purpose, consumers, rotation schedule, last-rotated-at, and next-rotation-due-at, and lets me create, rotate, retire, and inspect version history,
so that I have a single pane of glass for the platform's operational secrets — matching the user's design intent: "Secret management UI tells what this key is, where it's used and so on".

## Acceptance Criteria

1. Route `app.tamma.dev/admin/secrets` in the existing admin dashboard lists all platform-scoped secrets in a table with columns: Name, Purpose, Consumers (rendered from `ConsumerRefs` via the lookup table from 29-1), Last Rotated, Next Due, Active Version, Health (colour-coded badge: green = active, amber = rotation overdue, red = last rotation failed).
2. Clicking a row opens a detail drawer showing: metadata, consumer map (with direct links — e.g. "Tamma API / TammaAppDbContext" links to `/admin/runtime/dbcontexts`; "Cranl app X" links to the tenant page), version history (table of versions with status), recent audit events (last 20).
3. "Create secret" action opens a form: Name (slug-validated), Purpose (dropdown), Consumers (multi-select from typed lookup), Owner (current admin, editable), Rotation Schedule (None / Every N days / Cron), Initial Value (auto-generate toggle + length). Submit triggers `POST /api/v1/admin/secrets`; response surfaces the reveal modal from 29-3.
4. "Rotate" action on a detail drawer triggers `POST /api/v1/admin/secrets/{id}/rotate`; optional new-value / auto-generate choice; reveal modal for the new value; progress indicator shows the rotation workflow (29-6) status (started, push-to-consumer, probe, activated, old-retired) via Server-Sent Events.
5. "Retire version" action on a version row flips that version to `RetiredGrace` or `Revoked` depending on whether a later Active version exists; disallowed on the Active version (must rotate first).
6. Consumer map renders differently per `ConsumerRef` type:
   - `postgres` → links to the RLS runbook + shows the role and which DbContext uses it.
   - `cranl` → links to the tenant's Cranl app page; shows the env var name that receives the value on rotate.
   - `github_webhook` → shows which installation + repo.
   - `hmac_shared` → shows which other process / endpoint expects this HMAC.
7. Audit event feed on detail drawer shows the last 20 `SECRET.*` events with `{ at, type, actor, outcome }`; "Load more" paginates.
8. RBAC: only users with `platform_admin` role (per Epic 16 RBAC model) see `/admin/secrets`. Non-admin requests return 403.
9. E2E test (Playwright + seeded Postgres): create a platform secret, verify reveal modal, rotate, verify new reveal modal, verify audit events in the side panel.
10. Accessible UI per existing dashboard a11y conventions (axe clean on main page + drawer + reveal modal). Keyboard shortcut `c` creates new secret, `r` rotates the focused row.

## Technical Context

### Component layout

```
packages/dashboard/src/admin/secrets/
  ├─ SecretsPage.tsx            — table + filters
  ├─ SecretDetailDrawer.tsx     — metadata + version history + audit
  ├─ CreateSecretForm.tsx       — create modal
  ├─ RotateSecretDialog.tsx     — rotate trigger + progress SSE
  ├─ RevealModal.tsx            — one-shot copy-to-clipboard (shared with 29-5)
  └─ ConsumerLink.tsx           — typed consumer renderer (shared)
```

### API surface consumed

Defined by Story 29-3 and 29-6:

- `GET /api/v1/admin/secrets?filter=...`
- `POST /api/v1/admin/secrets`
- `POST /api/v1/admin/secrets/{id}/rotate`
- `POST /api/v1/admin/secrets/{id}/retire-version/{versionNumber}`
- `GET /api/v1/admin/secrets/{id}/versions`
- `GET /api/v1/admin/secrets/{id}/audit?limit=20&cursor=...`
- `GET /api/v1/secrets/reveal/{revealToken}`
- SSE `GET /api/v1/admin/secrets/{id}/rotation-progress?workflowId=...`

### Design constraints

- No plaintext is ever rendered in the table, drawer, or version history.
- No plaintext is stored in React state beyond the `RevealModal`'s one-shot render; modal dismiss zeroes the string ref.
- Consumer lookup is driven by the typed lookup table so UI strings are not hand-written per secret.

## Estimated hours

24 — React pages, API client, Playwright coverage, a11y pass, SSE
progress wiring.

## Files to touch

- `packages/dashboard/src/admin/secrets/` (new folder)
- `packages/dashboard/src/api-client/secrets.ts` (new)

## References

- Admin dashboard shell: Epic 16 Story 16-3 (already merged)
- Design intent: user quote 2026-04-20
- Research notes §3

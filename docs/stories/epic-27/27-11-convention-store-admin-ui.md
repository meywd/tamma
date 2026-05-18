# Story 27-11: Convention Store Admin UI

> Updated 2026-05-18: keyword model removed; see SPEC docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md

Status: ready-for-dev

## Story

As a **platform administrator**,
I want an admin panel page for managing system default conventions with a resolution test panel,
so that I can author and tune coding conventions that are automatically injected into LLM calls via exact `(role, action)` lookup with tenant override (SPEC §3.3).

## Acceptance Criteria

### Core UI (Convention Management)

1. A "Conventions" page is accessible from the admin panel navigation under "System > Conventions"
2. The page displays a table of all system default conventions with columns: Name, Role, Action, Enabled, Last Updated
3. The table supports filtering by role (dropdown) and enabled/disabled (toggle)
4. The table supports text search across name, description, and body content
5. Clicking a row opens an edit panel (inline split view) with all editable fields
6. A "New Convention" button opens a blank edit panel for creating a new system default
7. The edit panel has fields: Role (dropdown, read-only after creation), Action (dropdown scoped to selected role, read-only after creation), Name, Description, Body (Markdown editor), Enabled (toggle)
8. The edit panel has a "Save" button that calls `PUT /api/admin/conventions/:key`
9. The edit panel has a "Reset to Default" button (for seeded conventions) that calls `POST /api/admin/conventions/:key/reset`
10. The edit panel has a "Delete" button that calls `DELETE /api/admin/conventions/:key` with confirmation dialog
11. All changes require confirmation dialog
12. Error states are displayed inline (API failures, validation errors)
13. Only platform admin users (owner role) can access this page

### Role/Action Selector

14. Role is selected via a dropdown populated from `GET /api/conventions/registry/roles`
15. Action is selected via a dropdown scoped to the selected role, populated from `GET /api/conventions/registry/actions`
16. The full valid `(role, action)` matrix is available from `GET /api/conventions/registry/role-actions`
17. Creating a new convention requires selecting a `(role, action)` pair; the pair is immutable after creation
18. Duplicate `(role, action)` pairs are prevented — the UI shows an error if a system default already exists for the chosen pair

### Resolution Test Panel

19. A collapsible "Test Resolution" panel at the top of the edit view
20. Input fields: Role (dropdown from known roles), Action (dropdown scoped to selected role)
21. A "Test" button calls `POST /api/conventions/resolve` with `{ role, action }`
22. Results show: the resolved convention body and its source (`"system"` or `"tenant"`), confirming which row was returned via exact `(role, action)` lookup with tenant override (SPEC §3.3)
23. If the current convention being edited does NOT match the selected `(role, action)`, show a warning: "This convention would not be resolved for this role/action"
24. If the current convention was resolved, show a success indicator with the source (`"system"` or `"tenant"`)

### Seed Convention Indicator

25. Conventions seeded from `ConventionTemplates.cs` are marked with a "System Seed" badge
26. The "Reset to Default" button only appears for seeded conventions (identified by matching a key in `ConventionTemplates.cs`)
27. Non-seeded conventions (admin-created) have Delete instead of Reset

### Convention Preview

28. The body editor includes a Markdown preview toggle (edit / preview / split)
29. In preview mode, the body is rendered as formatted Markdown

## Technical Context

### Dashboard Stack

Same as Story 27-4: React 19 + Vite + Tailwind CSS, served from `app.tamma.dev`.

### API Endpoints Consumed

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/conventions/defaults` | GET | List all system default conventions |
| `GET /api/conventions/defaults/:key` | GET | Get specific system default |
| `PUT /api/admin/conventions/:key` | PUT | Create/update system default |
| `DELETE /api/admin/conventions/:key` | DELETE | Delete system default |
| `POST /api/admin/conventions/:key/reset` | POST | Reset to hardcoded default |
| `POST /api/conventions/resolve` | POST | Test resolution with `{ role, action }` |
| `GET /api/conventions/registry/roles` | GET | Role list for dropdown |
| `GET /api/conventions/registry/actions` | GET | Action list scoped to role |
| `GET /api/conventions/registry/role-actions` | GET | Full `(role, action)` matrix |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/admin/ConventionsPage.tsx` | Main conventions admin page |
| `packages/dashboard/src/components/conventions/ConventionTable.tsx` | Filterable, searchable table |
| `packages/dashboard/src/components/conventions/ConventionEditPanel.tsx` | Edit panel with all fields |
| `packages/dashboard/src/components/conventions/RoleActionSelector.tsx` | Role/action dropdown pair with taxonomy validation |
| `packages/dashboard/src/components/conventions/ResolutionTestPanel.tsx` | Test resolution panel |
| `packages/dashboard/src/components/conventions/BodyEditor.tsx` | Markdown editor with preview |
| `packages/dashboard/src/hooks/useConventions.ts` | Data fetching hook for admin convention API |
| `packages/dashboard/src/hooks/useConventionResolve.ts` | Hook for resolve test endpoint |
| `packages/dashboard/src/pages/admin/ConventionsPage.test.tsx` | Page tests |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/routes.tsx` | Add route for `/admin/conventions` |
| `packages/dashboard/src/components/navigation/AdminNav.tsx` | Add "Conventions" nav link |

## Implementation Plan

### Phase 1: Data Hooks + Table (4h)

1. `useConventions()` hook wrapping admin API calls (list, get, upsert, delete, reset)
2. `useConventionResolve()` hook wrapping the resolve test endpoint
3. `ConventionTable` with category filter, enabled filter, text search
4. Route and navigation wiring

### Phase 2: Edit Panel (6h)

1. `ConventionEditPanel` — inline split view with all fields
2. `RoleActionSelector` — role dropdown + action dropdown scoped to role, from registry endpoints
3. `BodyEditor` — Markdown textarea with preview toggle
4. Enabled toggle
5. Save/Delete/Reset buttons with confirmation dialogs
6. Seed convention badge detection

### Phase 3: Test Panel (4h)

1. `ResolutionTestPanel` — role dropdown, action dropdown (scoped to role)
2. Call resolve endpoint (`POST /api/conventions/resolve`) with `{ role, action }` on "Test" button
3. Display resolved body and source (`"system"` or `"tenant"`)
4. Highlight whether current convention matches the resolved `(role, action)`
5. Warning/success indicator for the current convention

## Testing Strategy

### Unit Tests

1. `ConventionTable` renders rows from mock data
2. Role filter reduces displayed rows
3. Enabled filter shows/hides disabled conventions
4. Search filter matches against name, description, body
5. `ConventionEditPanel` displays all fields correctly
6. Save calls `PUT /api/admin/conventions/:role/:action` with correct body
7. Delete calls `DELETE` after confirmation
8. Reset calls `POST /reset` and updates the view
9. `RoleActionSelector` populates action dropdown based on selected role
10. `RoleActionSelector` prevents duplicate `(role, action)` pairs
11. `RoleActionSelector` shows correct actions for each role from registry
12. `ResolutionTestPanel` calls resolve endpoint with `{ role, action }`
13. Test panel shows success when current convention matches resolved `(role, action)`
14. Test panel shows warning when current convention does not match
15. Non-admin users see 403 message
16. Seed convention shows "System Seed" badge
17. `BodyEditor` toggles between edit and preview modes

### Integration Tests

18. Full edit flow: load page → click row → edit → save → verify updated
19. Create flow: new convention → fill fields → save → appears in table
20. Reset flow: edit seeded convention → reset → verify original restored
21. Test resolution flow: open test panel → enter context → test → see results

## Dependencies

- **Story 27-10** (Convention Store API Endpoints) — API endpoints must exist
- **Story 27-15** (Taxonomy) — canonical `(role, action)` enum values for registry dropdowns
- **Story 16.3** (Admin Dashboard) — admin panel framework
- **Story 16.5** (RBAC) — platform admin role check

## Estimated Effort

| Task | Hours |
|------|-------|
| Data hooks (useConventions, useConventionResolve) | 2 |
| Convention table + filters + search | 3 |
| Edit panel (all fields, save/delete/reset) | 4 |
| Role/action selector (dropdowns, taxonomy validation) | 2 |
| Body editor (Markdown textarea + preview) | 1.5 |
| Resolution test panel | 3 |
| Route/nav wiring + RBAC | 1 |
| Unit tests (17 tests) | 3 |
| Integration tests (4 tests) | 1.5 |
| **Total** | **21 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-05-04 | 1.0 | Initial story creation | Architecture Team |

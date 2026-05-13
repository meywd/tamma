# Story 27-12: Convention Store Tenant UI

Status: ready-for-dev

## Story

As a **tenant administrator**,
I want a page to manage my organization's convention overrides,
so that I can customize which coding conventions apply to my team's LLM calls without modifying platform defaults.

## Acceptance Criteria

1. A "Conventions" page is accessible from the tenant settings navigation under "Settings > Conventions"
2. The page displays all resolved conventions for the current tenant: system defaults with tenant overrides visually highlighted (badge, different background color, or left border)
3. Each convention row shows: Name, Key, Category, Keywords (tag pills), Source (System Default / Tenant Override), Priority, Always Apply, Enabled, Last Updated
4. Clicking a convention row opens an edit panel with all fields editable
5. When editing a system default, the panel shows: "This is a platform default. Saving will create a tenant override."
6. The "Save" button calls `PUT /api/conventions/:key` to create/update the tenant override
7. For overridden conventions, a "Reset to Default" button calls `DELETE /api/conventions/:key` to remove the override and fall back to the system default
8. A "New Convention" button allows tenant admins to create tenant-only conventions (keys that don't exist in system defaults)
9. Filtering by category (dropdown) and source (System Default / Override / Tenant-Only) is supported
10. A count indicator shows "X of Y conventions overridden" at the top of the page
11. Only tenant admin or owner users can modify conventions; regular members see conventions as read-only
12. All changes display a success/error toast notification

### Resolution Test Panel

13. Same resolution test panel as Story 27-11 but scoped to the tenant's resolved conventions
14. The test panel calls `POST /api/conventions/resolve` (which resolves for the current tenant)
15. Results show which conventions would fire for a given context, helping tenant admins verify their keyword configuration

### Convention Comparison

16. When viewing a tenant override, a "Compare with Default" toggle shows an inline diff of the override body vs. the system default body
17. The diff highlights added lines (green), removed lines (red), and unchanged lines (grey)

## Technical Context

### Difference from Admin UI (Story 27-11)

| Aspect | Admin UI (27-11) | Tenant UI (27-12) |
|--------|-----------------|-------------------|
| Scope | System defaults (global) | Tenant overrides (per-organization) |
| Users | Platform admins only | Tenant admins/owners |
| Edit target | `tenant_id IS NULL` rows | `tenant_id = <current>` rows |
| Reset behavior | Restores hardcoded `ConventionTemplates.cs` default | Deletes override, falls back to system default |
| Create | Creates new system default (available to all tenants) | Creates tenant-only convention or overrides system default |
| Test panel | Tests against system defaults only | Tests against tenant-resolved conventions |

### API Endpoints Consumed

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/conventions` | GET | List resolved conventions (merged view for current tenant) |
| `GET /api/conventions/:key` | GET | Get resolved convention |
| `PUT /api/conventions/:key` | PUT | Create/update tenant override |
| `DELETE /api/conventions/:key` | DELETE | Remove tenant override |
| `POST /api/conventions/resolve` | POST | Test resolution for current tenant |
| `GET /api/conventions/defaults/:key` | GET | Get system default for comparison |
| `GET /api/conventions/registry/categories` | GET | Category list for filter |
| `GET /api/conventions/registry/actions` | GET | Action list for test panel |
| `GET /api/conventions/registry/tools` | GET | Tool list for test panel |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/tenant/ConventionsPage.tsx` | Tenant conventions page |
| `packages/dashboard/src/components/conventions/TenantConventionTable.tsx` | Table with override highlighting |
| `packages/dashboard/src/components/conventions/TenantConventionEditor.tsx` | Editor with override-aware behavior |
| `packages/dashboard/src/components/conventions/ConventionDiff.tsx` | Inline diff of override vs. system default |
| `packages/dashboard/src/components/conventions/OverrideBadge.tsx` | Visual indicator for overridden conventions (reuse from prompts if exists) |
| `packages/dashboard/src/hooks/useTenantConventions.ts` | Data fetching hook for tenant conventions |
| `packages/dashboard/src/pages/tenant/ConventionsPage.test.tsx` | Page tests |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/routes.tsx` | Add route for `/settings/conventions` |
| `packages/dashboard/src/components/navigation/TenantNav.tsx` | Add "Conventions" link |

## Implementation Plan

### Step 1: Data Fetching Hook (2h)

Create `useTenantConventions()` hook:
- `fetchConventions()`: `GET /api/conventions` (merged view)
- `upsertOverride(key, data)`: `PUT /api/conventions/:key`
- `deleteOverride(key)`: `DELETE /api/conventions/:key`
- `getSystemDefault(key)`: `GET /api/conventions/defaults/:key` (for diff comparison)
- `overrideCount`: computed from merged list

### Step 2: Tenant Convention Table (3h)

Table with:
- Override rows highlighted with a subtle left border or background
- Source badge: "System Default", "Override", "Tenant-Only"
- Category and source dropdown filters
- Override count at the top

### Step 3: Tenant Convention Editor (3h)

Editor panel:
- Info banner for system defaults vs. overrides
- Full field editing: name, description, category, body, keywords, match mode, priority, always_apply, enabled
- Save/Delete/Reset buttons with override-aware behavior
- "New Convention" flow for tenant-only entries

### Step 4: Convention Diff (2h)

When viewing an override:
- Fetch the system default for the same key
- Show inline diff using a lightweight diff library (diff-match-patch or similar)
- Toggle between diff view and edit view

### Step 5: Resolution Test Panel (2h)

Reuse `ResolutionTestPanel` component from Story 27-11 (or extract a shared component). The only difference is the API endpoint resolves for the current tenant.

## Implementation Notes

1. The `GET /api/conventions` response must include `isOverride` and `source` fields. `source` can be: `"system"` (platform default), `"override"` (tenant override of a system default), `"tenant"` (tenant-only, no system default with same key).
2. Read-only mode for regular members: the editor opens but Save/Delete buttons are hidden. Keywords and test panel are still usable for reference.
3. The diff component should be lightweight — not a full code editor. A simple side-by-side or inline diff renderer is sufficient.
4. When a tenant creates a convention with a key that matches a system default, it becomes an override. When they create one with a new key, it's tenant-only. The UI should explain this distinction.
5. Shared components with Story 27-11: `KeywordEditor`, `BodyEditor`, `ResolutionTestPanel` should live in `components/conventions/` and be imported by both admin and tenant pages.
6. **Keyword display**: Keywords shown as tag pills in the table and editor are sourced from the API response's `keywords` array, which is joined from the normalized `convention_keywords` table by the service layer. The UI does not interact with the keywords table directly — it sends/receives `keywords: string[]` in JSON.

## Testing Strategy

### Unit Tests

1. `TenantConventionTable` renders conventions with correct source badges
2. Override conventions have visual distinction from system defaults
3. Override count shows correct number
4. Category and source filters work correctly
5. `TenantConventionEditor` shows info banner for system defaults
6. `TenantConventionEditor` shows "Reset to Default" for overrides
7. Save button calls `PUT /api/conventions/:key`
8. Reset button calls `DELETE /api/conventions/:key`
9. `ConventionDiff` displays inline diff correctly
10. `ConventionDiff` shows "No changes" when override matches default
11. Resolution test panel displays triggered/skipped results
12. Read-only mode hides Save/Delete for regular members
13. "New Convention" creates a blank form with key input

### Integration Tests

14. Full override lifecycle: view system default → create override → see highlighted → reset → falls back
15. Test resolution flow: create override with different keywords → test → verify changed behavior
16. Diff view: create override → toggle diff → verify diff shows changes

## Dependencies

- **Story 27-10** (Convention Store API Endpoints) — API endpoints must exist
- **Epic 16** (Story 16.1: OAuth, Story 16.5: RBAC) — authentication and tenant context
- **Story 27-11** (Admin UI) — shared components (KeywordEditor, BodyEditor, ResolutionTestPanel)

## Estimated Effort

| Task | Hours |
|------|-------|
| useTenantConventions hook | 2 |
| TenantConventionTable (table, badges, filters, count) | 3 |
| TenantConventionEditor (editor, override-aware behavior) | 3 |
| ConventionDiff component | 2 |
| Resolution test panel integration | 2 |
| Route and navigation wiring | 1 |
| Unit tests (13 tests) | 2 |
| Integration tests (3 tests) | 1 |
| **Total** | **16 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-05-04 | 1.0 | Initial story creation | Architecture Team |

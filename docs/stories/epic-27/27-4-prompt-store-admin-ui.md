# Story 27-4: Prompt Store Admin UI

Status: ready-for-dev

## Story

As a **platform administrator**,
I want an admin panel page for managing system default prompts,
so that I can view, edit, and reset the 80+ role+action templates, 8 system prompts, and 10 action defaults that ship with Tamma.

## Acceptance Criteria

1. A "Prompts" page is accessible from the admin panel navigation under "System > Prompts"
2. The page displays a table of all system default prompts (80 role+action templates) with columns: Role, Action, Version, Enable Tools, Max Tokens, Last Updated
3. The table supports filtering by role (dropdown) and action (dropdown)
4. The table supports text search across template content
5. Clicking a row opens an edit dialog/drawer with: template editor (monospaced, syntax-highlighted textarea), variable list (auto-extracted from `{{...}}` patterns), system prompt textarea, tools toggle, max tokens input
6. The edit dialog shows a "Variables" section listing all `{{variable}}` placeholders found in the template, with their names
7. The edit dialog has a "Save" button that calls `PUT /api/prompts/system/:role/:action`
8. The edit dialog has a "Reset to Default" button that calls `DELETE /api/prompts/system/:role/:action` to restore the hardcoded default
9. A separate "System Prompts" tab shows the 8 role system prompts with inline editing
10. A separate "Action Defaults" tab shows the 10 action default templates with editing
11. A "Convention Templates" section shows the 20 convention templates (read-only, since these are static)
12. A "Convention Template Selector" dropdown lets the admin preview and copy convention text for use in prompt templates
13. All changes require confirmation ("Are you sure you want to update the system default for developer/implement?")
14. Error states are displayed inline (API failures, validation errors)
15. Only platform admin users (owner role) can access this page; non-admins see a 403 message

## Technical Context

### Dashboard Stack

The admin dashboard is a React SPA served from `app.tamma.dev`. It uses:
- React 19 with Vite
- Tailwind CSS for styling
- React Router for navigation
- Fetch API for HTTP calls to `api.tamma.dev`

### Existing Admin Panel

The admin dashboard already has navigation and page structure from Epic 16 (Story 16.3: Admin Dashboard). This story adds a new page within that framework.

### API Endpoints Consumed

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/prompts/system` | GET | List all system default prompts |
| `GET /api/prompts/system/:role/:action` | GET | Get specific system default |
| `PUT /api/prompts/system/:role/:action` | PUT | Update system default |
| `DELETE /api/prompts/system/:role/:action` | DELETE | Reset to hardcoded default |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/admin/PromptsPage.tsx` | Main prompts admin page |
| `packages/dashboard/src/components/prompts/PromptTable.tsx` | Table component for prompt listing |
| `packages/dashboard/src/components/prompts/PromptEditDialog.tsx` | Edit dialog/drawer |
| `packages/dashboard/src/components/prompts/SystemPromptEditor.tsx` | System prompt (role preamble) editor |
| `packages/dashboard/src/components/prompts/ActionDefaultEditor.tsx` | Action default editor |
| `packages/dashboard/src/components/prompts/ConventionPreview.tsx` | Convention template preview |
| `packages/dashboard/src/components/prompts/VariableExtractor.tsx` | Auto-extract and display `{{variables}}` |
| `packages/dashboard/src/hooks/usePrompts.ts` | Data fetching hook for prompt API calls |
| `packages/dashboard/src/pages/admin/PromptsPage.test.tsx` | Page tests |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/routes.tsx` (or equivalent) | Add route for `/admin/prompts` |
| `packages/dashboard/src/components/navigation/AdminNav.tsx` (or equivalent) | Add "Prompts" link |

## Implementation Plan

### Step 1: Data Fetching Hook

Create `usePrompts()` hook that wraps the API calls:

```typescript
function usePrompts() {
  const [prompts, setPrompts] = useState<PromptSummary[]>([]);
  const [loading, setLoading] = useState(true);
  // ...
  async function fetchSystemDefaults() { /* GET /api/prompts/system */ }
  async function updateSystemDefault(role, action, data) { /* PUT /api/prompts/system/:role/:action */ }
  async function resetSystemDefault(role, action) { /* DELETE /api/prompts/system/:role/:action */ }
  return { prompts, loading, updateSystemDefault, resetSystemDefault, refetch };
}
```

### Step 2: Prompt Table

Sortable, filterable table with role/action dropdowns:

- Columns: Role, Action, Version, Tools, Max Tokens, Updated
- Row click opens PromptEditDialog
- Filter by role (8 options + "All")
- Filter by action (10 options + "All")
- Search across template text (client-side filter)

### Step 3: Edit Dialog

Modal or drawer with:
- Template textarea (monospaced, ~30 lines visible)
- Auto-extracted variables list (real-time as user types)
- System prompt textarea (for this specific role+action override)
- Enable tools checkbox
- Max tokens number input
- Save and Reset buttons

### Step 4: System Prompts Tab

Simple table with 8 rows (one per role):
- Role name
- System prompt text (first 100 chars preview)
- Edit button opens inline editor

### Step 5: Convention Preview

Dropdown of 20 convention templates:
- Selecting one shows the full conventions text
- "Copy to clipboard" button
- Useful for admins who want to incorporate convention text into prompt templates

## Implementation Notes

1. The template editor should use a monospaced font (e.g., `font-mono` in Tailwind) for readability. A full code editor (Monaco, CodeMirror) is optional for v1; a styled textarea is sufficient.
2. Variable extraction is done client-side by scanning for `{{...}}` patterns in real-time as the admin types. This matches the server-side `_extractVariables()` logic.
3. The "Reset to Default" button should show the diff between current and hardcoded default before confirming (optional enhancement).
4. Pagination is not needed for 80 rows; the full table loads at once. Filtering is client-side.
5. The convention templates are fetched from a separate endpoint (`GET /api/conventions` or bundled in the dashboard build). Since they are static TypeScript constants, they can be imported at build time or fetched once.
6. Optimistic UI updates: show the change immediately, then revert if the API call fails.

## Testing Strategy

### Unit Tests

1. `PromptTable` renders 80 rows from mock data
2. Role filter reduces displayed rows to the correct subset (10 per role)
3. Action filter reduces displayed rows to the correct subset (8 per action)
4. Search filter matches against template content
5. `PromptEditDialog` displays template, variables, system prompt, tools, max tokens
6. `PromptEditDialog` extracts variables from template text in real time
7. Save button calls `PUT /api/prompts/system/:role/:action` with correct body
8. Reset button calls `DELETE /api/prompts/system/:role/:action` after confirmation
9. `SystemPromptEditor` renders 8 rows and supports inline editing
10. Non-admin users see 403 message (role check in route guard)

### Integration Tests

11. Full edit flow: load page, click row, edit template, save, verify updated in table
12. Reset flow: edit a prompt, reset, verify original content restored

## Dependencies

- **Story 27-3** (Prompt Store API Endpoints) -- API endpoints must exist
- **Epic 16** (Story 16.3: Admin Dashboard) -- admin panel framework and navigation must exist
- **Epic 16** (Story 16.5: RBAC) -- platform admin role check

## Estimated Effort

| Task | Hours |
|------|-------|
| usePrompts hook (data fetching) | 2 |
| PromptTable component (table, filters, search) | 3 |
| PromptEditDialog component (editor, variables, save, reset) | 4 |
| SystemPromptEditor component | 2 |
| ConventionPreview component | 1 |
| Route and navigation wiring | 1 |
| Unit tests (10 tests) | 2 |
| Integration tests (2 tests) | 1 |
| **Total** | **16 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |

# Story 27-5: Prompt Store Account UI

Status: ready-for-dev

## Story

As an **account administrator**,
I want a page to manage my organization's prompt overrides,
so that I can customize how Tamma's AI agents behave for my team without modifying system defaults.

## Acceptance Criteria

1. An "AI Prompts" page is accessible from the account settings navigation under "Settings > AI Prompts"
2. The page displays all resolved prompts for the current account: system defaults with account overrides visually highlighted (e.g., badge, different background color, or icon)
3. Each prompt row shows: Role, Action, Source (System Default / Account Override), Version, Enable Tools, Max Tokens, Last Updated
4. Clicking a prompt row opens an edit panel with: current template (editable), variable list (auto-extracted), system prompt (editable), tools toggle, max tokens input
5. When editing a system default, the panel shows a "This is a system default. Saving will create an account override." notice
6. The "Save" button calls `PUT /api/prompts/:role/:action` to create/update the account override
7. For overridden prompts, a "Reset to Default" button calls `DELETE /api/prompts/:role/:action` to remove the override and fall back to the system default
8. A "Convention Template" selector dropdown allows the admin to choose from the 20 convention templates and insert the text into the `{{conventions}}` variable field or directly into the template
9. A "Preview / Test" panel allows the admin to enter sample variable values and see the rendered prompt output (calls `POST /api/prompts/:role/:action/render`)
10. The preview panel shows: rendered template text, rendered system prompt, unresolved variables (highlighted)
11. Filtering by role (dropdown) and action (dropdown) is supported
12. A count indicator shows "X of 80 prompts overridden" at the top of the page
13. Only account admin or owner users can modify prompts; regular members see prompts as read-only
14. All changes display a success/error toast notification

## Technical Context

### Account Context

The dashboard knows the current user's account via the JWT token / session from Epic 16. The account ID is passed to the API automatically via the auth middleware.

### API Endpoints Consumed

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /api/prompts` | GET | List resolved prompts (merged view for current account) |
| `GET /api/prompts/:role/:action` | GET | Get resolved prompt |
| `PUT /api/prompts/:role/:action` | PUT | Create/update account override |
| `DELETE /api/prompts/:role/:action` | DELETE | Remove account override |
| `POST /api/prompts/:role/:action/render` | POST | Render prompt with test variables |

### Difference from Admin UI (Story 27-4)

| Aspect | Admin UI (27-4) | Account UI (27-5) |
|--------|----------------|-------------------|
| Scope | System defaults (global) | Account overrides (per-organization) |
| Users | Platform admins only | Account admins/owners |
| Edit target | `account_id IS NULL` rows | `account_id = <current>` rows |
| Reset behavior | Restores hardcoded TypeScript default | Deletes override, falls back to system default |
| Convention templates | Preview and copy | Selector for quick setup |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/account/PromptsPage.tsx` | Account prompts page |
| `packages/dashboard/src/components/prompts/AccountPromptTable.tsx` | Table with override highlighting |
| `packages/dashboard/src/components/prompts/AccountPromptEditor.tsx` | Editor with override-aware behavior |
| `packages/dashboard/src/components/prompts/PromptPreview.tsx` | Variable input + rendered output preview |
| `packages/dashboard/src/components/prompts/ConventionSelector.tsx` | Convention template dropdown with insert |
| `packages/dashboard/src/components/prompts/OverrideBadge.tsx` | Visual indicator for overridden prompts |
| `packages/dashboard/src/hooks/useAccountPrompts.ts` | Data fetching hook for account prompts |
| `packages/dashboard/src/pages/account/PromptsPage.test.tsx` | Page tests |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/routes.tsx` (or equivalent) | Add route for `/settings/prompts` |
| `packages/dashboard/src/components/navigation/AccountNav.tsx` (or equivalent) | Add "AI Prompts" link |

## Implementation Plan

### Step 1: Data Fetching Hook

Create `useAccountPrompts()` hook:

```typescript
function useAccountPrompts() {
  const [prompts, setPrompts] = useState<ResolvedPrompt[]>([]);
  const [loading, setLoading] = useState(true);

  async function fetchPrompts() { /* GET /api/prompts */ }
  async function upsertOverride(role, action, data) { /* PUT /api/prompts/:role/:action */ }
  async function deleteOverride(role, action) { /* DELETE /api/prompts/:role/:action */ }
  async function renderPreview(role, action, variables) { /* POST /api/prompts/:role/:action/render */ }

  const overrideCount = prompts.filter(p => p.isOverride).length;

  return { prompts, loading, overrideCount, upsertOverride, deleteOverride, renderPreview, refetch };
}
```

The API response for `GET /api/prompts` must include a field indicating whether each prompt is an account override or a system default. This can be a boolean `isOverride` field or an `accountId` field (non-null = override).

### Step 2: Account Prompt Table

Table with:
- Columns: Role, Action, Source (badge: "Override" or "Default"), Version, Tools, Max Tokens, Updated
- Override rows highlighted with a subtle background color or left border
- Role and action dropdown filters
- Override count at the top: "3 of 80 prompts overridden"

### Step 3: Account Prompt Editor

Editor panel (drawer or modal):
- If editing a system default: show info banner "This is a system default. Saving will create an override for your account."
- If editing an existing override: show "This is an account override." with "Reset to Default" button
- Template textarea with monospaced font
- Variable list (auto-extracted)
- System prompt textarea
- Enable tools toggle
- Max tokens input
- Convention selector: dropdown of 20 templates, selecting one inserts text at cursor or replaces selected text

### Step 4: Prompt Preview Panel

A collapsible panel below the editor:
- Input fields for each detected `{{variable}}` in the template
- Pre-fill with example values where possible (e.g., `role` = "developer")
- "Render Preview" button calls the render API
- Shows: rendered template (read-only), rendered system prompt, unresolved variables highlighted in red

### Step 5: Convention Selector

Dropdown listing 20 convention templates:
- Shows name and short description
- On select: inserts the conventions text into the template at the `{{conventions}}` placeholder position, or copies to clipboard

## Implementation Notes

1. The `GET /api/prompts` response needs to distinguish overrides from defaults. The API should include `source: 'system' | 'override'` or `accountId: string | null` in each prompt summary. This requires a minor addition to the `list()` method's response format in Story 27-2/27-3.
2. The preview panel makes API calls on demand (not on every keystroke). Debounce or explicit "Preview" button is used to avoid excessive API calls.
3. Read-only mode for regular members: the editor opens but Save/Delete buttons are disabled or hidden. The convention selector is still usable for reference.
4. The override count is computed client-side from the prompt list.
5. Convention text can be large (500-1000 chars). The selector should show a truncated preview with an "expand" option.
6. Mobile responsiveness: the table collapses to a card view on small screens.

## Testing Strategy

### Unit Tests

1. `AccountPromptTable` renders prompts with correct source badges
2. Override prompts have visual distinction from system defaults
3. Override count shows correct number
4. Role and action filters work correctly
5. `AccountPromptEditor` shows info banner for system defaults
6. `AccountPromptEditor` shows "Reset to Default" for overrides
7. Save button calls `PUT /api/prompts/:role/:action`
8. Reset button calls `DELETE /api/prompts/:role/:action`
9. `PromptPreview` displays variable input fields matching template variables
10. `PromptPreview` renders output from API response
11. `ConventionSelector` lists 20 templates and inserts text on selection
12. Read-only mode hides Save/Delete for regular members

### Integration Tests

13. Full override lifecycle: view system default, create override, see it highlighted, reset to default
14. Preview flow: select prompt, enter variables, preview rendered output

## Dependencies

- **Story 27-3** (Prompt Store API Endpoints) -- API endpoints must exist
- **Epic 16** (Story 16.1: OAuth, Story 16.5: RBAC) -- authentication and account context
- Internal: `packages/api/src/services/convention-templates.ts` (for convention template data)

## Estimated Effort

| Task | Hours |
|------|-------|
| useAccountPrompts hook (data fetching) | 2 |
| AccountPromptTable component (table, badges, filters, count) | 3 |
| AccountPromptEditor component (editor, override-aware behavior) | 3 |
| PromptPreview component (variable inputs, render, display) | 3 |
| ConventionSelector component | 1.5 |
| Route and navigation wiring | 1 |
| Unit tests (12 tests) | 1.5 |
| Integration tests (2 tests) | 1 |
| **Total** | **16 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |

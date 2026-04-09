# Story 27-4: Prompt Store Admin UI — Implementation Plan

## Overview

Add a "Prompts" admin page at `/admin/prompts` within the existing dashboard at `packages/dashboard/`. The page manages system default prompts (80 role+action templates, 8 system prompts, 10 action defaults) via the `/api/prompts/system*` endpoints from Story 27-3. Only platform admins (owner role) can access this page.

---

## Step-by-Step Implementation Tasks

### Task 1: Create Data Fetching Hook (2 hours)

**File to create**: `packages/dashboard/src/hooks/useSystemPrompts.ts`

```typescript
import { useState, useEffect, useCallback } from 'react';

export interface SystemPromptSummary {
  role: string;
  action: string;
  version: number;
  enableTools: boolean;
  maxTokens: number;
  variableCount: number;
  updatedAt: string;
  source: 'system' | 'override';
  tenantId: string | null;
}

export interface SystemPromptDetail {
  role: string;
  action: string;
  version: number;
  template: string;
  variables: string[];
  systemPrompt: string;
  enableTools: boolean;
  maxTokens: number;
  createdAt: string;
  updatedAt: string;
}

export interface UseSystemPromptsReturn {
  prompts: SystemPromptSummary[];
  loading: boolean;
  error: string | null;
  fetchPrompts: () => Promise<void>;
  getPrompt: (role: string, action: string) => Promise<SystemPromptDetail | null>;
  updatePrompt: (role: string, action: string, data: UpdatePromptData) => Promise<SystemPromptDetail>;
  resetPrompt: (role: string, action: string) => Promise<SystemPromptDetail | null>;
}

export interface UpdatePromptData {
  template: string;
  variables?: string[];
  systemPrompt?: string;
  enableTools?: boolean;
  maxTokens?: number;
}

export function useSystemPrompts(): UseSystemPromptsReturn {
  const [prompts, setPrompts] = useState<SystemPromptSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const apiBase = import.meta.env.VITE_API_URL ?? '';

  const fetchPrompts = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`${apiBase}/api/prompts/system`, { credentials: 'include' });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setPrompts(data.templates);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch prompts');
    } finally {
      setLoading(false);
    }
  }, [apiBase]);

  const getPrompt = useCallback(async (role: string, action: string): Promise<SystemPromptDetail | null> => {
    const res = await fetch(`${apiBase}/api/prompts/system/${role}/${action}`, { credentials: 'include' });
    if (!res.ok) return null;
    return res.json();
  }, [apiBase]);

  const updatePrompt = useCallback(async (role: string, action: string, data: UpdatePromptData): Promise<SystemPromptDetail> => {
    const res = await fetch(`${apiBase}/api/prompts/system/${role}/${action}`, {
      method: 'PUT', credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data),
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: `HTTP ${res.status}` }));
      throw new Error(err.error ?? 'Failed to update prompt');
    }
    return res.json();
  }, [apiBase]);

  const resetPrompt = useCallback(async (role: string, action: string): Promise<SystemPromptDetail | null> => {
    const res = await fetch(`${apiBase}/api/prompts/system/${role}/${action}`, {
      method: 'DELETE', credentials: 'include',
    });
    if (!res.ok) return null;
    return res.json();
  }, [apiBase]);

  useEffect(() => { fetchPrompts(); }, [fetchPrompts]);

  return { prompts, loading, error, fetchPrompts, getPrompt, updatePrompt, resetPrompt };
}
```

---

### Task 2: Create Prompt Table Component (3 hours)

**File to create**: `packages/dashboard/src/components/prompts/PromptTable.tsx`

Features:
- Displays 80 rows with columns: Role, Action, Version, Tools, Max Tokens, Updated
- Role dropdown filter (8 roles + "All")
- Action dropdown filter (10 actions + "All")
- Text search across template content (client-side)
- Row click handler to open edit dialog

```typescript
interface PromptTableProps {
  prompts: SystemPromptSummary[];
  onRowClick: (role: string, action: string) => void;
}

export function PromptTable({ prompts, onRowClick }: PromptTableProps): JSX.Element {
  const [roleFilter, setRoleFilter] = useState<string>('all');
  const [actionFilter, setActionFilter] = useState<string>('all');
  const [searchQuery, setSearchQuery] = useState('');
  // ... filter logic, render table rows
}
```

Key UI elements:
- Filter bar at top with two `<select>` dropdowns and a search `<input>`
- `<table>` with clickable `<tr>` rows
- Tailwind classes: `font-mono` for template previews, `hover:bg-gray-50` for rows

---

### Task 3: Create Edit Dialog Component (4 hours)

**File to create**: `packages/dashboard/src/components/prompts/PromptEditDialog.tsx`

A modal dialog for editing a system default prompt.

```typescript
interface PromptEditDialogProps {
  open: boolean;
  role: string;
  action: string;
  onClose: () => void;
  onSaved: () => void;
  getPrompt: (role: string, action: string) => Promise<SystemPromptDetail | null>;
  updatePrompt: (role: string, action: string, data: UpdatePromptData) => Promise<SystemPromptDetail>;
  resetPrompt: (role: string, action: string) => Promise<SystemPromptDetail | null>;
}

export function PromptEditDialog(props: PromptEditDialogProps): JSX.Element | null {
  // State: template, systemPrompt, enableTools, maxTokens, variables (auto-extracted)
  // Load prompt on open via getPrompt()
  // Save button calls updatePrompt()
  // Reset button calls resetPrompt() after confirmation
}
```

Editor sections:
1. **Template** — `<textarea>` with monospaced font, ~30 lines visible, resizable
2. **Variables** — Auto-extracted `{{...}}` list, real-time update as user types (using `extractVariables()` from prompt-interpolation or client-side regex)
3. **System Prompt** — Secondary `<textarea>` for the role+action specific system prompt override
4. **Enable Tools** — Toggle/checkbox
5. **Max Tokens** — Number input with validation (> 0)
6. **Save** / **Reset to Default** / **Cancel** buttons

Variable extraction (client-side):
```typescript
function extractClientVariables(template: string): string[] {
  const matches = template.matchAll(/\{\{([^}]{1,64})\}\}/g);
  const vars = new Set<string>();
  for (const match of matches) {
    if (match[1]) vars.add(match[1]);
  }
  return [...vars];
}
```

---

### Task 4: Create System Prompt Editor Component (2 hours)

**File to create**: `packages/dashboard/src/components/prompts/SystemPromptEditor.tsx`

A tab showing the 8 role system prompts with inline editing.

```typescript
interface SystemPromptEditorProps {
  // Uses a separate API or the system_prompts table directly
  // For v1, fetched from the prompts table's systemPrompt field
}

export function SystemPromptEditor(): JSX.Element {
  // Table with 8 rows: Role, Prompt (truncated preview), Edit button
  // Clicking Edit opens inline textarea
  // Save calls PUT /api/prompts/system/:role/:action for each affected prompt
}
```

---

### Task 5: Create Convention Preview Component (1 hour)

**File to create**: `packages/dashboard/src/components/prompts/ConventionPreview.tsx`

Dropdown of 20 convention templates (read-only). Data source: can be fetched from `/api/conventions` endpoint or bundled as a static import.

```typescript
export function ConventionPreview(): JSX.Element {
  // Dropdown <select> listing convention templates by name
  // On select, display full conventions text in read-only <pre> block
  // "Copy to clipboard" button
}
```

Convention data is fetched from:
- **Option A**: `GET /api/conventions` (already exists at `packages/api/src/routes/convention-templates.ts`)
- **Option B**: Static import of `CONVENTION_TEMPLATES` at build time

Option A is preferred for consistency.

---

### Task 6: Create Admin Prompts Page (1 hour)

**File to create**: `packages/dashboard/src/pages/admin/AdminPromptsPage.tsx`

```typescript
export function AdminPromptsPage(): JSX.Element {
  const { prompts, loading, error, fetchPrompts, getPrompt, updatePrompt, resetPrompt } = useSystemPrompts();
  const [selectedPrompt, setSelectedPrompt] = useState<{ role: string; action: string } | null>(null);
  const [activeTab, setActiveTab] = useState<'templates' | 'system-prompts' | 'conventions'>('templates');

  return (
    <div>
      <h1>System Prompts</h1>
      {/* Tab bar: Templates | System Prompts | Conventions */}
      {activeTab === 'templates' && (
        <PromptTable prompts={prompts} onRowClick={(role, action) => setSelectedPrompt({ role, action })} />
      )}
      {activeTab === 'system-prompts' && <SystemPromptEditor />}
      {activeTab === 'conventions' && <ConventionPreview />}

      {selectedPrompt && (
        <PromptEditDialog
          open={true}
          role={selectedPrompt.role}
          action={selectedPrompt.action}
          onClose={() => setSelectedPrompt(null)}
          onSaved={() => { setSelectedPrompt(null); fetchPrompts(); }}
          getPrompt={getPrompt}
          updatePrompt={updatePrompt}
          resetPrompt={resetPrompt}
        />
      )}
    </div>
  );
}
```

---

### Task 7: Route and Navigation Wiring (1 hour)

**File to modify**: `packages/dashboard/src/router.tsx`

Add route for `/admin/prompts`:

```typescript
import { AdminPromptsPage } from './pages/admin/AdminPromptsPage.js';

// Inside router children array, after existing admin route:
{
  path: '/admin/prompts',
  element: (
    <AdminGuard>
      <AdminPromptsPage />
    </AdminGuard>
  ),
},
```

**File to modify**: `packages/dashboard/src/components/layout/Sidebar.tsx`

Add "Prompts" link under the admin section:

```typescript
{ label: 'System Prompts', path: '/admin/prompts', icon: 'document-text' },
```

---

### Task 8: Unit Tests (2 hours)

**File to create**: `packages/dashboard/src/pages/admin/AdminPromptsPage.test.tsx`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `PromptTable` renders rows from mock data | All 80 rows displayed |
| 2 | Role filter reduces rows | Selecting "developer" shows 10 rows |
| 3 | Action filter reduces rows | Selecting "implement" shows 8 rows |
| 4 | Search filter matches template content | Query "implement" filters correctly |
| 5 | `PromptEditDialog` displays loaded prompt | Template, variables, tools shown |
| 6 | Variable extraction updates in real time | Typing `{{newVar}}` adds to list |
| 7 | Save button calls PUT endpoint | `updatePrompt` called with correct args |
| 8 | Reset button shows confirmation | Confirm dialog appears before API call |
| 9 | `SystemPromptEditor` renders 8 roles | All roles displayed |
| 10 | Non-admin sees 403 | `AdminGuard` blocks access |

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/dashboard/src/hooks/useSystemPrompts.ts` | Data fetching hook |
| 2 | `packages/dashboard/src/components/prompts/PromptTable.tsx` | Table with filters |
| 3 | `packages/dashboard/src/components/prompts/PromptEditDialog.tsx` | Edit modal |
| 4 | `packages/dashboard/src/components/prompts/SystemPromptEditor.tsx` | Role preamble editor |
| 5 | `packages/dashboard/src/components/prompts/ConventionPreview.tsx` | Convention viewer |
| 6 | `packages/dashboard/src/pages/admin/AdminPromptsPage.tsx` | Admin page |
| 7 | `packages/dashboard/src/pages/admin/AdminPromptsPage.test.tsx` | Tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/dashboard/src/router.tsx` | Add `/admin/prompts` route |
| 2 | `packages/dashboard/src/components/layout/Sidebar.tsx` | Add "System Prompts" nav link |

---

## Dependencies

- **Story 27-3** (API Endpoints) — `/api/prompts/system*` endpoints must exist
- **Epic 16** (Story 16.3: Admin Dashboard) — `AdminGuard`, `AppLayout`, existing navigation
- **Existing**: `packages/dashboard/src/components/common/` — reuse `Card`, `LoadingSpinner`, `ConfirmDialog`, `Toggle`, `FormField`

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Template text could be very long (50KB), slowing the edit dialog | Lazy-load template content only when dialog opens (`getPrompt()` on open, not on page load) |
| 80 rows in a single table page load | Client-side filtering is sufficient for 80 rows; no server-side pagination needed |
| Convention template text is large | Show truncated preview in dropdown; expand on selection |
| Monaco/CodeMirror editor would be better UX | Deferred to a follow-up enhancement; monospaced `<textarea>` is sufficient for v1 |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| useSystemPrompts hook | 2 |
| PromptTable component | 3 |
| PromptEditDialog component | 4 |
| SystemPromptEditor component | 2 |
| ConventionPreview component | 1 |
| AdminPromptsPage + wiring | 2 |
| Unit tests (10 tests) | 2 |
| **Total** | **16 hours** |

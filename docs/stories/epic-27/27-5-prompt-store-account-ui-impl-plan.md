# Story 27-5: Prompt Store Account UI — Implementation Plan

## Overview

Add an "AI Prompts" page at `/settings/prompts` (account scope) within the existing dashboard. Account admins can create overrides of system default prompts, preview rendered output with test variables, and use a convention template selector. Regular members see prompts as read-only.

---

## Step-by-Step Implementation Tasks

### Task 1: Create Data Fetching Hook (2 hours)

**File to create**: `packages/dashboard/src/hooks/useAccountPrompts.ts`

```typescript
import { useState, useEffect, useCallback } from 'react';

export interface ResolvedPrompt {
  role: string;
  action: string;
  version: number;
  enableTools: boolean;
  maxTokens: number;
  variableCount: number;
  updatedAt: string;
  source: 'system' | 'override';
  accountId: string | null;
}

export interface PromptDetail {
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

export interface RenderedResult {
  renderedTemplate: string;
  renderedSystemPrompt: string;
  unresolvedVariables: string[];
  enableTools: boolean;
  maxTokens: number;
}

export interface UseAccountPromptsReturn {
  prompts: ResolvedPrompt[];
  loading: boolean;
  error: string | null;
  overrideCount: number;
  fetchPrompts: () => Promise<void>;
  getPrompt: (role: string, action: string) => Promise<PromptDetail | null>;
  upsertOverride: (role: string, action: string, data: UpsertData) => Promise<PromptDetail>;
  deleteOverride: (role: string, action: string) => Promise<boolean>;
  renderPreview: (role: string, action: string, variables: Record<string, string>) => Promise<RenderedResult | null>;
}

interface UpsertData {
  template: string;
  variables?: string[];
  systemPrompt?: string;
  enableTools?: boolean;
  maxTokens?: number;
}

export function useAccountPrompts(): UseAccountPromptsReturn {
  const [prompts, setPrompts] = useState<ResolvedPrompt[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const apiBase = import.meta.env.VITE_API_URL ?? '';

  const fetchPrompts = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`${apiBase}/api/prompts`, { credentials: 'include' });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setPrompts(data.templates);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch prompts');
    } finally {
      setLoading(false);
    }
  }, [apiBase]);

  const getPrompt = useCallback(async (role: string, action: string) => {
    const res = await fetch(`${apiBase}/api/prompts/${role}/${action}`, { credentials: 'include' });
    if (!res.ok) return null;
    return res.json();
  }, [apiBase]);

  const upsertOverride = useCallback(async (role: string, action: string, data: UpsertData) => {
    const res = await fetch(`${apiBase}/api/prompts/${role}/${action}`, {
      method: 'PUT', credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data),
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: `HTTP ${res.status}` }));
      throw new Error(err.error ?? 'Failed to save override');
    }
    return res.json();
  }, [apiBase]);

  const deleteOverride = useCallback(async (role: string, action: string) => {
    const res = await fetch(`${apiBase}/api/prompts/${role}/${action}`, {
      method: 'DELETE', credentials: 'include',
    });
    return res.ok;
  }, [apiBase]);

  const renderPreview = useCallback(async (role: string, action: string, variables: Record<string, string>) => {
    const res = await fetch(`${apiBase}/api/prompts/${role}/${action}/render`, {
      method: 'POST', credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ variables }),
    });
    if (!res.ok) return null;
    return res.json();
  }, [apiBase]);

  useEffect(() => { fetchPrompts(); }, [fetchPrompts]);

  const overrideCount = prompts.filter((p) => p.source === 'override').length;

  return { prompts, loading, error, overrideCount, fetchPrompts, getPrompt, upsertOverride, deleteOverride, renderPreview };
}
```

---

### Task 2: Create Account Prompt Table Component (3 hours)

**File to create**: `packages/dashboard/src/components/prompts/AccountPromptTable.tsx`

```typescript
interface AccountPromptTableProps {
  prompts: ResolvedPrompt[];
  overrideCount: number;
  onRowClick: (role: string, action: string) => void;
}

export function AccountPromptTable({ prompts, overrideCount, onRowClick }: AccountPromptTableProps): JSX.Element {
  const [roleFilter, setRoleFilter] = useState('all');
  const [actionFilter, setActionFilter] = useState('all');

  // ... filter logic

  return (
    <div>
      {/* Override counter */}
      <div className="mb-4 text-sm text-gray-600">
        {overrideCount} of {prompts.length} prompts overridden
      </div>

      {/* Filter bar */}
      <div className="flex gap-4 mb-4">
        <select value={roleFilter} onChange={(e) => setRoleFilter(e.target.value)}>
          <option value="all">All Roles</option>
          {/* 8 role options */}
        </select>
        <select value={actionFilter} onChange={(e) => setActionFilter(e.target.value)}>
          <option value="all">All Actions</option>
          {/* 10 action options */}
        </select>
      </div>

      {/* Table */}
      <table className="w-full">
        <thead>
          <tr>
            <th>Role</th><th>Action</th><th>Source</th>
            <th>Version</th><th>Tools</th><th>Max Tokens</th><th>Updated</th>
          </tr>
        </thead>
        <tbody>
          {filteredPrompts.map((p) => (
            <tr
              key={`${p.role}:${p.action}`}
              onClick={() => onRowClick(p.role, p.action)}
              className={p.source === 'override' ? 'bg-blue-50 border-l-4 border-blue-400' : ''}
            >
              <td>{p.role}</td>
              <td>{p.action}</td>
              <td><OverrideBadge source={p.source} /></td>
              <td>{p.version}</td>
              <td>{p.enableTools ? 'Yes' : 'No'}</td>
              <td>{p.maxTokens}</td>
              <td>{new Date(p.updatedAt).toLocaleDateString()}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
```

---

### Task 3: Create Override Badge Component (0.5 hours)

**File to create**: `packages/dashboard/src/components/prompts/OverrideBadge.tsx`

```typescript
interface OverrideBadgeProps {
  source: 'system' | 'override';
}

export function OverrideBadge({ source }: OverrideBadgeProps): JSX.Element {
  if (source === 'override') {
    return <span className="inline-flex items-center px-2 py-0.5 text-xs font-medium bg-blue-100 text-blue-800 rounded-full">Override</span>;
  }
  return <span className="inline-flex items-center px-2 py-0.5 text-xs font-medium bg-gray-100 text-gray-600 rounded-full">Default</span>;
}
```

---

### Task 4: Create Account Prompt Editor Component (3 hours)

**File to create**: `packages/dashboard/src/components/prompts/AccountPromptEditor.tsx`

Override-aware editor that shows different UI depending on whether the prompt is a system default or an existing override.

```typescript
interface AccountPromptEditorProps {
  open: boolean;
  role: string;
  action: string;
  isOverride: boolean;
  readOnly: boolean;  // true for non-admin members
  onClose: () => void;
  onSaved: () => void;
  getPrompt: (role: string, action: string) => Promise<PromptDetail | null>;
  upsertOverride: (role: string, action: string, data: UpsertData) => Promise<PromptDetail>;
  deleteOverride: (role: string, action: string) => Promise<boolean>;
}

export function AccountPromptEditor(props: AccountPromptEditorProps): JSX.Element | null {
  // Load prompt on open
  // Show info banner:
  //   - System default: "This is a system default. Saving will create an override for your account."
  //   - Override: "This is an account override." + "Reset to Default" button
  // Template textarea (monospaced)
  // Variables list (auto-extracted)
  // System prompt textarea
  // Enable tools toggle
  // Max tokens input
  // Save / Reset / Cancel buttons (hidden if readOnly)
}
```

Key behaviors:
- **Info banner** depends on `isOverride` prop:
  - `false` (system default): yellow info banner with text "Saving will create an override for your account."
  - `true` (override): blue info banner with "Reset to Default" button
- **Read-only mode**: when `readOnly=true`, all inputs are disabled, Save/Delete buttons hidden
- **Reset to Default**: calls `deleteOverride()` after `ConfirmDialog`, then `onSaved()` to refresh

---

### Task 5: Create Prompt Preview Component (3 hours)

**File to create**: `packages/dashboard/src/components/prompts/PromptPreview.tsx`

A collapsible panel below the editor for testing prompt rendering.

```typescript
interface PromptPreviewProps {
  role: string;
  action: string;
  variables: string[];  // auto-extracted variable names
  renderPreview: (role: string, action: string, variables: Record<string, string>) => Promise<RenderedResult | null>;
}

export function PromptPreview({ role, action, variables, renderPreview }: PromptPreviewProps): JSX.Element {
  const [variableValues, setVariableValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<RenderedResult | null>(null);
  const [loading, setLoading] = useState(false);

  // Input field for each variable name
  // "Render Preview" button (explicit click, not on keystroke)
  // Result display:
  //   - Rendered template (read-only pre block)
  //   - Rendered system prompt (read-only pre block)
  //   - Unresolved variables highlighted in red

  return (
    <details className="mt-4 border rounded-lg p-4">
      <summary className="cursor-pointer font-medium">Preview / Test</summary>
      <div className="mt-4 space-y-4">
        {/* Variable input fields */}
        {variables.map((varName) => (
          <div key={varName} className="flex items-center gap-2">
            <label className="text-sm font-mono w-40">{`{{${varName}}}`}</label>
            <input
              type="text"
              value={variableValues[varName] ?? ''}
              onChange={(e) => setVariableValues((prev) => ({ ...prev, [varName]: e.target.value }))}
              className="flex-1 border rounded px-2 py-1 text-sm"
            />
          </div>
        ))}

        {/* Render button */}
        <button onClick={handleRender} disabled={loading}>
          {loading ? 'Rendering...' : 'Render Preview'}
        </button>

        {/* Result display */}
        {result && (
          <div>
            <h4>Rendered Template</h4>
            <pre className="bg-gray-50 p-3 text-sm overflow-x-auto">{result.renderedTemplate}</pre>
            {result.renderedSystemPrompt && (
              <>
                <h4>System Prompt</h4>
                <pre className="bg-gray-50 p-3 text-sm">{result.renderedSystemPrompt}</pre>
              </>
            )}
            {result.unresolvedVariables.length > 0 && (
              <div className="text-red-600 text-sm mt-2">
                Unresolved: {result.unresolvedVariables.map((v) => `{{${v}}}`).join(', ')}
              </div>
            )}
          </div>
        )}
      </div>
    </details>
  );
}
```

---

### Task 6: Create Convention Selector Component (1.5 hours)

**File to create**: `packages/dashboard/src/components/prompts/ConventionSelector.tsx`

Dropdown for selecting and inserting convention template text.

```typescript
interface ConventionSelectorProps {
  onInsert: (text: string) => void;
}

export function ConventionSelector({ onInsert }: ConventionSelectorProps): JSX.Element {
  const [conventions, setConventions] = useState<Array<{ key: string; name: string; description: string }>>([]);
  const [selected, setSelected] = useState<string>('');
  const [previewText, setPreviewText] = useState<string>('');

  // Fetch convention list from /api/conventions or static import
  // On select: fetch full text, show in preview
  // "Insert into Template" button calls onInsert(previewText)
  // "Copy to Clipboard" button
}
```

---

### Task 7: Create Account Prompts Page (1 hour)

**File to create**: `packages/dashboard/src/pages/settings/AccountPromptsPage.tsx`

This replaces/extends the existing `PromptsPage.tsx` at `/settings/prompts`. Since `PromptsPage.tsx` already exists, we modify it to use the new account-aware components.

**File to modify**: `packages/dashboard/src/pages/settings/PromptsPage.tsx`

```typescript
import { useAccountPrompts } from '../../hooks/useAccountPrompts.js';
import { AccountPromptTable } from '../../components/prompts/AccountPromptTable.js';
import { AccountPromptEditor } from '../../components/prompts/AccountPromptEditor.js';
import { useAuth } from '../../hooks/useAuth.js'; // hypothetical auth hook

export function PromptsPage(): JSX.Element {
  const { prompts, loading, error, overrideCount, fetchPrompts, getPrompt, upsertOverride, deleteOverride, renderPreview } = useAccountPrompts();
  const { userRole } = useAuth();  // 'owner' | 'admin' | 'member'
  const readOnly = userRole === 'member';
  const [selected, setSelected] = useState<{ role: string; action: string } | null>(null);
  const isOverride = selected ? prompts.find((p) => p.role === selected.role && p.action === selected.action)?.source === 'override' : false;

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6">AI Prompts</h1>
      {readOnly && (
        <div className="bg-yellow-50 border border-yellow-200 text-yellow-800 text-sm p-3 rounded mb-4">
          You have read-only access. Contact your account admin to modify prompts.
        </div>
      )}
      <AccountPromptTable prompts={prompts} overrideCount={overrideCount} onRowClick={(r, a) => setSelected({ role: r, action: a })} />
      {selected && (
        <AccountPromptEditor
          open={true} role={selected.role} action={selected.action}
          isOverride={isOverride} readOnly={readOnly}
          onClose={() => setSelected(null)}
          onSaved={() => { setSelected(null); fetchPrompts(); }}
          getPrompt={getPrompt} upsertOverride={upsertOverride} deleteOverride={deleteOverride}
        />
      )}
    </div>
  );
}
```

---

### Task 8: Route and Navigation Updates (1 hour)

**File to modify**: `packages/dashboard/src/router.tsx`

The route `/settings/prompts` already exists and points to `PromptsPage`. It currently uses `AdminGuard`. Since account prompts should be visible to all authenticated users (read-only for non-admins), change the guard:

```typescript
// Before:
{
  path: '/settings/prompts',
  element: (
    <AdminGuard>
      <PromptsPage />
    </AdminGuard>
  ),
},

// After:
{
  path: '/settings/prompts',
  element: <PromptsPage />,  // AuthGuard already wraps the parent layout
},
```

**File to modify**: `packages/dashboard/src/components/layout/Sidebar.tsx`

Rename the sidebar link from "Prompt Templates" to "AI Prompts" and move it to the member-accessible section (not admin-only).

---

### Task 9: Tests (2.5 hours)

**File to create**: `packages/dashboard/src/pages/settings/PromptsPage.test.tsx`

| # | Test | Assertion |
|---|------|-----------|
| 1 | Table renders with override badges | Override rows have blue badge |
| 2 | Override count shows correct number | "3 of 80" matches actual count |
| 3 | Role and action filters work | Filtering reduces displayed rows |
| 4 | Editor shows info banner for system defaults | Yellow banner present |
| 5 | Editor shows "Reset to Default" for overrides | Button present for overrides |
| 6 | Save calls PUT endpoint | `upsertOverride` called |
| 7 | Reset calls DELETE endpoint | `deleteOverride` called after confirm |
| 8 | Preview renders with API response | Rendered text displayed |
| 9 | Convention selector lists templates | 20 templates in dropdown |
| 10 | Read-only mode hides Save/Delete buttons | Buttons not rendered for members |
| 11 | Unresolved variables highlighted in red | Red text for missing vars |
| 12 | Override row has visual distinction | `bg-blue-50` class present |

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/dashboard/src/hooks/useAccountPrompts.ts` | Data fetching hook |
| 2 | `packages/dashboard/src/components/prompts/AccountPromptTable.tsx` | Table with override badges |
| 3 | `packages/dashboard/src/components/prompts/OverrideBadge.tsx` | Override/Default badge |
| 4 | `packages/dashboard/src/components/prompts/AccountPromptEditor.tsx` | Override-aware editor |
| 5 | `packages/dashboard/src/components/prompts/PromptPreview.tsx` | Variable input + render preview |
| 6 | `packages/dashboard/src/components/prompts/ConventionSelector.tsx` | Convention template dropdown |
| 7 | `packages/dashboard/src/pages/settings/PromptsPage.test.tsx` | Tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/dashboard/src/pages/settings/PromptsPage.tsx` | Replace with account-aware prompt management |
| 2 | `packages/dashboard/src/router.tsx` | Remove AdminGuard from `/settings/prompts` route |
| 3 | `packages/dashboard/src/components/layout/Sidebar.tsx` | Rename link, move to member section |

---

## Dependencies

- **Story 27-3** (API Endpoints) — `/api/prompts` account-scoped endpoints must exist
- **Epic 16** (Auth) — JWT session provides accountId and role
- **Internal**: Existing common components: `Card`, `LoadingSpinner`, `ConfirmDialog`, `Toggle`, `FormField`

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| `GET /api/prompts` response does not include `source` field | Story 27-2/27-3 must add `source: 'system' | 'override'` to `PromptSummary` — verify during integration |
| Preview API call on every keystroke | Use explicit "Render Preview" button, not real-time rendering |
| Convention text can be 500-1000 chars | Show truncated preview in dropdown; expand on selection in a scrollable panel |
| Existing `PromptsPage.tsx` and `PromptTemplatesPanel.tsx` conflict | Replace `PromptsPage.tsx` content entirely; old `PromptTemplatesPanel.tsx` becomes dead code (can be removed in cleanup) |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| useAccountPrompts hook | 2 |
| AccountPromptTable component | 3 |
| OverrideBadge component | 0.5 |
| AccountPromptEditor component | 3 |
| PromptPreview component | 3 |
| ConventionSelector component | 1.5 |
| Page + route wiring | 2 |
| Tests (12 tests) | 2.5 |
| **Total** | **17.5 hours** |

/**
 * Static metadata for the 8 roles and 10 actions that make up the
 * 80-cell role+action matrix. Sourced from CLAUDE.md "Prompt Store
 * Architecture" and the C# `SystemPrompts` registry.
 *
 * Kept here rather than inferred from the API response because:
 *   - filter dropdowns need stable order independent of result set,
 *   - the matrix view wants every cell rendered, including ones that
 *     have no shipped default (so we can't filter from the response).
 *
 * Display labels match the snake_case identifiers the backend persists,
 * with friendlier `Title Case` versions for the UI.
 */

export interface RoleMeta {
  /** Backend identifier — e.g. `developer`, `senior_developer`. */
  id: string;
  /** Friendly UI label. */
  label: string;
}

export interface ActionMeta {
  /** Backend identifier — e.g. `implement`, `code-review`. */
  id: string;
  label: string;
}

export const ROLES: readonly RoleMeta[] = [
  { id: 'developer', label: 'Developer' },
  { id: 'tester', label: 'Tester' },
  { id: 'security', label: 'Security' },
  { id: 'devops', label: 'DevOps' },
  { id: 'architect', label: 'Architect' },
  { id: 'product_owner', label: 'Product Owner' },
  { id: 'senior_developer', label: 'Senior Developer' },
  { id: 'tech_writer', label: 'Tech Writer' },
] as const;

export const ACTIONS: readonly ActionMeta[] = [
  { id: 'context-scan', label: 'Context Scan' },
  { id: 'plan', label: 'Plan' },
  { id: 'plan-review', label: 'Plan Review' },
  { id: 'implement', label: 'Implement' },
  { id: 'write-tests', label: 'Write Tests' },
  { id: 'refactor', label: 'Refactor' },
  { id: 'code-review', label: 'Code Review' },
  { id: 'triage', label: 'Triage' },
  { id: 'summarize', label: 'Summarize' },
  { id: 'debug', label: 'Debug' },
] as const;

export function roleLabel(id: string | null | undefined): string {
  if (!id) return '—';
  return ROLES.find((r) => r.id === id)?.label ?? id;
}

export function actionLabel(id: string | null | undefined): string {
  if (!id) return '—';
  return ACTIONS.find((a) => a.id === id)?.label ?? id;
}

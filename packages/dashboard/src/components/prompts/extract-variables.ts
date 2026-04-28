/**
 * Pure helper for client-side `{{variable_name}}` extraction.
 *
 * Mirrors the C# `PromptStoreService.ExtractVariables(...)` regex (the
 * server is the source of truth, but echoing the parse here lets the
 * editor surface the variable list as the user types — no round-trip).
 *
 * The 1..64 character bound matches the server-side validator: it stops
 * stray `{{ }}` (empty) or pathological 10KB names from polluting the UI.
 */
export function extractVariables(template: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  const matches = template.matchAll(/\{\{([^}]{1,64})\}\}/g);
  for (const m of matches) {
    const name = m[1]?.trim();
    if (!name) continue;
    if (seen.has(name)) continue;
    seen.add(name);
    out.push(name);
  }
  return out;
}

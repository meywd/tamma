# Story 27-18: Prompt Store Taxonomy Reshape

## Story

As the Tamma platform, I need the prompt store reshaped from the flat 8×10
matrix to the jagged ~80-cell SPEC §4 taxonomy, so prompts and conventions key
off the identical `(role, action)` taxonomy.

Canonical design: SPEC §1.2, §3.3, §4, §5.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (taxonomy), Story 27-16 (codegen), Story 27-1 (prompt store schema —
the `prompts` table already keys by `(tenant_id, role, action)`; only the seed
content and the action vocabulary change, not the schema).

## Acceptance Criteria

1. `SystemPrompts.cs` `RoleActionTemplates` is regenerated from the SPEC §4
   taxonomy via Story 27-16 codegen — jagged per-role cells, not flat 8×10.
2. `ActionDefaults` (the 10 generic action templates) is removed; there is no
   generic action fallback tier (SPEC §3.3). Transitional generic cells, where
   still needed pre-initiative-2, exist as ordinary `(role, action)` seed rows
   (SPEC §3.5), not as a separate defaults layer.
3. `RoleSystemPrompts` (8 role identity preambles) is retained, keyed by
   `AgentRole`.
4. Prompt resolution remains exact `(tenant_id, role, action)` lookup:
   tenant override → system default → `TammaError` for a taxonomy-valid pair
   with no row (no silent empty).
5. `ResolvePromptFromRegistryActivity` keys lookups by the taxonomy-validated
   `(role, action)` (via `AgentRole.Parse`/`AgentAction.Parse` at the boundary).
6. Story 27-1 / 27-2 docs are annotated: the schema is unchanged; the seed and
   action vocabulary are governed by Story 27-15/27-16.

## Technical Context

- Modify: `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs`,
  `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs`.
- No migration number change for the prompt store (schema stable; seed only).

## Estimate

12 hours.

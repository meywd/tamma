# Story 27-16: Taxonomy Codegen — Prompt Seed + Convention Seed

## Story

As the Tamma build, I need a codegen step that generates both the prompt-store
seed and the convention-store seed from the single Story 27-15 taxonomy, so the
two seeds share keys and cannot drift.

Canonical design: SPEC §3.4.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (the enums + RolePhaseMap per-role sets are the codegen input).

## Acceptance Criteria

1. A codegen tool reads the Story 27-15 per-role action sets and emits:
   - The prompt-store seed: one row per `(role, action)` cell in SPEC §4
     (jagged ~80 cells), each with a system-default template body placeholder
     keyed by `(role, action)`.
   - The convention-store seed: one row per `(role, action)` cell with a
     system-default convention body keyed by `(role, action)`.
2. Both seeds use the identical `(role, action)` key set — asserted equal by
   the generator (fail generation if the two key sets differ).
3. Generated SQL is idempotent (`INSERT ... ON CONFLICT DO NOTHING`).
4. Codegen output is deterministic (stable ordering) so diffs are reviewable.
5. The generator runs in CI and the working tree must be clean afterwards
   (generated files committed; CI fails if regeneration produces a diff).
6. Transitional generic-action cells (SPEC §3.5) are emitted as ordinary seed
   rows, marked with a comment `-- transitional: remove when dispatch site
   specialised (initiative 2)`.

## Technical Context

- Generates: `database/migrations/018_convention_store.sql` (seed portion) and
  the prompt-store seed migration (Story 27-1's seed portion / `SystemPrompts.cs`
  reshape per Story 27-18).
- The generator is the only writer of seed rows; hand-edited seed rows are
  forbidden (enforced by AC 5).

## Estimate

10 hours.

# Story 27-13: Convention Store Elsa Integration

## Story

As `LlmCallWorkflow`, I need a `ResolveConventionsActivity` that fetches the
convention for the resolved `(role, action)` at the prompt-pull boundary, so
`{{conventions}}` is populated from the convention store.

Canonical design: SPEC §3.3, §6. **No composite action string, no tokenizer,
no `LlmCallContext`-based matching.**

## Priority

P1 (High).

## Dependencies

Story 27-9 (resolver), Story 27-15 (taxonomy), Story 27-6 (prompt-pull
boundary in `LlmCallWorkflow`).

## Acceptance Criteria

1. `ResolveConventionsActivity` runs at the same boundary as
   `ResolvePromptFromRegistryActivity` inside `LlmCallWorkflow` (intra-workflow,
   after the role/action strings are read from the dispatch bag).
2. It calls `AgentRole.Parse(roleInput)` and `AgentAction.Parse(actionInput)`
   (fail-fast on unknown), then `IConventionStore.ResolveAsync(tenantId,
   role, action)`.
3. The resolved `ConventionResolution.Body` overrides the `{{conventions}}`
   template variable. If the convention store has no row for a taxonomy-valid
   pair it throws (codegen guarantees a row; absence = bug, not silent empty).
4. `ReadRepoConventionsActivity` remains only as the legacy fallback source for
   repos with an explicit `.tamma/config.json` `conventions` field; the store
   result takes precedence when present.
5. A `CONVENTIONS.RESOLVED.SUCCESS` event is emitted with `tenantId`, `role`,
   `action`, `source` (`tenant`/`system`), `chars`. (No keyword/trigger fields.)
6. No `agentRole + "/" + taskAction` composite is constructed anywhere
   (that approach is abandoned — SPEC §1.1).

## Technical Context

- Files: create
  `apps/tamma-elsa/src/Tamma.Activities/Context/ResolveConventionsActivity.cs`;
  modify `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`
  (wire the activity at the prompt-pull boundary), DI registration.
- Initiative (2) (`SingleIssueCycleWorkflow` roundabout) is a downstream
  consumer: it will choose the `(role, action)` dynamically and pass it through
  the *same* dispatch inputs this activity reads — no change to this activity
  required when (2) lands.

## Estimate

8 hours (down from 14 — no composite/tokenizer wiring).

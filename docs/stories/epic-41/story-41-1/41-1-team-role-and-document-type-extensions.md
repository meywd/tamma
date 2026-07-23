# Story 41-1: Team-Role & Document-Type Extensions

Status: drafted

## User Story

As the **Epic 41 program**, I want the agent taxonomy and the Epic 39 document registry extended with the
roles and typed documents the remaining team activities need, so that every Epic 41 workflow can bind a
real `(role, action)` cell and produce a registered typed document — enabling the *agent* execution path
(rule 4) at higher autonomy, while the human-assigned path already works.

## Priority

P0 — enabler. Gates the agent path of the role-family stories (41-6/41-7/41-8/41-27/41-28) and the typed
outputs of 41-2/41-3/41-6/41-13/41-19/41-27. Not a hard blocker for the human path of any story.

## Scope

1. **New `AgentRole`s** in `Tamma.Core/Agents/AgentRole.cs`: `scrum_master`, `project_manager`,
   `ux_designer`. Remove the `scrum_master → product_owner` entry from `LegacyRoleAliases` (it becomes a
   real role); keep `analyst` aliased. Add each role's `_system.md` identity preamble and its action
   cells under `Prompts/{role}/`.
2. **New `AgentAction` tokens** + `RolePhaseMap` eligibility entries for the activities that lack a cell:
   `plan-sprint`, `synthesize-standup`, `facilitate-retro`, `track-impediments` (scrum_master);
   `report-status`, `coordinate-release` (project_manager); `draft-user-flow`, `author-ui-spec`,
   `review-design`, `audit-accessibility` (ux_designer); `triage-tech-debt` (architect),
   `triage-pr` (senior_developer), `manage-regression` (tester). Existing cells (`write-adr`,
   `prioritize-backlog`, `verify-acceptance`, `threat-model`, …) are reused unchanged.
3. **New document types** registered in the 39-2 registry with schema + domain rules + prompt-contract
   renderer + examples + drift test: `AcceptanceCriteria`, `BacklogOrdering`, `SprintPlan`, `TestPlan`,
   `ThreatModel`, `UxSpec` (rules per the README table). Each declares which producers `produce` it.
4. **Prose-document audience tags** extended for the new prose outputs (ADR, postmortem, release-notes,
   changelog, runbook, user/API docs, stakeholder-update, retro, roadmap).

## Acceptance Criteria

1. The three new roles round-trip through `EnumWire`/`RolePhaseMap`; `ValidRoles` = 11; every new
   `(role, action)` pair passes `IsRoleEligibleForPhase`; the taxonomy drift test is green.
2. `PromptFileLoader` starts fail-loud: every new taxonomy cell has a file and no file sits outside the
   taxonomy (the existing one-cell-one-file invariant holds for the enlarged grid).
3. The six new document types are registered, carry executable domain rules with unit tests, and each has
   a generated prompt contract (39-16 mechanism) bound to its producer cell.
4. `ContractBindingTests` extends to the new cells with no build-gate regression.
5. No existing role/action/document behaviour changes (byte-stable for the 8 current roles).

## Dependencies

- **Blocking:** Epic 39 (39-2 registry, 39-3/39-4 type pattern, 39-16 contract generation), 27-15/27-18
  taxonomy machinery.
- **Unblocks:** the agent path of every Epic 41 role-family story.

## Estimated Effort

5–7 days

# Story 41-1a: Agent-Taxonomy Extension — three roles, fifteen cells, the panel-selector maps

Status: drafted

*Split from 41-1 — see [the enabler-set umbrella](./41-1-team-role-and-document-type-extensions.md).*

## User Story

As the **Epic 41 program**, I want `Tamma.Core/Agents` extended with the three missing team roles, the
action cells the epic's activities bind, and the **derived panel-selector maps** those roles and cells
must appear in, so that every Epic 41 workflow can bind a real `(role, action)` cell — and so that a
document review assigned to a role the epic introduces does not throw at runtime.

## Priority

P0 — hard gate for 41-6, 41-7, 41-8, 41-10, 41-11, 41-16, 41-17 (PR-triage half), 41-22, 41-27, 41-28 on
both the human-assigned and the agent path, plus the `(tech_writer, review-docs)` review stage of 41-24,
41-25 and 41-26.

## Scope

1. **Three new `AgentRole`s** in `Tamma.Core/Agents/AgentRole.cs`: `scrum_master`, `project_manager`,
   `ux_designer` (covering UX and visual-design work). Each gets its `_system.md` identity preamble and
   its action-cell files under `Prompts/{role}/`.
2. **Fifteen new `AgentAction` tokens** + `RolePhaseMap.EligibleActions` entries: `plan-sprint`,
   `synthesize-standup`, `facilitate-retro`, `track-impediments` (scrum_master); `report-status`,
   `coordinate-release` (project_manager); `draft-user-flow`, `author-ui-spec`, `review-design`,
   `audit-accessibility` (ux_designer); `triage-tech-debt` **and `design-system`** (architect),
   `triage-pr` (senior_developer), `manage-regression` (tester), **`incident-rootcause`** (devops).
   Existing cells (`write-adr`, `prioritize-backlog`, `verify-acceptance`, `threat-model`,
   `review-docs`, `plan-incident-response`, `write-postmortem`, `write-regression-test`, …) are reused
   unchanged.

   > **Corrected — the list was thirteen and omitted two cells that other stories name this story as
   > minting.** **41-10** requires `(architect, design-system)` (`41-10…:20`) because
   > `plan-system-design` is reserved as `plan-generation`'s `Plan` producer
   > (`ContractBindingTests.cs:160-164`) and the three `design-*` cells are facet-scoped in their
   > shipped templates. **41-22** requires `(devops, incident-rootcause)` (`41-22…:20-22`) because
   > `(devops, diagnose-incident)` is the **triage-panel review lens**
   > (`RolePhaseMap.GetTriageActionForRole`, `:404-412`) and is listed in
   > `ContractBindingTests.ReviewProducerDispatchablePairs` (`:542-543`), whose stale-entry guard
   > (`:579`) fails the build on any pair that is also in `Bindings`. Neither cell exists in
   > `AgentAction.cs` today. Each needs a `Prompts/{role}/{action}.md` template like the other thirteen
   > — `PromptFileLoader` refuses to start on a taxonomy cell with no file (AC2).
3. **The DERIVED panel-selector maps.** `RolePhaseMap.GetReviewActionForRole` (`RolePhaseMap.cs:376-387`)
   and `GetTriageActionForRole` (`:404-412`) are `switch` expressions that **throw
   `ArgumentOutOfRangeException` for any role not listed**; `GetPanelActionForRole` (`:430-433`) fans out
   to both. Today `GetReviewActionForRole` covers 7 of 8 roles (TechWriter throws) and
   `GetTriageActionForRole` covers 4. This story:
   - adds the `AgentRole.TechWriter => AgentAction.ReviewDocs` arm and extends
     `ReviewerSelectionHelper.s_documentRoster` (`ReviewerSelectionHelper.cs:61-70`) from 7 to 8 roles;
   - **decides, per new role, whether it sits on the document-review panel, the triage panel, both, or
     neither** — and records the decision either as a map arm or as a test pinning the throw.
4. **`LegacyRoleAliases` migration.** Remove the `scrum_master → product_owner` entry
   (`RolePhaseMap.cs:239`); keep `analyst` and `researcher` aliased. State and implement what happens to
   stored configs keyed `scrum_master`.
5. **Count-pin bumps** (see AC7) — every one a conscious, reviewed edit.

## Design decisions to record

- **D1 — TechWriter on the document-review panel.** The `(tech_writer, review-docs)` cell is *already*
  taxonomy-eligible (`AgentAction.cs:117`, `RolePhaseMap.cs:162`); only the selector cannot reach it.
  Adding the arm is the minimal fix and is what 41-24/41-25/41-26 assume.
- **D2 — panel membership for the three new roles.** Default position: `ux_designer` joins the
  **document-review** panel (41-1's own `review-design` cell implies it); `scrum_master` and
  `project_manager` join **neither** panel — they produce and accept, they do not critique documents.
  Whichever way this lands, it is asserted, not left to the throw.
- **D3 — alias-removal polarity.** Default position: existing rows keyed `scrum_master` **re-point to
  the new role** (the name finally means what it says). The alternative — pin them to `product_owner` via
  a one-shot data migration — must be chosen explicitly, because the read path is wide: `NormalizeRole`
  (`RolePhaseMap.cs:274`) sits on `AgentRoleExtensions.Parse` (`AgentRole.cs:36`) and is called from
  `AgentResolverService.cs:104/:138/:261/:293` and `AgentEndpoints.cs:446/:671`, while the raw dictionary
  is read by `ProviderChainResolver.cs:264`, `AgentConfigValidator.cs:80` and
  `AgentResolverService.cs:702`.

## Acceptance Criteria

1. `AgentRoleExtensions.Parse("scrum_master")` returns the new `AgentRole.ScrumMaster` — today it returns
   `ProductOwner` via the alias. `Parse("project_manager")` and `Parse("ux_designer")` return their roles
   — today both throw `ArgumentException`. Round-trip holds for all 11 roles.
2. Each of the fifteen new `(role, action)` pairs passes `RolePhaseMap.IsRoleEligibleForPhase`, and
   `GetPrimaryPhaseForRole` returns a non-throwing action for each new role. The two cells added by the
   Scope-2 correction are covered explicitly: `(architect, design-system)` for 41-10 and
   `(devops, incident-rootcause)` for 41-22.
3. **A `document-lifecycle` run whose acceptance rules name `tech_writer` as the reviewer completes its
   review stage and produces a `Review`.** Today that run fails:
   `DocumentLifecycleWorkflow.BuildReviewEnvelope` calls `RolePhaseMap.GetReviewActionForRole` unguarded
   (`DocumentLifecycleWorkflow.cs:1199`) and it throws for `TechWriter`, which
   `ReviewerSelectionHelper.ResolveDocumentAction` (`:153-168`) rethrows as an invalid-reviewer error.
   After this story `GetReviewActionForRole(TechWriter)` returns `ReviewDocs` and
   `ReviewerSelectionHelper.DocumentPanelRoster` has 8 entries.
4. **Every new role's panel membership is asserted in both directions.** For each of `scrum_master`,
   `project_manager`, `ux_designer`: either `GetReviewActionForRole`/`GetTriageActionForRole` returns the
   documented action and a lifecycle run with that reviewer completes, **or** a test asserts the call
   throws with the "is not on a review panel" / "is not on the triage panel" message. No new role reaches
   a selector by accident and none reaches one only to throw at dispatch time.
5. **The `scrum_master` alias removal is a controlled behaviour change with a proven migration.** A stored
   agent config keyed `scrum_master` still validates (`AgentConfigValidator.cs:80`) and still resolves to
   a provider chain (`ProviderChainResolver.cs:264`, `AgentResolverService.cs:702`) after the entry is
   gone; a test asserts which role it now resolves to (D3) and that the resolved chain/prompt set is the
   one D3 chose.
6. **No behaviour change for the 8 current roles, except the deliberate alias removal.**
   > **Corrected — the old AC5 ("No existing role/action/document behaviour changes (byte-stable for the
   > 8 current roles)") contradicted Scope item 1.** Removing the `scrum_master` alias *is* a behaviour
   > change: the alias resolves today to `product_owner`, which **is** one of the 8, so any tenant config
   > or agent row keyed `scrum_master` silently re-points to a different agent with a different provider
   > chain and different prompt cells. The carve-out is explicit here rather than hidden behind a
   > parenthetical.
7. **Count pins bumped consciously, each with a one-line reason in the test comment:**
   `AgentRoleTests.cs:12` `Be(8)` → `Be(11)`; `AgentActionTests.cs:38` `Be(80)` → `Be(95)`;
   `RolePhaseMapTests.cs:64` `ValidActions.Should().HaveCount(80)` → `HaveCount(95)`;
   `SystemPromptsTests.cs:61` `RoleSystemPrompts.Should().HaveCount(8)` → `HaveCount(11)`;
   `ConventionStoreEndpointsTests.cs:720` and `:744` `HaveCount(8)` → `HaveCount(11)`. If D1/D2 grow the
   document roster, `ReviewerSelectionHelperTests.cs:97` and `ContractBindingTests.cs:598`
   `HaveCount(16)` move by the number of added roster roles (17 for D1's TechWriter arm alone). `PromptFileLoaderTests.cs:35/:37` are
   **derived** (`ExpectedCellCount`, `RolePhaseMap.ValidRoles.Count`) and must NOT be hand-edited.
8. `PromptFileLoader` starts fail-loud over the enlarged grid: every new taxonomy cell has a file and no
   file sits outside the taxonomy — a deliberately deleted cell file fails startup with
   `PROMPT.SEED.UNKNOWN_CELL`.
9. `ContractBindingTests` classifies every newly-dispatchable reviewer pair (D1/D2 additions to
   `ReviewerSelectionHelper.AllDispatchablePairs`) as `Bindings`, `IntentionallyUnbound` or residual —
   an unclassified pair fails the build, which is the gate working as designed.

## Dependencies

- **Blocking:** 27-15/27-18 taxonomy machinery, Epic 39 (39-7 review producers — the selector's caller).
- **Unblocks:** 41-6, 41-7, 41-8, **41-10** (`design-system`), 41-11, 41-16, 41-17 (PR-triage half),
  **41-22** (`incident-rootcause`), 41-27, 41-28; the review stage of 41-24, 41-25, 41-26.

## Estimated Effort

4–5 days

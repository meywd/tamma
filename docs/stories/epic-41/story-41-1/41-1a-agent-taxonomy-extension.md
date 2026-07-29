# Story 41-1a: Agent-Taxonomy Extension — three roles, fifteen actions (eighteen cells), the panel-selector maps

Status: done — conformance-reviewed 2026-07-29; the three roles, sixteen action tokens, nineteen new cells, twenty-two prompt files, the derived panel-selector maps and the `scrum_master` alias removal all ship. The template-conformance gate is now **exhaustive** — every one of the 112 taxonomy cells is classified exactly once, replacing the 5-entry allowlist the earlier follow-up left open — and both AC5's `ProviderChainResolver` alias path and the case-variant `NormalizeRole` behaviour (finding 6) are now pinned by tests. Open: AC3's end-to-end half is still proven at the selector/helper/envelope level only (both full-runtime lifecycle fixtures remain `[Explicit]`), `IntentionallyUnboundCells` reasons are checked for presence but not against evidence, and finding 7 (the 41-27 single-producer watch) stands

*Split from 41-1 — see [the enabler-set umbrella](./41-1-team-role-and-document-type-extensions.md).*

## User Story

As the **Epic 41 program**, I want `Tamma.Core/Agents` extended with the three missing team roles, the
action cells the epic's activities bind, and the **derived panel-selector maps** those roles and cells
must appear in, so that every Epic 41 workflow can bind a real `(role, action)` cell — and so that a
document review assigned to a role the epic introduces does not throw at runtime.

## Priority

P0 — hard gate for 41-5 (`(project_manager, report-status)` — its `(product_owner,
summarize-stakeholder)` cell is unusable, see 41-5), 41-6, 41-7, 41-8, 41-10, 41-11, 41-16, 41-17
(PR-triage half), 41-22, 41-27, 41-28 on both the human-assigned and the agent path, plus the
`(tech_writer, review-docs)` review stage of 41-24, 41-25 and 41-26.

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

   **Cell/file arithmetic:** fifteen new *action tokens*, but **eighteen new cells** — the three new
   roles each also carry the existing `context-scan` token, as all 8 incumbent roles do — and therefore
   **twenty-one new prompt files** (18 cell files + 3 `_system.md`). "Fifteen cells" undercounts; the
   counts in AC7 are token/role counts and are unaffected.

   > **Lockstep amendment (41-8 Phase B):** this story also mints **`(scrum_master,
   > write-retro-narrative)`** — the prose retro-narrative producer cell. 41-8's plan established that
   > "Findings plus a prose narrative" cannot be one binding (one dispatch = one document; one cell = one
   > contract) and the original fifteen-token list carried no narrative cell, so 41-8 Phase B had no cell
   > to bind. The cell cannot be minted from 41-8 (`PromptFileLoader` is fail-loud in both directions —
   > a cell without a file or a file without a cell refuses to boot), so it lands here. It moves every
   > count by one: sixteen tokens, nineteen new cells, twenty-two new prompt files, and each AC7 pin one
   > higher than the base numbers stated there.
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
2. Each of the new `(role, action)` cells — eighteen base cells (fifteen new tokens + `context-scan` for
   each new role) plus the 41-8 lockstep `(scrum_master, write-retro-narrative)` — passes
   `RolePhaseMap.IsRoleEligibleForPhase`, and `GetPrimaryPhaseForRole` returns a non-throwing action for
   each new role. The two cells added by the Scope-2 correction are covered explicitly:
   `(architect, design-system)` for 41-10 and `(devops, incident-rootcause)` for 41-22.
3. **A `document-lifecycle` run whose acceptance rules name `tech_writer` as the reviewer completes its
   review stage and produces a `Review`.** Today that run fails:
   `DocumentLifecycleWorkflow.BuildReviewEnvelope` calls `RolePhaseMap.GetReviewActionForRole` unguarded
   (`DocumentLifecycleWorkflow.cs:1199`) and it throws for `TechWriter`, which
   `ReviewerSelectionHelper.ResolveDocumentAction` (`:153-168`) rethrows as an invalid-reviewer error.
   After this story `GetReviewActionForRole(TechWriter)` returns `ReviewDocs` and
   `ReviewerSelectionHelper.DocumentPanelRoster` has **9** entries.
   > **Corrected (2026-07-29):** the "8" assumed D1 alone. D2's `ux_designer => review-design` arm also
   > landed, so the selector roster is 9 and `AllDispatchablePairs` is 18 — the arithmetic
   > implementation-plan-41-1a D2 pinned. The end-to-end half of this AC is proven at the selector,
   > helper and envelope level (`RolePhaseMapTests.GetReviewActionForRole_Maps_Each_Panel_Role`,
   > `ReviewerSelectionHelperTests.Resolve_TechWriterOnDocument_ReturnsReviewDocs`,
   > `BuildReviewEnvelopeTests`), NOT by a full `document-lifecycle` run: both full-runtime fixtures
   > (`DocumentLifecycleExecutionTests`, `ProseLifecycleExecutionTests`) are `[Explicit]` and documented
   > as not running anywhere today.
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
   > **Closed (2026-07-29 conformance round) — the `ProviderChainResolver` half is now asserted.** The
   > `AgentResolverService.cs:702` walk was already pinned by `AgentAliasMigrationTests`; the
   > `ProviderChainResolver.cs:264` walk this AC also names was not pinned by anything. It is now, by
   > `tests/Tamma.Api.Tests/Agents/ProviderChainAliasMigrationTests.cs` — 11 cases, all green (72 green
   > across the whole alias/provider-chain filter). D3's polarity is what they assert: a stored
   > `roles.scrum_master.providerChain` is served under its own canonical key, and `product_owner` no
   > longer harvests it.
   > **Which of the eleven actually GUARD the removal, stated precisely:** three.
   > `ProductOwnerRequest_NoLongerHarvests_TheScrumMasterChain` and
   > `ProductOwnerRequest_FallsThroughToDefaults_NotTheScrumMasterChain` both change verdict if the alias
   > entry is restored (they would resolve to the `scrum_master` chain again), and
   > `NoAliasEntry_ShadowsACanonicalRole` carries an unconditional
   > `LegacyRoleAliases.Should().NotContainKey("scrum_master")` plus a sweep over the live table
   > asserting no alias shadows a canonical role. The rest — the canonical-key lookup, both
   > cross-contamination directions, the reverse-inheritance case and the ordinal case-sensitivity case —
   > hold identically with or without the alias entry: they document the surrounding behaviour rather
   > than guarding the removal, and the fixture's inline claim that a present canonical
   > `roles.product_owner.providerChain` could ever have been served the `scrum_master` chain is wrong
   > (`ProviderChainResolver.cs:259` tries the canonical key first, alias or no alias).
   > `EveryRetainedAlias_StillFoldsToItsCanonicalRole` sweeps the live eight-entry table, but two of
   > those entries (`tester`, `architect`) are self-mappings served by the canonical branch, so for those
   > two it proves nothing about the alias walk.
6. **No behaviour change for the 8 current roles, except the deliberate alias removal and the D1
   `tech_writer` selector arm.**
   > **Corrected (2026-07-29) — the carve-out was incomplete.** Two further deliberate changes to the
   > incumbent 8 landed with this story: (a) `GetReviewActionForRole(TechWriter)` moved from *throw* to
   > `ReviewDocs` (AC3/D1 — `RolePhaseMapTests.GetReviewActionForRole_TechWriter_Throws` was inverted,
   > not extended), and (b) four incumbent roles gained eligible actions (`architect`
   > +`triage-tech-debt`/`design-system`, `senior_developer` +`triage-pr`, `tester`
   > +`manage-regression`, `devops` +`incident-rootcause`). Everything else about the 8 is unchanged.
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
   `PROMPT.SEED.NO_BODY_FAMILY`, and a file outside the taxonomy with `PROMPT.SEED.UNKNOWN_CELL`.
   > **Corrected (2026-07-29):** the original text attached `UNKNOWN_CELL` to the deleted-file
   > direction (implementation-plan-41-1a C1). `PromptFileLoader` throws `PROMPT.SEED.NO_BODY_FAMILY`
   > for a taxonomy cell with no file and `PROMPT.SEED.UNKNOWN_CELL` for a file outside the taxonomy;
   > `PromptFileLoaderTests` asserts both, plus `MISSING_SYSTEM_PROMPT` and `MALFORMED_FILE`.
9. `ContractBindingTests` classifies every newly-dispatchable reviewer pair (D1/D2 additions to
   `ReviewerSelectionHelper.AllDispatchablePairs`) as `Bindings`, `IntentionallyUnbound` or residual —
   an unclassified pair fails the build, which is the gate working as designed.

## Dependencies

- **Blocking:** 27-15/27-18 taxonomy machinery, Epic 39 (39-7 review producers — the selector's caller).
- **Unblocks:** **41-5** (`report-status`), 41-6, 41-7, 41-8 (Phase A `facilitate-retro`; Phase B
  `write-retro-narrative`), **41-10** (`design-system`), 41-11, 41-16, 41-17 (PR-triage half),
  **41-22** (`incident-rootcause`), 41-27, 41-28; the review stage of 41-24, 41-25, 41-26.

## Estimated Effort

4–5 days

## Follow-ups from adversarial review (2026-07-29)

**Resolved in the review-fix pass (same date):** the shipped `plan-sprint` and `author-ui-spec`
templates instructed JSON shapes that fail their own intended validators
(`SprintPlanDocumentType`: `SPRINT_ID_MISSING`/`CAPACITY_INVALID`/`NO_COMMITTED_ITEMS`;
`UxSpecDocumentType`: `NO_FLOWS`/`SCREEN_UNKNOWN_FLOW`/`SCREEN_MISSING_A11Y_REQUIREMENTS`), breaking
this story's D5 cross-lane promise. Both templates were rewritten to the exact wire shapes
(version 1 → 2), and `TemplateExampleConformanceTests` gained an unbound-cell gate
(`ConformingUnboundCells` + `EveryConformingUnboundCell_ShippedExampleValidatesAgainstItsIntendedType`)
so an **enumerated** unbound cell whose intended type is registered can no longer drift silently.

**Scope of that gate (stated precisely):** `ConformingUnboundCells` is an explicit allowlist with no
completeness assertion — it lists five cells today (`plan-sprint`, `author-ui-spec`, `report-status`,
`write-retro-narrative`, `coordinate-release`). The other eleven cells this story mints instruct
examples in already-registered shapes (`findings` for `synthesize-standup`/`facilitate-retro`/
`track-impediments`; `review` for `review-design`/`audit-accessibility`; `triage-decision` for
`triage-tech-debt`/`triage-pr`/`manage-regression`; `design` for `design-system`; `diagnosis` for
`incident-rootcause`; a bespoke flow shape for `draft-user-flow`) and are in **no** net — neither
`Bindings`, nor `KnownNonConformingTemplates`, nor `ConformingUnboundCells`. Closing that requires
either entries for them or a completeness rule; tracked as an open follow-up, not a resolved one.

**Resolved (2026-07-29 conformance round) — the gate is now EXHAUSTIVE, not an allowlist.** The scope
note above was accurate when written; its gap is closed by a *completeness rule*, the stronger of the
two options it named. `TemplateExampleConformanceTests` gained
`EveryTaxonomyCell_IsClassifiedExactlyOnce`, which **derives** the full cell set from
`RolePhaseMap.EligibleActions` (a second test cross-checks that derived grid against the embedded
prompt-file grid in BOTH directions, so neither can drift behind the other) and requires every cell to
land in exactly ONE of four classifications: a live `ContractBindingTests.Bindings` entry (16),
`ConformingUnboundCells` (16 — up from 5), the `KnownNonConformingTemplates` ratchet (16), or the new
`IntentionallyUnboundCells` (64, each carrying a written reason). **16 + 16 + 16 + 64 = 112 = the
taxonomy** (112 prompt files across 11 role directories, `_system.md` excluded), and both failure
branches execute: a cell in no bucket fails naming the three entries an author could add and the
evidence for choosing between them; a cell in two buckets fails naming both. The eleven cells the note
above listed as being "in no net" are now classified — ten in `ConformingUnboundCells` against their
real registered types (`findings` x3, `review` x2, `triage-decision` x3, `design`, `diagnosis`) and
`(ux_designer, draft-user-flow)` in `IntentionallyUnboundCells` with its reason (a cell-local
`{summary, flows[screens…]}` shape; the `UxSpec` producer is `author-ui-spec`). A new taxonomy token —
the way this story's own cells arrived — can no longer ship unclassified. This is also the mechanism
that would have caught 41-1b's `(security, threat-model)` drift, which sat outside every table while
instructing a shape its own registered validator rejected.

**Three limits of that gate, stated precisely rather than glossed.** (a) `IntentionallyUnboundCells` is
the one classification that turns the gate OFF for a cell, and it is validated only for a non-blank
reason string — nothing cross-checks a reason against the evidence it claims is absent (no check for a
`// Producing cell` comment, a `Prose.cs` kind seed or a `RolePhaseMap` producer note), so a future
author can still mis-file a cell there. What the gate guarantees is therefore that no cell goes
UNNOTICED — an unclassified cell fails the build until someone makes an explicit written decision — not
that every written decision is correct. (b) The ratchet's count pin rose 11 → 16 in this change: the
five prose templates added carry no JSON fence at all, so they are pre-existing invisible debt the
widened lens revealed, and each is actively re-verified as still non-conforming by the ratchet's
staleness test rather than silently waived. The constant records this as a one-time exception to its own
shrink-only direction rule; nothing mechanically prevents the same justification being reused. (c) The
completeness sweep counts all 16 `Bindings` keys as "bound", but only the 12 parsed by a `DocumentType`
are example-validated — so binding a cell with a non-`DocumentType` parser drops it out of
example-conformance with no gate firing.

**Resolved (2026-07-29 conformance round) — finding 6 (case-variant alias removal nit):** decided as
"keep the behaviour, record it", and now recorded by test rather than by prose.
`ProviderChainAliasMigrationTests.NormalizeRole_UppercaseScrumMaster_PassesThroughAndThenThrows` pins
that `NormalizeRole("SCRUM_MASTER")` passes the string through unchanged and
`AgentRoleExtensions.Parse` then throws — before the removal it folded case-insensitively to
`product_owner`, so the test is non-vacuous with respect to the removal.
`NormalizeRole_UppercaseRetainedAlias_StillFolds` pins the asymmetry control (a retained alias still
folds case-insensitively, because `LegacyRoleAliases` is `OrdinalIgnoreCase` while
`RolePhaseMap.ValidRoles` is an ordinal frozen set), and
`ChainLookup_IsOrdinal_UppercaseRoleKeyFindsNoChain` records the same ordinal rule on the
provider-chain lookup. `NormalizeRole` was deliberately NOT changed to case-fold against `ValidRoles`;
exact-case `scrum_master` remains the only spelling the system ever wrote. The finding as originally
written follows, for the record.

**Finding 6 as originally written (superseded by the resolution above):** `LegacyRoleAliases` is an
`OrdinalIgnoreCase` table while `RolePhaseMap.ValidRoles` is an Ordinal (case-sensitive) set derived
from the enum wire strings. Before D3, a case-variant key such as `Scrum_Master` or `SCRUM_MASTER`
resolved (case-insensitively) through the alias to `product_owner`; after the alias removal it matches
neither the alias table nor `ValidRoles`, so `NormalizeRole` passes it through unchanged and
`AgentRoleExtensions.Parse` throws. Exact-case `scrum_master` (the only spelling the system ever
wrote) is unaffected. Low impact; if case-variant tolerance is wanted, `NormalizeRole` should
case-fold against `ValidRoles` — decide once, with a test either way.

**Open follow-up — finding 7 (single-producer watch for 41-27):** `ux_designer` carries both
`draft-user-flow` and `author-ui-spec`. 41-1b's shared-contract rule (its AC6 note) requires each
document type to declare exactly ONE producing cell, and `UxSpec` declares `(ux_designer,
author-ui-spec)`. When 41-27 lands its workflow it must bind `author-ui-spec` as the `ux-spec`
producer and must NOT also dispatch `draft-user-flow` as a `ux-spec` producer — if `draft-user-flow`
produces a typed document at all, it needs its own type (or stays prose/unbound). Watch this at
41-27's `ContractBindingTests` entry.

# Implementation Plan — Story 41-1a: Agent-Taxonomy Extension — three roles, fifteen cells, the panel-selector maps

## Scope & Deliverable

When this story is done, `Tamma.Core/Agents` models **11 roles and 95 actions** instead of 8 and 80;
`RolePhaseMap.EligibleActions` carries **111 jagged cells** instead of 93 (see D4 — 15 new tokens, but 18
new cells); `Tamma.Api/Prompts/` ships **119 embedded files** instead of 101 (18 new cells + 3 new
`_system.md` preambles); `DefaultAgentConfig` has a per-role row for each new role so
`AgentResolverService` cannot `KeyNotFoundException` on them; `RolePhaseMap.GetReviewActionForRole`
returns `ReviewDocs` for `TechWriter` so a `document-lifecycle` run with a `tech_writer` reviewer
completes instead of throwing; `ReviewerSelectionHelper.DocumentPanelRoster` is 8 roles and
`AllDispatchablePairs` is 17; the `scrum_master → product_owner` legacy alias is gone and its behaviour
change is proven by test; and every count pin, keyset-equality drift test and negative-assertion test
that guards those numbers has been moved *consciously*, each with its reason in the test comment.

No workflow is rewired, no document type is registered, no migration runs. Diff surface:
`Tamma.Core/Agents/**`, `Tamma.Api/Prompts/**`, `Tamma.Api/Services/Agents/DefaultAgentConfig.cs`,
`Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs`, and the pinned test files.

## Pre-Reading

- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/story-41-1/implementation-plan.md` — the shared lockstep rules; this plan
  instantiates the "adding an `AgentRole`/`AgentAction`" half of it
- `docs/stories/epic-41/README.md:94-129` (new roles + the hard-blocker table), `:476-483` (the
  review-panel selector gap this story owns)
- **The taxonomy itself:**
  - `apps/tamma-elsa/src/Tamma.Core/Agents/AgentRole.cs:9-19` — 8 members; `Parse` at `:31-42` runs
    `RolePhaseMap.NormalizeRole` *first*, which is why the alias removal changes `Parse` behaviour
  - `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs:16-118` — 80 members, grouped by owning role
  - `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs` — `s_eligibleActions` `:43-163`,
    `s_primaryAction` `:178-189`, `ValidRoles`/`ValidActions` `:200-208`, `LegacyRoleAliases` `:230-242`
    (the `scrum_master` row is `:239`), `NormalizeRole` `:270-275`, `GetPrimaryPhaseForRole` `:310-315`,
    `GetReviewActionForRole` `:376-387`, `GetTriageActionForRole` `:404-412`, `GetPanelActionForRole`
    `:430-433`
  - `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs` — the `[Wire]` bidirectional map
- **The prompt loader (fail-loud both ways):**
  `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs` — taxonomy set built at `:94-96`;
  `PROMPT.SEED.UNKNOWN_CELL` throw at `:114-119` + `:296-302`; `PROMPT.SEED.MISSING_SYSTEM_PROMPT` at
  `:132-143`; `PROMPT.SEED.NO_BODY_FAMILY` at `:159-168`; front-matter contract at `:214-272`
  (cell files require exactly `variables, enableTools, maxTokens, version`; `_system.md` exactly
  `version` — an extra key is `PROMPT.SEED.MALFORMED_FILE`)
- `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj:70-72` — `Prompts/**/*.md` embedded with a pinned
  `LogicalName`; a new role directory needs no csproj edit
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/review-docs.md` + `tech_writer/_system.md` — the
  file format to copy
- **The consumers of a role:**
  `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DefaultAgentConfig.cs:41-176` (`s_perRole`, 8 rows) and
  `:185-188` (`ForRole` = `AssertValidRole` then a **raw indexer**);
  `AgentResolverService.cs:104`/`:108`, `:138`, `:261`, `:293`, `:413`, `:702`;
  `AgentConfigValidator.cs:80`; `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs:264`;
  `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:446`, `:671`
- **The selector's callers:**
  `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:1212` (unguarded);
  `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskReviewWorkflow.cs:324` (compile-time roster);
  `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs` —
  `DiffReviewAction` `:44-58`, `s_documentRoster` `:61-70`, `s_diffRoster` `:73-80`, `TriagePanelRoster`
  `:88-94`, `ResolveDocumentAction` `:153-168`, `AllDispatchablePairs`/`BuildAllPairs` `:178-193`,
  `DocumentPanelRoster` `:198`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:54-69` — `PanelRoster`, a
  **second, different** 7-role roster that deliberately excludes `tech_writer`
- **Every pin this story moves** (see the Task Breakdown table for the full list):
  `tests/Tamma.Api.Tests/Agents/AgentRoleTests.cs`, `AgentActionTests.cs`, `RolePhaseMapTests.cs`;
  `tests/Tamma.Api.Tests/PromptStore/PromptFileLoaderTests.cs`, `SystemPromptsTests.cs`;
  `tests/Tamma.Api.Tests/Conventions/ConventionStoreEndpointsTests.cs`, `ConventionSeedDriftTests.cs`;
  `tests/Tamma.Activities.Tests/Workflows/ReviewerSelectionHelperTests.cs`, `ContractBindingTests.cs`,
  `TaxonomyDriftBuildTests.cs`;
  `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs:54-68` — read to confirm it
  needs **no** edit (fully derived from `RolePhaseMap.EligibleActions`)

## Corrections to the story

Verified against the tree at plan time. Each of these contradicts the story text and must be planned
against reality, not the snapshot.

- **C1 — AC8's error code is wrong.** "a deliberately deleted cell file fails startup with
  `PROMPT.SEED.UNKNOWN_CELL`" is the *other* direction. A taxonomy cell whose file is deleted fails with
  **`PROMPT.SEED.NO_BODY_FAMILY`** (`PromptFileLoader.cs:161-167`). `PROMPT.SEED.UNKNOWN_CELL`
  (`:114-119`, `:296-302`) fires for a *file outside the taxonomy*. AC8 asks for both directions, so the
  test must assert both codes, each against its own scenario.
- **C2 — `DefaultAgentConfig.s_perRole` is a hard, unnamed lockstep item, and it breaks AC5 as written.**
  `DefaultAgentConfig.ForRole` (`:185-188`) calls `RolePhaseMap.AssertValidRole` — which *passes* for a
  role that is in the enum — and then indexes `s_perRole[role]` raw. A new role in `AgentRole` with no
  `s_perRole` row therefore throws an untyped **`KeyNotFoundException`** from
  `AgentResolverService.cs:108` and `:413`. AC5 requires that a stored config keyed `scrum_master` "still
  resolves to a provider chain" after the alias is removed; without three new `s_perRole` rows it cannot.
  The story never mentions the file. This plan adds it as step 4.
- **C3 — `s_primaryAction` is a second raw indexer with the same failure mode.** AC2 requires
  `GetPrimaryPhaseForRole` to "return a non-throwing action for each new role"; `:314` does
  `s_primaryAction[parsed]`, so each new role needs a row in `RolePhaseMap.s_primaryAction` (`:178-189`).
  Not named in the story.
- **C4 — the unguarded selector call is at `DocumentLifecycleWorkflow.cs:1212`, not `:1199`.** `:1199` is
  `AppendDraft` inside `BuildDraftEnvelope`. `BuildReviewEnvelope` starts at `:1200` and the
  `RolePhaseMap.GetReviewActionForRole(reviewerRole)` call is at `:1212`. There is a **second** caller the
  story does not name: `TaskReviewWorkflow.cs:324`. It is safe today (its roster is a compile-time
  constant that excludes `tech_writer`) but it is exercised by
  `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs`, which materialises those dispatch delegates — so
  any change to the selector's throw surface must be re-checked against it.
- **C5 — the "79" the story family keeps citing is not in `AgentAction.cs`.** That file's header comment
  (`:9-15`) carries no count. The stale 79s are `RolePhaseMap.cs:18`, `RolePhaseMap.cs:204`,
  `RolePhaseMapTests.cs:14` (doc comment) and the test **method name**
  `ValidActions_Should_Contain_Seventy_Nine_Actions` (declared `RolePhaseMapTests.cs:50`) whose assertion
  at `:64` is `HaveCount(80)`. Real count: **80**. Fix all four in this story (they are about to be wrong
  by 15 more).
- **C6 — AC7's pin list is incomplete.** It names six pins and one derived pair. It misses, all of which
  fail the build the moment a role is added:
  `RolePhaseMapTests.ValidRoles_Should_Contain_All_Eight_Roles` (`:33-40`, a literal 8-string keyset);
  `ConventionStoreEndpointsTests.cs:721` (a *second* literal 8-string keyset, next to the `:720` count);
  `RolePhaseMapTests.GetReviewActionForRole_Maps_Each_Panel_Role` (`:596-607`) and
  `GetReviewActionForRole_Result_Is_Eligible_For_That_Role` (`:609-621`) `[TestCase]` lists;
  `RolePhaseMapTests.GetReviewActionForRole_TechWriter_Throws` (`:624-628`) — a **negative assertion that
  D1 inverts**, i.e. it must be rewritten, not extended;
  `RolePhaseMapTests.GetTriageActionForRole_NonPanelRole_Throws` (`:653-662`) `[TestCase]` list;
  `ReviewerSelectionHelperTests.DocumentRoster` (`:18-22`, a local 7-role copy) and
  `AllDispatchablePairs_AreSixteenAndAllEligible` (`:91-99`);
  `AcceptanceDefaultsDriftTests.cs:47`/`:55`/`:56` (the `AcceptanceDefaults.PanelRoster` equality,
  `HaveCount(7)`, and `NotContain(TechWriter)` pins).
- **C7 — two different "document panels" exist and AC3 conflates them.**
  `ReviewerSelectionHelper.s_documentRoster` (`:61-70`) is the *selector domain* — the set of roles for
  which `GetReviewActionForRole` is expected to work, used to build `AllDispatchablePairs`.
  `AcceptanceDefaults.PanelRoster` (`AcceptanceDefaults.cs:60-69`) is the *default panel membership* of
  the shipped acceptance rules, and `AcceptanceDefaultsDriftTests.cs:56` explicitly pins that
  `tech_writer` is **not** on it. D1 moves the first (7→8) and deliberately does **not** move the second —
  otherwise every existing panel review silently gains a tech-writer seat. Say so, and keep `:56` green.
- **C8 — `ProviderChainResolver.cs` is under `Services/Providers/`, not `Services/Agents/`.** D3's
  citation implies the Agents folder; the file is
  `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs:264`.
- **C9 — "fifteen new cells" undercounts the prompt files.** See D4: 15 new *action tokens*, but 18 new
  *cells* if the three new roles carry `context-scan` like all 8 incumbents do, and therefore 21 new
  prompt files (18 cells + 3 `_system.md`).

## Design Decisions

- **D1 — `TechWriter => ReviewDocs` on the document-review selector, and *only* on that selector.** The
  `(tech_writer, review-docs)` cell is already taxonomy-eligible (`AgentAction.cs:117`,
  `RolePhaseMap.cs:162`) with a shipped template (`Prompts/tech_writer/review-docs.md`); the sole gap is
  the selector arm. Adding it is a one-line change to `RolePhaseMap.cs:376-387` plus the roster extension
  at `ReviewerSelectionHelper.cs:61-70`. It moves `AllDispatchablePairs` 16 → 17, which the new pair must
  be classified against in `ContractBindingTests.ReviewProducerDispatchablePairs` (`:505-544`) as
  policy-only (no compiled emitter exists — 41-24/41-25/41-26 build the callers). It does **not** touch
  `AcceptanceDefaults.PanelRoster` (C7): 41-24/41-25/41-26 select `tech_writer` as a *single reviewer*
  per document type, not as a new seat on every existing panel. Rationale: minimal blast radius, and the
  two rosters mean different things.
- **D2 — panel membership for the three new roles: `ux_designer` on the document-review panel;
  `scrum_master` and `project_manager` on neither.** `ux_designer` gets
  `AgentRole.UxDesigner => AgentAction.ReviewDesign` on `GetReviewActionForRole`, because 41-28 is
  literally "design review & accessibility audit" over a `UxSpec`/`Design` document, so the role must be
  reachable as a reviewer. `scrum_master` and `project_manager` produce and accept; nothing in
  41-6/41-7/41-8/41-5 asks them to critique a document, and putting them on the panel would mint two
  dispatchable pairs with no consumer and no template contract. Both are recorded as **asserted throws**
  (AC4's "or"), by extending `GetTriageActionForRole_NonPanelRole_Throws`'s `[TestCase]` list
  (`RolePhaseMapTests.cs:653-662`) and adding the mirror-image `GetReviewActionForRole` throw cases. Net
  roster arithmetic: `s_documentRoster` 7 → **9** (`+TechWriter` from D1, `+UxDesigner`);
  `AllDispatchablePairs` 16 → **18**. *(The story's AC3/AC7 assume D1 alone, i.e. 8 and 17; D2's
  `ux_designer` arm makes it 9 and 18. Pick one and pin the arithmetic — this plan pins 9/18 and states
  the alternative.)* `TriagePanelRoster` (`ReviewerSelectionHelper.cs:88-94`) is untouched: no new role
  triages.
- **D3 — alias-removal polarity: `scrum_master` re-points to the new role, with a config-shape fallback,
  no data migration.** Removing `RolePhaseMap.cs:239` means `NormalizeRole("scrum_master")` returns
  `"scrum_master"` unchanged, which is now in `ValidRoles`, so every read path resolves to
  `AgentRole.ScrumMaster`: `AgentRoleExtensions.Parse` (`AgentRole.cs:36`),
  `AgentResolverService.cs:104/:138/:261/:293`, `AgentEndpoints.cs:446/:671`. The two raw-dictionary
  readers (`ProviderChainResolver.cs:264`, `AgentResolverService.cs:702`) iterate `LegacyRoleAliases` to
  build fallback chains and simply see one fewer alias — no null path. `AgentConfigValidator.cs:80`
  accepts a property name that is *either* in `ValidRoles` or in `LegacyRoleAliases`, and `scrum_master`
  moves from the second clause to the first, so a stored config still validates. **No data migration**:
  the JSONB key text is unchanged, only its interpretation. This is a deliberate behaviour change and is
  carved out of AC6 by the story itself. The alternative (a one-shot UPDATE rewriting `scrum_master` →
  `product_owner` in `agent_configs.config`) is rejected — it destroys the user's expressed intent to
  configure a scrum master, and the platform is not in production with users (CLAUDE.md, "No migration
  anxiety"). `analyst` and `researcher` stay aliased to `product_owner`.
- **D4 — the three new roles each carry `context-scan`, so the change is +15 tokens / +18 cells / +21
  files.** All 8 incumbent roles include `AgentAction.ContextScan` in their eligibility set
  (`RolePhaseMap.cs:48, 66, 81, 96, 115, 128, 140, 155`), and `context-scan` is the free-text
  context-gathering cell every role can be asked to run. Omitting it for three roles would be the only
  asymmetry in the matrix. `ContextScan` is an existing token, so `AgentAction` still moves 80 → **95**,
  but `EligibleActions.Sum(Count)` moves 93 → **111** and the embedded file count 101 → **122** (18 new
  cells + 3 `_system.md`). The story's "fifteen new cells" is the token count, not the cell count.
- **D5 — new prompt cells are written as real templates against a named parser-free contract, not
  placeholders.** `SystemPromptsTests.RoleActionTemplates_EveryCell_HasNonEmptyBody` (`:48-58`) forbids
  placeholders. None of the 18 new cells is dispatched by any workflow in this story, so none needs a
  `ContractBindingTests.Bindings` entry — the coverage guard
  (`EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`, `ContractBindingTests.cs:681`) enumerates only
  *dispatched* pairs via `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs`, and there is no inverse
  "every taxonomy cell must be bound" test. Each template therefore instructs the JSON shape its future
  consumer story will pin (e.g. `plan-sprint` instructs the `SprintPlan` shape 41-1b defines), so the
  consuming story adds a `Bindings` entry without editing the template. Front matter follows the shipped
  convention exactly: `variables, enableTools, maxTokens, version` in that order, `enableTools: false`
  for every new cell (no governed tools exist for these families — epic-41 README, Epic 42 dependency),
  `version: 1`.
- **D6 — `s_primaryAction` values (C3):** `ScrumMaster => PlanSprint`, `ProjectManager => ReportStatus`,
  `UxDesigner => AuthorUiSpec`. Each is in that role's own eligibility set, which is the invariant
  `RolePhaseMapTests` asserts for the incumbent 8.
- **D7 — `DefaultAgentConfig` rows for the new roles clone the `product_owner` row's shape** (C2):
  provider/model/temperature/token budget from the platform defaults, `Handle = "tamma-scrum-master"` /
  `"tamma-project-manager"` / `"tamma-ux-designer"`, `Tools = DefaultTools` (empty). These are
  planning/prose roles with no code-tool need; a wrong-but-safe default beats a `KeyNotFoundException`.
  Add a test that iterates `RolePhaseMap.ValidRoles` and asserts `ForRole` returns for every one — the
  guard the codebase is missing today and the reason C2 was invisible.

## Task Breakdown

1. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Agents/AgentRole.cs`** — append three members with `[Wire]`:
   `[Wire("scrum_master")] ScrumMaster`, `[Wire("project_manager")] ProjectManager`,
   `[Wire("ux_designer")] UxDesigner`. Append, do not reorder (the enum is `[Wire]`-keyed, not
   ordinal-keyed, but diff hygiene matters).

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs`** — append 15 members in three new
   role-grouped blocks plus four additions to existing blocks:

   | Owning role | New tokens |
   |---|---|
   | scrum_master | `plan-sprint`, `synthesize-standup`, `facilitate-retro`, `track-impediments` |
   | project_manager | `report-status`, `coordinate-release` |
   | ux_designer | `draft-user-flow`, `author-ui-spec`, `review-design`, `audit-accessibility` |
   | architect | `triage-tech-debt`, `design-system` |
   | senior_developer | `triage-pr` |
   | tester | `manage-regression` |
   | devops | `incident-rootcause` |

   `design-system` and `incident-rootcause` are the two the story's own Corrected note added: 41-10 cannot
   reuse `plan-system-design` (reserved as `plan-generation`'s `Plan` producer,
   `ContractBindingTests.cs:160-164`) and 41-22 cannot reuse `diagnose-incident` (it is the triage-panel
   lens, `RolePhaseMap.cs:408`, and is listed in `ReviewProducerDispatchablePairs` whose stale-entry guard
   at `:567-589` fails the build on any pair that is also in `Bindings`).

3. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs`** — five edits:
   - `s_eligibleActions` (`:43-163`): three new role blocks (each `ContextScan` + its own tokens, per D4)
     and four appended tokens in the architect / senior_developer / tester / devops blocks.
   - `s_primaryAction` (`:178-189`): three rows per D6.
   - `LegacyRoleAliases` (`:230-242`): delete `:239` (`["scrum_master"] = "product_owner"`) and update the
     doc comment at `:221-229`, which enumerates `analyst`, `scrum_master`, `researcher` as falling back.
   - `GetReviewActionForRole` (`:376-387`): add `AgentRole.TechWriter => AgentAction.ReviewDocs` (D1) and
     `AgentRole.UxDesigner => AgentAction.ReviewDesign` (D2); update the `<list>` doc comment and the
     `<exception>` note (which currently names `TechWriter` as *the* non-panel role).
   - Stale counts (C5): `:18` and `:204`, 79 → 95.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DefaultAgentConfig.cs`** (C2/D7) — three new
   `s_perRole` rows.

5. **CREATE 21 files under `apps/tamma-elsa/src/Tamma.Api/Prompts/`** (D4/D5):
   - `scrum_master/_system.md`, `project_manager/_system.md`, `ux_designer/_system.md` (front matter:
     `version: 1` only — any other key is `PROMPT.SEED.MALFORMED_FILE`, `PromptFileLoader.cs:256-272`);
   - 18 cell files: `scrum_master/{context-scan,plan-sprint,synthesize-standup,facilitate-retro,track-impediments}.md`,
     `project_manager/{context-scan,report-status,coordinate-release}.md`,
     `ux_designer/{context-scan,draft-user-flow,author-ui-spec,review-design,audit-accessibility}.md`,
     `architect/{triage-tech-debt,design-system}.md`, `senior_developer/triage-pr.md`,
     `tester/manage-regression.md`, `devops/incident-rootcause.md`.

   The three `context-scan` copies mirror `Prompts/product_owner/context-scan.md` with the role lens
   swapped. No csproj edit (`Tamma.Api.csproj:70-72` globs `Prompts/**/*.md`).

6. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs`** —
   extend `s_documentRoster` (`:61-70`) 7 → 9 with `TechWriter` and `UxDesigner` (D1/D2), and update the
   `AllDispatchablePairs` doc comment (`:170-177`) 16 → 18. `DiffReviewAction` (`:44-58`) and
   `TriagePanelRoster` (`:88-94`) are **not** touched — no new role reviews diffs or triages.

7. **MODIFY the count pins and keyset drift tests** — every one a conscious edit with a one-line reason
   in the comment naming this story:

   | File:line | Today | After | Note |
   |---|---|---|---|
   | `tests/Tamma.Api.Tests/Agents/AgentRoleTests.cs:12` | `Be(8)` | `Be(11)` | + rename `Has_exactly_eight_roles` |
   | `AgentRoleTests.cs:21-24` | alias `[TestCase]`s | add `("scrum_master", ScrumMaster)` | D3 proof at the `Parse` level |
   | `tests/Tamma.Api.Tests/Agents/AgentActionTests.cs:38` | `Be(80)` | `Be(95)` | |
   | `tests/Tamma.Api.Tests/Agents/RolePhaseMapTests.cs:14` | doc "79 actions" | 95 | C5 |
   | `RolePhaseMapTests.cs:33-40` | literal 8-role keyset | 11 | **C6** |
   | `RolePhaseMapTests.cs:50` / `:64` | name `..._Seventy_Nine_...`, `HaveCount(80)` | rename + `HaveCount(95)` | C5 |
   | `RolePhaseMapTests.cs:596-607` | 7 review `[TestCase]`s | 9 (+TechWriter, +UxDesigner) | D1/D2 |
   | `RolePhaseMapTests.cs:609-621` | 7 eligibility `[TestCase]`s | 9 | |
   | `RolePhaseMapTests.cs:624-628` | `GetReviewActionForRole_TechWriter_Throws` | **inverted** → returns `ReviewDocs`; new throw test for `ScrumMaster`/`ProjectManager` | **C6/AC4** |
   | `RolePhaseMapTests.cs:653-662` | 4 non-triage-panel `[TestCase]`s | 7 (+3 new roles) | AC4 |
   | `tests/Tamma.Api.Tests/PromptStore/SystemPromptsTests.cs:61` | `HaveCount(8)` | `HaveCount(11)` | + rename `..._ContainsAllEightRoles` |
   | `tests/Tamma.Api.Tests/Conventions/ConventionStoreEndpointsTests.cs:720` | `HaveCount(8)` | `HaveCount(11)` | |
   | `ConventionStoreEndpointsTests.cs:721` | literal 8-role list | 11 | **C6** |
   | `ConventionStoreEndpointsTests.cs:744` | `HaveCount(8)` | `HaveCount(11)` | |
   | `tests/Tamma.Activities.Tests/Workflows/ReviewerSelectionHelperTests.cs:18-22` | local 7-role `DocumentRoster` | 9 | **C6** |
   | `ReviewerSelectionHelperTests.cs:97` | `HaveCount(16)` | `HaveCount(18)` | D1+D2 |
   | `tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:598` | `HaveCount(16)` | `HaveCount(18)` | |
   | `ContractBindingTests.cs:505-544` (`ReviewProducerDispatchablePairs`) | 9 entries | +`("tech_writer","review-docs")`, +`("ux_designer","review-design")` with justifications | AC9 |
   | `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs:47/:55/:56` | 7-role `PanelRoster`, `NotContain(TechWriter)` | **unchanged** | **C7** — add a comment recording that D1 deliberately does not move it |

   **Do NOT hand-edit** the derived counts: `PromptFileLoaderTests.cs:20-21`, `SystemPromptsTests.cs:22-23`,
   `ConventionStoreTests.cs:66`, `ConventionStoreSeederTests.cs:38`, `ConventionStoreEndpointsTests.cs:56`
   (all `RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count)`), and `ConventionSeedSpecs.Build` — they
   follow the matrix automatically and are the proof that steps 3 and 5 stayed in lockstep.

8. **CREATE the new tests** (see Test Plan) — the D7 `ForRole`-covers-every-role guard, the D3 alias
   migration proof, the AC3 lifecycle-with-tech_writer-reviewer test, and the AC4 both-directions panel
   assertions.

9. **Run the full gate:** `dotnet test` (all five test projects — `PromptFileLoader` failures surface at
   *static init* of `SystemPrompts`, so a missing file fails dozens of unrelated tests at once, which is
   the intended loudness), then `dotnet ef migrations has-pending-model-changes` (must stay clean — this
   story touches no entity).

## Test Plan

NUnit + FluentAssertions throughout. No Testcontainers except where noted.

- **`AgentRoleTests` / `AgentActionTests` (unit, existing files).** AC1: `Parse("scrum_master")` →
  `ScrumMaster` (was `ProductOwner`), `Parse("project_manager")` / `Parse("ux_designer")` return their
  roles (both threw), round-trip over all 11, `Parse("analyst")` still → `ProductOwner`. Count pins per
  the table. **Covers AC1, AC7 (partial).**
- **`RolePhaseMapTests` (unit, existing file).** AC2: `IsRoleEligibleForPhase` true for each of the 18 new
  pairs, with `("design-system","architect")` and `("incident-rootcause","devops")` as named cases;
  `GetPrimaryPhaseForRole` returns for all 11 (C3). AC4: `GetReviewActionForRole` returns `ReviewDocs`
  for `TechWriter` and `ReviewDesign` for `UxDesigner`, and **throws** with the "is not on a review panel"
  message for `ScrumMaster`/`ProjectManager`; `GetTriageActionForRole` throws for all three new roles
  plus the existing three. Every returned pair re-checked through `IsRoleEligibleForPhase`. **Covers AC2,
  AC4.**
- **`DefaultAgentConfigTests` (unit, NEW or extended).** D7/C2: `foreach (var r in RolePhaseMap.ValidRoles)
  DefaultAgentConfig.ForRole(r).Should().NotBeNull()` — the missing guard; plus per-new-role handle/provider
  assertions. Regression-pin the `KeyNotFoundException` class of bug for every future role. **Covers C2.**
- **`AgentConfigResolutionMigrationTests` (unit, NEW; Moq'd repository).** AC5/D3: a stored
  `agent_configs.config` JSONB with a `scrum_master` property (a) passes `AgentConfigValidator`
  (`:80` — now via the `ValidRoles` clause, not the alias clause), (b) resolves through
  `AgentResolverService` to `AgentRole.ScrumMaster` with the D7 chain, and (c)
  `ProviderChainResolver`'s alias-iteration path (`:264`) produces no `scrum_master` entry and does not
  throw. A control case asserts `analyst` still resolves to `product_owner`. **Covers AC5, AC6's
  carve-out.**
- **`PromptFileLoaderTests` (unit, existing file).** AC8, **both** directions per C1: (a) drive
  `PromptFileLoader.Build` with a synthetic set missing `scrum_master/plan-sprint.md` → `TammaError` code
  **`PROMPT.SEED.NO_BODY_FAMILY`** naming the cell; (b) drive it with an extra
  `ux_designer/not-a-real-action.md` → **`PROMPT.SEED.UNKNOWN_CELL`**; (c) a role directory with no
  `_system.md` → `PROMPT.SEED.MISSING_SYSTEM_PROMPT`. The existing
  `Load_EmbeddedResources_ExistForEveryTaxonomyCell` (`:41-55`) and `Load_CellCount_MatchesTaxonomy`
  (`:31-39`) then cover the real 111-cell grid with no edit — that is the point of the derived count.
  **Covers AC8.**
- **`ConventionSeedDriftTests` (unit, existing file, NO edit).**
  `ConventionSeedKeyset_EqualsTaxonomyKeyset` / `PromptKeyset_EqualsTaxonomyKeyset` /
  `AllThreeKeysets_AreIdentical` (`:44-91`) pass iff steps 3 and 5 were done together. This is the
  keyset-equality drift gate; leaving it untouched and green is the evidence. **Covers the lockstep.**
- **`ReviewerSelectionHelperTests` (unit, existing file).** AC3 (helper half): `Resolve("tech_writer",
  null, "document", null)` returns `ReviewDocs` — today it throws `REVIEW.PRODUCER.INVALID_REVIEWER` via
  `ResolveDocumentAction`'s catch (`:153-168`); same for `ux_designer` → `ReviewDesign`;
  `Resolve("scrum_master", null, "document", null)` still throws `INVALID_REVIEWER`.
  `Resolve("tech_writer", null, "diff", null)` still throws `ROLE_NOT_ON_DIFF_PANEL`.
  `DocumentPanelRoster` has 9 entries; `AllDispatchablePairs` has 18 and all are taxonomy-eligible.
  **Covers AC3 (unit half), AC4.**
- **`ContractBindingTests` (build gate, existing file).** AC9:
  `EveryReviewProducerDispatchablePair_IsClassified` (`:547`) must stay green with the two new pairs
  classified in `ReviewProducerDispatchablePairs`; `ReviewProducerDispatchablePairs_HasNoStaleEntries`
  (`:567`) proves neither new pair is double-classified;
  `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` (`:655`) and
  `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` (`:681`) must stay green — no new *dispatched*
  pair is introduced by this story (D5). **Covers AC9.**
- **`TaxonomyDriftBuildTests` (build gate, existing file, NO edit expected).** It materialises every
  compiled `llm-call` dispatch delegate, including `TaskReviewWorkflow.cs:324`'s
  `GetReviewActionForRole` call (C4). Adding arms to that switch cannot break it; *removing* one would.
  Green with no edit is the regression proof.
- **`DocumentLifecycleTechWriterReviewTests` (integration, NEW; extends the 39-6/39-11 Testcontainers
  fixture used by `tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleExecutionTests.cs`).** AC3
  end-to-end: run `document-lifecycle` over a `findings` draft with acceptance rules naming
  `tech_writer` as the single reviewer; assert the review stage completes and persists a `Review`
  instance whose `ProducedByRole` is `tech_writer` and `ProducedByAction` is `review-docs`, and that
  `BuildReviewEnvelope` (`DocumentLifecycleWorkflow.cs:1200-1220`) did not throw. A pre-change control
  run of the same fixture fails at `:1212` — record that in completion notes. **Covers AC3.**

## Risks & Mitigations

- **`PromptFileLoader` is a static-init landmine.** A single missing file fails `SystemPrompts`'s type
  initializer, so the failure surfaces as `TypeInitializationException` across every test that touches
  prompts, not as one clean assertion. *Mitigation:* generate all 21 files in one commit before touching
  `RolePhaseMap`, then add the matrix rows; and run `PromptFileLoaderTests` first in the loop.
- **The `scrum_master` alias removal is a live behaviour change on a wide read path (D3).** Six call sites
  normalize roles and two iterate the alias dictionary directly. *Mitigation:* the C2 fix (three
  `DefaultAgentConfig` rows) removes the only hard-crash path; `AgentConfigResolutionMigrationTests`
  proves validate/resolve/chain across all three readers; the platform has no production users
  (CLAUDE.md).
- **D2's `ux_designer` review arm changes the pin arithmetic the story's ACs quote (7→9/16→18 vs the ACs'
  8/17).** *Mitigation:* the discrepancy is named in D2 and in the pin table; whoever implements picks and
  the reviewer checks one number, not three files.
- **Adding a role widens `AcceptanceRules` roster validation surface.** `ReviewerSelectionHelper.
  ResolvePanelRoster` (`:211-235`) parses every configured roster role fail-loud; a tenant that had
  `scrum_master` in a `PanelRoles` array would previously have resolved it to `product_owner` and now
  resolves it to a role that throws in `GetReviewActionForRole`. *Mitigation:* covered by D2's asserted
  throw plus the `INVALID_REVIEWER` wrapping at `ResolveDocumentAction` (`:162-167`) — the failure is a
  typed `TammaError` naming the role, not a crash. Record it in completion notes for 41-6/41-7/41-8.
- **Two rosters, one word ("panel") — C7.** *Mitigation:* the `AcceptanceDefaultsDriftTests.cs:56`
  no-change is an explicit line item in step 7 with a comment, so the next reader does not "fix" it.

## Est. Effort

**4.5 days**, matching the story's 4–5.

| Step | Work | Days |
|---|---|---|
| 1–3 | Enums + eligibility matrix + `s_primaryAction` + selector arms + alias removal + stale-count fixes | 0.75 |
| 4 | `DefaultAgentConfig` rows (C2) | 0.25 |
| 5 | 21 prompt files (3 preambles, 18 cells, real bodies per D5) | 1.5 |
| 6 | `ReviewerSelectionHelper` roster + doc comments | 0.25 |
| 7 | 19 pin/keyset edits incl. the inverted TechWriter throw test | 0.75 |
| 8 | New tests (`DefaultAgentConfig` guard, alias migration, loader both-directions, lifecycle integration) | 0.75 |
| 9 | Full-gate run + review polish | 0.25 |

## Blocks / Blocked by

- **Blocked by:** Stories **27-15** / **27-18** (the typed taxonomy + jagged prompt-store machinery this
  extends) and **39-7** (the review producers whose selector this fixes). Both landed — no open
  prerequisite.
- **Blocks (produce step):** **41-6** (`scrum_master` + `plan-sprint`), **41-7**
  (`synthesize-standup`), **41-8** (`facilitate-retro`), **41-10** (`design-system`), **41-11**
  (`triage-tech-debt`), **41-16** (`manage-regression`), **41-17** PR-triage half (`triage-pr`),
  **41-22** (`incident-rootcause`), **41-27** (`ux_designer` + `author-ui-spec`), **41-28**
  (`review-design`, `audit-accessibility`).
- **Blocks (review stage only):** **41-24**, **41-25**, **41-26** — the `(tech_writer, review-docs)`
  selector arm (D1).
- **Enables end-to-end proof of:** **41-1c**'s D2 prose-acceptance row (a `tech_writer` reviewer over a
  prose document).
- **Parallel with, partially before:** **41-1b** — `SprintPlan` and `UxSpec` need this story's
  `(scrum_master, plan-sprint)` and `(ux_designer, author-ui-spec)` cells and templates.
- **Does not block:** 41-29, 41-2, 41-3, 41-13, 41-19, and the code-review half of 41-17.

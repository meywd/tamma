# Implementation Plan — Story 41-1 (index): Team-Role & Document-Type Extensions, the Wave-0 enabler set

**This story is not implementable.** It was split into three independently-shippable sub-stories; this
file is the split index, the shared-sequencing register, and the shared-lockstep rules that all three
obey. The buildable plans are:

| Sub-story | Plan | Deliverable | Effort |
|---|---|---|---|
| **41-1a** Agent-Taxonomy Extension | [`implementation-plan-41-1a.md`](./implementation-plan-41-1a.md) | 3 `AgentRole`s, 15 `AgentAction` tokens, the eligibility matrix rows, 18–21 prompt files, the panel-selector maps, the `scrum_master` alias removal | 4–5 d |
| **41-1b** New Document Types | [`implementation-plan-41-1b.md`](./implementation-plan-41-1b.md) | 6 `DocumentTypeKey` members + 6 `IDocumentType` implementations + acceptance postures | 5–6 d |
| **41-1c** Prose Documents & Audience Tags | [`implementation-plan-41-1c.md`](./implementation-plan-41-1c.md) | `prose` type, `Audience` on envelope + entity + migration + lineage read, 2 vocabularies | 3–4 d |

## Split rationale

The pre-split 41-1 bundled four independently-shippable deliverables behind one 5–7 day estimate. The
landed Epic 39 precedent sizes *one* of those slices at more than that: 39-3 shipped four document types
in 4–5 days (`docs/stories/epic-39/story-39-3/implementation-plan.md`), 39-4 shipped six in 5–6
(`docs/stories/epic-39/story-39-4/implementation-plan.md`), each as its own story with its own plan. The
prose mechanism was owned by nobody at all.

The three sub-stories also touch **disjoint files**, which is what makes parallel execution real rather
than aspirational:

| Sub-story | Owns | Touches nothing in |
|---|---|---|
| 41-1a | `Tamma.Core/Agents/**`, `Tamma.Api/Prompts/**`, `Tamma.Api/Services/Agents/DefaultAgentConfig.cs`, `Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs` | `Tamma.Core/Documents/**`, `Tamma.Data/**` |
| 41-1b | `Tamma.Core/Documents/Types/**`, `DocumentTypeKey.cs`, `DocumentTypeRegistry.cs`, `Policy/AcceptanceDefaults.cs` | `Tamma.Core/Agents/**`, `Tamma.Data/**` |
| 41-1c | the same three `Documents` files **plus** `DocumentEnvelope.cs`, `Tamma.Data/Entities/DocumentInstance.cs`, `TammaModelConfiguration.cs`, `Migrations/Tenant/**`, `Lineage/IssueDocumentLineage.cs`, `Endpoints/DocumentEndpoints.cs` | `Tamma.Core/Agents/**` |

## Shared sequencing

```
41-1a ──┐ (independent)
41-1b ──┼── all three land → Wave 1 unblocks
41-1c ──┘ (independent of 41-1a; shares 3 files with 41-1b)
```

Three constraints, all verified against the tree:

1. **41-1a → 41-1b is a partial edge, not "independent".** The umbrella story says "41-1a and 41-1b are
   independent and can run in parallel"; 41-1b's own Dependencies says `SprintPlan` and `UxSpec` need
   41-1a for their producing role. Both cannot be true. **Four of six types (`AcceptanceCriteria`,
   `BacklogOrdering`, `TestPlan`, `ThreatModel`) are genuinely independent** — their producing cells
   (`define-acceptance-criteria`, `prioritize-backlog`, `plan-test-strategy`, `threat-model`) exist today
   with shipped prompt files. **Two are not**: `(scrum_master, plan-sprint)` and
   `(ux_designer, author-ui-spec)` do not exist until 41-1a mints role, action and template, and 41-1b
   AC6's `ContractBindingTests` entry for each is checked against that template by
   `EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken`
   (`tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:361`). Land those two types last, or
   land 41-1b's `Bindings` entries for them in 41-1a's wake.
2. **41-1b ↔ 41-1c share three files and one pin pair.** Both append to `DocumentTypeKey`
   (`src/Tamma.Core/Documents/DocumentTypeKey.cs:22-34`), both append to
   `DocumentTypeRegistry.s_registrations` (`:27-40`), both bump `DocumentTypeKeyTests.cs:20` and
   `DocumentTypeRegistryTests.cs:37`. Whichever merges second rebases the pin arithmetic (10→16→17 or
   10→11→17). Deliberately additive, deliberately conflict-prone at exactly two lines each — do not try
   to be clever about it, just rebase.
3. **41-1c's D2 reviewer row is only *provable* after 41-1a.** Prose's documented default reviewer is
   `tech_writer`, and `RolePhaseMap.GetReviewActionForRole` (`src/Tamma.Core/Agents/RolePhaseMap.cs:376-387`)
   throws `ArgumentOutOfRangeException` for `TechWriter` today. 41-1c proves the type with a
   non-`tech_writer` reviewer; the end-to-end prose-review assertion is 41-1a's AC3.

Cheapest schedule: start all three on day 0; 41-1c (3–4 d) finishes first and unblocks 41-9's Wave-1
reference implementation; the enabler set is ~6 days wall-clock, not 12–15.

## The shared lockstep rules (all three sub-stories obey these)

Both vocabularies in this repo are **fail-loud in both directions** and neither has a
register-now-implement-later escape hatch. Every plan below enumerates its own lockstep; these are the
rules they instantiate.

**Adding an `AgentRole` or `AgentAction` (41-1a) is one atomic change across seven artifacts:**

1. the enum member (`Tamma.Core/Agents/AgentRole.cs` / `AgentAction.cs`);
2. the eligibility matrix row (`RolePhaseMap.s_eligibleActions`, `RolePhaseMap.cs:43-163`) — plus
   `s_primaryAction` (`:178-189`) for a new *role*, because `GetPrimaryPhaseForRole` indexes it raw
   (`:314`);
3. `DefaultAgentConfig.s_perRole` (`Tamma.Api/Services/Agents/DefaultAgentConfig.cs:41-176`) for a new
   *role* — `ForRole` asserts the role is valid then indexes raw (`:185-188`), so a role in the enum with
   no config row is a `KeyNotFoundException` on the live resolver path
   (`AgentResolverService.cs:108`, `:413`);
4. one `Prompts/{role}/{action}.md` per new cell **and** one `Prompts/{role}/_system.md` per new role —
   `PromptFileLoader.Build` refuses to start with `PROMPT.SEED.NO_BODY_FAMILY` for a taxonomy cell with no
   file (`Tamma.Api/Auth/PromptFileLoader.cs:161-167`), `PROMPT.SEED.UNKNOWN_CELL` for a file outside the
   taxonomy (`:296-302`), and `PROMPT.SEED.MISSING_SYSTEM_PROMPT` for a role with no preamble
   (`:136-142`);
5. the **count pins** — `AgentRoleTests.cs:12`, `AgentActionTests.cs:38`, `RolePhaseMapTests.cs:64`,
   `SystemPromptsTests.cs:61`, `ConventionStoreEndpointsTests.cs:720` and `:744`;
6. the **keyset-equality drift tests** — `RolePhaseMapTests.ValidRoles_Should_Contain_All_Eight_Roles`
   (`:33-40`, a literal 8-string list), `ConventionStoreEndpointsTests.cs:721` (a second literal list),
   and `ConventionSeedDriftTests` (`tests/Tamma.Api.Tests/Conventions/ConventionSeedDriftTests.cs:44-91`
   — prompt keyset ≡ convention-seed keyset ≡ taxonomy keyset; all three derived, so they pass
   automatically iff (2) and (4) are done together and fail loudly otherwise);
7. the **derived** counts that must NOT be hand-edited — `PromptFileLoaderTests.ExpectedCellCount`
   (`:20-21`), `SystemPromptsTests.ExpectedCellCount` (`:22-23`), `ConventionStoreTests.cs:66`,
   `ConventionStoreSeederTests.cs:38`, `ConventionStoreEndpointsTests.cs:56`; and `ConventionSeedSpecs.Build`
   (`Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs:54-68`), which derives the whole seed from
   `RolePhaseMap.EligibleActions` and needs no edit at all.

**Adding a `DocumentTypeKey` (41-1b, 41-1c) is one atomic change across four artifacts:**

1. the enum member (`Tamma.Core/Documents/DocumentTypeKey.cs:22-34`);
2. the `IDocumentType` implementation **in the same commit** — `DocumentTypeRegistryTests`
   `Every_vocabulary_key_now_resolves_to_an_implementation` (`:113+`) fails on any key with no
   registration, and `WorkflowInterfaceGraphTests.PendingImplementations`
   (`tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:31-33`) is deliberately EMPTY with
   `Pending_entry_is_not_already_registered` failing on a re-added entry, so there is no defer-the-impl
   escape hatch;
3. the acceptance posture — `AcceptanceDefaults.For` (`Policy/AcceptanceDefaults.cs:129-134`) ends in
   `_ => Rules`, so a new key silently takes the single-`architect` unanimous row unless given an arm.
   The static ctor loops `Enum.GetValues<DocumentTypeKey>()` calling `For` (`:119-121`), so an *invalid*
   row fails at class load — but a *wrong-but-valid* row does not;
4. the two count pins — `DocumentTypeKeyTests.cs:20` and `DocumentTypeRegistryTests.cs:37`.

**Not moved by either half:** `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`,
`HaveCount(16)`). It counts `DocumentTypeRegistry.WorkflowInterfaces` rows keyed by Elsa `DefinitionId`
(`DocumentTypeRegistry.BuildSeed`, `:134-174`); it moves with a **producing workflow**, i.e. with
41-2/41-3/41-6/41-13/41-19/41-27, one `+1` each. Do not conflate the edge pin with the vocabulary pins.

## Corrections to the story (index-level)

- **"41-1a and 41-1b are independent"** (umbrella `:47`) is true for four of six types and false for
  `SprintPlan` and `UxSpec` — see Sequencing constraint 1. 41-1b's own Dependencies section already says
  so; the umbrella contradicts it.
- **`AgentAction.cs` carries no stale "79" comment.** The parent brief and several downstream notes
  attribute the stale count to `AgentAction.cs`'s header; it is not there. The live staleness is in
  `RolePhaseMap.cs:18` ("the 79-token `AgentAction` enum") and `:204` ("The 79 workflow actions"), plus
  `RolePhaseMapTests.cs:14` and the test method **name**
  `ValidActions_Should_Contain_Seventy_Nine_Actions` (`:63`) whose assertion at `:64` is `HaveCount(80)`.
  The real member count is **80** (`grep -c '\[Wire(' AgentAction.cs`), and the jagged **cell** count is
  **93** (`101` embedded prompt files = 93 cells + 8 `_system.md`). 41-1a fixes all four sites.
- **The pre-split story's "Estimated Effort: 12–15 days across the three sub-stories"** double-counts:
  the three are parallelisable, so 12–15 is person-days, ~6 is wall-clock. The README already says ~6.

## Est. Effort

**0 days** — index only, no production code. The buildable work is 4–5 d (41-1a) + 5–6 d (41-1b) +
3–4 d (41-1c) = **12–15 person-days, ~6 days wall-clock** with three engineers.

## Blocks / Blocked by

- **Blocked by:** Epic 39 — 39-2 (registry, envelope, drift tests), 39-3/39-4 (type pattern), 39-7
  (review producers), 39-11 (store + lineage API); Stories 27-15/27-18 (taxonomy machinery). All landed.
- **Blocks (20 of the epic's 29 stories):**
  - via **41-1a**: 41-6, 41-7, 41-8, 41-10, 41-11, 41-16, 41-17 (PR-triage half), 41-22, 41-27, 41-28,
    plus the *review stage* of 41-24, 41-25, 41-26.
  - via **41-1b**: 41-2, 41-3, 41-6, 41-13, 41-19, 41-27.
  - via **41-1c**: 41-4, 41-5, 41-8, 41-9, 41-22, 41-24, 41-25, 41-26.
  - Union = 20 (41-8, 41-22, 41-24, 41-25, 41-26 appear in two sets).
- **Does NOT block:** 41-29 (Task-Level Flow Router) and the code-review half of 41-17.
- **Not owned here:** the tenant-aware scheduled-trigger seam — the fourth Wave-0 enabler, still
  ownerless, blocking 41-5, 41-7, 41-11, 41-16, 41-17 (PR sweep), 41-20, 41-23.

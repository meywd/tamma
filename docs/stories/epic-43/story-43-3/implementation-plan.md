# Implementation Plan — Story 43-3: Groups and Behaviour-Preserving Defaults

## Scope & Deliverable

When this story is done, every one of the ~153 catalogued actions belongs to exactly one `ActionGroup`, that partition is enforced at static init (totality, disjointness, no empty group), `ByGroup` is projected from the descriptors, every group carries UI-facing description text, and every `DefaultMinAutonomy` is set so that **day one under an enforcing v1 reproduces today's behaviour exactly** — a small, explicit, verified `AlwaysHuman` set and `AutonomyDial.Min` everywhere else. 43-2's provisional-group marker test is deleted.

The code is small. **The deliverable is the assignment table and the defaults table, and both need review time out of proportion to the estimate.**

## Pre-Reading

- `docs/stories/epic-43/story-43-3/43-3-groups-and-behaviour-preserving-defaults.md` — AC3/AC5 carry the full proposed assignment; ACs are source of truth
- `docs/stories/epic-43/README.md` — §2 (groups), §3 (the threshold model, `AlwaysHuman = Max + 1`), §4 (resolution ladder), §5 (absorbing `AlwaysEscalate`), decision **D1** (v1 enforces with behaviour-preserving defaults)
- `docs/stories/epic-43/story-43-2/implementation-plan.md` — D3 (enum-referenced descriptor literals), D4 (the `ActionGroup`-declared-here / assigned-there seam and the provisional-marker test), D8 (`BuildIndex` codes)
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs` — all 80 members, grouped in-source **by role**; read the class doc: role is not the partition (`RolePhaseMap` owns `(role, action)` validity, shared tokens appear once)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:43-163` (the enum-referenced table), `:170-171` (`s_rolesForAction = BuildRolesForAction()` — the projected-index idiom `ByGroup` copies)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:105-133` — `s_humanAcceptorRules` (`AcceptorRequirement.Human` at `:115`, its **only** production occurrence) and the `For(DocumentTypeKey)` switch at `:128-133`. **This is the file that falsifies design.md §3.1's "10 document types".**
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:56-66` — the `AcceptorRequirement` property doc: "*Only `design` ships a non-default per-type value*"
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs:41-42,83,243,248` — the existing business-mode human gate: `FlowDecision(mode == "business" || requireProdApproval)` → `WaitForDeploymentApprovalActivity`. **Untouched by this story; joined by OR in 43-9.**
- The 11 `WaitFor*` activities under `apps/tamma-elsa/src/Tamma.Activities/` (`ADL/WaitForCycleCallbackActivity`, `ADL/WaitForDeploymentApprovalActivity`, `ADL/WaitForMergeApprovalActivity`, `ADL/WaitForPRApprovalActivity`, `ADL/WaitForPRMergedActivity`, `ADL/WaitForPlanApprovalActivity`, `Assessment/WaitForResponseActivity`, `Documents/WaitForDocumentDecisionActivity`, `Documents/WaitForDocumentInputActivity`, `Review/WaitForFixesActivity`, `Testing/WaitForCIResultsActivity`) — see D6: several wait on machines, not people
- `apps/tamma-elsa/src/Tamma.Activities/…/TriageBindingHelper.cs:157` — the live `EscalationClass(AgentAction, TriageIntake)` producer; why `triage-intake` must **not** get a catalog `AlwaysHuman` default (D7)
- `docs/stories/epic-43/story-43-1/…` — `AutonomyDial.Min` / `.AlwaysHuman`, referenced by name in all ~153 defaults

## Corrections to the design

- **C1 — the group list has SIXTEEN names while both the epic README and design.md §2.1 assert fifteen, and neither says which to drop.** Counted from the README's own bullet list: `planning-and-analysis`, `authoring`, `review-and-acceptance`, `docs`, `code-read`, `code-write`, `command-execution`, `ci-and-test`, `source-control-read`, `source-control-write`, `issue-tracking`, `deploy-control`, `external-comms`, `model-invocation`, `secrets`, `platform-automation` = **16**. design.md §2.1 notices the discrepancy ("*That is 16 rows above — the shipped set is 15*") and then resolves it by restating that four of them exist, which resolves nothing. **Resolution: ship 16** (D2). Merging two semantically distinct groups to hit a number is the definition of a wrong-but-consistent partition.
- **C2 — design.md §3.1's "the 10 `document-type:*` where `AcceptanceDefaults.For` ships `AcceptorRequirement.Human`" is false; it is exactly ONE.** `AcceptanceDefaults.For` (`:128-133`) maps `Plan`/`Review` → `s_panelRules` and `Design` → `s_humanAcceptorRules`; everything else → `Rules`. `AcceptorRequirement.Human` occurs once in production, at `AcceptanceDefaults.cs:115`. `AcceptanceRules.cs:60-63` states it in prose. **Under an enforcing v1 this error would have gated nine document types on day one.** Only `document-type:design` ships `AlwaysHuman` (AC8).
- **C3 — "the agent-actions behind the 11 landed `WaitFor*` activities" is not a usable default rule.** A `WaitFor*` is a suspend point in a *workflow graph*, not a property of an `AgentAction`; and at least three of the eleven wait on **machine** events (`WaitForPRMergedActivity`, `WaitForCIResultsActivity`, `WaitForCycleCallbackActivity`), so "behind a `WaitFor*`" does not imply "a person decides". See D6 for the rule actually used.
- **C4 — design.md §3.1 ships `deploy.promote-prod`, `deploy.rollback` and `mcp.tool.invoke` at `AlwaysHuman`, reasoning that `enforce` defaults `false`.** The epic's binding decision D1 removes that shield: **v1 enforces.** Those three ship at `AutonomyDial.Min`. See D3.
- **C5 — `AgentAction.cs`'s header comment says 79; the real count is 80.** 43-2 fixes the comment; this story's per-group counts sum to 80, not 79.

## Design Decisions

- **D1 — The partition is by *kind of consequence when the action completes*, not by role, not by risk class, not by producing agent.** Role is impossible (shared tokens appear once and are reused across roles; `RolePhaseMap` owns validity). Risk class already exists as `ActionRisk` and is orthogonal — a group is *what an admin wants to assign as a unit*, and an admin wants to say "all code writes need a human below 90", not "all `Mutating` actions". The consequence rule is stated once and applied consistently, which is what makes the four contested calls (D5) arguable rather than arbitrary.

- **D2 — Ship SIXTEEN groups (C1).** The named list is 16; no merge candidate survives scrutiny. `code-read` ∪ `source-control-read` → "read-only inspection" is the only remotely defensible merge, and it fuses two different trust surfaces (reading workspace files vs. reading repository history/remotes) that an admin may reasonably want to set differently. `docs` ∪ `authoring` is worse — separating low-consequence prose from binding artifacts is one of the main reasons to have groups at all. The count pin takes 16 and the discrepancy is recorded here so a reviewer does not "fix" the enum down to 15. **If the reviewer overrules this, the decision must be made before the enum ships** — `ActionGroup` wires are persisted in `action_assignments` (43-5) and merging afterwards is a migration.

- **D3 — `effect:deploy.promote-prod`, `effect:deploy.rollback`, `effect:mcp.tool.invoke` ship at `AutonomyDial.Min` (binding deviation from design.md §3.1).** Rationale: the epic's D1 makes v1 enforcing with no per-target `enforce=false` default and no soak period. design.md's justification for `AlwaysHuman` was explicitly *"because `enforce` defaults `false` they change nothing at runtime until an admin flips them"* — that premise is gone. Shipping them at `AlwaysHuman` would make an upgrade gate every production deploy and every MCP invocation in every deployment, which is a behaviour change hidden inside a story whose entire purpose is to document today's behaviour rather than create gating. **The admin opts in.** The existing safety property is not weakened: `DeploymentPipelineWorkflow.cs:243` still routes business-mode and `requireProdApproval` deploys to `WaitForDeploymentApprovalActivity`, and 43-9 adopts the gate **by OR**, never by replacement. A test pins all three at `Min` with a comment naming this decision, so "restoring" `AlwaysHuman` requires deleting an assertion that explains why not.

- **D4 — The `AlwaysHuman` set is DERIVED and SMALL, listed explicitly, never inferred from `ActionRisk`.** Inferring (`Destructive → AlwaysHuman`) is rejected as AC10 states: it is unenforceable at the override layer, and asserting it only over defaults adds nothing to an explicit table. The derivation rule is: **a member ships `AlwaysHuman` if and only if, today, a person must act before it can complete.** Applying it:
  - `document-type:design` — `AcceptanceDefaults.For(Design)` ships `AcceptorRequirement.Human` (C2). **The only member from the document-type plane.**
  - Everything else ships `AutonomyDial.Min`.

  That is a **one-member** `AlwaysHuman` set. This is smaller than design.md's "~15" and it is the honest answer: today, almost nothing is gated. A catalog that claims otherwise on day one is a catalog that changes behaviour while claiming not to. The `ShippedDefaults_ReproduceTodaysGatingBehaviour` test carries the set as an explicit table with the derivation rule in a comment, so growing it later is a reviewed decision.

- **D5 — The four contested assignments, each with the rule applied and the rejected alternative, recorded as descriptor comments (AC12).**
  1. **`implement-infrastructure` → `authoring`, not `deploy-control`.** Rule: group by consequence *at completion*. Writing IaC into a branch has code-write consequence; the production consequence arrives at deploy, which is separately gated. Rejected alternative: `deploy-control`, on the argument that infrastructure changes are production-affecting in intent. **This is the assignment most likely to be overruled** — an admin who sets `deploy-control` to `AlwaysHuman` expecting to have gated Terraform changes will not have. Flagged for review explicitly.
  2. **`write-tests` / `write-test-cases` / `write-regression-test` → `authoring`, not `ci-and-test`.** Rule: `ci-and-test` is *executing* tests (`tool:run_tests`, `effect:ci.tests.trigger`, `exploratory-test`); writing test code is authoring code. Rejected alternative: a single "testing" group, which would fuse a low-risk read-ish execution with code authorship.
  3. **`plan-*` splits between `planning-and-analysis` and `authoring` by whether the output is binding.** `plan-system-design`, `plan-migration-strategy`, `plan-implementation`, `plan-refactor`, `plan-fix` produce artifacts others build against → `authoring`. `plan-scope`, `plan-roadmap`, `plan-debugging`, `plan-test-strategy`, `plan-incident-response`, `plan-deployment` produce ordering/analysis → `planning-and-analysis` (except `plan-deployment` → `deploy-control`, where the subject matter dominates). Rejected alternative: all `plan-*` in one group, which would put "the implementation plan the developer codes to" beside "the sprint ordering".
  4. **`audit-secrets` → `secrets`, not `planning-and-analysis`,** even though it is an analysis action. Rule: for the `secrets` group the *subject* dominates the *verb*, because the group exists so an admin can gate everything touching secrets in one move; an audit action that reads secret material and lands outside the group defeats that. Rejected alternative: `planning-and-analysis` by verb-consistency with the other `audit-*`/`assess-*` actions. Note `audit-dependencies` stays in `planning-and-analysis` — it touches no secret material.

- **D6 — The `WaitFor*` set does NOT drive defaults (C3).** Instead of "the agent-actions behind the 11 landed `WaitFor*` activities", the rule is D4's: a person must act before completion, *today*. The `WaitFor*` inventory is used as an **input to the review**, not as a rule: during implementation, walk each of the 11 and record (in the PR description) whether it waits on a person or a machine, and which workflow it sits in. Three wait on machines (`WaitForPRMergedActivity`, `WaitForCIResultsActivity`, `WaitForCycleCallbackActivity`). The human-waiting ones gate *a workflow at a point*, not *an action in general* — the same `AgentAction` dispatched from another graph is not gated — so promoting them to per-action `AlwaysHuman` defaults would gate paths that are automated today. That is the behaviour change D3 exists to avoid, arriving through a different door.

- **D7 — `agent-action:triage-intake` ships `AutonomyDial.Min`, NOT `AlwaysHuman`, despite the live `AlwaysEscalate` entry.** `TriageBindingHelper.cs:157` ships `EscalationClass(AgentAction, TriageIntake)`; 43-5's evaluator contributes `AlwaysHuman` as a `max()` **floor** from that legacy surface. Duplicating it as a catalog default would mean deleting the legacy entry in the acceptance-rules UI **fails to lower the threshold** — the admin deletes the rule, the behaviour does not change, and the surface becomes untrustworthy. The floor must come from exactly one place. 43-5's `ShippedTriageDefault_StillEscalates` pins the composed outcome; this story pins the *default* at `Min` with a comment pointing at that test.

- **D8 — Group descriptions are descriptor-adjacent data authored here, rendered by 43-7 (AC6).** `deploy-control`'s description carries epic risk 8 verbatim in user-facing wording: production deploy is an LLM tool loop (`DeploymentPipelineWorkflow` dispatches generic `llm-call` with `enableTools=true`), so gating the deploy effect gates the **stage transition**; the deploy itself runs inside the loop under `tool:shell_execute`. A limitation that lives only in a design document is a limitation the admin will discover in production. Similarly `model-invocation`'s description states that MCP is one coarse member with no per-server/per-tool granularity, and `command-execution`'s states that `shell_execute` can reach any governed HTTP route by `curl` — the two bypasses in the epic's risk list. **Writing these is uncomfortable and is the point.**

- **D9 — `ByGroup` is projected; three new `BuildIndex` checks.** `ACTION.CATALOG.GROUP_EMPTY` (naming the group), plus totality and disjointness — which are *structurally* guaranteed by `Group` being a non-nullable field on a record, so they are asserted as tests rather than runtime throws, with one exception: a descriptor array containing two entries for the same `ActionKey` with different groups is caught by 43-2's existing `DUPLICATE_KEY`. Do not add a redundant runtime check that can never fire; do add the test that proves it cannot.

- **D10 — 43-2's provisional-marker test is deleted in this story's commit, not deprecated.** Leaving it passing-because-vacuous is exactly the dead-guard pattern the epic indicts.

## Implementation Steps

1. **Review-first pass — produce the assignment table as a reviewable document before touching code.** Walk all 80 `AgentAction` members against D1's rule, recording the group and (for the four contested ones) the rejected alternative. Walk the 11 `WaitFor*` activities and record person-vs-machine and host workflow (D6). Verify `AcceptanceDefaults.For` yourself (C2) — do not take this plan's word for it. Output: the table in the story's AC3/AC5, plus the `WaitFor*` findings, in the PR description. **This step is the story.**

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/ActionGroup.cs`** (D2) — confirm 16 members with wires `planning-and-analysis`, `authoring`, `review-and-acceptance`, `docs`, `code-read`, `code-write`, `command-execution`, `ci-and-test`, `source-control-read`, `source-control-write`, `issue-tracking`, `deploy-control`, `external-comms`, `model-invocation`, `secrets`, `platform-automation`; replace 43-2's declaration-only header with the partition contract; add each member's UI-facing description (D8) as XML doc or a companion `ActionGroupDescriptions` map, whichever 43-7 can consume without a second lookup.

3. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs`** — replace every provisional `Group` with the assigned value per AC3/AC5; add the four D5 comments; set every `DefaultMinAutonomy` to `AutonomyDial.Min` except `document-type:design` → `AutonomyDial.AlwaysHuman` (D4). **Written as named constants, never literals** — a literal here is what would collide with 43-1's drift guard and, worse, would not move when the dial does.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.cs`** — project `ByGroup` from the descriptors (`RolePhaseMap.cs:170-171` idiom); add `ACTION.CATALOG.GROUP_EMPTY` to `BuildIndex`, naming the offending group (D9).

5. **CREATE/MODIFY tests** under `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/` (see Test Plan); **DELETE** 43-2's `ActionCatalogProvisionalGroupTests` (D10).

6. **Verify** — `dotnet build`, `dotnet test`, both hosts boot (43-2's eager touch now also exercises the group checks). Rehearse the `GROUP_EMPTY` failure: temporarily reassign the sole member of a one-member group and confirm the app refuses to start naming the emptied group; revert.

## Data & Migrations

**None.** `Tamma.Core` has no EF dependency and nothing here is persisted. Two forward-looking notes for 43-5, which owns the tables:

- `ActionGroup` **wires become persisted vocabulary** the moment `action_assignments` accepts group-scope rows. Renaming or merging a group after 43-5 is a data migration. That is why D2's count decision must be settled in this story's review, not deferred.
- **No CHECK constraint on `min_autonomy`** in 43-5's migration — a CHECK lives in a migration snapshot and would become a permanent second hardcoding of the dial bound, defeating 43-1. Restated here because this story is where the defaults are chosen and the temptation to "protect" them in SQL arises.

`dotnet ef migrations has-pending-model-changes` must stay clean.

## Events

None emitted or consumed. Assignment-change audit events (`ACTION.ASSIGNMENT.*`) arrive with 43-5/43-6; gate outcomes with 43-9. Shipped defaults are compile-time data and produce no event.

## Test Plan

All NUnit + FluentAssertions in `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/`.

- **`ActionGroupPartitionTests`**
  - `EveryMember_HasExactlyOneGroup` — every catalogued `ActionKey` appears in exactly one `ByGroup` set, and the union equals `ByKey.Keys` (totality **and** disjointness in one assertion).
  - `EveryGroup_HasAtLeastOneMember` — no empty group.
  - `GroupCount_Is16` — with a comment carrying C1 and D2 so the number is not "corrected" downward.
  - `BuildIndex_throws_GROUP_EMPTY_naming_the_group` — via 43-2's internals-visible test seam.
- **`ActionGroupMembershipTests`** — one test per group asserting the **explicit expected set** of wires (not just a count). This is what makes a reassignment a reviewed diff: moving `implement-infrastructure` from `authoring` to `deploy-control` fails two named assertions rather than silently shifting two counts. Per-group counts: planning-and-analysis 29, authoring 19, review-and-acceptance 26 (16 agent + 10 document-type), docs 10, code-read 3, code-write 1, command-execution 2, ci-and-test 3, source-control-read 1, source-control-write 6, issue-tracking 2, deploy-control 6, external-comms 2, model-invocation 3, secrets 4, platform-automation 36 — **summing to 153**, asserted.
- **`ActionCatalogDefaultsTests`**
  - `ShippedDefaults_ReproduceTodaysGatingBehaviour` — an explicit table of the `AlwaysHuman` members (today: `document-type:design` alone), with D4's derivation rule in a comment.
  - `EveryOtherMember_DefaultsToMin` — the complement, so a new member added later lands as automated and the choice is visible in the diff.
  - `DesignDocumentType_MatchesAcceptanceDefaults` — asserts `document-type:design`'s default is `AlwaysHuman` **because** `AcceptanceDefaults.For(DocumentTypeKey.Design).AcceptorRequirement == AcceptorRequirement.Human`, and that every other `DocumentTypeKey` is `Any` and defaults to `Min`. Reads the real switch, so the two surfaces cannot diverge (C2).
  - `DeployAndMcp_ShipAtMin_PerEpicDecisionD1` — the three members from D3/C4, with the decision named in the assertion message.
  - `TriageIntake_ShipsAtMin_FloorComesFromAlwaysEscalate` — D7, pointing at 43-5's `ShippedTriageDefault_StillEscalates`.
  - `EveryDefault_IsOverridableOverTheApi` (inherited from 43-2) still passes.
- **`ActionGroupDescriptionTests`** — every group has non-empty description text; `deploy-control`'s mentions the LLM-tool-loop limitation; `command-execution`'s mentions the shell bypass; `model-invocation`'s mentions MCP coarseness (D8). Content assertions, not just non-empty — these are the only honest disclosure of three known holes and a blank-but-present string would satisfy a weaker test.
- **Boot rehearsal (step 6)** — `GROUP_EMPTY` fires and names the group.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — strict partition, projected `ByGroup`, `GROUP_EMPTY` | 3, 4 | `ActionGroupPartitionTests` (all four) |
| 2 — group count derived, not asserted in advance | 1, 2 (D2) | `GroupCount_Is16` + the C1 record; reviewer sign-off |
| 3 — all 80 agent-actions assigned | 1, 3 | `ActionGroupMembershipTests` per-group expected sets |
| 4 — 10 document types → `review-and-acceptance` | 3 | membership test |
| 5 — remaining 63 assigned, total 153 | 3 | membership tests + the 153 sum assertion |
| 6 — group descriptions incl. the deploy disclosure | 2 (D8) | `ActionGroupDescriptionTests` content assertions |
| 7 — defaults reproduce today's behaviour | 3 (D4) | `ShippedDefaults_…` + `EveryOtherMember_DefaultsToMin` |
| 8 — only `design` is `AlwaysHuman` from the doc plane | 3 (C2) | `DesignDocumentType_MatchesAcceptanceDefaults` |
| 9 — deploy ×2 and MCP ship at `Min` | 3 (D3) | `DeployAndMcp_ShipAtMin_PerEpicDecisionD1` |
| 10 — no `RiskFloor` invariant | — | absence verified by review; no such test exists, deliberately |
| 11 — the test set | 5 | full suite green; provisional-marker test deleted |
| 12 — contested assignments documented in code | 3 (D5) | reviewer check: four comments present with rule + rejected alternative |

## Risks & Mitigations

- **A wrong-but-consistent partition ships and no test catches it.** *This is the story's defining risk and it cannot be mitigated by testing.* Mitigations, all procedural: (a) step 1 produces the table as a reviewable artifact **before** code; (b) per-group **explicit expected-set** tests make every later reassignment a named diff rather than a count shift; (c) D5 records the four contested calls with rejected alternatives in the code, so a future reader disagrees with a decision instead of reverse-engineering one; (d) the story's Priority section says out loud that review, not implementation, is the binding constraint. **A reviewer who skims this story's diff has not reviewed it.**
- **`implement-infrastructure → authoring` may be the wrong call** (D5.1). An admin who raises `deploy-control` to `AlwaysHuman` will believe Terraform changes are gated; they are not. Mitigation: flagged explicitly for review, and `authoring`'s and `deploy-control`'s descriptions should each say where infrastructure authoring sits. If overruled, it is a one-line change **now** and a persisted-vocabulary question after 43-5.
- **The `AlwaysHuman` set is one member, which will look wrong.** Reviewers expecting design.md's "~15" will read a one-member set as an omission. Mitigation: C2's verified evidence and D4's derivation rule are stated in the story, the plan **and** the test comment. The honest fact is that Tamma gates almost nothing today; a catalog that claims more is a catalog that changed behaviour while claiming not to.
- **Sixteen groups vs. the design's asserted fifteen** (C1/D2). A reviewer may "fix" the enum down. Mitigation: the count pin's comment carries the reasoning, and the persisted-vocabulary consequence (Data & Migrations) makes the cost of deciding later explicit.
- **Shipping the deploy effects at `Min` reads as weakening safety** (D3). Mitigation: it is not — the deployment pipeline's business-mode gate is untouched (`DeploymentPipelineWorkflow.cs:243,248`) and 43-9 joins by OR. Say so in the PR description, not only here, because this is the change most likely to be challenged in review.
- **Group descriptions are the only disclosure of three known bypasses** (D8) and are easy to trim as "UI copy". Mitigation: content assertions in `ActionGroupDescriptionTests`, so trimming them fails a test.
- **`ActionGroup` wires become persisted vocabulary at 43-5.** Mitigation: settle the count and the four contested calls in this story's review; note the migration cost in Data & Migrations.

## Blocks / Blocked by

- **Blocked by 43-2** (hard) — `ActionDescriptor`, `Descriptors.cs`, the `ActionGroup` declaration, `BuildIndex`, and the provisional-marker test this story deletes.
- **Blocked by 43-1** (hard, transitively) — every default is `AutonomyDial.Min` / `.AlwaysHuman` by name.
- **Blocks 43-5** (the resolver's group tier resolves against these groups; the `AlwaysEscalate` floor composes with these defaults; `action_assignments` persists these wires), **43-6** (group-scope PUT), **43-7** (the grouped table, the greyed-row treatment, and the group descriptions), **43-9** (enforcement bites these thresholds on day one — which is only safe because of D3/D4).
- **Reads but does not modify:** `AcceptanceDefaults`, `AcceptorRequirement`, `DeploymentPipelineWorkflow`, `TriageBindingHelper`.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | The assignment pass: 80 agent-actions + 73 others against D1's rule; `WaitFor*` person-vs-machine walk; independent verification of `AcceptanceDefaults.For` | 1.25 |
| 2 | `ActionGroup` finalization + 16 UI-facing descriptions (incl. the three disclosures) | 0.4 |
| 3 | Descriptor edits: 153 `Group` values, 153 defaults, the four D5 comments | 0.5 |
| 4 | `ByGroup` projection + `GROUP_EMPTY` | 0.2 |
| 5 | Test suite (partition, 16 membership sets, defaults table, description content); delete the marker test | 0.5 |
| 6 | Build/test/boot verification + `GROUP_EMPTY` rehearsal + PR write-up carrying the table | 0.25 |
| — | **Review buffer** — the assignment table, not the code | (see note) |
| **Total** | | **3.1** (story estimate: 3 days) |

> The 3-day figure covers **authoring**. Review of the assignment table is not costed here and must be scheduled separately; the epic README says this story "gets disproportionate review relative to its estimate", and that is a scheduling instruction, not a sentiment.

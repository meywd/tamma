# Story 43-3: Groups — the Partition, the Assignment of All ~153 Members, and Behaviour-Preserving Defaults

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As an **admin who must set autonomy policy without editing 153 rows one at a time**,
I want every catalogued action to belong to exactly one group I can assign as a whole, and I want the shipped defaults to reproduce today's behaviour exactly,
So that turning enforcement on changes nothing until I deliberately raise a threshold — and so that "greyed out because it is automated at the current floor" is a real, visible state rather than an absent row.

## Priority

P0 — and **this is the largest judgment call in the epic.**

> **No test can catch a wrong-but-consistent partition.** Totality and disjointness are machine-checkable and will be enforced at static init. *Whether `implement-infrastructure` belongs in `authoring` or `deploy-control` is not.* A partition that is internally consistent and semantically wrong compiles, passes every test in this repo, and ships a bad safety policy — an admin who raises `deploy-control` to `AlwaysHuman` believing they have gated infrastructure changes will not have gated them. **This story must get review time disproportionate to its 3-day estimate, and the review must be of the assignment table, not of the code.**

## Architectural Context (READ FIRST)

### What 43-2 leaves for this story

43-2 ships `ActionGroup` as a **declaration-only** `[Wire]` enum and every `ActionDescriptor` with a provisional `Group` marked `// 43-3` (guarded by a test that fails while any marker remains). This story assigns all ~153 members for real, projects `ByGroup`, enforces totality and disjointness at static init, deletes the marker test, and sets every `DefaultMinAutonomy`.

The by-group index is **projected, never hand-maintained** — the `RolePhaseMap` idiom (`apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:170-171`: `s_rolesForAction = BuildRolesForAction()`). Membership is a field on the descriptor; there is no `[Category]` attribute and none is introduced — `EnumWire.cs`'s `WireAttribute` carries only a wire string, and inventing a second grouping idiom beside the shipped one is the drift this epic exists to prevent.

`BuildIndex` gains `ACTION.CATALOG.GROUP_EMPTY`, so a group cannot rot into a dead label.

### The 80 agent-actions are the real work

`apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs` declares **80** `[Wire]` members (verified by count; **the file's own header comment says 79 — it is stale**, and 43-2 fixes it). They are grouped in the source by role, but role is *not* the partition: `RolePhaseMap` defines which `(role, action)` pairs are valid, and shared tokens (`context-scan`, `code-review`, `plan-review`, `write-tests`) appear once and are reused across roles. **Grouping by role would therefore be both wrong and impossible.** The partition is by *kind of consequence when the action completes*.

The full proposed assignment is in Acceptance Criterion 3 below, per member, with counts.

### Behaviour-preserving defaults — and what "today's behaviour" actually is

**BINDING EPIC DECISION (D1): v1 ENFORCES, with defaults that reproduce today's behaviour exactly.** There is no separate enforcement-flip story and no soak precondition. Every action ships assigned as it behaves today; the admin opts into gating and it bites immediately.

That has a direct consequence this story must get right:

> **`effect:deploy.promote-prod`, `effect:deploy.rollback` and `effect:mcp.tool.invoke` ship at `AutonomyDial.Min`, NOT at `AlwaysHuman`.**

design.md §3.1 proposes `AlwaysHuman` for those three, reasoning that `enforce` defaults `false` so nothing changes at runtime. **Under the binding decision there is no `enforce=false` shield.** Shipping them at `AlwaysHuman` would gate production deploys on day one for every deployment that upgrades — a behaviour change smuggled into a "documents today's gating" story. The admin opts in. Recorded as a design decision in the plan.

**The existing deployment-pipeline business-mode human gate is untouched and keeps firing.** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs:243` routes `FlowDecision(mode == "business" || requireProdApproval)` to `WaitForDeploymentApprovalActivity` (`:248`). That predicate is not replaced, not weakened, and not folded into the threshold — 43-9 adopts the gate **by OR**, never by replacement, because a threshold-only replacement would be strictly weaker for business-mode tenants.

### Correction: only ONE document type ships a human acceptor, not ten

design.md §3.1 lists as `AlwaysHuman` "the 10 `document-type:*` where `AcceptanceDefaults.For` ships `AcceptorRequirement.Human`". **Verified false.** `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:128-133`:

```csharp
public static AcceptanceRules For(DocumentTypeKey type) => type switch
{
    DocumentTypeKey.Plan or DocumentTypeKey.Review => s_panelRules,
    DocumentTypeKey.Design => s_humanAcceptorRules,
    _ => Rules,
};
```

`AcceptorRequirement.Human` appears exactly once in production (`AcceptanceDefaults.cs:115`, inside `s_humanAcceptorRules`), and `AcceptanceRules.cs:60-63` states it outright: "*Only `design` ships a non-default per-type value.*" `Plan`/`Review` get **panel** rules — a multi-reviewer selection, not a human acceptor.

So the behaviour-preserving `AlwaysHuman` set from the document-type plane is **`document-type:design` alone**. Getting this wrong would ship nine document types gated on day one under an enforcing v1.

### The 11 landed `WaitFor*` activities

Verified present under `apps/tamma-elsa/src/Tamma.Activities/`: `ADL/WaitForCycleCallbackActivity`, `ADL/WaitForDeploymentApprovalActivity`, `ADL/WaitForMergeApprovalActivity`, `ADL/WaitForPRApprovalActivity`, `ADL/WaitForPRMergedActivity`, `ADL/WaitForPlanApprovalActivity`, `Assessment/WaitForResponseActivity`, `Documents/WaitForDocumentDecisionActivity`, `Documents/WaitForDocumentInputActivity`, `Review/WaitForFixesActivity`, `Testing/WaitForCIResultsActivity`.

These are **suspend points, not action gates.** A `WaitFor*` in a workflow graph means *that workflow* waits at *that point*; it does not mean the `AgentAction` the workflow dispatches is human-gated in general. Mapping "behind a `WaitFor*`" to `AlwaysHuman` per agent-action therefore requires walking the graphs, and several of them (`WaitForPRMergedActivity`, `WaitForCIResultsActivity`, `WaitForCycleCallbackActivity`) wait on **machine** events, not people. The plan resolves this rather than assuming the design's phrase.

### `AlwaysEscalate` is absorbed as a floor, not migrated

`AcceptanceRules.AlwaysEscalate` has a **live production producer** — `TriageBindingHelper.cs:157` ships `EscalationClass(AgentAction, TriageIntake)` — and is currently inert (`AcceptanceGuardrails.TryPreGate` has zero production callers). 43-5's evaluator gives `TryPreGate` its first production call site and contributes `AlwaysHuman` as a `max()` floor. **Nothing is deleted and nothing is migrated.** This story must therefore **not** assign `agent-action:triage-intake` a shipped default of `AlwaysHuman` to "match" it: the floor comes from the legacy surface, and duplicating it into a catalog default would make deleting the legacy entry fail to lower the threshold. A test (`ShippedTriageDefault_StillEscalates`, 43-5) pins the composed outcome.

## Acceptance Criteria

1. **`ActionGroup` is a strict partition, machine-enforced.** Every catalogued `ActionKey` has exactly one `Group`; `BuildIndex` throws `ACTION.CATALOG.GROUP_EMPTY` naming the group if any enum member has zero descriptors. `ByGroup` is a projected `FrozenDictionary<ActionGroup, FrozenSet<ActionKey>>`, built from the descriptors — never a hand-written second table.

2. **The group count is derived from the assignment, not asserted in advance.** The epic README and design.md both *name* **sixteen** groups while asserting the enum has fifteen, and neither says which to drop (see the plan's Corrections). This story ships the count the assignment requires, pins it, and records the resolution as a design decision. **Merging two semantically distinct groups to satisfy an arithmetic claim is explicitly rejected** — that is precisely the wrong-but-consistent partition this story exists to avoid.

3. **All 80 agent-actions assigned, per the table below.** This is the deliverable that needs review.

   **`planning-and-analysis` — 29** — *investigation, triage, estimation, ordering; produces understanding, not a binding artifact*
   `context-scan`, `triage-intake`, `clarify-requirements`, `plan-scope`, `define-acceptance-criteria`, `prioritize-backlog`, `plan-roadmap`, `generate-assessment-questions`, `analyze-assessment-response`, `research`, `score-ambiguity`, `triage-technical`, `assess-technical-risk`, `create-tasks`, `debug-rootcause`, `resolve-blocker`, `decompose-issue`, `plan-debugging`, `triage-context-scan`, `plan-test-strategy`, `triage-defect`, `threat-model`, `assess-vulnerability`, `audit-dependencies`, `analyze-security-incident`, `monitor-health`, `diagnose-incident`, `plan-incident-response`, `assess-capacity`

   **`authoring` — 19** — *produces a binding artifact: code, a technical design, or an implementation plan others build against*
   `incorporate-answers`, `plan-system-design`, `design-api-contract`, `design-data-model`, `design-integration`, `plan-migration-strategy`, `propose-design`, `plan-implementation`, `plan-refactor`, `plan-fix`, `implement-feature`, `implement-fix`, `write-tests`, `refactor`, `debug`, `address-review-comments`, `write-test-cases`, `write-regression-test`, `implement-infrastructure`

   **`review-and-acceptance` — 16 agent-actions** (+ the document-type plane, AC4)
   `review-acceptance`, `review-scope`, `plan-review`, `code-review-architecture`, `code-review`, `mentor-feedback`, `self-review`, `review-feasibility`, `verify-acceptance`, `code-review-coverage`, `review-testability`, `plan-review-security`, `code-review-security`, `review-compliance`, `review-operability`, `review-docs`

   **`docs` — 10** — *human-readable prose about work already done; no binding technical content*
   `summarize-stakeholder`, `write-adr`, `summarize-technical`, `write-postmortem`, `summarize-changes`, `write-user-docs`, `write-api-docs`, `write-release-notes`, `write-runbook`, `update-changelog`

   **`deploy-control` — 4 agent-actions** (+ 2 effects, AC5)
   `plan-deployment`, `configure-cicd`, `deploy`, `rollback`

   **`ci-and-test` — 1 agent-action** (+ 2, AC5) — *executing tests, not writing them*
   `exploratory-test`

   **`secrets` — 1 agent-action** (+ 3, AC5)
   `audit-secrets`

   Total 29 + 19 + 16 + 10 + 4 + 1 + 1 = **80**. The count is asserted by a test, and each group's membership is asserted by an explicit expected-set test so a reassignment is a reviewed diff.

4. **The 10 `document-type:*` members → `review-and-acceptance`.** They are acceptance decisions by construction.

5. **The remaining 63 members assigned:**
   - `code-read` — `tool:file_read`, `tool:search_code`, `tool:get_acceptance_rules` (3)
   - `code-write` — `tool:file_write` (1)
   - `command-execution` — `tool:shell_execute`, `effect:process.spawn` (2)
   - `ci-and-test` — `tool:run_tests`, `effect:ci.tests.trigger` (2)
   - `source-control-read` — `tool:git_operations.read` (1)
   - `source-control-write` — `tool:git_operations.write`, `effect:git.branch.create`, `effect:git.branch.delete`, `effect:git.pull-request.create`, `effect:git.pull-request.merge`, `effect:git.release.create` (6)
   - `issue-tracking` — `effect:git.issue.patch`, `effect:jira.ticket.patch` (2)
   - `deploy-control` — `effect:deploy.promote-prod`, `effect:deploy.rollback` (2)
   - `external-comms` — `effect:notify.slack.queue`, `effect:notify.email.send` (2)
   - `model-invocation` — `effect:llm.call`, `effect:mcp.tool.invoke`, `effect:agent-dispatch.run` (3)
   - `secrets` — `effect:secret.reveal`, `automation:secret-auto-rotation-scheduler`, `automation:retire-sweep` (3)
   - `platform-automation` — the 5 `effect:engine.*`, the remaining `automation:*` (24 as shipped), all 8 `platform-task:*` (37 as shipped; 36 at authoring — +1 priming service from the Epic 46 review)

   Grand total across all groups: **154 as shipped** (153 at authoring; the pin comment in `ActionVocabularyCountTests` documents the delta), pinned.

6. **Every group carries a UI-facing description**, because the group description is the only place some limitations can honestly appear. **`deploy-control`'s description must state that production deploy is an LLM tool loop, not a typed activity** — `DeploymentPipelineWorkflow` dispatches generic `llm-call` with `enableTools=true`, so gating the deploy effect gates the *stage transition* while the deploy itself happens inside the loop under `tool:shell_execute`. Epic risk 8 requires this in the UI, not only in a doc.

7. **Defaults reproduce today's behaviour exactly.** Every `ReadOnly`/`Mutating`/`Command` member ships at `AutonomyDial.Min` — including `tool:file_write`, `tool:git_operations.write` and `effect:git.pull-request.create`, all currently ungated. `AlwaysHuman` is reserved for members already behind a human wait today. **The `AlwaysHuman` set is derived, small, and listed explicitly in an inline table** — not inferred from risk class.

8. **`document-type:design` is the only document-type member shipping `AlwaysHuman`** — per the verified `AcceptanceDefaults.For` switch. The other nine ship `AutonomyDial.Min`. A test pins this against `AcceptanceDefaults.For(type).AcceptorRequirement`, so the two surfaces cannot diverge.

9. **`effect:deploy.promote-prod`, `effect:deploy.rollback` and `effect:mcp.tool.invoke` ship at `AutonomyDial.Min`** (the binding deviation from design.md §3.1). A test pins all three with a comment naming the decision, so the design's proposed `AlwaysHuman` cannot be "restored" as a bug fix.

10. **No `RiskFloor` invariant.** `Destructive → AlwaysHuman` is *not* enforced as a shipped-default lower bound. It is unenforceable at the override layer (every gating rule must be replaceable over the API) and asserting it only over defaults buys nothing the explicit defaults table does not already state.

11. **Tests.** `EveryMember_HasExactlyOneGroup` (totality + disjointness); `EveryGroup_HasAtLeastOneMember` (the `GROUP_EMPTY` path); per-group explicit expected-set assertions (so any reassignment is a reviewed diff, not a silent count shift); `ShippedDefaults_ReproduceTodaysGatingBehaviour` (an explicit table of the `AlwaysHuman` members); `EveryOtherMember_DefaultsToMin`; `DesignDocumentType_MatchesAcceptanceDefaults`; `DeployAndMcp_ShipAtMin_PerEpicDecisionD1`. 43-2's provisional-marker test is **deleted** in the same commit.

12. **The contested assignments are documented in the code**, not only in review. Each of the four calls named in the plan (D5) carries a one-line comment on its descriptor stating the rule applied and the rejected alternative, so a future reader can disagree with a decision rather than reverse-engineer one.

## Dependencies

- **Blocked by 43-2** (hard) — `ActionDescriptor`, `ActionCatalog`, `Descriptors.cs`, the `ActionGroup` declaration and `BuildIndex` must exist. This story fills in a field and adds three checks.
- **Blocked by 43-1** (hard, transitively) — every default is written as `AutonomyDial.Min` or `AutonomyDial.AlwaysHuman`, never a literal.
- **Blocks:** 43-5 (the resolver's group tier and the `AlwaysEscalate` floor compose against these defaults), 43-6 (group-scope PUT), 43-7 (the grouped table and the greyed-row treatment render `ByGroup` and the descriptions), 43-9 (enforcement bites these thresholds on day one).
- **Interlocks with 43-9** — the deployment-pipeline business-mode gate (`DeploymentPipelineWorkflow.cs:243,248`) stays and is joined by OR. This story's `Min` defaults for the deploy effects are only safe *because* that gate is untouched.

## Out of Scope

- **Storage, resolution order, provenance, the `TryPreGate` bridge** — 43-5. This story ships the shipped-default tier of the ladder; the platform ceiling, the legacy floor and the override tiers are 43-5's.
- **The admin API and the group-scope PUT** — 43-6.
- **Group descriptions rendered in a UI** — 43-7. This story authors the description *text* (AC6) as descriptor data; rendering it is 43-7's.
- **Enforcement at any seam** — 43-9.
- **Any change to `AcceptanceDefaults`, `AcceptorRequirement`, or the deployment pipeline's predicate.** This story reads them; it does not touch them.
- **Re-deriving member counts.** 43-2 froze them. If a count moves, that is a 43-2 correction, not a 43-3 change.

## Estimated Effort

3 days — **of which review, not implementation, is the binding constraint.** The code is a field assignment plus three static checks; the judgment is 153 decisions that no test can validate.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

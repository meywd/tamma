# Story 39-24: Acceptance-Step Coverage — Every Producer Routes Through a Designed Approver

Status: drafted

> **Numbering note.** `story-39-21` (RAG in C#), `story-39-22` (prompt quality pass) and
> `story-39-23` (autonomy gate, superseded by Epic 43) are taken. This story takes the
> next free number, **39-24**.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

Specifically required for this story:

- `docs/stories/epic-43/README.md` — the five gate seams and, at line 364, the sentence
  this story exists to close: *"A requires-human returned there reaches a dispatch whose
  calling workflow has no human route in 44 of 45 cases — escalation into a void."*
- `docs/stories/epic-39/story-39-6/…` — the generic lifecycle whose ACCEPT stage is the
  reference implementation of a designed approval step.
- `.dev/decisions/epic-43-action-catalog-design.md` — the action/effect vocabulary the
  smallest fixes below reuse rather than extend.

## User Story

As the **product owner who sets the autonomy dial**,
I want **every workflow that produces a durable artifact or takes an external effect to pass through a designed acceptance step** — one that routes to the orchestrator, a human, or a review panel depending on the dial — rather than only the fifteen workflows that happen to ride the document lifecycle,
So that moving the dial from 70 to 100 changes **who approves**, never **whether anything is approved**, and no producer signs off its own output.

## Priority

**P1.** This is a correctness gap against a stated product rule, not a feature. Of the
forty-eight workflows in `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`, **seven**
(G1–G7) produce something acted on downstream — or take an irreversible external effect —
with **no acceptance step at any dial position**, and **two more** (G8, G9) are approved on
their main path but publish or write on a side path that is not. Nine flagged in total.
At 70 they should be routing to a human and are not; at 100 they should be routing to the
orchestrator and are not. The mechanism to fix every one of them already exists and is
already proven in production paths; nothing here needs inventing.

## The rule this story is audited against

Stated by the product owner, 2026-08-01, correcting an earlier phrasing:

1. Acceptance is **always** a designed workflow step. There is no "self-accept" — a
   producer never signs off its own output.
2. What varies is **who** approves: the orchestrator (automated), a human, or a panel of
   reviewers. Not **whether** there is an approval.
3. The autonomy dial (70–100) selects the **approver**. At 100 the orchestrator approves.
   It does not remove the step.
4. At 100 everything is automated **except two runtime signals**: ambiguity above
   threshold, or no agreement (a review panel's decision rule not met). Those are the only
   things that pull in a person at full autonomy.

Consequence, and the audit predicate used below: a workflow that produces something acted
on downstream and has **no approval step at all** is a gap, regardless of dial position.

## Architectural Context (READ FIRST)

### The mechanism already exists, in three shapes

**Shape 1 — the generic document accept gate (39-6 / 39-8).**
`DocumentLifecycleWorkflow` (`DocumentLifecycleWorkflow.cs:63`) runs
`PRODUCE → VALIDATE → REVIEW → REVISE → ACCEPT` and its ACCEPT stage is exactly the
designed step the rule asks for:

- `BuildAcceptanceRequest` — `DocumentLifecycleWorkflow.cs:584`
- `PublishAcceptanceRequestActivity` — `DocumentLifecycleWorkflow.cs:609`
- `WaitForDocumentDecisionActivity` — `DocumentLifecycleWorkflow.cs:616`
- `ApplyGuardrails` (`AcceptanceGuardrails.Clamp`) — `DocumentLifecycleWorkflow.cs:674`
- `AcceptGate` / `RejectGate` / `ReviseGate` — `DocumentLifecycleWorkflow.cs:713`, `:716`, `:719`

The gate is **decider-agnostic by construction**, which is what makes it satisfy rule 3:
`Tamma.Activities/Documents/WaitForDocumentDecisionActivity.cs:19-26` — *"Suspends any
lifecycle … until the orchestrator (or the human the orchestrator assigned) supplies a
decision … self-decision and assigned-human decision resume it identically — the decider
varies, the gate does not."*

Rule 4's two escape hatches are present and are the only two:

- **Ambiguity above threshold** — `AmbiguityCheck` / `AmbiguityGate`,
  `DocumentLifecycleWorkflow.cs:436` and `:450`, seeding
  `DocumentLifecycleOutcome.AmbiguityAboveThreshold` at `:729`.
- **No agreement** — `PanelReviewWorkflow` undecidable path,
  `PanelReviewWorkflow.cs:301-315` (`DOCUMENT.REVIEW_PANEL_UNDECIDABLE`,
  `success=false`, no fabricated pessimistic aggregate), seeding
  `DocumentLifecycleOutcome.ReviewUndecidable` at `DocumentLifecycleWorkflow.cs:733`.

**Shape 2 — a human bookmark gate.** `WaitForMergeApprovalActivity`
(`MergeApprovalWorkflow.cs:148`) and `WaitForDeploymentApprovalActivity`
(`DeploymentPipelineWorkflow.cs:350`).

**Shape 3 — the orchestrator gate (Epic 43 Seam E).** `CheckActionGateActivity`
(`Tamma.Activities/Policy/CheckActionGateActivity.cs:91`) asks the autonomy gate over HTTP
and returns one of three edges — `automated` / `requires-human` / `denied`.

### Why the gap exists: Seam E has exactly one call site

`CheckActionGateActivity` is adopted in **one** workflow graph in the entire tree:
`DeploymentPipelineWorkflow.cs:300` (wired at `:631`, `:632`, `:633`). Every other
workflow either rides the document lifecycle's gate or has nothing.

Epic 43 says this plainly, and explains why the llm-call seam cannot compensate:
`docs/stories/epic-43/README.md:364` — *"Seam A never blocks, in any version. A
requires-human returned there reaches a dispatch whose calling workflow has no human route
in 44 of 45 cases — escalation into a void."* The action catalog agrees:
`Tamma.Core/Actions/ActionCatalog.Descriptors.cs:313` marks `effect:llm.call` as
*"Seam A observes and never blocks."*

So: **passing through `llm-call` is not an acceptance step**, and no workflow may be
counted as covered on that basis.

### What is not an acceptance step

Three suspension shapes appear in these graphs and are **not** approvals. They are recorded
here so a future reader does not mistake a bookmark for a gate:

| Activity | Where | What it actually waits for |
|---|---|---|
| `WaitForCIResultsActivity` | `TestingWorkflow.cs:153`, `:392` | an external CI result |
| `WaitForResponseActivity` | `AssessmentWorkflow.cs:281` | a junior developer's **answers** |
| `WaitForDocumentInputActivity` | `ClarifyingQuestionsWorkflow.cs:251` | **answers** to clarification questions |
| `WaitForFixesActivity` | `CodeReviewWorkflow.cs:370` | a contributor pushing fixes |
| `WaitForPRMergedActivity` | `SingleIssueCycleWorkflow.cs:701` | a webhook that a merge happened |
| `DetectProgressActivity` | `BlockerDiagnosisWorkflow.cs:586`, `:700`, `:815` | a progress signal on a timer |
| `EscalateToSeniorActivity` | `BlockerDiagnosisWorkflow.cs:916` | escalation ≠ acceptance of the artifact that caused it |

## Verified inventory

48 workflow classes (`grep -c "builder.DefinitionId"` over the directory yields 48; the
other six `.cs` files are `ActivityDisplayTextExtensions.cs`, `WorkflowVersions.cs`,
`HourlyAnalyticsRollupScheduler.cs`, `TenantCleanupRequestedTrigger.cs`,
`TenantDeleteRequestedTrigger.cs`, `TenantScheduledTriggerService.cs` — hosted services and
helpers, not workflows).

### Confirmation of the 15 `document-lifecycle` references

15 files mention `document-lifecycle`. One is `DocumentLifecycleWorkflow` itself
(`:29`, `:63`). Each of the other 14 was checked to **route its output through the gate**,
not merely mention it: every one dispatches `WorkflowDefinitionId = new("document-lifecycle")`
with `WaitForCompletion = new(true)` and branches on the lifecycle's accepted status via
`LifecycleBindingHelper.IsAccepted` (or a type-specific equivalent). All 14 confirmed:

| Workflow | Dispatch | `WaitForCompletion` | Reads acceptance |
|---|---|---|---|
| `AcceptanceCriteriaAuthoringWorkflow` | `:228` | `:262` | `:275`, gate `:301` |
| `AdrAuthoringWorkflow` | `:224` | `:250` | `:263`, gate `:283` |
| `AmbiguityScoringWorkflow` | `:149` | `:166` | `:179` |
| `BacklogPrioritizationWorkflow` | `:353` | `:379` | `:392`, gate `:415` |
| `ClarifyingQuestionsWorkflow` (2 runs) | `:171`, `:278` | `:188`, `:303` | `:200`, `:315` |
| `DebugDiagnosisWorkflow` | `:122` | `:146` | `:159` |
| `DesignProposalWorkflow` | `:140` | `:166` | gate `:198` |
| `IssueDecompositionWorkflow` | `:241` | `:260` | `:274` |
| `PlanGenerationWorkflow` | `:183` | `:213` | `:226` |
| `ResearchWorkflow` | `:197` | `:214` | `:227` |
| `TaskCreationWorkflow` | `:172` | `:197` | `:210` |
| `TestCaseCreationWorkflow` | `:130` | `:154` | `:167` |
| `TriageContextGatheringWorkflow` | `:145` | `:164` | `:177` |
| `TriagePODecisionWorkflow` | `:184` | `:208` | `:221` |

(All line numbers relative to each workflow's own file in
`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`.)

## Full classification table

Columns: **(a)** producer of a durable output / external effect? **(b)** acceptance step,
with the node that provides it. **(c)** gap?

| # | Workflow (`DefinitionId` line) | (a) Producer? | (b) Approver / node | (c) |
|---|---|---|---|---|
| 1 | `AcceptanceCriteriaAuthoringWorkflow.cs:68` | yes — Criteria doc | lifecycle accept gate, `:228`→`DocumentLifecycleWorkflow.cs:616` | no |
| 2 | `AdrAuthoringWorkflow.cs:69` | yes — ADR | lifecycle, `:224` | no |
| 3 | `AmbiguityScoringWorkflow.cs:44` | yes — Assessment | lifecycle, `:149` | no |
| 4 | `BacklogPrioritizationWorkflow.cs:90` | yes — Ordering | lifecycle, `:353` | no |
| 5 | `ClarifyingQuestionsWorkflow.cs:48` | yes — Clarification | lifecycle ×2, `:171`, `:278` | no |
| 6 | `DebugDiagnosisWorkflow.cs:47` | yes — Diagnosis | lifecycle, `:122` | no |
| 7 | `DesignProposalWorkflow.cs:48` | yes — Design | lifecycle, `:140` | no |
| 8 | `IssueDecompositionWorkflow.cs:78` | yes — Decomposition | lifecycle, `:241` | no |
| 9 | `PlanGenerationWorkflow.cs:63` | yes — Plan | lifecycle, `:183` | no |
| 10 | `ResearchWorkflow.cs:43` | yes — Findings | lifecycle, `:197` | no |
| 11 | `TaskCreationWorkflow.cs:56` | yes — Tasks | lifecycle, `:172` | no |
| 12 | `TestCaseCreationWorkflow.cs:45` | yes — TestSpec | lifecycle, `:130` | no |
| 13 | `TriageContextGatheringWorkflow.cs:47` | yes — Findings | lifecycle, `:145` | no |
| 14 | `TriagePODecisionWorkflow.cs:48` | yes — TriageDecision | lifecycle, `:184` | no |
| 15 | `DocumentLifecycleWorkflow.cs:63` | n/a — **is** the gate | `:609`–`:719` | no |
| 16 | `DocumentReviewWorkflow.cs:41` | no — thin router (`:22-33`, zero `llm-call` nodes) | n/a | no |
| 17 | `SingleReviewerWorkflow.cs:49` | no — **is** the review mechanism (`:25-35`) | consumed by lifecycle REVIEW, `DocumentLifecycleWorkflow.cs:462` | no |
| 18 | `PanelReviewWorkflow.cs:54` | no — **is** the panel mechanism; owns the "no agreement" signal `:301` | same | no |
| 19 | `PlanReviewWorkflow.cs:45` | no — deterministic read-through shim, zero LLM, zero dispatch (`:25-28`) | reads the lifecycle's acceptance | no |
| 20 | `DesignDeliveryWorkflow.cs:29` | delivery leaf | dispatched **by** the lifecycle pre-ACCEPT, `DocumentLifecycleWorkflow.cs:654`, gated `:650` | no |
| 21 | `LlmCallWorkflow.cs:55` | no — shared LLM dispatcher | caller owns acceptance; Seam A never blocks (`epic-43/README.md:364`) | no |
| 22 | `DeploymentPipelineWorkflow.cs:111` | yes — deploys | `CheckActionGateActivity` `:300` + `ProdApprovalNeeded` `:320` + `WaitForDeploymentApprovalActivity` `:350` | no |
| 23 | `MergeApprovalWorkflow.cs:61` | n/a — **is** the human gate | `WaitForMergeApprovalActivity` `:148` | no |
| 24 | `MergeWorkflow.cs:59` | yes — irreversible merge `:111` | dispatched only from `MergeApprovalWorkflow.cs:176`, after the human decision at `:148` | no |
| 25 | `TriageItemCycleWorkflow.cs:51` | yes — labels + comment `:317` | applies the **accepted** TriageDecision (`:184` binding); re-entry short-circuit `:116` | no |
| 26 | `TddWorkflow.cs:38` | yes — commits code `:373` | feature-branch containment → `MergeApprovalWorkflow.cs:148` | no |
| 27 | `TddWithDebugRetryWorkflow.cs:60` | no — orchestrator over `tdd-cycle` + `debugging` | n/a | no |
| 28 | `CiWithDebugRetryWorkflow.cs:40` | no — orchestrator over `testing-pipeline` + `debugging` | n/a | no |
| 29 | `TestingWorkflow.cs:73` | yes — auto-fix commit `:291` | branch containment; escalation terminal `:542` | no |
| 30 | `DebuggingWorkflow.cs:60` | yes — applies fixes `:635`, regression tests `:551` | branch containment | no |
| 31 | `BranchCreationWorkflow.cs:44` | effect only — creates a branch `:99` | none, benign (see §Non-gaps) | no |
| 32 | `UpdateIssueStatusWorkflow.cs:42` | effect only — posts a status comment `:89` | none; text is a compile-time constant (`SingleIssueCycleWorkflow.cs:1270`, `:1285`) | no |
| 33 | `SingleIssueCycleWorkflow.cs:42` | no — top-level orchestrator | every produced artifact gated by a child; merge gated `:637` | no |
| 34 | `AdlOrchestratorWorkflow.cs:35` | no — selector + fire-and-forget dispatcher `:127` | n/a | no |
| 35 | `IssueTriageWorkflow.cs:42` | no — fan-out dispatcher `:63` | n/a | no |
| 36 | `HourlyAnalyticsRollupWorkflow.cs:64` | infrastructure — deterministic rollup `:114`, `:122` | n/a | no |
| 37 | `CreateTenantWorkflow.cs:60` | infrastructure — provisioning | human-initiated upstream (verify-email → `TENANT.PROVISIONING_REQUESTED`) | no |
| 38 | `DeleteTenantWorkflow.cs:70` | infrastructure — teardown | admin-initiated + cooling-off + cancellation guard before the drop | no |
| 39 | `CleanUpFailedTenantWorkflow.cs:102` | infrastructure — cleanup | operator-triggered `Event` `:118` | no |
| 40 | `ContextGatheringWorkflow.cs:36` | **yes** — 5 vector-store writes `:97`,`:106`,`:119`,`:133`,`:148` + PO summary `:154` | **none** | **G1** |
| 41 | `AssessmentWorkflow.cs:50` | **yes** — skill-profile write `:478`,`:493`; `skillLevel` output `:577` | **none** (`:281` is a wait for answers) | **G2** |
| 42 | `BlockerDiagnosisWorkflow.cs:78` | **yes** — AI diagnosis `:244` drives a 4-rung delivery ladder; `BlockerResolution` output `:371` | **none** | **G3** |
| 43 | `TaskReviewWorkflow.cs:45` | **yes** — verdict **and a rewritten `tasksJson`** `:241` | **none** | **G4** |
| 44 | `PullRequestWorkflow.cs:41` | **yes** — opens a real PR with LLM-authored body `:149` | **none** | **G5** |
| 45 | `ReviewFixWorkflow.cs:112` | **yes** — applies fixes to the tree `:291`, re-indexes `:343` | **none** | **G6** |
| 46 | `RotateSecretWorkflow.cs:39` | **yes** — irreversible credential rotation `:110` | **none on the scheduled path** | **G7** |
| 47 | `CodeReviewWorkflow.cs:56` | mixed — merge `:401`; LLM guidance delivery `:347` | merge **is** approved (`MonitorReviewActivity:232` → `Approved` edge `:678`); guidance is not | **G8** (narrow) |
| 48 | `MentorshipWorkflow.cs:47` | mixed — orchestrator, but writes the skill profile `:296` and a report `:289` | merge approved via `monitorReview:257`; profile write is not | **G9** (narrow) |

## The gaps, with the smallest change for each

### G1 — `ContextGatheringWorkflow`: findings enter the knowledge base unaccepted

**What it produces.** Five role scans, each written straight to the vector store —
`StoreRoleFindingActivity` at `ContextGatheringWorkflow.cs:97`, `:106`, `:119`, `:133`,
`:148` — plus a PO summary produced by an `llm-call` at `:154` and emitted as `summary` /
`contextIds` at `:219`, `:221` (outputs sequence `:213`). Those `contextIds` feed plan generation in five callers
(`SingleIssueCycleWorkflow.cs:164`, `IssueDecompositionWorkflow.cs:194`,
`ResearchWorkflow.cs:153`, `AssessmentWorkflow.cs:140`, `MentorshipWorkflow.cs:369`), and
the stored findings are RAG-retrievable by **every later run**, so the blast radius
outlives the cycle that produced them.

**Who should approve.** The orchestrator. Context scanning is read-only analysis —
`AgentAction.ContextScan` is catalogued `ActionRisk.ReadOnly`
(`Tamma.Core/Actions/ActionCatalog.Descriptors.cs:84`) — so at 100 the orchestrator
accepts and at 70 it routes to a human, but the step must exist either way.

**Smallest change.** This workflow is the un-migrated third sibling of a pattern that
already shipped twice: `ResearchWorkflow.cs:197` and `TriageContextGatheringWorkflow.cs:145`
both wrap a context scan in `document-lifecycle` producing a `findings` document. Make
`ContextGatheringWorkflow` a lifecycle binding the same way — the document type, the
producer-dispatch spec shape, and the `IsAccepted` read are all copy-shaped from
`TriageContextGatheringWorkflow.cs:145-186`. The vector-store writes move **after** the
accept edge so unaccepted findings never enter the KB.

### G2 — `AssessmentWorkflow`: a person's skill level is written with no acceptance

**What it produces.** LLM-generated questions delivered to a human
(`DeliverQuestionsActivity`, `AssessmentWorkflow.cs:268`), an LLM classification of their
answers (`ClassifyResultActivity` `:450`), and then a **durable write to the skill profile**
(`UpdateSkillProfileActivity` `:478` and `:493` — the timeout branch writes too). The
resulting `skillLevel` output (`:577`, `:591`) is acted on downstream: `TestingWorkflow`
takes skill-level-aware thresholds, and `BlockerDiagnosisWorkflow`'s ladder wait times vary
by skill (`BlockerDiagnosisWorkflow.cs:8-11`).

`WaitForResponseActivity` (`:281`) is **not** an approval — it waits for the junior's
answers, which are the input to the judgement, not a sign-off on it.

**Who should approve.** Orchestrator at 100, human at 70. It records a judgement **about a
person**, so this is a strong candidate for an admin-pinned `AlwaysHuman` row rather than a
threshold — the mechanism for that already exists (`AcceptanceRules.AlwaysEscalate`,
`EscalationClass`).

**Smallest change.** Insert the decider-agnostic pair between `ClassifyResult` and
`UpdateSkillProfile`: `PublishAcceptanceRequestActivity` + `WaitForDocumentDecisionActivity`
(the exact pairing at `DocumentLifecycleWorkflow.cs:609`/`:616`), with the `Accept` edge
going to `UpdateSkillProfile` and `Reject` going to the existing `LlmCallError` terminal.
Full migration to the lifecycle with an `assessment` document type is the larger,
better-shaped option; the gate pair is the minimum that satisfies the rule.

### G3 — `BlockerDiagnosisWorkflow`: an AI diagnosis drives a delivery ladder unaccepted

**What it produces.** `ClassifyBlockerActivity` (`BlockerDiagnosisWorkflow.cs:244`) turns
collected signals into a blocker type + severity via LLM, and that classification then
selects and executes a four-rung ladder that delivers LLM-authored hints, guidance and
assistance to a human, ending in `EscalateToSeniorActivity` (`:916`). The
`BlockerResolution` output (`:371`) is consumed by `MentorshipWorkflow.cs:186`.

Nothing on that path is an acceptance. `DetectProgressActivity` (`:586`, `:700`, `:815`)
waits for a progress signal on a durable timer; the escalation at `:916` is the *outcome*
of an unaccepted diagnosis, not acceptance of it.

**Who should approve.** The orchestrator (`AgentAction.ResolveBlocker` is
`ActionRisk.ReadOnly`, `ActionCatalog.Descriptors.cs:122`).

**Smallest change.** Its sibling is already migrated: `DebugDiagnosisWorkflow.cs:122`
dispatches `document-lifecycle` with a `diagnosis` document and reads
`DiagnosisBindingHelper.HasUsableHypotheses` at `:162`. Do the same here — dispatch the
lifecycle around `ClassifyBlocker`, and hang the ladder off the accepted edge. Only the
producer-variables payload differs.

### G4 — `TaskReviewWorkflow`: an ungated review that rewrites already-accepted tasks

**What it produces.** Four sequential `llm-call` role reviews
(`TaskReviewWorkflow.cs:109`, `:115`, `:121`, `:127`; dispatch helper `:310`), aggregated by `AllApprovedCheck`
(`:200`) into a `decision` (`:239`) — **and a `tasksJson` output** (`:241`).

Two things make this a gap rather than a review mechanism:

1. It is **not** the unified review surface. `SingleReviewerWorkflow` and
   `PanelReviewWorkflow` emit typed `Review` documents that the lifecycle's ACCEPT gate
   then decides on. `TaskReviewWorkflow` emits a bare verdict with no gate behind it, and
   `SingleIssueCycleWorkflow.cs:376`/`:382` switches straight from that verdict to branch creation.
2. It can **overwrite an artifact that was already accepted**. Tasks come from
   `TaskCreationWorkflow.cs:172`, which is a lifecycle binding — they are accepted. Then
   `SingleIssueCycleWorkflow.cs:370` writes `TaskReviewWorkflow`'s `tasksJson` back over
   them, unaccepted. That is the producer-signs-off-its-own-output shape the rule forbids,
   one hop displaced.

**Who should approve.** Nobody new — the Plan/Tasks lifecycle already approved these tasks.

**Smallest change.** Reduce it to a read-through shim exactly as Story 39-14 did to
`PlanReviewWorkflow` (see `PlanReviewWorkflow.cs:16-35`: *"Zero LLM, zero dispatch … the
review already happened inside the lifecycle"*). If the full reduction is out of budget for
one story, the **minimum** is to stop `SingleIssueCycleWorkflow.cs:370` writing the reviewed
`tasksJson` back, which removes the unaccepted-rewrite path without touching the graph.

### G5 — `PullRequestWorkflow`: LLM-authored content published externally, ungated

**What it takes.** `CreatePullRequestActivity` (`PullRequestWorkflow.cs:149`) opens a real
PR on the git platform with a body generated by an `llm-call`. The effect is catalogued —
`ExternalEffect.GitPullRequestCreate`, `ActionCatalog.Descriptors.cs:319` — but the graph
never asks the gate, because `CheckActionGateActivity` has exactly one call site
(`DeploymentPipelineWorkflow.cs:300`).

**Who should approve.** The orchestrator at 100. A PR is a proposal and its merge is
separately human-gated (`MergeApprovalWorkflow.cs:148`), so this does not need a human at
full autonomy — but at 70 it should route to one and today it cannot.

**Smallest change.** One `CheckActionGateActivity` before `CreatePR`, keyed
`effect:git.pull_request.create`, wired exactly as the proven Seam E adoption:
`automated` → `CreatePR`; `requires-human` → a bookmark wait; `denied` → the workflow's
**existing** failure terminal (`:226` `EmitPrEventActivity` / failure outputs), so the
denial path needs no new nodes. Read `DeploymentPipelineWorkflow.cs:246-298` first — it
documents the two traps (gate on the *effect*, not the shared dispatch; and a denial is a
hard stop, never a wait a human can approve past).

### G6 — `ReviewFixWorkflow`: applies code changes with no gate, and is currently unreachable

**What it takes.** `ApplyReviewFixesActivity` (`ReviewFixWorkflow.cs:291`) applies
LLM-generated fixes and `UpdateCodeIndexActivity` (`:343`) re-indexes them. No acceptance
step exists on any edge.

**Also true, and it changes the priority.** No workflow in the tree dispatches
`"review-fix"` — the string appears only at its own `builder.DefinitionId`
(`ReviewFixWorkflow.cs:112`). Its header says wiring into `SingleIssueCycleWorkflow` is
gated on Epic 38. So this is a **latent** gap: real in the graph, not reachable today.

**Who should approve.** The orchestrator (fixes land on the feature branch; the merge is
human-gated).

**Smallest change.** A `CheckActionGateActivity` on
`agent-action:address-review-comments` (`ActionCatalog.Descriptors.cs:138`) before
`ApplyFixes`, with `denied` routed to the existing `OutputFailure` terminal. Do it as part
of the Epic 38 wiring rather than as a standalone change — but do not wire it in without it.

### G7 — `RotateSecretWorkflow`: irreversible rotation on an unattended schedule

**What it takes.** `RotateSecretSagaActivity` (`RotateSecretWorkflow.cs:110`) runs
`mint → push → probe → activate → retire` against live external systems.

**Where the gap actually is.** The graph itself is a saga executor and is fine. The gap is
at the *trigger*: `SecretAutoRotationScheduler`
(`Tamma.Api/Services/Secrets/Rotation/SecretAutoRotationScheduler.cs:68`, dispatching at
`:175`) fires the workflow for due secrets with no human and no gate check, alongside the
operator-triggered endpoint path which does have one.

**Who should approve.** The orchestrator, via **Seam D**, not Seam E — Epic 43 is explicit
that a background actor cannot suspend for a person (`epic-43/README.md:389`: *"Seam D can
only deny"*). So the correct shape is a deny-only `automation:*` gate call at the
scheduler tick.

**Smallest change.** One gate call per tick inside
`SecretAutoRotationScheduler.ExecuteAsync`, before `TriggerRotationAsync` at `:175`; a
denial skips the secret and emits the rotation-audit event that already exists. **No change
to the workflow graph.** Flagged here because the audit is per-workflow and this workflow
is the thing that acts — but the fix does not live in `Workflows/`.

### G8 — `CodeReviewWorkflow` (narrow): guidance is delivered unaccepted

The merge is **not** a gap: `MergeAndCompleteReviewActivity` (`CodeReviewWorkflow.cs:401`)
is reachable only from the `Approved` edge of `MonitorReviewActivity`
(`:232`, wired `:678`) — a human's approval on the git platform, observed. That is a real
approver, just an external one.

The gap is the `ChangesRequested` branch: `StoreGuidance` captures raw LLM output
(`:337`) and `DeliverGuidanceActivity` (`:347`) publishes it to a human contributor with
no acceptance in between.

**Who should approve.** The orchestrator (`AgentAction.MentorFeedback`,
`ActionCatalog.Descriptors.cs:123`).

**Smallest change.** A `CheckActionGateActivity` on `agent-action:mentor-feedback` between
`StoreGuidance` and `DeliverGuidance`, with `denied` routed to the existing
`escalateGuidance` terminal (`:479`) — which already exists precisely for "guidance
failure", so the denial path needs no new terminal.

### G9 — `MentorshipWorkflow` (narrow): the same skill-profile write, second site

`MentorshipWorkflow` is a top-level orchestrator and its merge is approved
(`monitorReview` `:257` → `mergeAndComplete` `:282`). But it carries its **own**
`UpdateSkillProfileFlowActivity` (`:296`) and `GenerateReportFlowActivity` (`:289`) — the
same unaccepted durable write as G2, at a second call site.

**Smallest change.** Whatever gate G2 lands, apply it here too. This is a one-node
insertion, not independent design work — it is called out separately only so the G2 fix is
not declared done while this site remains open.

## Explicitly NOT gaps — do not re-audit these

Recorded with reasons so this analysis does not have to be repeated:

- **`LlmCallWorkflow` (`:55`)** — the shared LLM dispatcher. Every caller owns the
  acceptance of what the call produced. Confirmed the PO's claim: it has no `Wait*`
  activity and no accept edge, and Epic 43 pins Seam A as observe-only forever
  (`epic-43/README.md:364`; `ActionCatalog.Descriptors.cs:313`). Adding a gate here would
  escalate into a void.
- **`SingleReviewerWorkflow` (`:49`) and `PanelReviewWorkflow` (`:54`)** — these **are** the
  review mechanism. Their output is a `Review` envelope consumed by the lifecycle's REVIEW
  stage (`DocumentLifecycleWorkflow.cs:462`), and the ACCEPT gate decides on it downstream.
  `PanelReviewWorkflow` additionally owns rule 4's second escape hatch (`:301`).
- **`DocumentReviewWorkflow` (`:41`)** — a thin router between those two producers, zero
  `llm-call` nodes (`:22-33`).
- **`DeploymentPipelineWorkflow` (`:111`)** — has its own prod-approval gate. Confirmed the
  PO's claim and then some: `CheckActionGateActivity` `:300` → `ProdApprovalNeeded` `:320`
  → `WaitForDeploymentApprovalActivity` `:350`, with a separate hard-stop refusal terminal
  for `denied` (`:337`, wired `:633`). This is the one place all three approver kinds are
  already expressed.
- **`PlanReviewWorkflow` (`:45`)** — a deterministic read-through shim over the document
  store; zero LLM, zero dispatch (`:25-28`). It *reports* the lifecycle's acceptance rather
  than producing anything.
- **`DesignDeliveryWorkflow` (`:29`)** — a delivery leaf dispatched **by** the lifecycle
  before its own accept gate (`DocumentLifecycleWorkflow.cs:654`, once-only gated at
  `:650`). Delivering the proposal before the human decides is the deliberate 39-13 D5
  design, not a missing gate.
- **`MergeWorkflow` (`:59`)** — performs an irreversible merge (`:111`) but is dispatched
  only from `MergeApprovalWorkflow.cs:176`, downstream of the human decision at `:148`.
- **`TriageItemCycleWorkflow` (`:51`)** — applies labels and a comment (`:317`), but the
  decision it applies is the **accepted** `TriageDecision` from
  `TriagePODecisionWorkflow.cs:184`. Mechanical application of an approved artifact.
- **`TddWorkflow` (`:38`), `TestingWorkflow` (`:73`), `DebuggingWorkflow` (`:60`)** — all
  three write and commit code (`TddWorkflow.cs:373`, `TestingWorkflow.cs:291`,
  `DebuggingWorkflow.cs:635`), but every write lands on the **feature branch**, and the
  branch reaches the default branch only through `MergeApprovalWorkflow.cs:148` (human) or
  `CodeReviewWorkflow.cs:678` (human PR approval). The approval exists one hop out, on the
  aggregate. Their bookmark waits are CI results, not approvals.
- **`TddWithDebugRetryWorkflow` (`:60`) and `CiWithDebugRetryWorkflow` (`:40`)** — pure
  bounded-retry orchestrators over other workflows; they produce no artifact of their own.
- **`AdlOrchestratorWorkflow` (`:35`) and `IssueTriageWorkflow` (`:42`)** — dispatchers.
  `AdlOrchestrator` selects and fires cycles (`:127`); `IssueTriage` fans out per item
  (`:63`). Neither produces an artifact.
- **`SingleIssueCycleWorkflow` (`:42`)** — top-level orchestrator. Every artifact-producing
  step it dispatches is gated in its child, and its one irreversible effect (merge) is gated
  at `:637`.
- **`BranchCreationWorkflow` (`:44`)** — creates a branch (`:99`). Reversible, no LLM
  content, and a precondition rather than a judgement anything acts on. Catalogued
  (`ExternalEffect.GitBranchCreate`, `ActionCatalog.Descriptors.cs:315`) and therefore
  gateable by admin policy if a tenant wants it, but not a gap under the rule.
- **`UpdateIssueStatusWorkflow` (`:42`)** — posts a status comment (`:89`), but the message
  is a **compile-time constant** supplied by the caller
  (`SingleIssueCycleWorkflow.cs:1270` parameter → `:1285` input); no LLM text passes through
  it. A deterministic echo of events that already happened.
- **`HourlyAnalyticsRollupWorkflow` (`:64`)** — deterministic analytics aggregation
  (`:114`, `:122`); no LLM, no external effect.
- **`CreateTenantWorkflow` (`:60`), `DeleteTenantWorkflow` (`:70`),
  `CleanUpFailedTenantWorkflow` (`:102`)** — infrastructure lifecycle. Each is initiated by
  an explicit human act upstream (email verification; the admin delete endpoint, plus a
  cooling-off window and a cancellation guard re-read immediately before the irreversible
  drop; and an operator `Event` trigger at `CleanUpFailedTenantWorkflow.cs:118`).

## Could not decide — stated plainly rather than guessed

1. **`RotateSecretWorkflow` — is unattended auto-rotation already "approved" by the admin
   who enabled it?** A standing configuration choice is arguably a standing approval, in
   which case G7 is not a gap at all. This is a product call, not something the code
   answers. G7 is written on the assumption that it *is* a gap; if the product owner
   decides otherwise, delete G7 and record the reasoning here.
2. **`BranchCreationWorkflow` — is creating a branch an "effect" under the rule?** It is
   catalogued as one and it is a real external write, but nothing downstream treats the
   branch as a judgement. Classified as not-a-gap; flagging the call.
3. **`ContextGatheringWorkflow`'s blast radius.** The knowledge-base retrieval path from
   `StoreRoleFindingActivity` back into a later run's prompt was **not** traced end to end
   in this audit (it crosses into the `intelligence-server` sidecar). G1's severity rests on
   that path being live. Verify before sizing G1.

## Separate defect found during the audit (not an acceptance gap)

`SingleIssueCycleWorkflow.cs:283` and `:300` dispatch
`WorkflowDefinitionId = new("create-issues")`. **No workflow in the tree declares that
definition id** — a full-tree grep for `"create-issues"` returns only those two call sites.
The `defer` and `split` branches of the plan-review outcome therefore dispatch a
non-existent workflow. Out of scope here; file it separately.

## Acceptance Criteria

1. **Every producer has a designed acceptance step.** For each of G1–G6, G8 and G9, the
   workflow graph contains a node whose outcome determines whether the durable output is
   written or the effect is taken. A test enumerates the eight workflows by `DefinitionId`
   and asserts, per workflow, that the write/effect node named in this story is **not
   reachable** from the graph's entry without traversing the new gate node. A graph that
   still reaches the write directly **fails**.

2. **Coverage is enforced going forward, not just fixed once.** A structural test
   (sibling of `ResumableStandardStructuralTests`) enumerates all `WorkflowBase` types in
   `Tamma.ElsaServer/Workflows/` and asserts each is in exactly one of three buckets:
   *(i)* dispatches `document-lifecycle` with `WaitForCompletion=true`; *(ii)* contains a
   `WaitFor*Approval` / `WaitForDocumentDecisionActivity` / `CheckActionGateActivity` node;
   *(iii)* is on an explicit allow-list whose entries each carry a one-line reason string.
   The allow-list ships pre-populated with exactly the "NOT gaps" section above and **no
   other entries**. Adding a new workflow without one of the three fails the build. This AC
   fails today: 8 workflows are in none of the three buckets.

3. **The dial selects the approver; it never removes the step.** For at least one fixed
   workflow per gate shape (one lifecycle migration from G1/G3, one Seam E adoption from
   G5), a test runs the same graph at `AutonomyLevel = 70` and `AutonomyLevel = 100` and
   asserts the gate node is **executed in both**, differing only in the resolved decider
   (human bookmark vs orchestrator decision). A test that passes because the gate was
   skipped at 100 **fails** this AC.

4. **The two runtime escape hatches remain the only ones.** A test asserts that at
   `AutonomyLevel = 100`, a human is pulled in on exactly two conditions —
   `DocumentLifecycleOutcome.AmbiguityAboveThreshold`
   (`DocumentLifecycleWorkflow.cs:729`) and `DocumentLifecycleOutcome.ReviewUndecidable`
   (`:733`) — and on no other outcome across the fixed workflows. Any third human-routing
   condition introduced by this story's changes fails the test.

5. **No new gate vocabulary.** Every gate added by this story keys off an action or effect
   that already exists in `Tamma.Core/Actions/ActionCatalog.Descriptors.cs`. A test asserts
   the catalog member count is unchanged by this story. If a gap genuinely needs a new
   member, that is a separate story against Epic 43, not a quiet addition here.

6. **Denial is a hard stop, never a wait.** For each `CheckActionGateActivity` added, the
   `denied` edge routes to a terminal that does **not** perform the effect, and never to an
   approval bookmark. Pinned per call site, mirroring
   `DeploymentPipelineWorkflow.cs:633` and the reasoning recorded at `:275-298`. A `denied`
   edge wired to a wait fails the test.

7. **G4's unaccepted rewrite is closed.** A test asserts that the `tasksJson` reaching
   `CreateBranch` in `SingleIssueCycleWorkflow` is byte-identical to the `tasksJson` the
   `task-creation` lifecycle accepted — i.e. `TaskReviewWorkflow` can no longer overwrite an
   accepted artifact (`SingleIssueCycleWorkflow.cs:370`). Fails today.

8. **Both scoping models.** Every gate added resolves its policy through the existing
   principal split — `user_id` in single-user mode, `tenant_id` in SaaS — with no new
   resolution path. A test covers one gate in each mode. (This is inherited from
   `AcceptanceRulesService` / `CheckActionGateActivity`, so the AC is a regression pin, not
   new work.)

9. **Audit trail on every new gate.** Each added gate emits a DCB event on both the
   approve and the refuse edge, tenant-tagged, through the durable drain — matching the
   `APPROVAL.REQUESTED` / `APPROVAL.PROVIDED` pair the document gate already emits
   (`WaitForDocumentDecisionActivity.cs:42-48`). A gate that blocks with no audit row fails
   the test.

10. **The classification table stays true.** The table in this story is reproduced as a
    fixture the AC2 structural test reads, so a workflow that changes bucket without the
    table being updated fails the build. This is what keeps the audit from going stale the
    week after it lands.

## Dependencies

- **Epic 43 (action catalog + five seams)** — ✅ the vocabulary and
  `CheckActionGateActivity` have landed; `DeploymentPipelineWorkflow.cs:300` is the working
  reference adoption. G5, G6, G8 are Seam E adoptions and cannot start before it. G7 is a
  Seam D adoption and depends on the background-actor gate helper.
- **39-6 (document lifecycle)** — ✅ landed. G1, G2, G3 are lifecycle migrations against it.
- **39-8 (escalation/approval surface)** — ✅ landed. Supplies
  `PublishAcceptanceRequestActivity` / `WaitForDocumentDecisionActivity`, the pair G2's
  minimum fix reuses directly.
- **39-13 / 39-15 (producer migrations)** — ✅ landed. G1 and G3 are the two producers those
  stories did not reach; their bindings are the templates.
- **39-14 (PlanReview reduced to a shim)** — ✅ landed. G4's fix is the same reduction
  applied to `TaskReviewWorkflow`.
- **Epic 38 (non-LLM / git mediation)** — G6 should land **with** the Epic 38 wiring of
  `review-fix`, not before it and not after.
- **Not blocked by 39-17/39-19 (orchestrator agent, Task View).** The gate is
  decider-agnostic today (`WaitForDocumentDecisionActivity.cs:19-26`), so the steps can be
  added before the orchestrator's decision-making is richer.

## Out of Scope

- **Changing any default autonomy threshold.** This story adds *steps*; what each step
  decides at a given dial position is Epic 43 policy configuration.
- **New catalog members** (AC5). If a gap needs vocabulary that does not exist, that is an
  Epic 43 story.
- **The `"create-issues"` dangling dispatch** (`SingleIssueCycleWorkflow.cs:283`, `:300`) —
  a real defect found here, but not an acceptance gap. File separately.
- **`RotateSecretWorkflow`'s graph.** G7's fix is at the scheduler
  (`SecretAutoRotationScheduler.cs:175`), not in `Workflows/`.
- **Retro-gating the four `Wait*` activities listed as "not an acceptance step".** They are
  correct as external-signal waits and must stay that way; conflating them with approvals is
  the mistake this story is guarding against.

## Est. Effort

**8–11 days.**

- G1, G3 (lifecycle migrations, template exists): 3–4 days
- G2, G9 (accept-gate pair + the second profile-write site): 1.5–2 days
- G4 (shim reduction, or the 2-line minimum): 0.5–2 days depending on which is chosen
- G5, G8 (Seam E adoptions against an existing pattern): 1 day
- G6 (deferred into Epic 38 wiring): 0.5 day of design, execution not counted here
- G7 (scheduler-side Seam D call): 0.5 day
- AC2 + AC10 structural test and the fixture table: 1.5 days — this is the piece that keeps
  the audit from needing to be redone, and should not be cut.

## Change Log

| Date       | Version | Changes                                                                 | Author |
| ---------- | ------- | ----------------------------------------------------------------------- | ------ |
| 2026-08-01 | 1.0.0   | Initial story creation — full 48-workflow acceptance-step audit, 9 gaps | Claude |

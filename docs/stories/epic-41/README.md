# Epic 41: Full-Team Workflow Coverage — the remaining recurring SDLC activities as lifecycle workflows

## Overview

Epic 39 gave Tamma a **spine**: typed work documents, one produce→validate→review→revise→accept
lifecycle, resumable-by-design workflows, a document store + lineage API, and the real-time channels the
accept gate publishes on. Epic 40 made the coding step durable on that spine.

> **Corrected — the spine is not complete.** Earlier drafts also credited Epic 39 with "an orchestrator
> that routes acceptance by the 70–100 autonomy dial" and "a Task View where a suspended decision lands
> in a tenant role's inbox". **Neither has landed** — 39-17 (orchestrator agent), 39-19 (chat + Task
> View) and 39-20 (teams/roles/repo access & task routing) are all still stubbed fail-closed in the tree.
> Rules 3 and 4 below describe the intended contract, not today's behaviour. See **Dependencies** for the
> per-story impact.

But the platform only *runs* that spine for a narrow slice of what a software team does day to day: the
**issue → decompose → plan → tasks → TDD → PR → review → merge → deploy** happy path, plus intake
(triage/clarify/research/ambiguity) and mentorship. Tamma's stated intent is broader — *automate the
entire software-development process*: every recurring activity an **engineer, UX, designer, product
owner, project manager, scrum master, architect, or tester** performs should eventually be a Tamma
workflow.

This epic closes that gap. It takes the activities that today exist only as a **prompt cell** (an
`(role, action)` template dispatchable through `llm-call`, but with no owning workflow that saves
documents, rides the lifecycle, or routes acceptance) — or don't exist at all (the UX/designer/PM/scrum
role families) — and turns each into a **first-class lifecycle workflow** on the Epic 39 substrate.

**Every workflow in this epic follows the same five rules (no new architecture):**

1. **Thin binding over `document-lifecycle`.** Each producing workflow declares `consumes: [...]` /
   `produces: <DocumentType>`, binds one `(role, action)` produce cell, and contributes no bespoke
   parse/branch/terminal logic — exactly the 39-12/39-13/39-14 migration pattern. **"Thin" is
   checkable, not a slogan** — a binding is thin iff it passes the structure-test set the migrated
   producers already ship (`tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`
   is the reference shape): (a) exactly one `DispatchWorkflow`, whose literal definition id is
   `document-lifecycle`; (b) zero `DispatchWorkflow` targeting `llm-call`; (c) zero `Finish` activities
   (no bespoke terminal — every non-accept exit is a typed lifecycle outcome); (d) no validate/retry
   plumbing variables (`ValidationErrors` / `RetryCount` / `MaxRetries` / `*Valid`); (e) the dispatch
   materialises the canonical `(role, action)` pair + `documentType` + a **declared**
   `feedbackVariableName` carrier, asserted via `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches`.
   Any story that cannot meet (a)–(e) must name the deviation and justify it in its own ACs — the rule
   is enforced per story or it is not claimed.

   > **Corrected — prose has no mechanism, and no story owns building one.** Earlier drafts said prose
   > output (ADR, postmortem, release notes, changelog, runbook, docs, stakeholder update) "rides the
   > lifecycle as a **prose document with an audience tag** (Epic 39: *prose stays prose*)". Epic 39
   > states that only as a *principle*, and 39-1 records prose/tech-writer output as explicitly **out of
   > scope** of the 10-type table. In code there is no prose type and no audience tag:
   > `Tamma.Core/Documents/DocumentTypeKey.cs:24-33` has exactly ten members (findings,
   > ambiguity-assessment, clarification, decomposition, plan, design, review, triage-decision,
   > diagnosis, test-spec) and neither `Tamma.Data/Entities/DocumentInstance.cs` nor
   > `Tamma.Core/Documents/DocumentEnvelope.cs` carries an `Audience` member. **41-4, 41-5, 41-9, 41-22,
   > 41-24, 41-25 and 41-26 are therefore blocked on an enabler that does not yet exist** (a `prose`
   > `DocumentTypeKey` member or a documented decision to model prose as an untyped body, an `Audience`
   > column + envelope field, and an audience vocabulary). See **Sequencing → Wave 0**.

2. **DCB events.** Every transition emits the generic `DOCUMENT.*` family plus a domain-specific family
   (`ADR.*`, `SPRINT.*`, `THREATMODEL.*`, …) so existing dashboards and new ones both see a loud,
   tagged, `issueId`/`repository`-lineaged trail.
3. **Orchestrator-routed acceptance, autonomy-gated.** The accept gate **always** publishes an
   `AcceptanceRequest` on the workflow↔orchestrator channel and suspends (39-8). The orchestrator reads
   the acceptance rules + the autonomy level (70–100, per-document-type overridable) and decides WHO
   decides — itself (more, the higher the dial) or a **tenant role** whose holders see the decision in
   their Task View. Never an if-else that skips the decision; never an embedded accept `llm-call`.
4. **Human-or-agent execution.** The produce/review step is written so **either** fulfils it: at lower
   autonomy it is assigned to a human holder of the appropriate tenant role (lands in their Task View,
   scoped by 39-20 access); at higher autonomy the appropriate `AgentRole` performs the `llm-call`. The
   workflow binds a `(role, action)` cell either way — the assignment target (agent process vs. human
   role) is the orchestrator's routing decision, not the workflow's shape.
5. **Resumable by design.** Interactive workflows declare `[ResumeBehavior(Both)]` (accept gate suspends
   on a canonical tenant-folded bookmark via `LifecycleBookmarks`; Init reconstructs from the document
   store + DCB events); run-to-completion producers declare `[ResumeBehavior(LatestStateReEntry)]`. All
   pass the 39-10 structural test without an allowlist entry. **Scheduled workflows have no reusable
   pattern yet** — see the scheduler note under **Dependencies**.

**Vocabulary is reused, not reinvented.** Where an activity's output fits an existing Epic 39 type
(`Findings`, `Review`, `Design`, `Plan`, `Diagnosis`, `TriageDecision`, `TestSpec`) this epic uses it.
Only a handful of genuinely new types are proposed (Story 41-1) and each is justified against an
existing type it could NOT reuse — plus prose, which is *not* an existing type and needs the Wave-0
enabler described in the Corrected note above.

## New roles & the two role families that don't exist yet

The taxonomy (`Tamma.Core/Agents`) models **8 roles**: developer, senior_developer, tester, security,
devops, architect, product_owner, tech_writer. The user's target set names **four the platform has no
role for** — they currently fall back to `product_owner` via `LegacyRoleAliases` (`scrum_master`,
`analyst`) or aren't modelled at all (UX, designer, project_manager). Story **41-1** adds
`scrum_master`, `project_manager`, and `ux_designer` (covering both UX and visual-design work) as first
-class `AgentRole`s with their action cells, plus the new document types the epic needs.

> **Corrected — 41-1 is a hard blocker, on BOTH paths, for eleven stories.** Earlier drafts claimed
> "every other story in the epic can still ship and run human-assigned before 41-1 lands … 41-1 gates
> only the *agent* path". That is false and it contradicted the stories' own Dependencies. A document
> type that is not in the vocabulary cannot be validated or persisted **no matter who executes the
> step**: `DocumentTypeKeyExtensions.Parse` throws `DOCUMENT.TYPE.UNKNOWN` for any non-vocabulary wire
> string (`DocumentTypeKey.cs:54`), `DocumentTypeRegistry.Resolve` throws
> `DOCUMENT.TYPE.NOT_REGISTERED` (`DocumentTypeRegistry.cs:86`), and `DocumentInstance.DocumentType`
> is a `DocumentTypeKey` wire string. The same holds for a missing `(role, action)` cell — a human
> assignee still needs a cell to bind. **Eleven stories name 41-1 in their own `Blocking:` line:**
>
> | Blocked on 41-1 for | Stories |
> |---|---|
> | a new **document type** (unpersistable until registered) | 41-2 `AcceptanceCriteria` · 41-3 `BacklogOrdering` · 41-6 `SprintPlan` · 41-13 `TestPlan` · 41-19 `ThreatModel` · 41-27 `UxSpec` |
> | a new **role** | 41-6, 41-7, 41-8 (`scrum_master`) · 41-27, 41-28 (`ux_designer`) |
> | a new **action cell** | 41-11 `triage-tech-debt` · 41-16 `manage-regression` |
>
> **41-17 belongs on this list too** — its PR-triage half produces on `(senior_developer, triage-pr)`,
> a cell 41-1 owns and that does not exist today (absent from `AgentAction.cs`; not in
> `SeniorDeveloper`'s eligible set at `RolePhaseMap.cs:80-92`) — but 41-17's `Blocking:` line omits it.
> Its stand-alone code-review half needs no new cell and is genuinely 41-1-independent.
>
> What *is* true: for a story whose type/role/cell already exist, rule 4 lets the produce step run
> human-assigned at low autonomy without an agent. That is a narrower claim than the one deleted.

## Coverage matrix

Legend — **✅ covered** (a named workflow owns it) · **◑ partial** (touched inside a larger workflow /
only as a panel lens / prose-in-PR, no first-class workflow) · **✗ missing** (prompt cell only, or no
cell at all). "New story" names the Epic 41 story that closes a ◑/✗.

> **Corrected — there is no `triage-panel-review` workflow.** Three rows below used to cite one; Story
> 39-15 **deleted** it (the dispatch + `extractPanelResult` + `panelUsable` nodes are gone —
> `TriageItemCycleWorkflow.cs:28`, and the deletion is ratcheted by negative-assertion tests at
> `TriageItemCycleRoutingTests.cs:47` and `TriagePODecisionWorkflowTests.cs:50`). The triage panel
> is now **the document-lifecycle REVIEW stage over a `triage-decision` draft**, not a standalone
> workflow: `triage-po-decision` → `document-lifecycle` → `document-review`
> (`DocumentLifecycleWorkflow.cs:58`) → `review-panel` (`DocumentReviewWorkflow.cs:117`,
> `PanelReviewWorkflow.ReviewPanelDefinitionId`). Each panellist's lens is selected per role by
> `RolePhaseMap.GetPanelActionForRole(role, "triage-decision")` → `GetTriageActionForRole`
> (`RolePhaseMap.cs:404-436`): security → `assess-vulnerability`, developer/tester → `triage-defect`,
> devops → `diagnose-incident`. The rows below cite `review-panel` accordingly.

### Engineer (developer + senior_developer)

| Activity | Status | Workflow / story |
|---|---|---|
| Implement feature/fix (TDD) | ✅ | `tdd-cycle`, `tdd-with-debug-retry`, `single-issue-cycle` |
| Write tests | ✅ | `test-case-creation`, `testing-pipeline` |
| Debug | ✅ | `debugging` |
| Address review comments | ✅ | `review-fix` |
| Issue decomposition / task creation | ✅ | `issue-decomposition`, `task-creation` |
| Implementation planning | ✅ | `plan-generation` |
| Blocker resolution / mentorship | ✅ | `blocker-diagnosis`, `mentorship` |
| **Standalone code review (not mentorship-bound)** | ◑ | **41-17** (only inside `code-review`/panel today) |
| **PR triage / review-queue** | ✗ | **41-17** |
| **Refactor planning** | ✗ | **41-18** (cell `plan-refactor`, no workflow) |

### Architect

| Activity | Status | Workflow / story |
|---|---|---|
| Design proposal | ✅ | `design-proposal` |
| Plan review (architecture lens) | ✅ | `plan-review`, `review-panel` |
| **ADR authoring** | ✗ | **41-9** (cell `write-adr`) |
| **System design doc (API contract / data model / integration)** | ✗ | **41-10** (cells exist, no workflow) |
| **Tech-debt & technical-risk triage** | ✗ | **41-11** (cell `assess-technical-risk`; no tech-debt cell) |
| **Dependency & upgrade planning** | ✗ | **41-12** (cell `plan-migration-strategy` + security `audit-dependencies`) |

### Product Owner

| Activity | Status | Workflow / story |
|---|---|---|
| Intake / triage | ✅ | `issue-triage`, `triage-*` |
| Clarify requirements | ✅ | `clarifying-questions` |
| Research | ✅ | `research` |
| Ambiguity scoring | ✅ | `ambiguity-scoring` |
| Skill assessment | ✅ | `assessment` |
| **Acceptance-criteria authoring** | ✗ | **41-2** (cell `define-acceptance-criteria`) |
| **Backlog prioritization / grooming** | ✗ | **41-3** (cell `prioritize-backlog`) |
| **Roadmap shaping** | ✗ | **41-4** (cell `plan-roadmap`) |
| **Stakeholder / status update** | ✗ | **41-5** (cell `summarize-stakeholder`) |

### Project Manager & Scrum Master (no role today → 41-1)

| Activity | Status | Workflow / story |
|---|---|---|
| **Sprint planning** | ✗ | **41-6** |
| **Standup synthesis** | ✗ | **41-7** (event-sourced digest) |
| **Retrospective facilitation** | ✗ | **41-8** |
| Impediment tracking | ◑ | `blocker-diagnosis` (dev-blocker only; team-level in 41-7/41-8) |
| Status reporting | ✗ | **41-5** (shared with PO) |

### Tester

| Activity | Status | Workflow / story |
|---|---|---|
| Test-case authoring (TDD red) | ✅ | `test-case-creation` |
| Test execution / quality gate | ✅ | `testing-pipeline` |
| Defect triage (panel lens) | ◑ | `review-panel` as the lifecycle REVIEW stage (tester lens `triage-defect`) |
| Testability review (panel lens) | ◑ | `review-panel` |
| **Test-plan / strategy authoring** | ✗ | **41-13** (cell `plan-test-strategy`) |
| **Exploratory test charter** | ✗ | **41-14** (cell `exploratory-test`) |
| **Acceptance verification** | ✗ | **41-15** (cell `verify-acceptance`) |
| **Regression & flaky-test management** | ✗ | **41-16** (cell `write-regression-test`) |

### Security

| Activity | Status | Workflow / story |
|---|---|---|
| Vulnerability / security review (panel & triage lens) | ◑ | `review-panel` (review lens `plan-review-security`; triage lens `assess-vulnerability` — one panel, doc-type-parameterized) |
| Secret rotation | ◑ | `rotate-secret` (ops saga, not a review) |
| **Threat modeling** | ✗ | **41-19** (cell `threat-model`) |
| **Scheduled dependency / secret / compliance audit** | ✗ | **41-20** (cells `audit-dependencies`/`audit-secrets`/`review-compliance`) |
| **Security incident analysis** | ✗ | **41-21** (cell `analyze-security-incident`) |

### DevOps

| Activity | Status | Workflow / story |
|---|---|---|
| Deploy / promotion pipeline | ✅ | `deployment-pipeline` |
| CI configuration | ◑ | `ci-with-debug-retry` (runs CI; doesn't author config) |
| Incident diagnosis (panel lens) | ◑ | `review-panel` as the lifecycle REVIEW stage (devops lens `diagnose-incident`) |
| **Incident response & postmortem** | ✗ | **41-22** (cells `plan-incident-response`/`write-postmortem`) |
| **Capacity & health review** | ✗ | **41-23** (cells `assess-capacity`/`monitor-health`) |
| Rollback | ✅ | `deployment-pipeline` — auto rollback-on-prod-failure branch |

> **Corrected — rollback is not missing.** It was listed `✗ / folded into 41-22 (cell rollback)`. It is
> a **landed, executed step**: `DeploymentPipelineWorkflow.cs:299-329` builds the rollback branch
> (`emitRollbackStarted` → `rollbackCall` → `extractRollbackResult` → `rollbackOk` →
> `emitRollbackSuccess` / `emitRollbackFailed`), wired at `:545-552` off production failure, dispatching
> the mediated `(devops, rollback)` cell (`:602`) with `enableTools = true` (`:614`) and emitting a
> `DEPLOY.ROLLBACK.STARTED` / `.SUCCESS` / `.FAILED` audit trail (`DeployEvents.cs:61,64,70`). This also
> removes the inconsistency with the `deployment-pipeline` ✅ row above it. Consequence for **41-22**:
> `(devops, rollback)` is an existing **execution** cell (bound to
> `DeploymentPipelineWorkflow.ParseStageStatus` and listed in `ContractBindingTests.NonDocumentTypeResidual`),
> so 41-22 must **dispatch `deployment-pipeline`** rather than re-bind that cell as a document producer.

### Tech Writer

| Activity | Status | Workflow / story |
|---|---|---|
| PR description | ◑ | `pull-request` (inline, not a doc) |
| **Release notes & changelog** | ✗ | **41-24** (cells `write-release-notes`/`update-changelog`) |
| **User & API documentation** | ✗ | **41-25** (cells `write-user-docs`/`write-api-docs`) |
| **Runbook & ops-docs** | ✗ | **41-26** (cell `write-runbook`) |
| Doc review | ◑ | folded into 41-24/41-25/41-26 review stage (cell `review-docs`) — **but the review-action selector cannot reach it today**; see Dependencies |

### UX / Designer (no role today → 41-1)

| Activity | Status | Workflow / story |
|---|---|---|
| **User-flow drafting** | ✗ | **41-27** |
| **Wireframe / UI spec authoring** | ✗ | **41-27** |
| **Design review & accessibility audit** | ✗ | **41-28** |

## New document types (Story 41-1)

Reuse first; a new type is proposed only when no existing Epic 39 type carries the domain rules.

| New type | Why not an existing type | Domain rules beyond schema |
|---|---|---|
| `AcceptanceCriteria` | Not a `Clarification` (that resolves ambiguity) nor a `Plan` (that maps files); it is the testable definition-of-done consumed by 41-15 and the merge gate | each criterion independently verifiable; Given/When/Then or checklist form; bound to an `issueId`; no criterion references unimplemented scope |
| `BacklogOrdering` | A `TriageDecision` classifies one item; this **ranks a set** with rationale | total order over the referenced item set; every item has a rationale + value/effort estimate; no ties |
| `SprintPlan` | A `Plan` maps tasks-to-files for one issue; a sprint commits a **capacity-bounded set of issues** to a time-box | committed set ≤ stated capacity; every committed item has an owner-role + estimate; carry-over flagged |
| `TestPlan` | A `TestSpec` is executable cases bound to task IDs; a test plan is the **strategy** (scope, risk-based coverage, environments, entry/exit) above them | risk areas ranked; each strategy line maps to a coverage target; entry/exit criteria stated |
| `ThreatModel` | `Findings` cite evidence but carry no attack structure; threat modelling needs assets/threats/mitigations | STRIDE (or configured) categorisation; each threat has asset + mitigation + residual-risk; unmitigated high-risk ⇒ escalation |
| `UxSpec` | A `Design` weighs technical alternatives; a UX spec captures **flows/states/acceptance** for an interface | every flow has entry + success + error states; each screen/step lists a11y requirements; maps to acceptance criteria |

Everything else reuses: **`Findings`** (standup digest, retro, capacity/health review, exploratory
charter, security audit, technical-risk), **`Review`** (standalone code review, acceptance verification,
design/a11y review, doc review), **`Diagnosis`** (incident/security-incident analysis), **`Plan`**
(refactor plan, dependency-upgrade plan, roadmap, incident-response plan), **`TriageDecision`** (PR
triage, tech-debt triage, regression/flaky triage), and **prose** (ADR, postmortem, release notes,
changelog, runbook, user/API docs, stakeholder update) — which, per the Corrected note under rule 1, has
**no type and no audience tag in code yet** and needs the Wave-0 prose enabler below.

## Sequencing (highest-leverage first)

**Wave 0 — enablers. All three are hard gates; two of the three are unowned.**

| Enabler | Owner | State |
|---|---|---|
| Roles + action cells + the six new document types | **41-1** | drafted; hard-blocks eleven stories on **both** paths (see the Corrected note above) |
| **Prose document support** — a `prose` type (or a documented decision to model prose as an untyped body), an `Audience` column on `DocumentInstance` + envelope field, an audience vocabulary | **none — must be written** | blocks 41-4, 41-5, 41-9, 41-22, 41-24, 41-25, 41-26 |
| **Tenant-aware scheduled-trigger seam** (see Dependencies) | **none — must be written** | blocks 41-5, 41-7, 41-11, 41-16, 41-17 (PR sweep), 41-20, 41-23 |

**Wave 1 — highest leverage (closes the biggest holes on the critical path).** *Only 41-29 and 41-17's
code-review half are Wave-0-independent; the rest are listed here for leverage, not for start order —
41-2, 41-15 and 41-9 cannot begin until Wave 0 clears.*
- **41-29 Task-Level Flow Router (+ issue-level pre-route)** — *the activation story.* Adds a task `kind`
  to the `Plan` and switches `single-issue-cycle` to dispatch each task to the workflow matching its kind
  (code→TDD, docs→docs, design→UX, …) plus a lightweight issue-level pre-route for `question`/`docs`-only
  issues. Without it, every issue is forced through the code-writing pipeline and the per-role workflows
  below are unreachable from the issue pipeline. Ships against today's workflows and lights up each new
  kind as its Epic 41 target lands. **Not blocked by 41-1.** 39-15 has landed (`TaskCreationWorkflow.cs:19`
  is already the thin binding), so its remaining blockers are its own `Plan` schema change and the 39-16
  contract regeneration. **Rebases onto Epic 40:** 40-2/40-4/40-5 rewire the same per-task loop region of
  `SingleIssueCycleWorkflow.cs`, so 41-29 lands after them (40-2 → 40-4 → 40-5 → 41-29).
- **41-2 Acceptance-Criteria Authoring** — feeds `verify-acceptance` (41-15) *and* the merge gate; today
  "done" is undefined outside a plan. Highest single-story leverage. **Gated on 41-1** (`AcceptanceCriteria`
  type) — it cannot precede Wave 0.
- **41-15 Acceptance Verification** — closes the loop 41-2 opens; turns "tests pass" into "requirement
  met" at the accept gate. Gated on 41-2, hence transitively on 41-1.
- **41-17 Standalone Code Review & PR Triage** — code review only exists mentorship-bound; every repo
  needs review-of-a-diff and a routed PR queue as a stand-alone. **Split it:** the code-review half needs
  no new cell and is Wave-1-startable; the PR-triage half needs 41-1's `triage-pr` cell **and** the
  scheduler enabler, so it lands after Wave 0.
- **41-9 ADR Authoring** — cheap, high-value; intended to prove the prose path for the whole
  tech-writer/devops family behind it. **Gated on the Wave-0 prose enabler** — it cannot be the reference
  implementation of a path that does not exist. Either the enabler lands first or 41-9 leaves Wave 1.

**Wave 2 — recurring, event-sourced, scheduled (compounding value).**
- **41-7 Standup Synthesis**, **41-16 Regression & Flaky-Test Management**, **41-11 Tech-Debt & Risk
  Triage**, **41-20 Scheduled Security Audit**, **41-23 Capacity & Health Review** — all read the DCB
  stream / CI history on a cron and produce a `Findings`/`TriageDecision`; each replaces a standing human
  chore. 41-24 Release Notes & 41-25 User/API Docs are release/merge-triggered siblings.
  **All five cron stories are gated on the Wave-0 scheduler enabler**; 41-11 and 41-16 additionally on
  41-1's cells, and 41-24/41-25 on the prose enabler. Wave 2 cannot start before Wave 0 finishes.

**Wave 3 — planning & design depth.**
- 41-3 Backlog Prioritization, 41-6 Sprint Planning, 41-4 Roadmap, 41-8 Retro, 41-5 Stakeholder Update,
  41-10 System Design Doc, 41-18 Refactor Planning, 41-19 Threat Modeling, 41-21 Security Incident,
  41-22 Incident & Postmortem, 41-12 Dependency & Upgrade, 41-13 Test-Plan, 41-14 Exploratory Charter,
  41-26 Runbook.

**Wave 4 — new surface (UX/design; depends on 41-1's `ux_designer` role + `UxSpec` type).**
- 41-27 User-Flow & Wireframe Drafting, 41-28 Design Review & Accessibility Audit.

### Planning artifacts this epic does not have

Epic 39 and Epic 40 each ship an `EXECUTION-PLAN.md`; Epic 40 also ships a `sprint-status.yaml`. **Epic 41
has neither, and exactly one of its 29 stories (41-29) has an `implementation-plan.md`.** The waves above
are therefore a leverage ordering, not a schedule: there is no per-story effort estimate, no critical path,
no cross-story shared-edit register, and no wave roll-up that a scheduler could execute against. Two
consequences worth naming before anyone reads a start date into this section:

- The Wave-0 gates above (41-1, prose, scheduler) are the only hard edges recorded anywhere; **all other
  ordering lives in individual stories' `Blocking:` lines** and has never been reconciled across them.
- The one cross-epic shared edit that *is* known — `SingleIssueCycleWorkflow.cs`'s per-task loop, written
  by 40-2, 40-4, 40-5 and 41-29 — is registered in Epic 40's execution plan, not here.

Producing an `EXECUTION-PLAN.md` (+ `sprint-status.yaml`) is a prerequisite for treating Epic 41 as
schedulable. Until then, treat every wave boundary as a dependency statement only.

## Deliberately out of scope (not automated as a workflow)

- **Live human ceremonies as real-time events** (the actual standup/retro *meeting*, sprint *review demo*
  to stakeholders). Tamma automates the **artifact** (digest, retro report, plan) and routes it, not the
  synchronous human conversation. — *A meeting is a human-coordination act; the value Tamma adds is the
  durable, event-sourced artifact around it.*
- **Pixel-level visual design production** (actual mockup rendering in a design tool). 41-27 produces a
  structured `UxSpec` + flow/state description; rendering pixels is a design-tool integration, not a
  document-lifecycle workflow. — *Out until a design-tool provider abstraction exists, parallel to the
  Git/AI provider abstractions.*
- **Hiring, budgeting, vendor/contract management, people-management 1:1s.** — *Team-operations, not
  software-development activities; outside Tamma's SDLC charter.*
- **Final production-deploy authorization for regulated/breaking changes.** Stays a human decision by
  acceptance-rules policy (always-escalate class), not a new workflow. — *Epic 39 already models this as
  policy, not code.*

## Dependencies

- **Epic 39** — but **not all of it has landed, and rules 3 and 4 lean on the part that has not.** What is
  in the tree today: document core/types/registry, `DocumentLifecycleWorkflow`, review producers,
  acceptance rules, escalation surface, the resume standard + its structural test, document store &
  lineage API, and the real-time channels (39-18: `OrchestratorChannelHub` / `UserChannelHub` mapped at
  `Tamma.Api/Program.cs:3384-3385`, plus the per-tenant channel outbox + sweeper). What is **not**:

  | Missing | Evidence in code | Epic 41 impact |
  |---|---|---|
  | **39-17 orchestrator agent** (the long-running LLM process that *decides*) | no agent host exists; `GetAcceptanceRulesTool` is deliberately not a registered `IToolExecutor` and waits on "the 39-17 host" (`Program.cs:414-417`, `GetAcceptanceRulesTool.cs:13,17,121`); `OrchestratorChannelHandler.cs:11` waits on 39-17 to mint the claim | **rule 3** — the `AcceptanceRequest` is published and the workflow suspends, but nothing on the other end decides. Every accept gate parks. |
  | **39-19 orchestrator chat + Task View** | `AgentOfflineChatRelay` is the registered `IOrchestratorChatRelay` (`Program.cs:448-451`) and refuses every message; the outbox refuses conversation-kind enqueues | **rule 4** — no surface for a human assignee. Directly hits 41-2:36 (accept "in the Task View or by asking the orchestrator in chat"). |
  | **39-20 teams, roles, repo access & task routing** | `ITaskAudienceResolver` is stubbed fail-closed by `InitiatorOnlyTaskAudienceResolver` (`Program.cs:445-447`) — only the issue initiator is ever admitted | role-addressed delivery does not work. Named in ACs: **41-6**:45, **41-7**:49, **41-8**:46 ("role-scoped Task View entries via 39-20"), and relied on by 41-11:35, 41-17:40, 41-22:51, 41-23:33, 41-29:126. |
  | **39-1** (I/O & lifecycle audit) and **39-21** (RAG in C#) | no audit artifact and no C# RAG path in the tree | 39-1 is where prose/tech-writer output was recorded out of scope — see the rule-1 Corrected note. |

  Net: **every** Epic 41 story's rule-3/rule-4 promise is currently unreachable end-to-end. Three stories
  fail at the AC level, not merely in prose — **41-6**:45, **41-7**:49 and **41-8**:46 each make
  "role-scoped Task View entries via 39-20" an acceptance criterion. This does not change the epic's
  design; it changes what "done" can mean for any story shipped before 39-17/39-19/39-20, and each such
  story must say in its own ACs which half it is claiming.
- **Epic 40** for any workflow whose accept leads into a coding execution (41-18 refactor plan → coding
  step reuses the durable runner). Also a **file-level** prerequisite for **41-29**: 40-2, 40-4 and 40-5
  rewire the same per-task loop of `SingleIssueCycleWorkflow.cs` that 41-29's `FlowSwitch` wraps
  (order: 40-2 → 40-4 → 40-5 → 41-29).
- **Epic 42 (Agent Capability & Tool Layer) — the reciprocal edge Epic 42 already declares.**
  `docs/stories/epic-42/README.md:77-96` names Epic 41 as its consumer ("the missing foundation under
  Epic 41"), and `:357` lists "Epic 41 / 41-29: the consumers" — yet this section previously did not
  name Epic 42 at all, so the edge existed in one direction only. Only **six** `IToolExecutor`s are
  registered (`Tamma.Api/Program.cs:753-764`: `FileRead`, `FileWrite`, `SearchCode`, `ShellExecute`,
  `GitOperations`, `RunTests`) and the registry is DI-seeded from exactly that set — all six are
  coding-oriented. So the **agent** path of the non-code stories has no governed tool:

  | Story | Needs (Epic 42) | Reachable today? |
  |---|---|---|
  | **41-5** stakeholder update, **41-7** standup publish | authenticated HTTP / external API (42-9) | no executor |
  | **41-22** incident response, incl. execute-a-response-class & kill-switch | cloud/VPS ops (42-7), feature-flag toggle (42-8) | no executor |
  | **41-23** capacity & health review | health/metric signal reads (42-9) | no executor |
  | **41-24 / 41-25 / 41-26** docs publish | publish capability (42-9) | no executor |
  | **41-28** audit of a *shipped* UI | browser/render capability | no executor |
  | **41-20** dependency/secret/compliance audit | governed audit tooling | degrades to raw `ShellExecute` — possible but **ungoverned and unclassified** |
  | **41-14** tool-enabled exploratory charter | governed exploration tooling | degrades to the six coding tools |

  Until Epic 42 lands these stories are **human-assigned only** (rule 4). Each story's Autonomy section
  should carry that caveat explicitly rather than implying a day-one agent path.
- **Scheduled workflows have no reusable pattern.** `HourlyAnalyticsRollupScheduler` was cited here as a
  "cron pattern"; it is not reusable as one (all line cites below are
  `Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs`). It hardcodes its target workflow
  (`HourlyAnalyticsRollupWorkflow.DefinitionId`, `:197-198`) and its options section (`:17`), offers a
  single `FireAtMinute` int rather than a window or cron shape (`:34`), and is **not tenant-aware**: its
  advisory-lock key is `(year, dayOfYear, hour)` with no tenant component (`:241`, so one tenant's leader
  suppresses every other tenant's fire), the dispatch threads no `tenantId` (`:201-202`), and idempotency
  rests on `_lastFired` in-process memory (`:83`) plus the target workflow's own per-row UPSERT — a
  property a document-producing lifecycle workflow does not have. The consumers need the opposite of all
  four: **41-5, 41-7, 41-11, 41-16, 41-17 (PR sweep), 41-20, 41-23** each ask for tenant-scoped,
  per-window, durable idempotency. **No story owns building that seam** — it is the third Wave-0 enabler.
  (41-17 does not even list the scheduler in its `Blocking:` line despite naming it in Scope.)
- `Tamma.Core/Agents` taxonomy extension (**41-1**) — see the Corrected note above: it gates **eleven**
  stories on both the agent and the human path, not just the agent path of 41-6/41-7/41-8/41-27/41-28.
- **Review-panel selector gap.** `RolePhaseMap.GetReviewActionForRole` (`RolePhaseMap.cs:376-387`) covers 7 of the 8
  roles and **throws** for `tech_writer`, and `DocumentLifecycleWorkflow.cs:1199` calls it unguarded — so
  configuring `tech_writer` as a document reviewer fails at runtime. 41-24/41-25/41-26 all specify review
  via `(tech_writer, review-docs)`; the cell itself is legal (`AgentAction.cs:117`, `RolePhaseMap.cs:162`)
  but the selector cannot reach it. 41-1 must add the `TechWriter` arm and extend
  `ReviewerSelectionHelper.s_documentRoster` from 7 to 8, and state whether `scrum_master` /
  `project_manager` / `ux_designer` are on the review/triage panels or deliberately off them.

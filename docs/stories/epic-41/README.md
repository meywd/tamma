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
   `feedbackVariableName` carrier, asserted via `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches`;
   and (f) the binding declares its `WorkflowDocumentInterface` row in `DocumentTypeRegistry.BuildSeed`
   and **bumps `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned`** (`:45`, `HaveCount(16)`
   today) in the same change — the pin is deliberately a conscious edit, one per new producing workflow.
   Any story that cannot meet (a)–(f) must name the deviation and justify it in its own ACs — the rule
   is enforced per story or it is not claimed.

   > **Clause (f) is an epic-level rule because only some stories state it.** 41-10, 41-12, 41-15,
   > 41-16, 41-17 and 41-18 carry it as an explicit AC; the rest inherit it from here. Note the
   > direction: the edge pin moves with a **producing workflow**, not with a document type — 41-1b
   > registers six types and deliberately moves **no** edges (its D2), while it *does* move the two
   > vocabulary pins (`DocumentTypeKeyTests.cs:20` `Be(10)`, `DocumentTypeRegistryTests.cs:37`
   > `HaveCount(10)`). Do not conflate the three.

   > **Corrected — prose has no mechanism in code; Story 41-1c now owns building one.** Earlier drafts
   > said prose output (ADR, postmortem, release notes, changelog, runbook, docs, stakeholder update)
   > "rides the lifecycle as a **prose document with an audience tag** (Epic 39: *prose stays prose*)".
   > Epic 39 states that only as a *principle*, and 39-1 records prose/tech-writer output as explicitly
   > **out of scope** of the 10-type table. In code there is no prose type and no audience tag:
   > `Tamma.Core/Documents/DocumentTypeKey.cs:22-33` has exactly ten members (findings,
   > ambiguity-assessment, clarification, decomposition, plan, design, review, triage-decision,
   > diagnosis, test-spec) and neither `Tamma.Data/Entities/DocumentInstance.cs` nor
   > `Tamma.Core/Documents/DocumentEnvelope.cs` carries an `Audience` member. **41-4, 41-5, 41-8, 41-9,
   > 41-22, 41-24, 41-25 and 41-26 — eight stories — are written against that mechanism**, and until the
   > 41-1 split none owned it. **[41-1c](./story-41-1/41-1c-prose-documents-and-audience-tags.md)** now
   > does: a `prose` `DocumentTypeKey` member + `ProseDocumentType` (body unvalidated markdown), an
   > `Audience` field on `DocumentEnvelope` **and** `DocumentInstance` (+ EF config + migration), and the
   > audience/kind vocabularies. See **Sequencing → Wave 0**.

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
5. **Resumable by design.** Every **thin producer binding** in this epic declares
   `[ResumeBehavior(LatestStateReEntry)]` — the binding's own graph owns no suspend node; the accept
   gate suspends on its canonical tenant-folded bookmark **inside the dispatched `document-lifecycle`
   child** (`LifecycleBookmarks`), which the parent awaits with `WaitForCompletion = true`. Only a
   workflow that suspends on a bookmark in its OWN graph (e.g. 41-31's approval gate) declares
   `[ResumeBehavior(Both)]` — `ResumableStandardStructuralTests` clause (b) fails a `Both` declaration
   with no canonical suspend node in the declaring graph. All pass the 39-10 structural test without an
   allowlist entry. **Scheduled workflows have no reusable
   pattern yet** — see the scheduler note under **Dependencies**.

**Vocabulary is reused, not reinvented.** Where an activity's output fits an existing Epic 39 type
(`Findings`, `Review`, `Design`, `Plan`, `Diagnosis`, `TriageDecision`, `TestSpec`) this epic uses it.
Only a handful of genuinely new types are proposed (Story **41-1b**) and each is justified against an
existing type it could NOT reuse — plus prose, which is *not* an existing type and is built by
**41-1c**, per the Corrected note above.

## New roles & the two role families that don't exist yet

The taxonomy (`Tamma.Core/Agents`) models **8 roles**: developer, senior_developer, tester, security,
devops, architect, product_owner, tech_writer. The user's target set names **four the platform has no
role for** — they currently fall back to `product_owner` via `LegacyRoleAliases` (`scrum_master`,
`analyst`) or aren't modelled at all (UX, designer, project_manager). Story **41-1a** adds
`scrum_master`, `project_manager`, and `ux_designer` (covering both UX and visual-design work) as first
-class `AgentRole`s with their action cells; **41-1b** adds the new document types the epic needs and
**41-1c** the prose mechanism.

> **Corrected — the enabler set is a hard blocker on BOTH paths, for fourteen stories.** Earlier
> drafts claimed "every other story in the epic can still ship and run human-assigned before 41-1
> lands … 41-1 gates only the *agent* path". That is false and it contradicted the stories' own
> Dependencies. A document type that is not in the vocabulary cannot be validated or persisted **no
> matter who executes the step**: `DocumentTypeKeyExtensions.Parse` throws `DOCUMENT.TYPE.UNKNOWN`
> for any non-vocabulary wire string (`DocumentTypeKey.cs:49-59`), `DocumentTypeRegistry.Resolve`
> throws `DOCUMENT.TYPE.NOT_REGISTERED` (`DocumentTypeRegistry.cs:85-91`), and
> `DocumentInstance.DocumentType` is a `DocumentTypeKey` wire string. The same holds for a missing
> `(role, action)` cell — a human assignee still needs a cell to bind.
>
> | Blocked on | For | Stories |
> |---|---|---|
> | **41-1b** | a new **document type** (unpersistable until registered) | 41-2 `AcceptanceCriteria` · 41-3 `BacklogOrdering` · 41-6 `SprintPlan` · 41-13 `TestPlan` · 41-19 `ThreatModel` · 41-27 `UxSpec` |
> | **41-1a** | a new **role** | 41-6, 41-7, 41-8 (`scrum_master`) · 41-27, 41-28 (`ux_designer`) |
> | **41-1a** | a new **action cell** | 41-10 `design-system` · 41-11 `triage-tech-debt` · 41-16 `manage-regression` · 41-17 `triage-pr` (PR-triage half) · 41-22 `incident-rootcause` |
> | **41-1a** | the **review-action selector** arm the review stage needs | 41-24, 41-25, 41-26 (`(tech_writer, review-docs)`) |
> | **41-1c** | the **prose type + audience tag** | 41-4, 41-5, 41-8, 41-9, 41-22, 41-24, 41-25, 41-26 |
>
> Union across the taxonomy/document-type halves (41-1a + 41-1b): **seventeen** stories — the fourteen
> above plus 41-24/41-25/41-26, whose *review* stage (not their produce step) needs the selector arm.
> Adding 41-1c's prose set of eight, and netting the five stories in both: **twenty of twenty-nine**. *(Corrected twice over: the count was "eleven" while 41-10,
> 41-17 and 41-22 were separately shown to need new 41-1a cells; and this note used to say 41-17's
> `Blocking:` line omitted 41-1 — 41-17 has since been revised and names 41-1a explicitly.)*
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
| **System design doc (API contract / data model / integration)** | ✗ | **41-10** (`system-design`, on a **new** `(architect, design-system)` cell from 41-1a; the three `design-*` facet cells stay unbound and become *sections* of the one `Design`) |
| **Tech-debt & technical-risk triage** | ✗ | **41-11** (cell `assess-technical-risk` exists; the `triage-tech-debt` cell is minted by 41-1a) |
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

### Project Manager & Scrum Master (no role today → 41-1a)

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
| **Regression & flaky-test management** | ✗ | **41-16** (`(tester, manage-regression)` from 41-1a + the existing `write-regression-test`) |

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
| **Incident response & postmortem** | ✗ | **41-22** (new `(devops, incident-rootcause)` cell from 41-1a + existing `plan-incident-response`/`write-postmortem`; **not** `(devops, diagnose-incident)`, which is the triage-panel lens). **Its trigger is 41-32** |
| **Capacity & health review** | ✗ | **41-23** (cells `assess-capacity`/`monitor-health`) |
| Rollback of a deploy **in flight** | ✅ | `deployment-pipeline` — auto rollback-on-prod-failure branch |
| **Rollback of a deploy that already succeeded** | ✗ | **41-31** (`emergency-rollback`) |

> **Corrected twice — rollback is not missing, but the shipped capability is narrower than the ✅
> implied, so the row is now split.** It was first listed `✗ / folded into 41-22 (cell rollback)`. It is
> a **landed, executed step**: `DeploymentPipelineWorkflow.cs:299-329` builds the rollback branch
> (`emitRollbackStarted` → `rollbackCall` → `extractRollbackResult` → `rollbackOk` →
> `emitRollbackSuccess` / `emitRollbackFailed`), wired at `:545-552` off production failure, dispatching
> the mediated `(devops, rollback)` cell (`:602`) with `enableTools = true` (`:614`) and emitting a
> `DEPLOY.ROLLBACK.STARTED` / `.SUCCESS` / `.FAILED` audit trail (`DeployEvents.cs:61,64,70`). This also
> removes the inconsistency with the `deployment-pipeline` ✅ row above it. Consequence for **41-22**:
> `(devops, rollback)` is an existing **execution** cell (bound to
> `DeploymentPipelineWorkflow.ParseStageStatus` and listed in `ContractBindingTests.NonDocumentTypeResidual`),
> so 41-22 must **dispatch** an execution workflow rather than re-bind that cell as a document producer.
>
> **Corrected again (2026-07-27) — that dispatch target cannot be `deployment-pipeline`, and 41-31 now
> supplies the one it can be.** The correction above is right about the *branch* and wrong about the
> *capability*, because of where the branch is wired. The only inbound edge is
> `Connect(emitProdFailed, emitRollbackStarted)` (`:546`), and `emitProdFailed` is reachable only from
> `ConnectOutcome(prodRetryCheck, "False", …)` (`:543`) — i.e. **after a production deploy in the same
> run has failed `MaxStageRetries = 3` times** (`:102`). Four failure paths bypass it entirely
> (`:506-507`, `:519-520`, `:531`, `:562`). `deployment-pipeline` also has **no standalone entry
> point** (sole dispatch site `SingleIssueCycleWorkflow.cs:721-742`, post-merge, `mergeSha` from
> `WaitForPRMergedActivity`) and its rollback dispatch passes **the failing `mergeSha` and no
> previous-release ref at all** (`:604-613`), while `Prompts/devops/rollback.md` asks the agent to find
> "the previous known-good release" with nothing to find it from.
>
> So the shipped capability is *"undo the deploy I was in the middle of"*, not *"revert a release that
> is already live"*. **41-22's own implementation plan found this** (finding C5: *"AC3's 'a rollback is
> performed by dispatching `deployment-pipeline`' [is] not implementable"*) and recorded it as *"filed,
> not fixed here"* — **and the `.dev/findings/` file it says it filed does not exist.**
> **[41-31](./story-41-31/41-31-standalone-emergency-rollback.md)** is that fix: a new
> `emergency-rollback` execution workflow (no document, no new cell, no count-pin movement) plus a
> one-variable amendment giving *both* rollback paths a resolved target. **41-22 must be revised to
> dispatch `emergency-rollback`.**

### Tech Writer

| Activity | Status | Workflow / story |
|---|---|---|
| PR description | ◑ | `pull-request` (inline, not a doc) |
| **Release notes & changelog** | ✗ | **41-24** (cells `write-release-notes`/`update-changelog`) |
| **User & API documentation** | ✗ | **41-25** (cells `write-user-docs`/`write-api-docs`) |
| **Runbook & ops-docs** | ✗ | **41-26** (cell `write-runbook`) |
| Doc review | ◑ | folded into 41-24/41-25/41-26 review stage (cell `review-docs`) — **but the review-action selector cannot reach it today**; see Dependencies |

### UX / Designer (no role today → 41-1a)

| Activity | Status | Workflow / story |
|---|---|---|
| **User-flow drafting** | ✗ | **41-27** |
| **Wireframe / UI spec authoring** | ✗ | **41-27** |
| **Design review & accessibility audit** | ✗ | **41-28** |

## New document types (Story 41-1b)

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
changelog, runbook, user/API docs, stakeholder update, retro narrative) — which, per the Corrected note
under rule 1, has **no type and no audience tag in code yet** and is built by the Wave-0 enabler
**41-1c** below.

## Sequencing (highest-leverage first)

**Wave 0 — enablers. All are hard gates; one of the four is still unowned.**

*Corrected: this table used to list three enablers and give the prose row the owner "none — must be
written". Story 41-1 has since been **split into three independently-shippable sub-stories**, and the
third of them (41-1c) is the prose enabler. Only the scheduler seam remains unowned.*

| Enabler | Owner | Effort | State |
|---|---|---|---|
| Three roles + fifteen action tokens (eighteen cells incl. per-role `context-scan`, plus the 41-8 lockstep `write-retro-narrative` amendment) + the derived panel-selector maps + the `scrum_master` alias removal | **[41-1a](./story-41-1/41-1a-agent-taxonomy-extension.md)** | 4–5 d | drafted |
| The six new document types (`AcceptanceCriteria`, `BacklogOrdering`, `SprintPlan`, `TestPlan`, `ThreatModel`, `UxSpec`) | **[41-1b](./story-41-1/41-1b-new-document-types.md)** | 5–6 d | drafted |
| **Prose document support** — a `prose` type, an `Audience` field on envelope **and** `DocumentInstance` (+ migration), the audience/kind vocabularies | **[41-1c](./story-41-1/41-1c-prose-documents-and-audience-tags.md)** | 3–4 d | drafted |
| **Tenant-aware scheduled-trigger seam** (see Dependencies) | **[41-30](./story-41-30/41-30-tenant-aware-scheduled-trigger-seam.md)** | 6–7 d | drafted — blocks **41-11, 41-16, 41-17 (PR sweep), 41-20, 41-23**; see the scoping decision below |

> **Corrected 2026-07-27 — the seam now has an owner, and the premise behind "it needs new Elsa
> packages" was false.** The Wave-0 row above read *"none — must be written"* from this epic's first
> draft until **41-30** was written. Two factual corrections came out of writing it, both of which make
> the seam cheaper than every prior document assumed:
>
> - **Cron parsing is already an in-tree dependency.** `HourlyAnalyticsRollupScheduler.cs:45-49` calls
>   itself a *"lightweight alternative to wiring a full Elsa cron-trigger activity (which would require
>   additional Elsa packages)"*, and this epic inherited that framing. It is not true today:
>   `Tamma.ElsaServer.csproj:29` references **`Elsa.Scheduling` 3.5.3**, `Program.cs:100` already calls
>   `elsa.UseScheduling()`, `Elsa.Scheduling` declares **`Cronos` 0.11.0** as a direct dependency and
>   ships `ICronParser`/`CronosCronParser`, and `AdlOrchestratorWorkflow.cs:2` already imports
>   `Elsa.Scheduling.Activities`. **The seam needs zero new NuGet packages.**
> - **That does not mean Elsa's `Cron` trigger can replace the seam.** It arms one trigger per workflow
>   *definition* with no tenant dimension, and the shipped `IScheduler` is `LocalScheduler` —
>   in-process, so an N-pod deploy arms N copies. Use Elsa's **parser**, not its **scheduler**. 41-30
>   D3 records the choice; 41-30 Correction 2 records the rejection.
>
> **The seam is a hosted service + two control-plane tables + an admin API — not a workflow**, and
> 41-30's "Shape" section argues that explicitly, because this epic's default answer is "make it a
> workflow".

> ### Epic 41 assumes two trigger classes and, until 41-30/41-32, owned neither
>
> The scheduled one is above. The **reactive** one is the same size of hole and was never named:
> **41-21** and **41-22** each declare *"Reactive trigger (alert / security alert / health-review
> escalation)"* in their own Scope line, and nothing in the platform can deliver it.
> `AlertRuleEvaluator` → `IAlertSink` → `NotificationDispatcher` → four **notification** channels
> (email, PagerDuty, Slack, webhook); `IElsaWorkflowService` — the only way anything in `Tamma.Api`
> starts a workflow — has five consumers and **none is in `Services/Alerts/`**. Tamma detects, pages a
> human, and stops. **[41-32](./story-41-32/41-32-alert-triggered-workflow-response-seam.md)** closes
> it as an *amendment to the alert stack*, not a new workflow — 41-21 and 41-22 are the workflows.

> ### Scheduling is needed for audits, NOT for ceremonies (product owner, 2026-07-25)
>
> > "scrum and stuff are not automated, they are for users if they exist, but audits sure need to
> > exist, so it depends"
>
> The seam was listed as blocking seven stories. It blocks **five**. The ceremony stories are
> **user-initiated**, not scheduled:
>
> | Story | Trigger | Needs the seam? |
> |---|---|---|
> | 41-5 Stakeholder / status reporting | a person asks for it | **No** |
> | 41-7 Standup synthesis | a person asks for it | **No** |
> | 41-8 Retrospective facilitation | already event-triggered (sprint close), not cron | No (already) |
> | 41-6 Sprint planning | a person runs it | No (already) |
> | **41-11** Tech-debt sweep | recurring audit | **Yes** |
> | **41-16** Regression / flaky-test management | recurring audit | **Yes** |
> | **41-17** PR-triage sweep half | recurring audit | **Yes** |
> | **41-20** Scheduled security audit | recurring audit | **Yes** |
> | **41-23** Capacity / health review | recurring audit | **Yes** |
>
> **The distinction is who decides it should happen now.** A standup summary is something a team
> asks for when they want it — automating it on a cron produces a document nobody asked for, on a
> day nobody was working. An audit is exactly the opposite: its value is that it runs whether or not
> anyone remembered.
>
> **Consequences:** 41-5 and 41-7 come off the blocked list and become schedulable immediately —
> both need only a manual trigger, which already exists. Their plans currently say "⛔ BLOCKED
> (scheduler seam, unowned)" and must be corrected. The seam itself is still unowned and still needs
> a decision on who builds it, but it now gates five audit stories rather than seven mixed ones —
> which also makes it a cleaner thing to specify, since every remaining consumer wants the same
> shape: run this on a cadence, per tenant, and do not double-fire.

41-1a + 41-1b hard-block **seventeen** stories on both execution paths — **fifteen** at their
*produce* step (a missing type/role/cell — 41-5 joined this set when its produce cell moved to
`(project_manager, report-status)`), plus 41-24/41-25 at their *review* stage (the
`(tech_writer, review-docs)` selector arm; 41-26 left this set — its default reviewer is now the
already-reachable `(devops, review-operability)`, making 41-1a an upgrade there, not a gate).
41-1c blocks **eight**. Five stories (41-5, 41-8, 41-22, 41-24, 41-25) are in both sets, so
**twenty of the epic's twenty-nine** original workflow stories wait on some part of the enabler set
(17 + 8 − 5 = 20; the 2026-07-27 additions 41-30/41-31/41-32 are enabler/seam stories and are not
gated). The per-story breakdown is the "What each sub-story gates" table in
[the 41-1 umbrella](./story-41-1/41-1-team-role-and-document-type-extensions.md). 41-1a and 41-1b are
independent of each other; 41-1c is independent of both — so the enabler set is ~6 days of
wall-clock, not 12–15.

**Wave 1 — highest leverage (closes the biggest holes on the critical path).** *Only 41-29 and 41-17's
code-review half are Wave-0-independent; the rest are listed here for leverage, not for start order —
41-2, 41-15 and 41-9 cannot begin until Wave 0 clears.*
- **41-29 Task-Level Flow Router (+ issue-level pre-route)** — *the activation story.* Adds a task `kind`
  to the `Plan` and switches `single-issue-cycle` to dispatch each task to the workflow matching its kind
  (code→TDD, docs→docs, design→UX, …) plus a lightweight issue-level pre-route for `question`/`docs`-only
  issues. Without it, every issue is forced through the code-writing pipeline and the per-role workflows
  below are unreachable from the issue pipeline. Ships against today's workflows and lights up each new
  kind as its Epic 41 target lands. **Not blocked by any part of 41-1.** 39-15 has landed
  (`TaskCreationWorkflow.cs:19` is already the thin binding), so its remaining blocker is its own `Plan`
  schema + shared contract change (39-16's generated-region markers do not exist in any prompt file, so
  the two plan templates are hand-edited — see the story). **Rebases onto Epic 40:** 40-2/40-4/40-5
  rewire the same per-task loop region of `SingleIssueCycleWorkflow.cs`, so 41-29 lands after them
  (40-2 → 40-4 → 40-5 → 41-29).
- **41-2 Acceptance-Criteria Authoring** — feeds `verify-acceptance` (41-15) *and* the merge gate; today
  "done" is undefined outside a plan. Highest single-story leverage. **Gated on 41-1b**
  (`AcceptanceCriteria` type) — it cannot precede Wave 0.
- **41-15 Acceptance Verification** — closes the loop 41-2 opens; turns "tests pass" into "requirement
  met" at the accept gate. Gated on 41-2, hence transitively on 41-1b.
- **41-17 Standalone Code Review & PR Triage** — code review only exists mentorship-bound; every repo
  needs review-of-a-diff and a routed PR queue as a stand-alone. **Split it:** the code-review half needs
  no new cell and is Wave-1-startable; the PR-triage half needs 41-1a's `triage-pr` cell **and** the
  scheduler enabler, so it lands after Wave 0. (The story now records both, and the disposition of the
  incumbent `code-review` DefinitionId: the new bindings take new ids — `diff-review` /
  `pr-triage-sweep` — and rewire neither live caller.)
- **41-9 ADR Authoring** — cheap, high-value; intended to prove the prose path for the whole
  tech-writer/devops family behind it. **Gated on 41-1c** — it cannot be the reference implementation of
  a path that does not exist. 41-1c is 3–4 days and independent of 41-1a/41-1b, so the cheapest fix is
  to land it first rather than to demote 41-9.

**Wave 2 — recurring, event-sourced, scheduled (compounding value).**
- **41-7 Standup Synthesis**, **41-16 Regression & Flaky-Test Management**, **41-11 Tech-Debt & Risk
  Triage**, **41-20 Scheduled Security Audit**, **41-23 Capacity & Health Review** — all read the DCB
  stream / CI history on a cron and produce a `Findings`/`TriageDecision`; each replaces a standing human
  chore. 41-24 Release Notes & 41-25 User/API Docs are release/merge-triggered siblings.
  **All five cron stories are gated on the Wave-0 scheduler enabler — now owned by 41-30**;
  41-11 and 41-16 additionally on 41-1a's cells, and 41-24/41-25 on 41-1c. Wave 2 cannot start before
  Wave 0 finishes. *(41-7 is listed here for its event-sourced digest shape; per the 2026-07-25 scoping
  decision it is **user-initiated** and does **not** need the seam.)*

**Wave 3 additions — the two reachability stories (2026-07-27).**
- **41-31 Standalone Emergency Rollback** — the execution workflow 41-22 AC3 needs and cannot have
  today. Not blocked by any part of Wave 0 (no new cell, no new document type, no prose); startable
  immediately.
- **41-32 Alert-Triggered Workflow-Response Seam** — the reactive trigger 41-21 and 41-22 both
  specify. Not blocked by Wave 0 either. Its highest-value binding is *alert → 41-31*, which is also
  why its AC7 (starting a workflow via an alert never bypasses that workflow's own governance) is a
  hard pin rather than a note.

**Wave 3 — planning & design depth.**
- 41-3 Backlog Prioritization, 41-6 Sprint Planning, 41-4 Roadmap, 41-8 Retro, 41-5 Stakeholder Update,
  41-10 System Design Doc, 41-18 Refactor Planning, 41-19 Threat Modeling, 41-21 Security Incident,
  41-22 Incident & Postmortem, 41-12 Dependency & Upgrade, 41-13 Test-Plan, 41-14 Exploratory Charter,
  41-26 Runbook.

**Wave 4 — new surface (UX/design; depends on 41-1a's `ux_designer` role + 41-1b's `UxSpec` type).**
- 41-27 User-Flow & Wireframe Drafting, 41-28 Design Review & Accessibility Audit.

### Planning artifacts

**This epic now ships an [`EXECUTION-PLAN.md`](./EXECUTION-PLAN.md)** (added 2026-07-27), reconciled
from all 34 implementation plans: total ≈ 169 person-days, internal critical path 16.4 days
(`41-1b → 41-2 → 41-15`), wave wall-clock ≈ 20 days, and the serialized
`WorkflowInterfaceGraphTests` edge-count-pin bump as the epic's merge-rate limiter. Its per-story
efforts (the plans' bottom-line totals) supersede the story files' older ranges. The epic's stories
are tracked in the shared `docs/sprint-status.yaml`. *(An earlier revision of this section said Epic
41 had no execution plan and almost no per-story estimates — both were true when written and are now
resolved.)* Two standing notes:

- The Wave-0 gates above (41-1a, 41-1b, 41-1c, 41-30) plus the per-story `Blocking:` lines are
  reconciled in the execution plan's per-story table; treat that table as authoritative when they
  disagree with a wave label here.
- The one cross-epic shared edit — `SingleIssueCycleWorkflow.cs`'s per-task loop, written by 40-2,
  40-4, 40-5 and 41-29 (and 41-15's merge region) — is registered in Epic 40's execution plan and
  cross-referenced in this epic's.

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
  `docs/stories/epic-42/README.md:81-101` ("The gap this epic fills — and how it underpins Epic 41" …
  "So Epic 42 is the **missing foundation under Epic 41**") names Epic 41 as its consumer, `:255-260`
  gives a per-tool-family "Epic 41 consumers" column, and `:375` lists "Epic 41 / 41-29: the
  consumers" — yet this section previously did not name Epic 42 at all, so the edge existed in one
  direction only. Only **six** `IToolExecutor`s are registered (`Tamma.Api/Program.cs:753-764`:
  `FileRead`, `FileWrite`, `SearchCode`, `ShellExecute`, `GitOperations`, `RunTests`) and the registry
  is DI-seeded from exactly that set — all six are coding-oriented. So the **agent** path of the
  non-code stories has no governed tool:

  | Story | Needs (Epic 42) | Reachable today? |
  |---|---|---|
  | **41-5** stakeholder update, **41-7** standup publish | authenticated HTTP / external API (42-9) | no executor |
  | **41-22** incident response, incl. execute-a-response-class & kill-switch | cloud/VPS ops (42-7), feature-flag toggle (42-8) | no executor |
  | **41-23** capacity & health review | health/metric signal reads (42-9) | no executor |
  | **41-24 / 41-25 / 41-26** docs publish | publish capability (42-9) | no executor |
  | **41-28** audit of a *shipped* UI | browser/render capability | no executor |
  | **41-20** dependency/secret/compliance audit | governed audit tooling | degrades to raw `ShellExecute` — possible but **ungoverned and unclassified** |
  | **41-14** tool-enabled exploratory charter | governed exploration tooling | degrades to the six coding tools |

  Until Epic 42 lands these stories are **human-assigned only** (rule 4) for the tool-using half.
  ✅ Each of 41-5, 41-7, 41-14, 41-20, 41-23, 41-24, 41-25, 41-26 and 41-28 now carries that caveat in
  its own **Autonomy behavior** section, so the promise is not read as day-one. (41-22 states it in
  Scope.) Note the honest gradation: 41-23 and 41-28 have **no registered executor at all** for what
  they need; 41-5/41-7/41-24/25/26 have none for *publication* but can draft; 41-20's audit path is
  *possible* today via raw `ShellExecute` — it is **ungoverned and unclassified**, not impossible;
  41-14 degrades to the six coding tools.

  > **One claim in Epic 42's consumer list no longer matches 41-29 — flagged, not silently absorbed
  > (Epic 42's docs are owned elsewhere; do not edit them from here).** `epic-42/README.md:90-91`
  > ("An `infra` task routed to `deployment-pipeline` needs deploy-control and cloud/VPS tools"),
  > `:257-258` (`deployment-pipeline` "infra tasks via 41-29", "41-29 `infra` kind") and `:97` all
  > assume 41-29 routes `infra` → `deployment-pipeline`. **41-29 no longer does, and cannot**:
  > `deployment-pipeline` is the post-merge step-15 promotion and requires a `MergeSha` that does not
  > exist inside the per-task loop (`WaitForPRMergedActivity` is its first writer,
  > `SingleIssueCycleWorkflow.cs:701-708`), so `infra` takes the coding path and 41-29 AC2 adds a
  > standing negative assertion that `deployment-pipeline` is unreachable from the loop. This does
  > **not** weaken Epic 42's case — `deployment-pipeline` still needs 42-7/42-8B for its own
  > post-merge stages, and an `infra` *task* still runs the coding agent, which needs governed
  > IaC/cloud tooling. Only the routing sentence is stale. Reconcile when epic-42 is next revised.
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
  per-window, durable idempotency. **No story owns building that seam** — it is the one Wave-0 enabler
  still without an owner, and the only genuinely unowned dependency left in the epic. *(Corrected: this
  bullet also said "41-17 does not even list the scheduler in its `Blocking:` line". It does now, and so
  do all seven consumers — 41-5, 41-7, 41-11, 41-16, 41-17, 41-20 and 41-23 each name the **seam**, not
  the non-reusable "scheduler pattern", and state which of their ACs is unreachable without it.)*
  It needs a tenant component in the advisory-lock key, a
  `tenantId` threaded into the dispatch, a **persisted** last-fired window (not `_lastFired` in process
  memory), and a window/cron shape rather than a single `FireAtMinute`.
  **Corrected 2026-07-27 — "the one thing in Epic 41 that no story builds" is now built by
  [41-30](./story-41-30/41-30-tenant-aware-scheduled-trigger-seam.md)**, whose D10 answers each of the
  requirement lists 41-5's plan (`:160-176`, *"Design Notes — Part B (BLOCKED; requirements only)"*)
  and 41-20's plan (D8) left behind, item by item. Two things those lists did not say, and 41-30 does:
  an advisory lock is *session*-scoped, so it prevents **concurrent** double-fire but not **sequential**
  double-fire after a pod crash — only a committed ledger row does that (41-30 Correction 3, D2); and
  exactly-once across a process boundary is impossible, so the honest contract is **at-most-once**
  (Correction 4).
- **Reactive triggers have no seam either — [41-32](./story-41-32/41-32-alert-triggered-workflow-response-seam.md).**
  See the boxed note in *Sequencing → Wave 0*. 41-21 and 41-22 both specify a reactive trigger; the
  alert stack can only notify. 41-32 amends the alert stack with an `alert_responses` binding + an
  at-most-once dispatch ledger, and adds no workflow. Note the two seams deliberately share an idiom
  (`INSERT … ON CONFLICT DO NOTHING` as the dedupe answer) and a closed dispatchable-definition
  allowlist, but **no table** — the scheduled key is `(trigger, window)`, the reactive key is
  `(alert, response)`, and the latter needs no clock at all.
- **`MentorshipController.cs:79` dispatches `"tamma-autonomous-mentorship"`, a definition id that
  exists nowhere** (the real one is `mentorship`), and `SingleIssueCycleWorkflow.cs:283,300` dispatch
  `"create-issues"`, which no workflow defines. There is **no definition-id constants file** — ~105
  magic-string sites — which is how both went undetected. Not an Epic 41 deliverable, but it is the
  standing argument for 41-30/41-32's write-time definition-id allowlists, and worth its own fix.
- `Tamma.Core/Agents` taxonomy extension (**41-1a**) + the document-type extension (**41-1b**) — see the
  Corrected note above: together they gate **seventeen** stories on both the agent and the human path,
  not just the agent path of 41-6/41-7/41-8/41-27/41-28.
- **Review-panel selector gap — owned by 41-1a (Scope 3 / D1-D2).** `RolePhaseMap.GetReviewActionForRole`
  (`RolePhaseMap.cs:376-387`) covers 7 of the 8 roles and **throws** for `tech_writer`, and
  `DocumentLifecycleWorkflow.cs:1199` calls it unguarded — so configuring `tech_writer` as a document
  reviewer fails at runtime. 41-24/41-25/41-26 all specify review via `(tech_writer, review-docs)`; the
  cell itself is legal (`AgentAction.cs:117`, `RolePhaseMap.cs:162`) but the selector cannot reach it.
  41-1a adds the `TechWriter` arm, extends `ReviewerSelectionHelper.s_documentRoster` from 7 to 8, and
  decides per new role whether `scrum_master` / `project_manager` / `ux_designer` are on the
  review/triage panels or deliberately off them (pinning the throw either way).

# Epic 41: Full-Team Workflow Coverage — the remaining recurring SDLC activities as lifecycle workflows

## Overview

Epic 39 gave Tamma a **spine**: typed work documents, one produce→validate→review→revise→accept
lifecycle, an orchestrator that routes acceptance by the 70–100 autonomy dial, resumable-by-design
workflows, and a Task View where a suspended decision lands in a tenant role's inbox. Epic 40 made the
coding step durable on that spine.

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
   parse/branch/terminal logic — exactly the 39-12/39-13/39-14 migration pattern. Where the output is
   prose (ADR, postmortem, release notes, changelog, runbook, docs, stakeholder update), it rides the
   lifecycle as a **prose document with an audience tag** (Epic 39: *"prose stays prose"*).
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
   store + DCB events); run-to-completion producers declare `[ResumeBehavior(LatestStateReEntry)]`;
   scheduled workflows use the `HourlyAnalyticsRollupScheduler` cron pattern. All pass the 39-10
   structural test without an allowlist entry.

**Vocabulary is reused, not reinvented.** Where an activity's output fits an existing Epic 39 type
(`Findings`, `Review`, `Design`, `Plan`, `Diagnosis`, `TriageDecision`, `TestSpec`, prose) this epic uses
it. Only a handful of genuinely new types are proposed (Story 41-1) and each is justified against an
existing type it could NOT reuse.

## New roles & the two role families that don't exist yet

The taxonomy (`Tamma.Core/Agents`) models **8 roles**: developer, senior_developer, tester, security,
devops, architect, product_owner, tech_writer. The user's target set names **four the platform has no
role for** — they currently fall back to `product_owner` via `LegacyRoleAliases` (`scrum_master`,
`analyst`) or aren't modelled at all (UX, designer, project_manager). Story **41-1** adds
`scrum_master`, `project_manager`, and `ux_designer` (covering both UX and visual-design work) as first
-class `AgentRole`s with their action cells, plus the new document types the epic needs. **Every other
story in the epic can still ship and run human-assigned before 41-1 lands** (rule 4 — a lower-autonomy
step routes to a human role regardless of whether an agent exists); 41-1 is what unlocks *agent*
execution of those steps at higher autonomy, so it is P0 but not a hard blocker for the human path.

## Coverage matrix

Legend — **✅ covered** (a named workflow owns it) · **◑ partial** (touched inside a larger workflow /
only as a panel lens / prose-in-PR, no first-class workflow) · **✗ missing** (prompt cell only, or no
cell at all). "New story" names the Epic 41 story that closes a ◑/✗.

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
| Defect triage (panel lens) | ◑ | `triage-panel-review` |
| Testability review (panel lens) | ◑ | `review-panel` |
| **Test-plan / strategy authoring** | ✗ | **41-13** (cell `plan-test-strategy`) |
| **Exploratory test charter** | ✗ | **41-14** (cell `exploratory-test`) |
| **Acceptance verification** | ✗ | **41-15** (cell `verify-acceptance`) |
| **Regression & flaky-test management** | ✗ | **41-16** (cell `write-regression-test`) |

### Security

| Activity | Status | Workflow / story |
|---|---|---|
| Vulnerability / security review (panel & triage lens) | ◑ | `triage-panel-review`, `review-panel` |
| Secret rotation | ◑ | `rotate-secret` (ops saga, not a review) |
| **Threat modeling** | ✗ | **41-19** (cell `threat-model`) |
| **Scheduled dependency / secret / compliance audit** | ✗ | **41-20** (cells `audit-dependencies`/`audit-secrets`/`review-compliance`) |
| **Security incident analysis** | ✗ | **41-21** (cell `analyze-security-incident`) |

### DevOps

| Activity | Status | Workflow / story |
|---|---|---|
| Deploy / promotion pipeline | ✅ | `deployment-pipeline` |
| CI configuration | ◑ | `ci-with-debug-retry` (runs CI; doesn't author config) |
| Incident diagnosis (panel lens) | ◑ | `triage-panel-review` |
| **Incident response & postmortem** | ✗ | **41-22** (cells `plan-incident-response`/`write-postmortem`) |
| **Capacity & health review** | ✗ | **41-23** (cells `assess-capacity`/`monitor-health`) |
| Rollback | ✗ | folded into **41-22** (cell `rollback`) |

### Tech Writer

| Activity | Status | Workflow / story |
|---|---|---|
| PR description | ◑ | `pull-request` (inline, not a doc) |
| **Release notes & changelog** | ✗ | **41-24** (cells `write-release-notes`/`update-changelog`) |
| **User & API documentation** | ✗ | **41-25** (cells `write-user-docs`/`write-api-docs`) |
| **Runbook & ops-docs** | ✗ | **41-26** (cell `write-runbook`) |
| Doc review | ◑ | folded into 41-24/41-25/41-26 review stage (cell `review-docs`) |

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
triage, tech-debt triage, regression/flaky triage), and **prose-with-audience-tag** (ADR, postmortem,
release notes, changelog, runbook, user/API docs, stakeholder update).

## Sequencing (highest-leverage first)

**Wave 0 — enabler.** 41-1 (roles + doc types). Human path for every other story can start in parallel;
41-1 gates only the agent path.

**Wave 1 — highest leverage (closes the biggest holes on the critical path).**
- **41-29 Task-Level Flow Router (+ issue-level pre-route)** — *the activation story.* Adds a task `kind`
  to the `Plan` and switches `single-issue-cycle` to dispatch each task to the workflow matching its kind
  (code→TDD, docs→docs, infra→deploy, design→UX, …) plus a lightweight issue-level pre-route for
  `question`/`docs`-only issues. Without it, every issue is forced through the code-writing pipeline and
  the per-role workflows below are unreachable from the issue pipeline. Ships against today's workflows and
  lights up each new kind as its Epic 41 target lands. Depends on 39-15 + the `Plan` schema change.
- **41-2 Acceptance-Criteria Authoring** — feeds `verify-acceptance` (41-15) *and* the merge gate; today
  "done" is undefined outside a plan. Highest single-story leverage.
- **41-15 Acceptance Verification** — closes the loop 41-2 opens; turns "tests pass" into "requirement
  met" at the accept gate.
- **41-17 Standalone Code Review & PR Triage** — code review only exists mentorship-bound; every repo
  needs review-of-a-diff and a routed PR queue as a stand-alone.
- **41-9 ADR Authoring** — cheap, high-value, pure prose-on-lifecycle; proves the prose path for the
  whole tech-writer/devops family behind it.

**Wave 2 — recurring, event-sourced, scheduled (compounding value).**
- **41-7 Standup Synthesis**, **41-16 Regression & Flaky-Test Management**, **41-11 Tech-Debt & Risk
  Triage**, **41-20 Scheduled Security Audit**, **41-23 Capacity & Health Review** — all read the DCB
  stream / CI history on a cron and produce a `Findings`/`TriageDecision`; each replaces a standing human
  chore. 41-24 Release Notes & 41-25 User/API Docs are release/merge-triggered siblings.

**Wave 3 — planning & design depth.**
- 41-3 Backlog Prioritization, 41-6 Sprint Planning, 41-4 Roadmap, 41-8 Retro, 41-5 Stakeholder Update,
  41-10 System Design Doc, 41-18 Refactor Planning, 41-19 Threat Modeling, 41-21 Security Incident,
  41-22 Incident & Postmortem, 41-12 Dependency & Upgrade, 41-13 Test-Plan, 41-14 Exploratory Charter,
  41-26 Runbook.

**Wave 4 — new surface (UX/design; depends on 41-1's `ux_designer` role + `UxSpec` type).**
- 41-27 User-Flow & Wireframe Drafting, 41-28 Design Review & Accessibility Audit.

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

- **Epic 39** (all of it): document core/types/registry, `DocumentLifecycleWorkflow`, review producers,
  acceptance rules + orchestrator routing, escalation surface, resume standard + structural test,
  document store & lineage API, teams/roles/repo-access & task routing, orchestrator chat + Task View.
- **Epic 40** for any workflow whose accept leads into a coding execution (41-18 refactor plan → coding
  step reuses the durable runner).
- `HourlyAnalyticsRollupScheduler` cron pattern for the scheduled workflows (41-7/41-11/41-16/41-20/41-23).
- `Tamma.Core/Agents` taxonomy extension (41-1) for the agent path of 41-6/41-7/41-8/41-27/41-28.

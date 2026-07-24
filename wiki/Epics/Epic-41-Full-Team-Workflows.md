# Epic 41: Full-Team Workflow Coverage

**Status:** Planned / docs — briefs authored, no code yet. Turns every remaining recurring SDLC activity into a lifecycle workflow on the Epic 39 spine.
**Stories:** 28 (41-1 through 41-28) + 41-29 the task-level flow router, all drafted
**Layer:** Layer 4 (integration/orchestration)
**Depends on:** Epic 39 (all of it), Epic 40 (for workflows whose accept leads into a coding execution), the `HourlyAnalyticsRollupScheduler` cron pattern, the `Tamma.Core/Agents` taxonomy extension (41-1)

> This epic is **backlog** — scoped and specified, not built. It adds **no new architecture**; every workflow is a thin binding over the Epic 39 `document-lifecycle`.

## 1. Overview

Epic 39 gave Tamma a **spine**: typed work documents, one produce→validate→review→revise→accept lifecycle, an orchestrator that routes acceptance by the 70–100 autonomy dial, resumable-by-design workflows, and a Task View where a suspended decision lands in a tenant role's inbox. Epic 40 made the coding step durable on that spine.

But the platform only *runs* that spine for a narrow slice of what a software team does day to day: the **issue → decompose → plan → tasks → TDD → PR → review → merge → deploy** happy path, plus intake (triage/clarify/research/ambiguity) and mentorship. Tamma's stated intent is broader — *automate the entire software-development process*: every recurring activity an **engineer, UX, designer, product owner, project manager, scrum master, architect, or tester** performs should eventually be a Tamma workflow.

This epic closes that gap. It takes the activities that today exist only as a **prompt cell** (an `(role, action)` template dispatchable through `llm-call`, but with no owning workflow that saves documents, rides the lifecycle, or routes acceptance) — or don't exist at all (the UX/designer/PM/scrum role families) — and turns each into a **first-class lifecycle workflow** on the Epic 39 substrate.

### The five rules (no new architecture)

1. **Thin binding over `document-lifecycle`.** Each producing workflow declares `consumes: [...]` / `produces: <DocumentType>`, binds one `(role, action)` produce cell, and contributes no bespoke parse/branch/terminal logic — exactly the 39-12/39-13/39-14 migration pattern. Prose outputs (ADR, postmortem, release notes, changelog, runbook, docs, stakeholder update) ride the lifecycle as a **prose document with an audience tag**.
2. **DCB events.** Every transition emits the generic `DOCUMENT.*` family plus a domain-specific family (`ADR.*`, `SPRINT.*`, `THREATMODEL.*`, …).
3. **Orchestrator-routed acceptance, autonomy-gated.** The accept gate always publishes an `AcceptanceRequest` and suspends (39-8); the orchestrator reads the rules + the autonomy level (per-document-type overridable) and decides WHO decides.
4. **Human-or-agent execution.** The produce/review step is written so **either** fulfils it: at lower autonomy it is assigned to a human holder of the appropriate tenant role; at higher autonomy the appropriate `AgentRole` performs the `llm-call`.
5. **Resumable by design.** Interactive workflows declare `[ResumeBehavior(Both)]`; run-to-completion producers declare `[ResumeBehavior(LatestStateReEntry)]`; scheduled workflows use the `HourlyAnalyticsRollupScheduler` cron pattern. All pass the 39-10 structural test without an allowlist entry.

**Vocabulary is reused, not reinvented.** Where an activity's output fits an existing Epic 39 type (`Findings`, `Review`, `Design`, `Plan`, `Diagnosis`, `TriageDecision`, `TestSpec`, prose) this epic uses it. Only a handful of genuinely new types are proposed (Story 41-1), each justified against an existing type it could NOT reuse.

## 2. New roles & role families (Story 41-1)

The taxonomy (`Tamma.Core/Agents`) models **8 roles**: developer, senior_developer, tester, security, devops, architect, product_owner, tech_writer. The target set names **four the platform has no role for** — today they fall back to `product_owner` via `LegacyRoleAliases` (`scrum_master`, `analyst`) or aren't modelled at all (UX, designer, project_manager). Story **41-1** adds `scrum_master`, `project_manager`, and `ux_designer` (covering both UX and visual-design work) as first-class `AgentRole`s with their action cells, plus the new document types the epic needs.

**Every other story can still ship and run human-assigned before 41-1 lands** (rule 4 — a lower-autonomy step routes to a human role regardless of whether an agent exists); 41-1 is what unlocks *agent* execution of those steps at higher autonomy, so it is P0 but not a hard blocker for the human path.

### New document types (41-1)

Reuse first; a new type is proposed only when no existing Epic 39 type carries the domain rules.

| New type | Why not an existing type | Domain rules beyond schema |
|---|---|---|
| `AcceptanceCriteria` | Not a `Clarification` nor a `Plan`; it is the testable definition-of-done consumed by 41-15 and the merge gate | each criterion independently verifiable; Given/When/Then or checklist; bound to `issueId` |
| `BacklogOrdering` | A `TriageDecision` classifies one item; this **ranks a set** with rationale | total order over the item set; every item has a rationale + value/effort estimate; no ties |
| `SprintPlan` | A `Plan` maps tasks-to-files for one issue; a sprint commits a **capacity-bounded set of issues** | committed set ≤ stated capacity; every committed item has an owner-role + estimate; carry-over flagged |
| `TestPlan` | A `TestSpec` is executable cases; a test plan is the **strategy** above them | risk areas ranked; each strategy line maps to a coverage target; entry/exit criteria stated |
| `ThreatModel` | `Findings` carry no attack structure | STRIDE (or configured) categorisation; each threat has asset + mitigation + residual-risk |
| `UxSpec` | A `Design` weighs technical alternatives; a UX spec captures **flows/states/acceptance** | every flow has entry + success + error states; each screen lists a11y requirements |

Everything else reuses `Findings`, `Review`, `Diagnosis`, `Plan`, `TriageDecision`, and prose-with-audience-tag.

## 3. Coverage matrix

Legend — **covered** (a named workflow owns it) · **partial** (touched inside a larger workflow / only a panel lens / prose-in-PR) · **missing** (prompt cell only, or no cell at all).

| Role family | Already covered | New Epic-41 stories (closing partial/missing) |
|---|---|---|
| Engineer (developer / senior_developer) | TDD, tests, debug, review-fix, decomposition, planning, blocker/mentorship | **41-17** standalone code review + PR triage · **41-18** refactor planning |
| Architect | design-proposal, plan-review | **41-9** ADR authoring · **41-10** system design doc · **41-11** tech-debt & risk triage · **41-12** dependency & upgrade planning |
| Product Owner | triage, clarify, research, ambiguity, assessment | **41-2** acceptance-criteria authoring · **41-3** backlog prioritization · **41-4** roadmap shaping · **41-5** stakeholder update |
| Project Manager & Scrum Master (→ 41-1) | (none — no role today) | **41-6** sprint planning · **41-7** standup synthesis · **41-8** retrospective |
| Tester | test-case authoring, testing pipeline | **41-13** test-plan/strategy · **41-14** exploratory charter · **41-15** acceptance verification · **41-16** regression & flaky-test mgmt |
| Security | vuln/security review (panel lens), secret rotation | **41-19** threat modeling · **41-20** scheduled dependency/secret/compliance audit · **41-21** security incident analysis |
| DevOps | deployment pipeline, CI runs | **41-22** incident response & postmortem (incl. rollback) · **41-23** capacity & health review |
| Tech Writer | PR description (inline) | **41-24** release notes & changelog · **41-25** user & API docs · **41-26** runbook & ops-docs |
| UX / Designer (→ 41-1) | (none — no role today) | **41-27** user-flow & wireframe drafting · **41-28** design review & accessibility audit |

## 4. Story 41-29 — the Task-Level Flow Router (the activation story)

**41-29** is *the activation story* for the whole epic. It adds a task `kind` to the `Plan` and switches `single-issue-cycle` to dispatch each task to the workflow matching its kind (code→TDD, docs→docs, infra→deploy, design→UX, …) plus a lightweight issue-level pre-route for `question`/`docs`-only issues. Without it, every issue is forced through the code-writing pipeline and the per-role workflows above are unreachable from the issue pipeline. It ships against today's workflows and lights up each new kind as its Epic 41 target lands. Depends on 39-15 + the `Plan` schema change.

## 5. Sequencing (highest-leverage first)

- **Wave 0 — enabler.** 41-1 (roles + doc types). Human path for every other story can start in parallel; 41-1 gates only the agent path.
- **Wave 1 — highest leverage.** 41-29 (task-level flow router), 41-2 (acceptance-criteria authoring), 41-15 (acceptance verification), 41-17 (standalone code review & PR triage), 41-9 (ADR authoring — proves the prose path).
- **Wave 2 — recurring, event-sourced, scheduled.** 41-7 standup synthesis, 41-16 regression/flaky mgmt, 41-11 tech-debt & risk triage, 41-20 scheduled security audit, 41-23 capacity & health; 41-24 release notes + 41-25 user/API docs are release/merge-triggered siblings.
- **Wave 3 — planning & design depth.** 41-3, 41-6, 41-4, 41-8, 41-5, 41-10, 41-18, 41-19, 41-21, 41-22, 41-12, 41-13, 41-14, 41-26.
- **Wave 4 — new surface (UX/design).** 41-27 user-flow & wireframe drafting, 41-28 design review & accessibility audit (depend on 41-1's `ux_designer` role + `UxSpec` type).

## 6. Deliberately out of scope

- **Live human ceremonies as real-time events** (the actual standup/retro *meeting*, sprint *review demo*). Tamma automates the **artifact** and routes it, not the synchronous conversation.
- **Pixel-level visual design production** (actual mockup rendering in a design tool). 41-27 produces a structured `UxSpec` + flow/state description; rendering pixels awaits a design-tool provider abstraction.
- **Hiring, budgeting, vendor/contract management, people-management 1:1s** — team-operations, outside Tamma's SDLC charter.
- **Final production-deploy authorization for regulated/breaking changes** — stays a human decision by acceptance-rules policy (always-escalate class), not a new workflow.

## 7. Dependencies

- **Epic 39** (all of it): document core/types/registry, `DocumentLifecycleWorkflow`, review producers, acceptance rules + orchestrator routing, escalation surface, resume standard + structural test, document store & lineage API, teams/roles/repo-access & task routing, orchestrator chat + Task View.
- **Epic 40** for any workflow whose accept leads into a coding execution (41-18 refactor plan → coding step reuses the durable runner).
- **Epic 42** supplies the governed tools the non-code kinds (`docs`/`infra`/`design`) need — 41-29 routes to those workflows, but their *agent* path lights up only as Epic 42's tool families land.
- `HourlyAnalyticsRollupScheduler` cron pattern for the scheduled workflows (41-7/41-11/41-16/41-20/41-23).
- `Tamma.Core/Agents` taxonomy extension (41-1) for the agent path of 41-6/41-7/41-8/41-27/41-28.

## 8. See also

- [Document Lifecycle](Document-Lifecycle) — Epic 39, the spine every workflow here binds to
- [Resumable Workflows](Resumable-Workflows) — the 39-10 standard these workflows declare
- [Epic 39: Document Lifecycle](Epics/Epic-39-Document-Lifecycle) — the substrate
- [Epic 40: Resumable Coding](Epics/Epic-40-Resumable-Coding) — the durable coding runner reused by refactor flows
- [Epic 42: Tool Layer](Epics/Epic-42-Tool-Layer) — the tool catalog the non-code agent paths depend on
- [Epic 26: Project Management & Triage](Epics/Epic-26-Project-Management) — the triage/scrum family this epic extends onto the lifecycle
- [Role/Action Taxonomy](Role-Action-Taxonomy) — the roles (incl. the three new ones) and action cells
- Story files: [Epic 41 on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-41)

---

_Last updated: 2026-07-24_

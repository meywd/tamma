# Story 41-1: Team-Role & Document-Type Extensions — enabler set (SPLIT)

Status: superseded — split into 41-1a / 41-1b / 41-1c. This file is the split index + the
cross-story gate table; it is not itself implementable. (Matches `docs/sprint-status.yaml`, and the
shape Epic 42 used for its 42-8 split.)

## User Story

As the **Epic 41 program**, I want the agent taxonomy, the Epic 39 document registry, and prose-document
support extended with the roles, cells and typed documents the remaining team activities need, so that
every Epic 41 workflow can bind a real `(role, action)` cell and produce a persistable document — on the
human-assigned path *and* the agent path.

## Priority

**P0 — the epic's hard gate.** Its taxonomy and document-type halves block **seventeen** stories on BOTH
execution paths (fifteen at their produce step — 41-5 joined when its cell moved to
`(project_manager, report-status)` — plus 41-24/41-25 at their review stage; 41-26 left the review set,
its default reviewer being the already-reachable `(devops, review-operability)`); its prose half blocks
**eight**. Five are in both sets — **twenty of twenty-nine** in all.

> **Corrected — the previous Priority paragraph contradicted itself in consecutive sentences.** It read
> "Gates the agent path of … and the typed outputs of …" and then "Not a hard blocker for the human path
> of any story." Both cannot be true: a *typed output* is unreachable by a human-assigned run too.
> `DocumentTypeKeyExtensions.Parse` throws `DOCUMENT.TYPE.UNKNOWN` for any non-vocabulary wire string
> (`DocumentTypeKey.cs:49-59`), `DocumentTypeRegistry.Resolve` throws `DOCUMENT.TYPE.NOT_REGISTERED`
> (`DocumentTypeRegistry.cs:85-91`), and `DocumentInstance.DocumentType` (`DocumentInstance.cs:34`) is a
> `DocumentTypeKey` wire string — none of that consults *who* executed the produce step. The same holds
> for a missing `(role, action)` cell: a human assignee still needs a cell to bind. The "human path is
> unblocked" claim is deleted, not softened.
>
> What survives: for a story whose type, role and cell already exist, rule 4 lets the produce step run
> human-assigned at low autonomy without an agent. That is a narrower claim about *those* stories.

## Why this is split

The single story bundled four independently-shippable deliverables — three new roles, fifteen new action
cells, six new document types, and the prose/audience mechanism — behind one 5–7 day estimate. The landed
Epic 39 precedent sizes just *one* of those slices at more than that: **39-3** shipped four document types
(4–5 days) and **39-4** shipped six (5–6 days), each as its own story. The prose mechanism is a schema +
migration + vocabulary change that no story owned at all.

| Sub-story | Deliverable | Effort |
|---|---|---|
| **41-1a** — [Agent-Taxonomy Extension](./41-1a-agent-taxonomy-extension.md) | 3 roles, 15 action tokens (18 cells incl. per-role `context-scan`, plus the 41-8 lockstep `write-retro-narrative` amendment), the DERIVED panel-selector maps, the `scrum_master` alias removal | 4–5 days |
| **41-1b** — [New Document Types](./41-1b-new-document-types.md) | `AcceptanceCriteria`, `BacklogOrdering`, `SprintPlan`, `TestPlan`, `ThreatModel`, `UxSpec` | 5–6 days |
| **41-1c** — [Prose Documents & Audience Tags](./41-1c-prose-documents-and-audience-tags.md) | the prose type + `Audience` field + audience/kind vocabularies | 3–4 days |

41-1a and 41-1b are independent and can run in parallel. 41-1c is independent of both.

> **Corrected — 41-1c is new scope, not an extension.** The old Scope item 4 read "Prose-document audience
> tags **extended** for the new prose outputs", presupposing a mechanism that exists. It does not exist:
> `DocumentTypeKey.cs:22-33` has exactly ten members and no prose member, and neither
> `DocumentInstance.cs:23-89` nor `DocumentEnvelope.cs` carries an `Audience` member. Epic 39 states
> *"prose stays prose"* only as a **principle** (`epic-39/README.md:115-116`), and 39-1:58 records
> prose/tech-writer output as explicitly **out of scope** of the 10-type table. Eight Epic 41 stories
> write as though the mechanism shipped. 41-1c builds it.

## What each sub-story gates

| Downstream story | Waits on | For |
|---|---|---|
| 41-2 | 41-1b | `AcceptanceCriteria` type |
| 41-3 | 41-1b | `BacklogOrdering` type |
| 41-6 | 41-1a + 41-1b | `scrum_master` role, `SprintPlan` type |
| 41-5 | 41-1a | `project_manager` role + `report-status` cell — `(product_owner, summarize-stakeholder)` is live-bound to `ContextGatheringWorkflow` and cannot be a producer |
| 41-7, 41-8 (Phase A) | 41-1a | `scrum_master` role + `synthesize-standup` / `facilitate-retro` cells; 41-8 Phase B additionally waits on the 41-1a amendment minting `(scrum_master, write-retro-narrative)` |
| 41-10 | 41-1a | `design-system` cell — `plan-system-design` is reserved as plan-generation's `Plan` producer |
| 41-11 | 41-1a | `triage-tech-debt` cell |
| 41-13 | 41-1b | `TestPlan` type |
| 41-16 | 41-1a | `manage-regression` cell |
| 41-17 (PR-triage half) | 41-1a | `triage-pr` cell |
| 41-19 | 41-1b | `ThreatModel` type |
| 41-22 | 41-1a | `incident-rootcause` cell — `diagnose-incident` is the triage-panel lens, not bindable |
| 41-27 | 41-1a + 41-1b | `ux_designer` role, `UxSpec` type |
| 41-28 | 41-1a | `ux_designer` role + `review-design` / `audit-accessibility` cells |
| 41-4, 41-5, 41-9, 41-22, 41-24, 41-25, 41-26 | **41-1c** | prose type + audience tag |
| 41-8 (Phase B) | **41-1c** | audience tag on its retro narrative (its `Findings` half needs only 41-1a) |
| 41-24, 41-25 | 41-1a | the `(tech_writer, review-docs)` **review-selector** arm — see 41-1a AC3. *41-26 no longer waits here: its default reviewer is `(devops, review-operability)`, reachable today; the tech-writer review is its upgrade path.* |

**20 of the epic's 29 original workflow stories wait on some part of this set** — seventeen on the
taxonomy/document-type halves (41-1a + 41-1b; fifteen at their produce step plus 41-24/41-25 at their
review stage) and eight on the prose half (41-1c). 41-5, 41-8, 41-22, 41-24 and 41-25 are in both:
17 + 8 − 5 = 20.

*Corrected: this table and the counts above previously read twelve / nineteen, omitting 41-10 and 41-22,
both of which name 41-1a as the minter of a cell that does not exist in `AgentAction.cs`. 41-1a's Scope
item 2 has been widened from thirteen cells to fifteen to match. The taxonomy-half count also omitted
41-24/41-25/41-26, which the last row of this table has always listed.*

## Downstream references to reconcile (owned by other files)

The split gives the prose enabler an owner for the first time; the documents that assumed it was somebody
else's job still pointed elsewhere. Status of that loop after the 2026-07-24 audit pass:

- ✅ **epic-41 README, Wave-0 table** — the "Prose document support / **none — must be written**" row now
  names **41-1c**, and the 41-1 row is split into the three sub-stories with their efforts. The
  scheduler seam has since gained an owner too (41-30, 2026-07-27).
- ✅ **41-17** — its `Blocking:` line now names **41-1a** (`triage-pr`) and the scheduler seam (41-30).
- ✅ **41-4, 41-5, 41-8, 41-9, 41-22, 41-24, 41-25, 41-26** — their `Blocking:` lines named "Epic 39
  (prose-document handling …)" for a deliverable 39-1:58 records as out of Epic 39's scope; they now
  name **41-1c**. 41-8 was missing from that list and has been added.
- ✅ **`docs/sprint-status.yaml`** — tracked 41-1 as a single story; it now carries 41-1a/41-1b/41-1c
  rows with 41-1 marked `superseded`.
- ✅ **Resolved (2026-07-27): the tenant-aware scheduled-trigger seam is owned by 41-30.** It gates the
  five audit stories (41-11, 41-16, 41-17 PR-sweep, 41-20, 41-23) at their cadence AC only. Per the
  product owner's 2026-07-25 decision, 41-5 and 41-7 are user-initiated ceremonies and do **not** wait
  on it.

## Dependencies

- **Blocking:** Epic 39 (39-2 registry, 39-3/39-4 type pattern, 39-11 store, 39-16 contract generation),
  27-15/27-18 taxonomy machinery.
- **Unblocks:** see the table above.

## Estimated Effort

12–15 days across the three sub-stories (was: 5–7 days for all four deliverables at once).

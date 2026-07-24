# Story 41-1: Team-Role & Document-Type Extensions — enabler set (SPLIT)

Status: drafted (split into 41-1a / 41-1b / 41-1c)

## User Story

As the **Epic 41 program**, I want the agent taxonomy, the Epic 39 document registry, and prose-document
support extended with the roles, cells and typed documents the remaining team activities need, so that
every Epic 41 workflow can bind a real `(role, action)` cell and produce a persistable document — on the
human-assigned path *and* the agent path.

## Priority

**P0 — the epic's hard gate.** Its taxonomy and document-type halves block **twelve** stories on BOTH
execution paths; its prose half blocks **eight** more (41-8 in both) — nineteen of twenty-nine in all.

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

The single story bundled four independently-shippable deliverables — three new roles, thirteen new action
cells, six new document types, and the prose/audience mechanism — behind one 5–7 day estimate. The landed
Epic 39 precedent sizes just *one* of those slices at more than that: **39-3** shipped four document types
(4–5 days) and **39-4** shipped six (5–6 days), each as its own story. The prose mechanism is a schema +
migration + vocabulary change that no story owned at all.

| Sub-story | Deliverable | Effort |
|---|---|---|
| **41-1a** — [Agent-Taxonomy Extension](./41-1a-agent-taxonomy-extension.md) | 3 roles, 13 action cells, the DERIVED panel-selector maps, the `scrum_master` alias removal | 4–5 days |
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
| 41-7, 41-8 | 41-1a | `scrum_master` role + `synthesize-standup` / `facilitate-retro` cells |
| 41-11 | 41-1a | `triage-tech-debt` cell |
| 41-13 | 41-1b | `TestPlan` type |
| 41-16 | 41-1a | `manage-regression` cell |
| 41-17 (PR-triage half) | 41-1a | `triage-pr` cell — **41-17's own `Blocking:` line omits this** |
| 41-19 | 41-1b | `ThreatModel` type |
| 41-27 | 41-1a + 41-1b | `ux_designer` role, `UxSpec` type |
| 41-28 | 41-1a | `ux_designer` role + `review-design` / `audit-accessibility` cells |
| 41-4, 41-5, 41-9, 41-22, 41-24, 41-25, 41-26 | **41-1c** | prose type + audience tag |
| 41-8 | **41-1c** | audience tag on its retro narrative (its `Findings` half needs only 41-1a) |
| 41-24, 41-25, 41-26 | 41-1a | the `(tech_writer, review-docs)` **review-selector** arm — see 41-1a AC3 |

**19 of the epic's 29 stories wait on some part of this set** — twelve on the taxonomy/document-type
halves (41-1a + 41-1b) and eight on the prose half (41-1c), with 41-8 in both.

## Downstream references to reconcile (owned by other files)

The split gives the prose enabler an owner for the first time; the documents that assumed it was somebody
else's job still point elsewhere. Three edits outside this folder close that loop:

- **41-4, 41-5, 41-9, 41-22, 41-24, 41-25, 41-26** — their `Blocking:` lines name "Epic 39 (prose-document
  handling …)" for a deliverable 39-1:58 records as out of Epic 39's scope. They should name **41-1c**.
  41-8 needs the audience tag too and is missing from that list wherever it is enumerated.
- **41-17** — its `Blocking:` line omits 41-1 entirely although its PR-triage half produces on
  `(senior_developer, triage-pr)`, a cell that does not exist. It should name **41-1a**.
- **epic-41 README, Wave-0 table** — the "Prose document support / **none — must be written**" row now has
  an owner: **41-1c**. The 41-1 row should point at the three sub-stories and their 12–15 day total.

## Dependencies

- **Blocking:** Epic 39 (39-2 registry, 39-3/39-4 type pattern, 39-11 store, 39-16 contract generation),
  27-15/27-18 taxonomy machinery.
- **Unblocks:** see the table above.

## Estimated Effort

12–15 days across the three sub-stories (was: 5–7 days for all four deliverables at once).

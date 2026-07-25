# Story 44-9: Dogfood — `docs/sprint-status.yaml` Becomes Generated From the Tracker

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

As the **Tamma team**,
I want Tamma's own 45 epics and ~900 stories tracked in Tamma's tracker, with `docs/sprint-status.yaml` generated from it rather than hand-maintained,
So that the platform's stated self-maintenance goal is demonstrated on the one workload we can verify — and so that the status file stops drifting from the code it describes.

## Priority

P3 — last. It is the epic's proof, not its foundation. **If the product owner answers Open question 2 with "customers first", this story drops and its 4 days move to 44-6.**

## Architectural Context (READ FIRST)

- **The current tracker is a 689-line YAML file plus 914 markdown files across 45 epic directories.** `docs/sprint-status.yaml` declares `tracking_system: file-system` and `story_location: "{project-root}/docs/stories"` (`:44-45`). Story statuses are `backlog | drafted | ready-for-dev | in-progress | review | done | superseded` (`:19-25`); epic statuses are `backlog | contexted` (`:14-15`).
- **Nothing programmatic reads it.** A grep for `sprint-status` / `sprint_status` / `sprintStatus` across `apps/` and `packages/` `.cs`/`.ts`/`.tsx` returns **zero** hits. Its readers are humans, agents, and a docs pipeline.
- **It has already forked.** `docs/stories/epic-40/sprint-status.yaml` is a second, epic-scoped copy with its own header and its own corrections — the failure mode a file-system tracker has by construction.
- **It is visibly drifting, by its own admission.** The header carries `last_reconciled_against_code: 2026-07-24` (`:4`) and per-line corrections such as 39-16's *"CORRECTED: the 2026-07-24 reconciliation brief listed 39-16 among the merged spine; the code refutes it"* (`:545`) and 39-18's *"MOSTLY LANDED — CORRECTED: the brief listed 39-18 as unstarted; the code refutes it"* (`:547`). Reconciliation is a recurring manual audit.
- **`docs/stories/` is published.** `apps/wiki-site/scripts/sync-content.ts:273-280` syncs `docs/stories/` into the Starlight site behind wiki.tamma.dev, rewriting GitHub tree links to site paths (`:138`). **The markdown files are the public narrative artifact and must not be deleted or relocated by this story.**
- **The tracker's vocabularies do not match the file's.** `WorkItemStatus` (44-0) is `backlog | ready | in_progress | in_review | blocked | done | cancelled`; the file's is `backlog | drafted | ready-for-dev | in-progress | review | done | superseded`. Four map cleanly; `drafted`, `ready-for-dev` and `superseded` do not.
- **Single-user mode is the operating mode here.** `TammaModeProvider.Resolve` (`Tamma.Api/Services/PromptStore/TammaMode.cs:67`) resolves `SingleUser` absent SaaS config; the personal tenant is auto-provisioned by `EnsurePersonalTenantMiddleware.cs:176`. The dogfood instance is therefore the single-user ownership model of epic README §6.
- **Bulk creation must use `CreateManyAsync`** (44-1 D6) — ~900 items through a per-row `FOR UPDATE` lock is the pathology 44-1's Risks flagged, and 44-8 already made the same call for import.

## Acceptance Criteria

1. **A status-vocabulary mapping is decided and documented**, not invented per row:
   - `backlog → backlog`, `in-progress → in_progress`, `review → in_review`, `done → done`
   - `drafted` and `ready-for-dev` → `ready`, distinguished by a `readiness` label rather than by a status (the tracker's status set stays the fixed seven of 44-0 D3; adding two members to serve one importer is the customizable-status-set decision the epic deferred, epic D11)
   - `superseded → cancelled`, with the superseding reference preserved in the description
   The mapping lives in one file and is unit-tested table-wise.

2. **An importer** — a `dotnet` tool or an admin endpoint, decided in the plan — reads `docs/sprint-status.yaml` and the `docs/stories/**` tree and creates: one `Project` per top-level grouping, one `Epic` work item per `epic-N`, one `Story` work item per story, parented correctly, with `Rank` following file order, `Status` per AC1, and a link to the story's markdown path.

3. **The markdown files stay exactly where they are.** No file under `docs/stories/` is moved, renamed or deleted. `apps/wiki-site/scripts/sync-content.ts` continues to publish them unchanged, and a test asserts the sync script's input tree is byte-identical after an import.

4. **The import is idempotent and re-runnable.** Re-running matches on the story slug and updates status and rank only; it never duplicates an item and never overwrites a title or description edited in the tracker. A test runs the import twice over the real tree and asserts a stable item count.

5. **`docs/sprint-status.yaml` becomes generated.** A generator emits the file from the tracker in the existing format — same key order, same status vocabulary, same per-line trailing comments (carried through from the work item's description) — so the diff on first generation against a synced tracker is **empty or trivially explainable**. AC5 is met when that diff is demonstrated in the PR.

6. **The generated file is verified in CI.** A workflow step regenerates it and fails if the working tree differs — the "the generator runs in CI, tree clean" guarantee `ConventionSeedDriftTests.cs:9-25` already articulates for the no-codegen repo. This is what makes it *generated* rather than *occasionally regenerated*.

7. **The second copy is reconciled.** `docs/stories/epic-40/sprint-status.yaml` is either folded into the generated output or deleted with its content preserved in the tracker. A test asserts no second `sprint-status.yaml` exists under `docs/`.

8. **The import's audit trail is the verification.** After a full import, `GET /api/work-items/{key}/timeline` (44-5) shows `WORKITEM.CREATED.SUCCESS` for each item, and the import emits one summary event with counts. A drift check compares the tracker's item count against the file tree's story count and reports discrepancies by name.

9. **A findings note records what the migration revealed.** The reconciliation is guaranteed to surface stories whose declared status disagrees with the tree (the file already documents several). Those discrepancies are the deliverable, not a nuisance: they are written up rather than silently normalised.

10. **`tracking_system` is updated.** The generated file's header declares `tracking_system: tamma-tracker` with the project key, so a reader knows the file is an export.

## Technical Notes

- The direction matters: the tracker becomes the source of truth for **status and ordering**; the markdown files remain the source of truth for **narrative** (ACs, technical context, implementation plans). Neither replaces the other, and the work item links to the file rather than embedding it.
- Generating the file rather than deleting it preserves every current reader — humans, agents following `BEFORE_YOU_CODE.md`, and the wiki pipeline — while removing the hand-maintenance that causes the drift.
- The per-line trailing comments in `sprint-status.yaml` are load-bearing prose (they carry the code-reconciliation evidence). Round-tripping them through the work item's description is the hard part of AC5 and the main reason this story is 4 days rather than 2.
- Epic status (`backlog | contexted`) is not a `WorkItemStatus`. It maps to a field on the Epic work item, not to a status transition.

## Dependencies

- **Stories 44-1** (`CreateManyAsync`), **44-2** (API), **44-3** (hierarchy + rank), **44-5** (events + timeline) — blocking.
- **Story 44-4** (iterations) — optional; the import assigns none.
- **Story 44-7** — not required, but a dogfood instance with native intake enabled is the fuller demonstration.
- **Existing, no change required:** `apps/wiki-site/scripts/sync-content.ts`, the `docs/stories/` tree.

## Out of Scope

- Deleting, moving or restructuring any file under `docs/stories/`. AC3 is a hard constraint.
- Migrating story *content* into the tracker. Work items link to markdown; they do not embed it.
- Making the wiki site read from the tracker API. It reads the markdown tree, unchanged.
- A general-purpose "import any YAML tracker" feature. This is one importer for one file format that this repo owns.
- Adding `drafted` / `ready-for-dev` / `superseded` to `WorkItemStatus`. Epic Decisions D11; AC1's label mapping is the answer.

## Estimated Effort

4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

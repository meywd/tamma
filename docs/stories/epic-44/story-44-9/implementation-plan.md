# Implementation Plan — Story 44-9: Dogfood — `docs/sprint-status.yaml` Becomes Generated From the Tracker

## Scope & Deliverable

When this story is done Tamma's own 45 epics and ~900 stories live in Tamma's tracker; `docs/sprint-status.yaml` is emitted from it in its existing format, verified clean by CI; the second forked copy under `docs/stories/epic-40/` is gone; and every markdown file under `docs/stories/` is byte-unchanged so wiki.tamma.dev keeps publishing exactly what it publishes today. The reconciliation discrepancies the import surfaces are written up as a findings note rather than silently normalised — they are the point of the exercise.

## Pre-Reading

- `docs/stories/epic-44/README.md` — Overview (the three places work is planned), §6 (single-user ownership), Open question 2 (**which may cancel this story**)
- `docs/sprint-status.yaml` **in full** — the header (`:1-10`), the status definitions (`:14-25`), the workflow notes (`:27-33`), and the per-line comment style throughout; note `last_reconciled_against_code` at `:4` and the explicit `CORRECTED:` lines at `:545`, `:547`
- `docs/stories/epic-40/sprint-status.yaml` — the fork, and its own corrective header
- `apps/wiki-site/scripts/sync-content.ts:138,273-280` — what publishes `docs/stories/` and how it rewrites links
- `docs/stories/README.md` — the human-facing index this story does not replace
- `docs/stories/epic-44/story-44-1/implementation-plan.md` — D6 (`CreateManyAsync` block allocation; the per-row-lock pathology)
- `docs/stories/epic-44/story-44-0/implementation-plan.md` — D3 (the seven statuses are fixed) and the epic's D11
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Conventions/ConventionSeedDriftTests.cs:9-25` — the "generator runs in CI, tree clean" guarantee this story copies
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs:67` + `Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs:176` — the single-user/personal-tenant context the dogfood runs in
- **All referenced paths exist.** NOT FOUND (this story creates them): `apps/tamma-elsa/tools/Tamma.TrackerImport/`, `docs/stories/epic-44/status-vocabulary-mapping.md`.

## Design Decisions

- **D1 — A standalone `dotnet` tool, not an admin endpoint.** The import reads the repository working tree, which an API process does not have and should not. `apps/tamma-elsa/tools/Tamma.TrackerImport/` — a console project that speaks the same HTTP API as any other client (44-2), so it exercises the real surface rather than a privileged back door, and so its output is auditable through the same events. It is also what CI runs for AC6's generate-and-diff check.

- **D2 — The status mapping is a table in one file, and `WorkItemStatus` gains nothing.** The file's seven story statuses do not match the tracker's seven, and three have no counterpart:
  | File | Tracker | Carried as |
  |---|---|---|
  | `backlog` | `backlog` | — |
  | `drafted` | `ready` | label `readiness:drafted` |
  | `ready-for-dev` | `ready` | label `readiness:ready-for-dev` |
  | `in-progress` | `in_progress` | — |
  | `review` | `in_review` | — |
  | `done` | `done` | — |
  | `superseded` | `cancelled` | description prefix `Superseded by …` |
  Adding `drafted` / `ready-for-dev` / `superseded` as enum members to serve one importer is the customizable-status-set decision the epic deferred (D11), and it would move two count pins and a CHECK constraint for a workload of one. Labels carry the distinction losslessly and the generator reverses it exactly (D4).

- **D3 — Epic status is a field, not a status.** `backlog | contexted` (`:14-15`) describes whether an epic has a tech context, which is orthogonal to work state. It becomes a labelled attribute on the Epic work item. Mapping it onto `WorkItemStatus` would make every `contexted` epic indistinguishable from a `ready` story.

- **D4 — Generation is lossless because the *file* is the schema, and the trailing comments are data.** The hardest part of AC5 is not the YAML — it is that ~200 of the file's lines carry substantive trailing prose (`# NOT landed. No /chat or /tasks route…`, `# CORRECTED: …the code refutes it`). That prose is the file's most valuable content and it must survive a round trip.
  Decision: each work item's **description** holds the comment verbatim in a delimited region (`<!-- sprint-status-note -->…`), the importer extracts it, and the generator re-emits it. A round-trip test over the **real** file asserts byte equality after import→generate. If a line cannot round-trip, the importer fails loudly naming it rather than dropping it — the `PromptFileLoader` posture.

- **D5 — First generation must produce an empty or trivially explainable diff, and that diff is the acceptance evidence.** A generator whose first run rewrites 689 lines has not been verified; it has replaced the file. So: import, generate, `git diff`, and the PR contains the diff with every hunk explained. Expected legitimate hunks: the `tracking_system` header change (AC10), and the epic-40 fold (AC7). Anything else is a mapping bug.

- **D6 — CI verifies the file is generated, not merely generatable.** A workflow step runs the tool in generate-only mode against a seeded tracker and fails on a dirty tree — `ConventionSeedDriftTests.cs:9-25`'s stated guarantee for a repo with no codegen step. Without it, "generated" decays to "was generated once" within two sprints, which is exactly the drift this story exists to remove.
  **This needs a tracker to generate from in CI.** Two options, decided at implementation: (a) CI seeds an ephemeral tracker from a committed export fixture and regenerates from that — self-contained, verifies the generator, not the live data; (b) CI queries a hosted dogfood instance — verifies the real thing, but couples the build to a deployment. **Prefer (a)**; the live-instance drift check is AC8's, run on a schedule, not in the build.

- **D7 — Markdown files are never touched, and a test proves it.** `apps/wiki-site/scripts/sync-content.ts:273-280` publishes `docs/stories/` to wiki.tamma.dev; the files are long-form specification, not tracker rows, and moving them would break the public site and every `BEFORE_YOU_CODE.md`-following agent. The work item stores the **path**; the file stays. Test 6 hashes the tree before and after an import.

- **D8 — Idempotence keys on the story slug, and re-import never overwrites tracker-side edits.** Slug = the directory/file name (`39-10-resumable-by-design-…`), stable and already unique. On re-run: status and rank are updated from the file; **title and description are not**, because once a human edits an item in the tracker the tracker is the source of truth for that field. Otherwise the first re-run silently reverts every edit — the failure mode that kills every one-way importer.

- **D9 — Bulk create via `CreateManyAsync`, one block allocation per project.** ~900 items. 44-1 D6 mints keys under a `FOR UPDATE` row lock and 44-1's Risks section flagged looping `CreateAsync` explicitly; 44-8 D4 made the same call. Test 3 asserts contiguous keys and a bounded statement count over the real tree.

- **D10 — The discrepancy report is a deliverable, not a diagnostic.** The file already documents cases where its own claims were refuted by the code (`:545`, `:547`), and the import will surface more: stories in the tree with no line in the file, lines with no directory, and epic-40's fork disagreeing with the root file. The importer emits a named report and AC9 requires it be written up in `.dev/findings/`. Normalising silently would destroy the single most useful output of the exercise.

- **D11 — The forked `docs/stories/epic-40/sprint-status.yaml` is deleted, its content folded in.** Two files with the same name and disagreeing content is the file-system tracker's structural failure and the clearest argument the epic has. Its distinct content (the corrected gating note) becomes the epic-40 work item's description. Test 8 asserts no second `sprint-status.yaml` under `docs/`.

## Implementation Steps

1. **CREATE `docs/stories/epic-44/status-vocabulary-mapping.md`** — D2/D3's table with rationale, referenced by the importer's tests.

2. **CREATE `apps/tamma-elsa/tools/Tamma.TrackerImport/`** — a console project with three verbs:
   - `import --root <repo> --api <url>` — parse + create/update
   - `generate --api <url> --out docs/sprint-status.yaml` — emit
   - `check --api <url> --root <repo>` — the AC8 drift report
   Added to the solution; **excluded from the API's publish output**.

3. **CREATE `.../Tamma.TrackerImport/SprintStatusParser.cs`** — a comment-preserving YAML reader. Line-oriented, not a generic YAML deserializer: the trailing comments and key order are the data (D4), and a round-trip through an object graph loses both.

4. **CREATE `.../Tamma.TrackerImport/StoryTreeScanner.cs`** — walks `docs/stories/epic-*/`, extracts slug, title (the `# Story N-M:` heading), `Status:` line and `## Estimated Effort`, and the path. Reports tree-vs-file discrepancies (D10).

5. **CREATE `.../Tamma.TrackerImport/StatusMapper.cs`** — D2's table, pure, table-tested both directions.

6. **CREATE `.../Tamma.TrackerImport/TrackerClient.cs`** — the HTTP client over 44-2's API. Uses `CreateManyAsync`-backed bulk create (D9) and 44-3's `parent` and `move`.

7. **CREATE `.../Tamma.TrackerImport/SprintStatusGenerator.cs`** — the inverse of step 3. Reads the tracker, re-emits key order, statuses (reversing D2's labels) and comment regions (D4).

8. **DELETE `docs/stories/epic-40/sprint-status.yaml`**, folding its content into the epic-40 work item's description (D11).

9. **MODIFY `docs/sprint-status.yaml`** — replace with generated output; header `tracking_system: tamma-tracker` (AC10); add a "this file is generated — edit in the tracker" banner.

10. **MODIFY `.github/workflows/ci.yml`** — a `tracker-file-generated` job per D6 option (a): seed from the committed export fixture, generate, `git diff --exit-code docs/sprint-status.yaml`.

11. **CREATE `.dev/findings/tracker-dogfood-reconciliation-<date>.md`** — D10's report (AC9).

12. **CREATE tests** under `apps/tamma-elsa/tests/Tamma.TrackerImport.Tests/`.

## Data & Migrations

**None.** This story creates data, not schema.

## Events

Uses 44-5's constants. `WORKITEM.CREATED.SUCCESS` per item (best-effort, so a ~900-item import is not gated on 900 transactional appends), plus one `WORKITEM.IMPORTED.SUCCESS` (44-8's constant, reused) per run carrying `{ source: "sprint-status", epics, stories, updated, skipped, discrepancies }`.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `StatusMapperTests.Maps_all_seven_file_statuses` | D2's table, forward |
| 2 | `StatusMapperTests.Reverses_losslessly_including_labels` | `ready`+`readiness:drafted` → `drafted` |
| 3 | `ImportTests.Real_tree_imports_with_contiguous_keys_in_bounded_statements` | against the actual `docs/stories/` — **D9** |
| 4 | `ImportTests.Rerun_updates_status_and_rank_only` | title/description edits survive — **D8** |
| 5 | `ImportTests.Rerun_produces_a_stable_item_count` | **AC4** |
| 6 | `ImportTests.Markdown_tree_is_byte_unchanged` | tree hash before/after — **AC3 / D7** |
| 7 | `RoundTripTests.Real_file_round_trips_byte_identically` | parse → import → generate → compare against the **real** 689-line file — **AC5 / D4, the load-bearing test** |
| 8 | `RoundTripTests.No_second_sprint_status_file_exists` | **AC7 / D11** |
| 9 | `GeneratorTests.Unroundtrippable_line_fails_loudly` | a synthetic pathological comment → named failure, not a drop — **D4** |
| 10 | `DriftCheckTests.Reports_tree_vs_file_discrepancies_by_name` | seeded mismatches — **AC8 / D10** |
| 11 | `ScannerTests.Extracts_slug_title_status_and_effort` | across several real story files, including 41-1's split-index shape |
| 12 | CI job `tracker-file-generated` | dirty tree fails the build — **AC6 / D6** |

Test 7 runs against the committed real file and is the one that decides whether this story is done.

## Definition of Done

- 12 tests green, test 7 against the real 689-line file.
- The PR contains the **first-generation diff** with every hunk explained (D5).
- `docs/stories/` tree hash unchanged (test 6); wiki sync verified by running `apps/wiki-site/scripts/sync-content.ts` before and after and diffing its output.
- `docs/stories/epic-40/sprint-status.yaml` deleted, content preserved (test 8).
- `.dev/findings/tracker-dogfood-reconciliation-<date>.md` exists and names every discrepancy (AC9/D10).
- `docs/sprint-status.yaml` carries the generated banner and `tracking_system: tamma-tracker`.
- The import tool is excluded from the API publish output.

## Dependencies & Sequencing

- **Blocked by:** 44-1, 44-2, 44-3, 44-5. **Last in the epic.**
- **Blocks:** nothing.
- **May be cancelled outright** by Open question 2 ("is the tracker for Tamma's own development, for customers, or both?"). If the answer is customers-first, this story drops and its 4 days move to 44-6, which is the epic's weakest estimate. **Confirm before starting** — this is the one story in the epic whose existence is contingent.
- **Shared-edit register:** `docs/sprint-status.yaml` is edited by **every** story in flight across every epic. This story changes it from hand-edited to generated, which is a workflow change for every other author. **Announce before landing**, and land at a low-traffic moment.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The round trip is not byte-exact** and generation rewrites 689 lines of hand-written prose. The story's central risk. | D4's delimited comment regions; D5's requirement that the first-generation diff be empty or explained hunk by hunk; test 7 against the real file; test 9 making an unroundtrippable line a loud failure rather than a silent drop. |
| **Making the file generated breaks every other author's workflow.** ~10 in-flight stories edit it by hand. | Shared-edit register calls for an announcement; the generated banner tells an editor where to go; the CI check (D6) fails fast with a clear message rather than letting a hand edit be silently overwritten later. |
| **The import surfaces a large discrepancy list** and looks like the tracker is wrong. | D10 reframes it: the file already carries `CORRECTED:` lines admitting past drift. The report is the deliverable, and AC9 requires writing it up rather than normalising it. |
| **The CI check needs a live tracker.** | D6 prefers option (a): a committed export fixture, so the build verifies the generator and stays self-contained. The live-instance drift check is AC8's `check` verb, scheduled, not in the build. |
| **Scope creep into migrating story content.** | Out of Scope is explicit; work items store the path, not the body; test 6 makes touching the tree a build failure. |
| **The story is built and then cancelled** by Open question 2. | Dependencies flag it as contingent and require confirmation before starting. It is sequenced last precisely so cancellation costs nothing already spent. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1, 5 (mapping table + mapper, both directions) | 0.5 |
| Steps 3, 7 (comment-preserving parser + generator — the round trip) | 1.5 |
| Steps 2, 4, 6 (tool scaffold, tree scanner, HTTP client, bulk create) | 1.0 |
| Steps 8–10 (fold the fork, regenerate, CI job) | 0.5 |
| Steps 11–12 (findings note + 12 tests) | 0.5 |
| **Total** | **4.0** |

# Implementation Plan — Story 44-7: Loop Integration, the `issueId` Join, and the Broken-Selector Fix

## Scope & Deliverable

When this story is done the autonomous loop can take work from the native tracker: `SelectWorkItemActivity` selects across a fail-loud registry of intake sources, native items carry a `Key` that **is** the `issueId` every document, event and approval already keys on — so a tracker item's lifecycle documents attach with no adapter — and optional, idempotent, per-project status write-back moves a card as the loop runs it. It also fixes a live bug: the non-mock intake path has always thrown a swallowed `JsonException` and reported an empty backlog, in two activities, neither of which has a unit test.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §2 (the `issueId` join and the `IssueNumber` consequence), Open questions 3 and 4
- `apps/tamma-elsa/src/Tamma.Activities/ADL/SelectWorkItemActivity.cs` **in full** — inputs `:45-55`, outputs `:60-75`, `RunAsync` `:88-160`, `FetchCandidates` `:165-232` (the bug at `:186`/`:220`, the swallow at `:229-232`), `ResolvePriority` `:237-243`, triaged-label set `:209-212`, `SimulateCandidates` `:254-265`, `WorkItem` `:279-291`
- `apps/tamma-elsa/src/Tamma.Activities/ADL/FetchUntriagedItemsActivity.cs:92-93` — the same bug
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:689-707` — the wrapper response at `:706`; mapped at `Tamma.Api/Program.cs:2823`
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/Models/Issue.cs:7-19` — `string Number`, `HtmlUrl`, `IssueState`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:25-80` — the documented flow and the variables (`WorkItemJson:60`, `IssueNumber:62`, `Mode:69`); **read to confirm the variable contract, do not edit**
- `apps/tamma-elsa/src/Tamma.Activities/ADL/DispatchCycleActivity.cs:128` and `Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs:90-99` — dispatch and wiring
- `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs:37` — `IssueId` is a `string`
- `docs/sprint-status.yaml:583-585` and `docs/stories/epic-41/story-41-29/41-29-task-level-flow-router.md:262` — the `SingleIssueCycleWorkflow.cs` merge chain this story stays out of
- `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandlerRegistry.cs:21-38` — the fail-loud keyed registry shape (startup throws on duplicate) copied for D4
- **All referenced paths exist.** NOT FOUND (this story creates them): `Tamma.Activities/ADL/IWorkIntakeSource.cs`, `PlatformIssueIntakeSource.cs`, `TrackerIntakeSource.cs`, `WorkIntakeSourceRegistry.cs`, and the two activities' test files.

## Design Decisions

- **D1 — The bug fix ships as its own commit, first, with the regression test written before the fix.** `SelectWorkItemActivity`'s non-mock path has never worked: it deserializes `List<WorkItem>` from `{ issues, total }` (`:186`, `:220` vs `EngineEndpoints.cs:706`), the `JsonException` is swallowed at `:229-232`, and the activity reports `NothingFound`. `FetchUntriagedItemsActivity.cs:92-93` is identical. Neither has a unit test — which is exactly how a bug survives in a shipped activity that the workflow-structure tests happily cover.
  Order: write the failing test, fix, then build the abstraction on a working base. Reviewable and landable independently of the rest of Epic 44.

- **D2 — The swallow becomes a distinguishable outcome, not a rethrow.** Rethrowing would fail the ADL cycle on a transient 503, which is worse than today. But conflating "the fetch threw" with "there is no work" is how this bug hid for the life of the activity. So the catch stays, and it emits `ADL.WORKITEM.SELECT` with a failure status and a reason, taking the same `NothingFound` edge (no graph change — see D3) while being loudly different in the event stream. Test 3 asserts a transport failure and a genuinely empty result produce different events.

- **D3 — `SingleIssueCycleWorkflow.cs` is not edited, and neither is any workflow graph.** `docs/sprint-status.yaml:583-585` records a four-way merge chain (`40-2 → 40-4 → 40-5 → 41-29`) on that file; adding a fifth claimant for a feature that does not need one would be a scheduling cost with no technical return. The workflow already consumes `WorkItemJson` (`:60`) and `IssueNumber` (`:62`) as variables, and native items flow through the same variables with a richer JSON payload. All of this story's behaviour lives inside `SelectWorkItemActivity` and its new collaborators.
  **Consequence, accepted and audited:** `IssueNumber` is `0` for native items. Step 8 audits every consumer that branches on it and lists them in the PR; AC9's structure test asserts the file's hash is unchanged.

- **D4 — `IWorkIntakeSource`, keyed and fail-loud, at the *source* level not the *item* level.** One interface, two implementations, a registry that throws at startup on an unknown configured name or a duplicate key — the `IPlatformTaskHandlerRegistry.cs:21-38` posture (startup throws on duplicate), which Epic 43 cites as one of only two drift-guarded vocabularies in the repo.
  **Crucially, `WorkItem` stays one shape.** A per-source item type would propagate into `WorkItemJson`, into `SingleIssueCycleWorkflow`'s variables, and into every downstream activity — and would make D3 impossible. The sources differ in *where candidates come from*, not in *what a candidate is*.

- **D5 — `WorkItem.Key` is the `issueId`, for both sources, and for platform items it is byte-identical to today's string.** This is the epic's D2 made concrete. Native: `WorkItemRef.ToWire()` → `TAM-142`. Platform: exactly the coordinate string the current code already writes, unchanged, so **no existing lineage moves**. A migration of historical `issueId` values is thereby avoided entirely — which is the reason the format was not "improved" while the opportunity was there.
  `WorkItem.Source` is a two-member `[Wire]` enum (`platform-issue | tracker`) so downstream code and the event tags can branch without string sniffing.

- **D6 — Native selection uses the tracker's own gate, not the `tamma-auto` label.** `SelectWorkItemActivity`'s `AutoLabels` default (`:49`) and `ExcludeLabels` (`:52`) are a GitHub-label protocol; applying them to native items would mean inventing labels on a model that has a status, a priority and a project instead. `TrackerIntakeSource` selects `Status == ready`, ordered by `Rank` then `TriagePriority`, filtered by a per-project `AutomationMode` (`off | assist | auto`) that **defaults to `off`**. Default-off is the safe direction and matches the epic's Open question 3 being unresolved: nothing gets picked up autonomously until someone opts a project in.
  `AutomationMode` is a new column on `projects` — see Data & Migrations for how that lands without a second tenant migration.

- **D7 — Status write-back is per-project opt-in, idempotent by target state, and off by default.** Epic README Open question 4 records two coherent models — human-owned status with workflows only reporting, versus the loop driving transitions — and v1 assumes the first with an explicit opt-in for the second. Turning it on by default would make every workflow a tracker writer on day one and couple the tracker's correctness to workflow re-entry.
  **Idempotence is by target state, not by a guard flag:** "set status to `in_progress`" is a no-op when the item is already `in_progress`. That is what makes it safe under 39-10's crash re-entry (`done`), which re-runs steps by design. Test 9 replays the transition twice and asserts one event and one row change.

- **D8 — Write-back goes through the HTTP API, not the repository.** The engine plane (`Tamma.ElsaServer` / `Tamma.Activities`) registers no tracker repository and mediates everything through the API client — the same constraint Epic 43 records for its Seam E ("reaches the gate over HTTP, not by DI"). So write-back is a `POST /api/work-items/{id}/status` call from an activity, reusing the existing engine-callback client pattern.

- **D9 — `ADL.WORKITEM.SELECT` gains `source` and `key` tags.** Without them, an operator looking at a cycle cannot tell whether it came from GitHub or the tracker, which is the first question during an incident. Additive to an existing event type; no new family, so 44-5's ratchet is unaffected.

## Implementation Steps

**Commit 1 — the bug fix (D1), independently reviewable:**

1. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/ADL/SelectWorkItemActivityTests.cs`** — a test that feeds the real `{ issues, total }` shape and asserts candidates are parsed. **It fails against `main`.**

2. **CREATE `.../ADL/FetchUntriagedItemsActivityTests.cs`** — the same for `:92-93`.

3. **MODIFY `Tamma.Activities/ADL/SelectWorkItemActivity.cs:180-232`** — deserialize an `IssuesEnvelope { Issues, Total }`; map `Issue.Number` (string) → `int` with a loud reject on non-numeric, `HtmlUrl` → `Url`. **MODIFY `FetchUntriagedItemsActivity.cs:92-93`** identically.

4. **MODIFY `SelectWorkItemActivity.cs:229-232`** — per D2: keep the catch, emit a failure-status `ADL.WORKITEM.SELECT` with a reason, keep the outcome edge.

**Commit 2 — the abstraction and native intake:**

5. **CREATE `Tamma.Activities/ADL/IWorkIntakeSource.cs`** + `WorkIntakeQuery` (repository/project, auto+exclude labels, bot assignee, limit) + `WorkIntakeSourceKind` (the `[Wire]` enum of D5).

6. **CREATE `.../ADL/PlatformIssueIntakeSource.cs`** — today's `FetchCandidates` body moved verbatim (post-fix), setting `Source = platform-issue` and `Key` to the existing coordinate string (D5).

7. **CREATE `.../ADL/TrackerIntakeSource.cs`** — `GET /api/work-items?projectId=…&status=ready&automation=…` over the engine-callback HTTP client, mapping to `WorkItem` with `Source = tracker`, `Key = <item key>`, `IssueNumber = 0`, `Priority` from `TriagePriority`, `Labels` from kind + issue type.

8. **CREATE `.../ADL/WorkIntakeSourceRegistry.cs`** — keyed, throws on duplicate and on unknown configured name naming the accepted set (D4). **AUDIT** every consumer of the `IssueNumber` workflow variable and list them in the PR (D3).

9. **MODIFY `SelectWorkItemActivity.cs`** — inject the registry, iterate configured sources, merge candidates, keep `ResolvePriority` (`:237-243`), the exclude/assignee filters (`:194-197`) and the priority-then-age sort (`:140-145`) unchanged. Add `source`/`key` to the emitted tags (D9).

10. **MODIFY `SelectWorkItemActivity.WorkItem`** — add `Key` and `Source`; both serialize into `WorkItemJson`.

**Commit 3 — write-back:**

11. **CREATE `Tamma.Activities/ADL/UpdateWorkItemStatusActivity.cs`** — D7/D8. Inputs `(key, targetStatus)`; no-ops when already at target; posts to `/api/work-items/{id}/status`; emits `ISSUE_STATUS`-family-adjacent output. Registered in `CanonicalSuspendActivities`? **No** — it does not suspend.

12. **MODIFY `Tamma.ElsaServer/Workflows/UpdateIssueStatusWorkflow.cs`** — branch on `WorkItem.Source`: native → the new activity, platform → today's behaviour. **This is the only workflow graph touched, and it is not the contended file.**

13. **CREATE tests** under `tests/Tamma.Activities.Tests/ADL/`.

## Data & Migrations

**One column: `projects."AutomationMode" text NOT NULL DEFAULT 'off'` with `ck_projects_automation_mode`** (D6).

- **Preferred:** fold into 44-1's `AddTrackerCore` if it has not been deployed. 44-1 D4's reasoning applies — tenant migrations are the scarcest resource in the repo and the sweep is an operator action per deploy.
- **Otherwise:** a second tenant migration `AddProjectAutomationMode`, and the operator runs 44-1's sweep again.

The path taken is recorded in the PR. 44-2's project DTO and 44-6's project admin page gain the field; both are additive.

## Events

No new family. Two additive changes:
- `ADL.WORKITEM.SELECT` gains `source` and `key` tags and a failure status (D2, D9).
- Native status write-back emits `WORKITEM.STATUS_CHANGED.SUCCESS` (44-5's constant) via the API, with `Data.actor = "loop"` distinguishing it from a human transition.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `SelectWorkItemActivityTests.Parses_the_real_envelope_shape` | **fails on `main`** — the D1 regression test |
| 2 | `FetchUntriagedItemsActivityTests.Parses_the_real_envelope_shape` | the second instance of the same bug |
| 3 | `SelectWorkItemActivityTests.Transport_failure_and_empty_result_differ` | distinct events — **AC2 / D2** |
| 4 | `SelectWorkItemActivityTests.Non_numeric_platform_number_is_rejected_loudly` | not silently `0` |
| 5 | `WorkIntakeSourceRegistryTests.Unknown_source_throws_naming_the_set` | fail-loud — **D4** |
| 6 | `WorkIntakeSourceRegistryTests.Duplicate_key_throws_at_startup` | the `IPlatformTaskHandlerRegistry` posture |
| 7 | `TrackerIntakeSourceTests.Selects_ready_items_by_rank_then_priority` | **AC7 / D6** |
| 8 | `TrackerIntakeSourceTests.Automation_off_yields_nothing` | default-off proven |
| 9 | `UpdateWorkItemStatusActivityTests.Replay_is_a_no_op` | twice → one event, one row change — **AC8 / D7** |
| 10 | `PlatformIssueIntakeSourceTests.Key_is_byte_identical_to_today` | **no lineage moves** — D5 |
| 11 | `SelectWorkItemActivityTests.Existing_filters_and_ordering_are_unchanged` | exclude labels, bot assignee, priority-then-age |
| 12 | `IssueIdJoinTests.Native_item_documents_attach_to_its_key` | dispatch a lifecycle for a native item; `DocumentInstance.IssueId == key`; `GET /api/work-items/{key}/timeline` shows the `DOCUMENT.*` rows — **AC6, the payoff test** |
| 13 | `SingleIssueCycleUntouchedTests.Workflow_file_is_unmodified` | content hash pinned — **AC9 / D3** |
| 14 | Existing `SingleIssueCycleRoutingTests` / `SafetyTests` / `MergeSlaTests` | pass **unmodified** |

Test 12 is Testcontainers (it crosses the API and the document store); the rest are unit with a faked HTTP handler.

## Definition of Done

- 14 tests green, and tests 1–2 demonstrably fail on `main` (recorded in the PR).
- `SingleIssueCycleWorkflow.cs` byte-unchanged (test 13); the `IssueNumber`-consumer audit (step 8) is in the PR description.
- Platform-item `issueId` strings are byte-identical to pre-change output (test 10) — **no lineage migration is required, and the PR says so explicitly**.
- `AutomationMode` defaults to `off` in the migration and in the DTO.
- A `.dev/bugs/` note recording the two-activity swallowed-`JsonException` bug, its lifetime, and why the workflow-structure tests did not catch it.

## Dependencies & Sequencing

- **Blocked by:** 44-0, 44-1, 44-2. **44-5** for test 12 only (the rest can land first).
- **Blocks:** 44-9 (the dogfood run selects native items).
- **Commit 1 is blocked by nothing** and can land ahead of the whole epic.
- **Coordination:** confirm with the owners of 40-2/40-4/40-5/41-29 that this story stays out of the `SingleIssueCycleWorkflow.cs` chain (D3). If a reviewer pushes for a graph change, that is a scope escalation to raise, not to absorb.
- **Shared-edit register:** `SelectWorkItemActivity.cs` and `FetchUntriagedItemsActivity.cs` — no other in-flight story touches them. `UpdateIssueStatusWorkflow.cs` — likewise. `AddTrackerCore` migration if the Data & Migrations preferred path is taken (shared with 44-1, 44-4).

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **Fixing the selector changes production behaviour** — a deployment whose non-mock intake has silently done nothing starts selecting work. | This is the point, and it is a behaviour change worth flagging loudly in the release note. `AutomationMode` defaults to `off` for native items, and the platform path's `AutoLabels` gate (`tamma-auto`) is unchanged, so the blast radius is exactly "issues someone already labelled for Tamma". |
| **Review pressure to edit `SingleIssueCycleWorkflow.cs`** for a cleaner `IssueNumber` model. | D3 states the merge-chain cost; test 13 makes the constraint mechanical; the audit lists exactly what `IssueNumber = 0` affects so the tradeoff is visible rather than assumed. |
| **`IssueNumber = 0` collides** with a consumer that treats `0` as "unset" and behaves differently. | Step 8's audit is the mitigation and its output is a PR artifact. If the audit finds a consumer that cannot tolerate it, that is a finding that reopens D3 with evidence. |
| **A second tenant migration for one column.** | Data & Migrations prefers folding into `AddTrackerCore`; the sweep (44-1) makes the fallback survivable rather than blocking. |
| **Write-back double-transitions under 39-10 re-entry.** | D7's idempotence-by-target-state plus test 9's replay. Default-off means the risk is not on by accident. |
| **Native items bypass triage** and enter the loop unclassified. | Out of scope by design and stated; `AutomationMode` default-off means a human opts a project in, and `Status == ready` is a human-set gate. Extending `TRIAGE.*` to native items is a named follow-on. |

## Effort Breakdown

| Task | Days |
|---|---|
| Commit 1 — steps 1–4 (regression tests + the two-activity fix + the loud catch) | 1.0 |
| Steps 5–6 (interface, extract the platform source) | 0.5 |
| Step 7 (tracker intake source) | 0.75 |
| Steps 8–10 (registry, `IssueNumber` audit, selector wiring, `WorkItem` fields) | 1.0 |
| Steps 11–12 (write-back activity + the one workflow branch) | 0.75 |
| Step 13 (14 tests, incl. the Testcontainers join test) | 0.75 |
| Bug note, release-note flag, review | 0.25 |
| **Total** | **5.0** |

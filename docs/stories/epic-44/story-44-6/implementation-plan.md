# Implementation Plan — Story 44-6: Tracker UI in `packages/dashboard`, and the Missing CI Test Line

## Scope & Deliverable

When this story is done `packages/dashboard` — the dashboard that is actually deployed — has `/work`, `/work/board`, `/work/:key` and `/work/projects`; the board is cross-container drag-and-drop with optimistic updates that roll back correctly on a `409`; the detail page renders the interleaved timeline that makes the `issueId` join visible to a human; three reusable primitives (`GroupedTable`, `RowToggle`, `BoardColumn`/`BoardCard`) land in `common/` where Epic 43 Story 7 can reuse them; the assignee picker and list header say honestly which visibility model is in force; and — separately valuable — `pnpm --filter @tamma/dashboard test` and `typecheck` run in CI for the first time, with the package's pre-existing 449 tests green.

## Pre-Reading

- `docs/stories/epic-44/README.md` — "Where the UI lives — and the honest cost" (the full primitive-gap table and the CI gap), Decisions D9
- `docs/stories/epic-44/story-44-2/implementation-plan.md` — D2 (tri-state PATCH), D5/D6 (`source` and `visibilityMode` discriminators), D7 (keyset paging), D8 (`If-Match`/409)
- `docs/stories/epic-44/story-44-4/implementation-plan.md` — D4/D6 (the board is one call; all seven columns including empties; the assignee cap)
- `docs/stories/epic-44/story-44-5/implementation-plan.md` — D7 (the timeline returns foreign families unfiltered)
- `packages/dashboard/src/pages/runs/RunsPage.tsx` — the closest existing shape end to end (`:26-29`, `:31-42`, `:58`, `:154`)
- `packages/dashboard/src/components/monitoring/DataTable.tsx:58,82,107,112,122,228-249` — everything it does, and the two things it does not (grouping, row expand)
- `packages/dashboard/src/components/settings/agents/ProviderChainEditor.tsx:40,178-179` — the **only** `@dnd-kit` usage; single-container, so a reference for the API and not for the architecture
- `packages/dashboard/src/services/admin/admin-api-client.ts:11-17` — the client shape to copy; `src/services/repos/repos-api-client.ts:11` — the `API_BASE` inconsistency **not** to copy
- `packages/dashboard/src/services/onboarding/onboarding-api-client.ts:4-8` — the "keep in sync with the backend record" comment convention
- `packages/dashboard/src/components/monitoring/events/EventDetailPanel.tsx:45` + `event-explorer-utils.ts` — the only detail panel and the only grouping logic in the repo
- `.github/workflows/ci.yml:45-50`, `vitest.config.ts:62,64`, `package.json:25` — the CI gap in full
- **All referenced paths exist.** NOT FOUND (this story creates them): `packages/dashboard/src/pages/work/`, `src/services/tracker/`, `src/stores/tracker/`, and the three primitives in `src/components/common/`.

## Design Decisions

- **D1 — `packages/dashboard`, and the choice is about deployment, not preference.** `dashboard-user` has no Dockerfile, no compose service, no GHCR image, no deploy step and no nginx vhost; its only footprint outside docs is one CI test line. Shipping a board there means building that path first, which is a story this epic did not budget and should not absorb silently. `packages/dashboard` is deployed today at `docker-compose.yml:310-319` → `deploy.yml:250`. The tension with 39-19 (which targets `dashboard-user` for `/chat` and `/tasks`) is real and is raised as an open question rather than resolved here.

- **D2 — Route prefix `/work`, never `/tasks`.** `packages/dashboard-user`'s `/tasks` is 39-19's suspended-decision inbox. Two features called "tasks" in adjacent products, meaning different things, is a permanent support cost; the epic README's boundary table asks 39-19 for a disambiguating line and this is the reciprocal half.

- **D3 — No React Query, no new data-fetching library.** The codebase has ~22 hand-rolled `fetchJSON` clients and four Zustand stores. Introducing a query library for one feature leaves two paradigms and makes every future page a choice. One client in the house style (`admin-api-client.ts:11-17`), one store in the house style. Cost: manual cache invalidation after mutations — accepted, and small because the board refetches its own project on mutation.
  **`API_BASE` defaults to `'/api'`**, matching `admin-api-client.ts:13` and deliberately **not** `repos-api-client.ts:11`'s `''`, which is an existing inconsistency this story declines to propagate and declines to fix.

- **D4 — The board renders the server's columns verbatim, including empties, and never synthesises.** 44-4 D6 returns the complete `WorkItemStatus` skeleton for exactly this reason: a client that fills in missing columns duplicates vocabulary knowledge that the wire already carries, and every future client re-implements it. The `groupBy=assignee` cap and `other` fold are also rendered as returned, with the cap stated in the UI — pretending the board is complete when it is capped is the failure mode.

- **D5 — Optimistic drag with server-truth rollback, not with a retry.** A drag mutates local state, sends `If-Match`, and on `409` **refetches the column and replaces local state**, showing a non-blocking notice. It does not re-send with the new version: two people dragging the same card means the second intent is probably stale, and silently applying it is how a board loses a change nobody can explain. Test 6 simulates the 409 and asserts the card returns rather than sticking.

- **D6 — The three primitives go in `src/components/common/`, and this is coordinated with Epic 43 Story 7.** 43-7 is described as "the least reliable estimate in the plan — three React primitives with no in-repo precedent (a row-level toggle, a grouped table, and a dimmed row with a why-disabled tooltip)" (`epic-43/README.md:380-386`). Two of those three are this story's `RowToggle` and `GroupedTable`. Building them twice, in two shapes, in the same quarter, is the outcome to avoid; whichever story lands first owns them and the second imports them. Placing them in `common/` (not in `pages/work/`) is what makes that possible. **This must be agreed before either story starts.**

- **D7 — Cross-container drag is `DndContext` + one `useDroppable` per column + `DragOverlay`, not an adaptation of `ProviderChainEditor`.** That file is a single `SortableContext` with `verticalListSortingStrategy` (`:179`) — the right reference for the `@dnd-kit` API surface and the wrong one for the architecture. A board needs per-column droppables, `closestCorners` collision detection, a portal-rendered `DragOverlay` so the card is not clipped by column overflow, and the keyboard sensor (D8). Budgeted as new work.

- **D8 — Keyboard operability is an AC, not polish.** `@dnd-kit`'s `KeyboardSensor` with a coordinate getter gives grab/arrow/drop for free *if* it is wired at the start; retrofitting it after a mouse-only implementation means re-deriving the announcement and focus model. There is no other board in the repo to copy, so the pattern established here is the pattern.

- **D9 — The timeline renders foreign families read-only and visibly labelled.** 44-5 D7 returns `DOCUMENT.*` / `APPROVAL.*` / `ESCALATION.*` rows for the same `issueId` unfiltered — that interleaving is the visible payoff of the join key and the reason a comments table is deferred. Rendering them identically to tracker events would suggest a user can act on them; a `DOCUMENT.ACCEPTED` row is workflow history. So: a distinct visual treatment and a "workflow activity" label, with a deep link where 39-11's lineage API offers one.

- **D10 — Degradation is rendered, not hidden.** 44-2 D5/D6 put `source` and `visibilityMode` on the wire precisely so this UI can be honest. `source: "tenant-membership"` → a one-line note that repo-scoped eligibility is not configured. `visibilityMode: "tenant"` → the list header says the view is tenant-wide. Both are driven by wire values, so both vanish when 39-20 lands **with no change to this code**.

- **D11 — Turning the CI job on is in scope, and green is the bar.** `packages/dashboard`'s 449 tests are excluded from the root run (`vitest.config.ts:62`) with a comment deferring to a filter command that no workflow contains — so this story would otherwise add ~2 500 lines of untested-in-CI React on top of 449 already-untested-in-CI tests. Adding the line may surface pre-existing failures; those are fixed, or skipped with a `TODO` naming the owner. `typecheck` is added in the same change because `package.json:25` typechecks five packages and neither dashboard.
  This is real, unbudgeted-looking work that belongs here: the alternative is a story that doubles the size of an untested surface.

- **D12 — No SignalR.** The hubs ship (`Tamma.Api/Hubs/OrchestratorChannelHub.cs`, `UserChannelHub.cs`, mapped in `Program.cs`), but delivery goes through `ChannelOutboxService`, whose audience resolver returns empty for every input (`ChannelOutboxService.cs:143`). Subscribing a board to a channel that never delivers would ship a live-update feature that is silently dead. Refetch on window focus and after mutation; revisit when 39-20 lands.

## Implementation Steps

1. **CREATE `packages/dashboard/src/services/tracker/tracker-api-client.ts`** — per D3. `listWorkItems`, `getWorkItem`, `getByKey`, `createWorkItem`, `patchWorkItem`, `setStatus`, `assign`, `move`, `setParent`, `getSubtree`, `getTimeline`, `getBoard`, `listProjects`, `createProject`, `patchProject`, `listIterations`, `commitItems`, `getAssignable`, `getPreferences`, `putPreferences`. Types hand-written with the sync comment.

2. **CREATE `packages/dashboard/src/stores/tracker/store.ts`** — Zustand, following `src/stores/admin/store.ts`. Board columns, list page + cursor, filters, in-flight optimistic map keyed by work-item id.

3. **CREATE `packages/dashboard/src/components/common/GroupedTable.tsx`** (+ test) — D6. Generic over row type; `groups: {key,label,count,rows}[]`; collapsible group headers; reuses `DataTable`'s cell renderer contract so column definitions are portable between the two.

4. **CREATE `packages/dashboard/src/components/common/RowToggle.tsx`** (+ test) — D6. A chevron control with `aria-expanded`, driving an expanded-row slot. `DataTable.tsx:158` already uses `aria-expanded` on dropdown buttons; match the pattern.

5. **CREATE `packages/dashboard/src/components/common/board/{BoardColumn,BoardCard,BoardDragContext}.tsx`** (+ tests) — D7/D8. `DndContext` wrapper with `PointerSensor` + `KeyboardSensor`, `closestCorners`, per-column `useDroppable`, portalled `DragOverlay`, and `onDragEnd` returning `(itemId, toColumnKey, afterId, beforeId)` so pages stay transport-agnostic.

6. **CREATE `packages/dashboard/src/pages/work/WorkListPage.tsx`** — `DataTable` + filter bar + keyset cursor paging + the `visibilityMode` header note (D10).

7. **CREATE `packages/dashboard/src/pages/work/WorkBoardPage.tsx`** — server columns verbatim (D4), the drag pair from step 5, optimistic apply + rollback (D5), per-column "load more".

8. **CREATE `packages/dashboard/src/pages/work/WorkItemDetailPage.tsx`** — fields with inline single-field PATCH, parent/children navigation via `getSubtree` + `RowToggle`, and `WorkItemTimeline.tsx` (D9).

9. **CREATE `packages/dashboard/src/pages/work/ProjectsAdminPage.tsx`** — project CRUD under `TenantAdminGuard`.

10. **CREATE `packages/dashboard/src/components/work/AssigneePicker.tsx`** — `getAssignable`, `source` note (D10).

11. **MODIFY `packages/dashboard/src/router.tsx`** — four routes near `:106-108` (where `repos`/`runs` are wired), plus nav entries in the layout.

12. **MODIFY `.github/workflows/ci.yml`** — after `:50`, add:
    ```yaml
          - run: pnpm --filter @tamma/dashboard test
          - run: pnpm --filter @tamma/dashboard typecheck
    ```
    Then make the existing 449 green (D11).

13. **CREATE tests** under `packages/dashboard/src/pages/work/__tests__/` and beside each primitive, using `src/test/render-helpers.tsx` and `src/test/fixtures.ts`.

## Data & Migrations

None. Frontend only.

## Events

None emitted by the client.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `GroupedTable.test.tsx` | groups render with headers + counts; collapse hides rows; empty group renders |
| 2 | `RowToggle.test.tsx` | `aria-expanded` toggles; expanded slot mounts/unmounts; keyboard activation |
| 3 | `BoardDragContext.test.tsx` | `onDragEnd` yields `(itemId, toColumnKey, afterId, beforeId)` for within-column and cross-column drops |
| 4 | `BoardDragContext.keyboard.test.tsx` | grab/arrow/drop moves a card without a pointer — **AC12** |
| 5 | `WorkBoardPage.test.tsx` — `renders_all_columns_including_empty_ones` | seven columns from a fixture with three populated — **AC3 / D4** |
| 6 | `WorkBoardPage.test.tsx` — `409_reverts_the_card` | mock 409 → card back in origin column, notice shown, no duplicate — **AC4 / D5** |
| 7 | `WorkBoardPage.test.tsx` — `cross_column_drag_calls_status_and_within_column_calls_move` | correct endpoint per gesture |
| 8 | `WorkBoardPage.test.tsx` — `assignee_board_shows_the_cap` | `other` column + cap note rendered |
| 9 | `WorkListPage.test.tsx` — `keyset_paging_uses_the_cursor` | no offset params issued |
| 10 | `WorkListPage.test.tsx` — `tenant_visibility_note_is_shown` | `visibilityMode: "tenant"` → header note — **AC7 / D10** |
| 11 | `AssigneePicker.test.tsx` — `membership_fallback_is_labelled_and_non_empty` | `source: "tenant-membership"` → note + populated list — **AC7** |
| 12 | `AssigneePicker.test.tsx` — `resolver_source_shows_no_note` | the post-39-20 state |
| 13 | `WorkItemDetailPage.test.tsx` — `inline_edit_sends_one_field` | PATCH body has exactly the edited key — **AC5 / 44-2 AC3** |
| 14 | `WorkItemTimeline.test.tsx` — `foreign_families_are_labelled_and_read_only` | `DOCUMENT.*` row rendered as workflow activity, no action affordance — **AC5 / D9** |
| 15 | `tracker-api-client.test.ts` — `uses_credentials_include_and_api_base` | cookie-session posture, `'/api'` default — **AC8 / D3** |
| 16 | Pre-existing 449 tests | green under the new CI line — **AC10 / D11** |

## Definition of Done

- Tests 1–15 green **and** the pre-existing 449 green under the new CI job.
- `.github/workflows/ci.yml` runs both `test` and `typecheck` for `@tamma/dashboard`; a red build is demonstrable by breaking a tracker test locally and pushing.
- `GroupedTable` and `RowToggle` live in `src/components/common/`, are exported from the barrel, and 43-7's owner has confirmed they will import rather than rebuild (D6).
- No route named `/tasks` is added (D2, grep-checked).
- No `@tanstack/react-query` or equivalent in `package.json` (D3).
- No file under `packages/dashboard-user/` is modified.
- `repos-api-client.ts:11` is unchanged (noted, not fixed).

## Dependencies & Sequencing

- **Blocked by:** 44-2, 44-3, 44-4, 44-5 — the client is written against their wire contracts and the discriminators only exist because they put them there.
- **Blocks:** nothing in Epic 44. 44-7, 44-8 and 44-9 are headless and can run in parallel with this.
- **Coordination, before starting:** Epic 43 Story 7 (`epic-43/README.md:380-386`) owns the same two primitives. Agree ownership; whichever starts second imports.
- **Shared-edit register:** `packages/dashboard/src/router.tsx` and the nav layout are shared with 43-7 (which adds an admin catalog page). `.github/workflows/ci.yml` is shared with nothing in flight.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The 9-day estimate is the epic's weakest.** Three primitives with no precedent, one being cross-container DnD, plus turning on a CI job that has never run. | Stated as such in the story and the epic README. Mitigation is sequencing: steps 3–5 (the primitives, with their own tests) land as an independently reviewable slice before any page is written, so the unknown is retired first and the remaining work is conventional. |
| **The pre-existing 449 tests are not green**, and D11 turns a UI story into a repair job. | Run `pnpm --filter @tamma/dashboard test` **on day 0**, before any code. If the failure count is non-trivial, that is a finding to raise immediately, and the CI line moves to its own story rather than silently expanding this one. This check is the first task in the effort breakdown. |
| **Duplicate primitives with 43-7.** | D6; explicit coordination gate in Dependencies; `common/` placement makes reuse the path of least resistance. |
| **Optimistic drag rollback is subtly wrong** — the classic bug is a card that duplicates or lands in a third position. | Test 6 asserts the exact origin position and asserts no duplicate. The store keeps a pre-drag snapshot per item rather than attempting an inverse operation. |
| **Board performance with 500 cards in a column.** | The per-column limit + cursor is server-side (44-4 AC5); the client never holds an unbounded column. No virtualization in v1; if it is needed the limit is lowered, which is a config change. |
| **Keyboard DnD is dropped under schedule pressure**, as it usually is. | It is AC12 with its own test (4). Wired at the start it is nearly free; retrofitted it is not, which is why D8 states the ordering. |

## Effort Breakdown

| Task | Days |
|---|---|
| Day-0 check: run the existing 449 tests; report the count (risk gate) | 0.25 |
| Steps 3–5 (three primitives + their tests — the independently reviewable slice) | 3.0 |
| Steps 1–2 (client + store) | 0.75 |
| Step 7 (board page: optimistic drag, rollback, per-column paging) | 1.75 |
| Steps 6, 8–10 (list, detail + timeline, projects admin, picker) | 1.75 |
| Step 11 (routes + nav) | 0.25 |
| Step 12 (CI lines + making the pre-existing suite green) | 0.75 |
| Step 13 (page-level tests) | 0.5 |
| **Total** | **9.0** |

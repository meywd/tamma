# Story 44-6: Tracker UI in `packages/dashboard` — List, Board, Detail — and the Missing CI Test Line

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

As a **team member using Tamma**,
I want a work list, a drag-and-drop board, and a work-item detail view with its full timeline, in the dashboard that is actually deployed,
So that the tracker is usable by a human and not only by an HTTP client — and so that the ~2 500 lines of React this adds are covered by tests that actually run in CI.

## Priority

P1 — Wave 2. The API is complete and useful without it (44-7 consumes it headlessly), but a tracker nobody can see is not the product ask.

## Architectural Context (READ FIRST)

- **`packages/dashboard` is deployed; `packages/dashboard-user` is not.** `packages/dashboard` is compose service `tamma-dashboard` (`docker/docker-compose.yml:310-319`), built by `docker/Dockerfile.dashboard:18,24,31`, published to GHCR (`.github/workflows/docker-publish.yml:142,170,185`), pinned and started by `.github/workflows/deploy.yml:140-141,250` with a health loop at `:310`, reverse-proxied at `docker/nginx-proxy.conf.template:65-66,164`. `packages/dashboard-user`'s **entire** non-doc footprint is `.github/workflows/ci.yml:49-50`, `eslint.config.js:75-76`, `vitest.config.ts:64` and a lockfile row — no Dockerfile, no compose service, no image, no vhost. Its own `src/layouts/AppLayout.tsx:24-35` renders nav links to `/repos`, `/runs` and `/settings`, **none of which exist in `src/App.tsx:41-84`**. Epic Decisions D9.
- **⚠ `packages/dashboard`'s 449 tests do not run in CI.** Root `vitest.config.ts:62` excludes `packages/dashboard/**` with a comment deferring to `pnpm --filter @tamma/dashboard test`; **no workflow contains that line** — `.github/workflows/ci.yml:45-50` runs `pnpm vitest run` and `pnpm --filter @tamma/dashboard-user test` only. Neither dashboard is typechecked either: root `package.json:25` builds five other packages. This story must not widen that gap.
- **Stack.** React 19.2 + Vite 8 + TypeScript 6, `react-router-dom` ^7 (`src/router.tsx:53`), **Zustand** ^5 (four stores under `src/stores/`), **Tailwind v4** via `@tailwindcss/vite` (`vite.config.ts:7`). **No component library** — no MUI/Radix/shadcn. **No data-fetching library** — no React Query/SWR; ~22 hand-rolled copies of `const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api'` plus a local `fetchJSON<T>` (`src/services/admin/admin-api-client.ts:11-17` is the canonical one; `src/services/repos/repos-api-client.ts:11` defaults to `''` instead of `'/api'` — an existing inconsistency, do not propagate it).
- **Auth is cookie-session, not bearer.** Every client sets `credentials: 'include'`; a repo-wide grep for `Authorization`/`Bearer`/`localStorage` in `packages/dashboard/src` returns one hit and it is display text in a curl example (`src/pages/MyApiKeysPage.tsx:127`). The session is minted by oauth2-proxy → nginx `auth_request` (`nginx-proxy.conf.template:116-127,155-164`) → `ProxyHeaderAuthMiddleware`. Guards: `src/guards/AuthGuard.tsx:19`, `AdminGuard.tsx:20`, `TenantAdminGuard.tsx`.
- **Primitives that exist:** `src/components/common/{Badge,Card,ConfirmDialog,FormField,LoadingSpinner,Slider,Toggle}.tsx`; `src/components/monitoring/{DataTable,StatusBadge,MetricCard,MetricGrid,TimeSeriesChart,EmptyState,ErrorBanner,MonitoringLayout}.tsx`, barrel-exported at `src/components/monitoring/index.ts:7-23`.
- **Primitives that DO NOT exist — this is the estimate risk:**
  - **No grouped or collapsible table.** `DataTable.tsx:58` is flat: sort `:112`, filter `:82`, paginate `:107`, column-hide `:122`. No `groupBy`, no `rowSpan` anywhere in `src/**/*.tsx`. 25 files render a raw `<table>`; none groups rows.
  - **No row-level expand/collapse.** Rows are one `<tr>` with `onRowClick` (`:228-249`). The only collapsibles are panel-level `<details>` (`src/components/prompts/PromptPreview.tsx:52`).
  - **No multi-container drag-and-drop.** `@dnd-kit/{core,sortable,utilities}` are dependencies (`package.json:16-18`) but used in exactly one place — `src/components/settings/agents/ProviderChainEditor.tsx` (`useSortable:40`, `DndContext:178`, `SortableContext`/`verticalListSortingStrategy:179`) — for **vertical reordering inside one container**. A board is cross-container.
- **Closest shapes to model on:** `src/pages/runs/RunsPage.tsx` (DataTable + status filter + `StatusBadge` + row→detail; state `:26-29`, load `:31-42`, columns `:58`, render `:154`), `src/pages/runs/RunDetailPage.tsx`, `src/pages/repos/ReposPage.tsx`. The only grouping logic in the codebase is `src/components/monitoring/events/event-explorer-utils.ts` (`groupByType`, used at `EventExplorerPage.tsx:35,100`); the only detail side-panel is `src/components/monitoring/events/EventDetailPanel.tsx:45`.
- **The board API is one call.** 44-4 AC5: `GET /api/projects/{id}/board?groupBy=status` returns all seven columns (including empty ones) ordered by `Rank`, with per-column `hasMore` and cursors. The client must not synthesise columns.
- **The assignee picker and the visibility banner have wire discriminators.** 44-2 AC6/AC7 return `source` (`audience-resolver | tenant-membership | single-user`) and `visibilityMode` (`tenant | per-user`) precisely so this UI can say which it is showing rather than silently misrepresenting scope.

## Acceptance Criteria

1. **Routes** added to `packages/dashboard/src/router.tsx` under `AuthGuard`: `/work` (list), `/work/board` (board), `/work/:key` (detail), `/work/projects` (project admin, under `TenantAdminGuard`). Nav entries in the existing layout.

2. **List page** reusing `DataTable` — columns key, title, kind, status, priority, assignee, iteration; filters for project, status set, kind, assignee, iteration; **keyset paging** driven by the API's cursor, not offset. Clicking a row routes to detail.

3. **Board page** rendering the API's columns verbatim, **including empty ones**, in the order returned. Cross-container drag between columns issues `POST /api/work-items/{id}/status`; drag within a column issues `POST /api/work-items/{id}/move` with `(afterId, beforeId)`. Per-column "load more" uses the column cursor.

4. **Optimistic drag with correct rollback.** A drag updates local state immediately, sends the request with `If-Match`, and on `409` **reverts to the server's state and shows a non-blocking notice**, never leaving the card in the dragged position. A test simulates a 409 and asserts the card returns.

5. **Detail page** — fields, inline edit issuing **single-field PATCHes** (44-2 AC3), parent/children navigation, and the **timeline** from `GET /api/work-items/{key}/timeline` rendering tracker events *and* the `DOCUMENT.*` / `APPROVAL.*` / `ESCALATION.*` rows for the same `issueId`, with foreign families visibly labelled as workflow activity.

6. **Three new primitives, added to `src/components/common/` as reusable components, not inlined into pages:**
   - `GroupedTable` — column-grouped rendering with per-group headers and counts.
   - `RowToggle` — row-level expand/collapse for parent rows revealing children.
   - `BoardColumn` / `BoardCard` — the `@dnd-kit` cross-container pair.
   Each ships with its own tests. They are placed in `common/` because Epic 43 Story 7 needs the first two as well (`docs/stories/epic-43/README.md:380-386`), and building them twice is the outcome to avoid.

7. **Honest degradation is rendered, not hidden.** When `GET /api/work-items/assignable` returns `source: "tenant-membership"`, the picker shows a one-line note that repo-scoped eligibility is not yet configured. When the list returns `visibilityMode: "tenant"`, the list header says the view is tenant-wide. Both disappear without a code change when 39-20 lands.

8. **One API client, in the house style, not twenty-third copy of `fetchJSON`.** `src/services/tracker/tracker-api-client.ts` follows `src/services/admin/admin-api-client.ts:11-17` — `API_BASE` defaulting to `'/api'` (**not** `''`, per the `repos-api-client.ts:11` inconsistency), `credentials: 'include'`, hand-written types with the "keep in sync with the backend record" comment the house uses (`src/services/onboarding/onboarding-api-client.ts:4-8`).

9. **A Zustand store** `src/stores/tracker/store.ts` following the four existing stores, holding board/list state, filters and optimistic-update bookkeeping.

10. **`pnpm --filter @tamma/dashboard test` is added to `.github/workflows/ci.yml`.** This turns on the package's existing 449 tests plus this story's. **Landing them green is part of this story**: if pre-existing tests fail, they are fixed or explicitly skipped with a `TODO` naming the owner — not left red, and not left off.

11. **`pnpm --filter @tamma/dashboard typecheck` is added to CI** in the same change. The package has the script; nothing runs it.

12. **Accessibility floor.** Drag is keyboard-operable via `@dnd-kit`'s keyboard sensor (grab, arrow, drop); columns and cards carry roles and labels; the status control is reachable without a pointer. A tracker board that only works with a mouse excludes users, and the repo has no other board to copy the pattern from.

## Technical Notes

- The board is the only place cross-container `@dnd-kit` is used in the repo. `ProviderChainEditor.tsx` is a `SortableContext` in one container; a board needs multiple droppable containers plus a `DragOverlay`, and the collision-detection strategy for column targets differs. Budget for reading `@dnd-kit` docs, not for adapting the existing file.
- No React Query is introduced. Adding a data-fetching library for one feature would leave the codebase with two paradigms; the Zustand + `fetchJSON` shape is what the other 22 clients do and consistency is worth more here than ergonomics.
- The timeline renders foreign event families **read-only and labelled**, never as tracker actions. A `DOCUMENT.ACCEPTED` row is workflow history, not something a user can undo from this page.
- Do not add a `/tasks` route. `packages/dashboard-user`'s `/tasks` is 39-19's decision inbox and the two must stay distinguishable; this route is `/work`.

## Dependencies

- **Stories 44-2 (API + `source`/`visibilityMode` discriminators), 44-3 (`move`, `parent`, subtree), 44-4 (board projection), 44-5 (timeline endpoint)** — all blocking.
- **Existing, no change required:** `DataTable`, `StatusBadge`, `ConfirmDialog`, `AuthGuard`, `TenantAdminGuard`, `@dnd-kit`, the router.
- **Adjacent:** Epic 43 Story 7 needs `GroupedTable` and `RowToggle` too (`epic-43/README.md:380-386`); AC6 puts them in `common/` so whichever lands second reuses rather than rebuilds. **Coordinate before starting.**

## Out of Scope

- Any work in `packages/dashboard-user`, including porting this UI there. Blocked on that package having a deployment path at all — an open question for the product owner (epic README).
- Saved views, custom filters persisted per user, swimlanes — deferred (epic README).
- Charts, burndown, velocity — Epic 36.
- Real-time board updates over SignalR. The hubs ship (`Tamma.Api/Hubs/`) but their audience resolver is a no-op stub; wiring to it would ship silence. Polling on focus is the v1 behaviour.
- Fixing `repos-api-client.ts:11`'s `API_BASE` inconsistency. Noted so it is not copied; changing it is someone else's regression risk.

## Estimated Effort

9 days — **the least reliable estimate in the epic.** Three primitives with no in-repo precedent, one of which is cross-container drag-and-drop, plus turning on a CI job that has never run against 449 existing tests.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

# Implementation Plan — Story 43-7: Action Catalog Admin UI

## Scope & Deliverable

When this story is done, `/admin/actions` in `packages/dashboard` renders every catalog member, grouped,
at a level the admin previews — with rows already automated at that level shown **greyed but still
editable**, so lowering the automation floor later needs no redesign. Flipping a row writes `L + 1` or `L`;
the admin never types a number. Three new `components/common/` primitives (`RowToggle`, `GroupedTable`,
`DimmedRow` + `InfoTooltip`) land as shared code, jointly owned with Epic 44's story 44-6. Every control PUTs
one field and refreshes — no draft state, no dirty tracking, and therefore no way to reproduce the
full-object-reset bug. The sidebar gains the new page plus the two admin pages that are currently
deep-link-only.

## Pre-Reading

- `docs/stories/epic-43/README.md` — the greyed-row requirement verbatim; S3 (level-independent storage,
  level-parameterized display); "Story 7 is the least reliable estimate in the plan"
- `docs/stories/epic-43/story-43-6/` — the exact route list, the single-field DTOs, and the server-computed
  `automatedAtLevel` / `editable` / `enforcementSites` fields this page binds to
- `packages/dashboard/src/components/layout/Sidebar.tsx:21-93` — `MEMBER_NAV_GROUPS`, `ADMIN_NAV_GROUPS`
  (`:61`, spreads member then appends), the `Administration` group at `:78-87` (three items), and the
  wholesale swap at `:93` with **no per-item predicate**
- `packages/dashboard/src/router.tsx:186-244` — the admin child routes; `/admin/acceptance-rules` at
  `:202-210` and `/admin/secrets` at `:234-244`, **both wrapped in `AdminGuard`**
- `packages/dashboard/src/guards/AdminGuard.tsx:20-35` (checks `useCurrentUser().isAdmin`, `<Navigate to="/account">`)
  vs `guards/TenantAdminGuard.tsx:23-70` (tenant role, inline 403, **and a "No active organization" screen
  when `tenantId` is falsy** — the reason for Correction 1)
- `packages/dashboard/src/components/common/Toggle.tsx:13-32` — the whole file. Layout at `:14-19`
  (`flex items-center justify-between py-3` + label + description), switch core at `:20-29`
  (`role="switch"`, `aria-checked`, `aria-label`, `disabled`, `opacity-50 cursor-not-allowed`), knob at `:30`
- `packages/dashboard/src/components/settings/security/SecuritySettingsPanel.tsx:62,68,74` — **the only three
  `<Toggle>` call sites in the app**; they must be byte-behaviour-identical after the refactor
- `packages/dashboard/src/components/monitoring/DataTable.tsx:1-45` — read it to confirm the non-fit:
  `DataTableColumn<T>` is `accessor`/`render` over flat rows with internal sort/filter/paginate/column-hide
  state. **Do not modify.**
- `packages/dashboard/src/components/prompts/prompt-constants.ts:39-50` — the stale `ACTIONS` array
  (`plan`, `implement`, `summarize`, `triage`, `debug`). **Never import.**
- `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts` +
  `packages/dashboard/src/hooks/admin/useAcceptanceRules.ts` — the api-client + hook shape to copy
- `packages/dashboard/src/services/admin/conventions-api-client.ts` — the mistyped `getActions`
  (`string[]` vs the server's `[{role, actions[]}]`); the cautionary precedent for hand-written types
- `packages/dashboard/src/components/common/ConfirmDialog.tsx:3-14` — props + usage
- `apps/tamma-elsa/src/Tamma.Studio/Pages/Admin/Alerts/AlertRules.razor:48-77` — the **only**
  shown-but-disabled-with-a-why-tooltip precedent in the repo; `:289-305` — the immediate-write-then-refresh
  posture this page adopts
- `packages/dashboard/src/pages/admin/acceptance-rules/AcceptanceRulesAdminPage.tsx` and its
  `RulesEditDialog.tsx` — the page this one sits beside; note 43-0 fixes its `acceptorRequirement` omission
- `docs/stories/epic-44/story-44-6/` (if drafted) — the joint consumer of `RowToggle` + `GroupedTable`

## Corrections to the design

1. **Guard: use `AdminGuard`, not `TenantAdminGuard`.** The design specifies `TenantAdminGuard` on the
   grounds that `actions:manage` grants admin+owner. But **every** existing admin page uses `AdminGuard`,
   including the two closest siblings (`/admin/acceptance-rules` at `router.tsx:202-210` and `/admin/secrets`
   at `:234-244`), and `TenantAdminGuard` renders a **"No active organization"** screen when `tenantId` is
   falsy (`TenantAdminGuard.tsx:45-70`) — which in single-user mode, or for an admin who has not switched
   into an org, would replace the page with an org-picker hint. Using `AdminGuard` keeps this page
   consistent with its neighbours and relies on the server's 403 (43-6 AC9) as the authoritative check. A
   later swap is one line. **Recorded as a deliberate divergence.**
2. **The `Administration` sidebar group has exactly three items** (`Sidebar.tsx:78-87`): `/admin`,
   `/admin/prompts`, `/admin/conventions`. The design's characterization is correct; the line reference is
   `:78-87`, and `ADMIN_NAV_GROUPS` begins at `:61` by spreading `MEMBER_NAV_GROUPS`.
3. **`Toggle` has no `size` prop and no variant surface** — it is a single fixed layout. So `RowToggle` is an
   **extraction of the switch core into a new component that `Toggle` then composes**, not a new prop on
   `Toggle`. Same outcome, different mechanic; stated so the diff is expected.

## Design Decisions

- **D1 — Three primitives in `components/common/`, props frozen jointly with Epic 44 story 44-6.** Both
  stories need a row-level toggle and a grouped table. Placing them in `common/` and agreeing their props
  once is the difference between one primitive and two bespoke copies. If 44-6 is not yet drafted when this
  starts, ship the primitives with deliberately minimal, additive-friendly props (no page-specific
  concepts leak in: `GroupedTable` knows about groups and rows, not about thresholds or autonomy).

- **D2 — `RowToggle` is the extraction; `Toggle` is re-implemented over it.** The switch core at
  `Toggle.tsx:20-29` is already correct and accessible (`role="switch"`, `aria-checked`, `aria-label`,
  disabled styling). Extracting it and rebuilding `Toggle` as `layout + <RowToggle>` means the three existing
  call sites need **zero** changes, and their behaviour is pinned by a DOM test on
  `SecuritySettingsPanel`. Copy-pasting the core instead would leave two switch implementations to keep
  accessible.

- **D3 — `GroupedTable` is new code, and the plan says so rather than implying a refactor.**
  `DataTable.tsx` is sort/filter/paginate/column-visibility over flat read-only rows with all state
  internal, and monitoring is its only consumer. Grouping + per-row mutation + dimming is a different
  component, not a superset. Building it separately costs ~180 LOC and costs monitoring nothing; extending
  `DataTable` would put group state and mutation callbacks into a component whose only current consumer
  wants neither. **This is the one place in the epic where reuse was hoped for and is not available.**

- **D4 — `DimmedRow` is a port from Blazor, and that is a real cost.** `AlertRules.razor:48-77` is the only
  shown-but-disabled-with-a-why-explanation pattern in the repo. React's alternative
  (`UsersTab.tsx`'s swap-control-for-Badge) is wrong here because the greyed-row contract **requires the
  control to stay interactive**. Port the semantics (`aria-disabled` on the row, reduced opacity, an
  explanatory tooltip) and explicitly **do not** propagate `disabled` to inner controls. A test
  (`keeps_threshold_control_editable_on_greyed_row`) is the guard against a future "fix" that disables them.

- **D5 — No draft state. Every control PUTs one field, then reloads.** Three consequences, all wanted:
  (i) it skips the `structuredClone` + `JSON.stringify` dirty-tracking every other admin page reimplements;
  (ii) it structurally cannot reproduce the full-object-reset bug, because there is no full object;
  (iii) it matches 43-6's single-field DTOs, so client and server are one design. The cost is one round trip
  per click — acceptable on an admin page, and the reload is a single `GET /policy?level=`.

- **D6 — Preview level is local state that never writes and never refetches.** `automatedAtLevel` arrives
  server-computed for the requested level, but the dimming predicate the UI applies on slider move is
  `preview >= row.minAutonomy` over data already held — so dragging the slider is instant and issues zero
  requests. The server value is used on load and after each mutation (so the greying rule and the gate rule
  are the same method at every point the data is fetched); the local predicate is the identical comparison,
  pinned by `dimmed_rows_recompute_on_slider_move_without_refetch` plus a test that the local predicate and
  the server field agree on load.

- **D7 — `LevelPreviewControl` is visually and structurally separated from `DialHeader`.** Conflating "the
  dial I am setting" with "the level I am previewing" is the most likely UX failure on this page. `DialHeader`
  is read-only with a deep link to `/admin/acceptance-rules`; `LevelPreviewControl` sits in its own bordered
  block with an explicit "preview only — does not change the dial" caption; and
  `level_preview_does_not_issue_a_write` is a test, not a hope.

- **D8 — `ThresholdControl` is three-state (two for non-escalatable) with an "advanced" numeric escape.**
  The three states map to `dial.min` / `preview + 1` / `dial.alwaysHuman` — every bound read from the `dial`
  payload, never a literal (`slider_bounds_come_from_dial_payload_not_literals`). For
  `escalatableToHuman === false` (every `automation:*` member) the middle state is absent, because a sweeper
  cannot suspend for a person and 43-6 rejects a mid-range value there anyway; rendering it would offer a
  choice the server refuses.

- **D9 — Confirmation is risk-and-direction-scoped, and covers the group case.** `ConfirmDialog` fronts any
  change that **lowers** a threshold on a `Destructive`-risk action, and any **group** threshold change that
  would lower a `Destructive` member — the group case is the dangerous one, because one PUT can un-gate
  several members at once. The dialog names the affected members.

- **D10 — Types are hand-written against the server DTOs and pinned by a keyset test.** The
  `conventions-api-client.ts` mistype is latent only because nothing calls it; here a mistype would silently
  send the wrong body. `putThreshold_body_keyset_equals_the_exported_DTO_keyset` is the client half of 43-6
  AC2.

## Implementation Steps

1. **CREATE `packages/dashboard/src/components/common/RowToggle.tsx`** (~50 LOC, D2) — the extracted switch
   core with `checked`, `onChange`, `disabled`, `ariaLabel`, and an optional `size`.
   **MODIFY `packages/dashboard/src/components/common/Toggle.tsx`** — keep its exported signature and
   layout, delegate the switch to `<RowToggle>`. **Do not touch `SecuritySettingsPanel.tsx`.**

2. **CREATE `packages/dashboard/src/components/common/GroupedTable.tsx`** (~180 LOC, D1/D3) — generic over
   `TGroup`/`TRow`, props: `groups`, `getRowsForGroup`, `renderGroupHeader`, `renderRow`, `getGroupId`,
   `getRowId`, `defaultExpanded`. Disclosure via a header button with `aria-expanded` / `aria-controls`;
   keyboard-operable; no internal sort/filter/pagination (out of scope and not needed for ~153 rows across
   15 groups).

3. **CREATE `packages/dashboard/src/components/common/InfoTooltip.tsx` and `DimmedRow.tsx`** (D4) —
   `InfoTooltip` is a small hover/focus-triggered explanatory bubble (ported semantics from
   `AlertRules.razor:68-77`); `DimmedRow` wraps a `<tr>` with `aria-disabled="true"` + `opacity-60` + the
   tooltip, and **explicitly does not** pass `disabled` to children.

4. **CREATE `packages/dashboard/src/services/admin/action-catalog-api-client.ts`** (AC6, D10) — `fetchJSON`
   + method object covering all 15 Story 43-6 routes; hand-written types (`DialDto`, `ActionPolicyRowDto`,
   `ActionGroupDto`, `EffectivePolicyResponse`, the four write bodies) exported so the keyset test can read
   them.

5. **CREATE `packages/dashboard/src/hooks/admin/useActionCatalog.ts`** (AC7, D5) — `Promise.all` over
   catalog + dial + policy on mount; `putGroupThreshold`, `putGroupEnforce`, `deleteGroup`,
   `putActionThreshold`, `putActionEnforce`, `putActionEnabled`, `putActionRoles`, `deleteAction`,
   `resetAll`, `decideAuthorization` — each mutate-then-reload. **No draft, no dirty flag.** Preview level is
   *not* in this hook (it is page state, D6).

6. **CREATE the page and its sub-components** under `packages/dashboard/src/pages/admin/actions/`:
   `ActionCatalogAdminPage.tsx`, `DialHeader.tsx`, `LevelPreviewControl.tsx`, `ProvenanceLegend.tsx`,
   `ThresholdControl.tsx`, `RiskBadge.tsx`, `ProvenanceBadge.tsx`, `GroupHeaderRow.tsx`,
   `PendingAuthorizationsPanel.tsx` (AC8–AC11, AC13). The `deploy-control` group header renders the
   LLM-tool-loop caveat from the server-supplied group description (AC13) — it must come from the API, not
   be hardcoded in the page.

7. **MODIFY `packages/dashboard/src/router.tsx`** (AC1, Correction 1) — add the `/admin/actions` route
   wrapped in `<AdminGuard>`, immediately after the `/admin/acceptance-rules` block at `:202-210`, with a
   comment naming this story and the guard rationale.

8. **MODIFY `packages/dashboard/src/components/layout/Sidebar.tsx:78-87`** (AC2) — add the three entries to
   the `Administration` group, with a comment noting that `/admin/acceptance-rules` and `/admin/secrets`
   were routed-but-unlisted before this story.

9. **CREATE the test files** — `pages/admin/actions/__tests__/ActionCatalogAdminPage.test.tsx`,
   `components/common/__tests__/{RowToggle,GroupedTable,DimmedRow}.test.tsx`,
   `services/admin/__tests__/action-catalog-api-client.test.ts`,
   `components/layout/__tests__/Sidebar.test.tsx` (amended or created). See Test Plan.

10. **Coordinate the primitive props with Epic 44 story 44-6** before merging step 2 — a short written
    agreement in that story's plan, not a verbal one.

## Test Plan

Vitest + Testing Library, the repo's dashboard convention. All server data mocked at the api-client
boundary; no MSW dependency introduced if the existing pages do not use one — follow whatever
`useAcceptanceRules`' tests do.

- **`ActionCatalogAdminPage.test.tsx`** —
  `renders_every_catalog_member_at_every_level` (a fixture with members whose thresholds straddle the range;
  assert the row count is constant across three preview levels);
  `greys_rows_automated_at_previewed_level`;
  `keeps_threshold_control_editable_on_greyed_row` (**the greyed-row contract** — the control fires
  `onChange` while `aria-disabled` is on the row);
  `dimmed_rows_recompute_on_slider_move_without_refetch` (assert zero additional client calls);
  `local_dim_predicate_agrees_with_server_automatedAtLevel_on_load`;
  `slider_bounds_come_from_dial_payload_not_literals` (a fixture dial of `{min: 0, max: 100}` renders bounds
  0–100 — this is the test that proves widening the dial needs no UI edit);
  `level_preview_does_not_issue_a_write`;
  `flipping_a_row_writes_L_plus_1_or_L_and_never_a_typed_number` (assert the PUT body value equals
  `preview + 1` / `preview`);
  `group_threshold_change_applies_to_unoverridden_members_only`;
  `action_row_badges_overrides_group_and_resets_to_group` (provenance badge + DELETE falls back);
  `lowering_destructive_threshold_requires_confirm` **and** `group_change_lowering_a_destructive_member_requires_confirm`;
  `automation_rows_render_two_state_control`;
  `zero_enforcement_sites_renders_not_enforced_badge`;
  `deploy_control_group_shows_the_tool_loop_caveat`;
  `member_sees_403` (server 403 → inline message, page does not crash).
  **Covers AC5, AC8–AC13.**
- **`RowToggle.test.tsx` + `existing_Toggle_call_sites_are_unchanged`** — `role="switch"`, `aria-checked`
  tracks `checked`, `disabled` applies `opacity-50 cursor-not-allowed` and suppresses `onChange`; plus a DOM
  test rendering `SecuritySettingsPanel` and asserting its three toggles behave exactly as before.
  **Covers AC3.**
- **`GroupedTable.test.tsx`** — renders group headers and rows; collapse hides rows and sets
  `aria-expanded="false"`; keyboard `Enter`/`Space` on the header toggles; per-row control slots receive
  their row; an empty group still renders its header. **Covers AC4.**
- **`DimmedRow.test.tsx`** — `aria-disabled="true"` on the `<tr>`, reduced-opacity class present, tooltip
  content reachable by hover **and** focus, and **inner controls are not disabled**. **Covers AC5.**
- **`action-catalog-api-client.test.ts`** — `putThreshold_body_keyset_equals_the_exported_DTO_keyset` and
  the same for enforce/enabled/roles; every method targets the documented route and verb.
  **Covers AC6.**
- **`Sidebar.test.tsx`** — the `Administration` group renders all three new entries for an admin user and
  none of them for a member. **Covers AC2.**
- **Route test** — `/admin/actions` renders the page for an admin and redirects a non-admin (matching
  `AdminGuard` semantics). **Covers AC1.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — route + `AdminGuard` | 7 | Route test |
| 2 — three sidebar entries | 8 | `Sidebar.test.tsx` |
| 3 — `RowToggle`, `Toggle` re-implemented, call sites unchanged | 1 | `RowToggle.test.tsx`, `existing_Toggle_call_sites_are_unchanged` |
| 4 — `GroupedTable`, `DataTable` untouched | 2 | `GroupedTable.test.tsx`; `git diff` shows no change to `DataTable.tsx` |
| 5 — `DimmedRow` + `InfoTooltip`, controls stay live | 3 | `DimmedRow.test.tsx`, `keeps_threshold_control_editable_on_greyed_row` |
| 6 — api client, keyset-pinned | 4 | `action-catalog-api-client.test.ts` |
| 7 — hook, no draft state, preview never writes/refetches | 5 | `level_preview_does_not_issue_a_write`, `dimmed_rows_recompute_…_without_refetch` |
| 8 — page composition | 6 | `ActionCatalogAdminPage.test.tsx` render assertions |
| 9 — three-state control, bounds from payload, two-state for automation | 6 | `slider_bounds_come_from_dial_payload_not_literals`, `automation_rows_render_two_state_control` |
| 10 — "not enforced" badge | 6 | `zero_enforcement_sites_renders_not_enforced_badge` |
| 11 — confirm on destructive lowering, incl. group scope | 6 | the two confirm tests |
| 12 — the named test list | 9 | All present and green |
| 13 — deploy caveat visible in the UI | 6 | `deploy_control_group_shows_the_tool_loop_caveat` |

## Risks & Mitigations

- **The estimate. Three primitives with no in-repo React precedent is the epic's largest unknown.**
  Mitigation: build all three **first** (steps 1–3, ~1.5 days) and review them before the page exists, so a
  slip is visible on day two rather than day five. If `GroupedTable` proves harder than expected, the
  fallback is a flat table with sticky group header rows and no collapse — degraded, shippable, and it does
  not change any test that matters except the two collapse assertions.
- **A future "fix" disables the controls on greyed rows.** It looks like a bug to anyone who has not read
  the requirement. Mitigation: `keeps_threshold_control_editable_on_greyed_row`, plus a comment in
  `DimmedRow` stating that not propagating `disabled` is the point.
- **Preview level conflated with the dial.** The most likely UX failure. Mitigation: D7's structural
  separation, the explicit caption, the read-only `DialHeader` deep-linking elsewhere, and
  `level_preview_does_not_issue_a_write`.
- **Guard divergence from the design (Correction 1).** If a tenant-role distinction is genuinely required,
  `AdminGuard` under-restricts relative to intent. Mitigation: the server 403 is authoritative
  (43-6 AC9), `member_sees_403` proves the page handles it, and the swap is one line.
- **Primitives diverge from Epic 44's needs.** Mitigation: step 10 — a written props agreement before
  merging step 2. If 44-6 has not been drafted, keep the props minimal and page-agnostic so widening is
  additive.
- **~153 rows across 15 groups with a control per row.** Not obviously slow, but not measured. Mitigation:
  the dim predicate is pure and local (no refetch), rows are memoized by key, and groups default to
  collapsed except the first — revisit only if a render profile says so.
- **43-6 slip blocks everything visual.** Mitigation: steps 1–3 (the primitives) depend on nothing and can
  be built and merged against 44-6's needs alone.

## Blocks / Blocked by

- **Blocked by:** 43-6 (every route and DTO — hard for steps 4–9; steps 1–3 are independent),
  43-1/43-3 transitively (the dial and catalog payloads).
- **Blocks:** nothing in Epic 43 — this is the top of the declarative spine. Story 43-9 adds enforcement
  behind the policy this page authors; the `PendingAuthorizationsPanel` becomes populated when 43-9's ledger
  writes begin.
- **Coordinates with:** Epic 44 story 44-6 (`RowToggle`, `GroupedTable` — shared, props frozen jointly).

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | `RowToggle` + `Toggle` re-implementation + call-site pin | 0.4 |
| 2 | `GroupedTable` (~180 LOC, a11y disclosure) | 0.9 |
| 3 | `InfoTooltip` + `DimmedRow` (Blazor port) | 0.5 |
| 4 | api client + hand-written pinned types | 0.5 |
| 5 | `useActionCatalog` hook (10 mutators, no draft state) | 0.5 |
| 6 | Page + 9 sub-components (`ThresholdControl` is the substantial one) | 1.6 |
| 7, 8 | Router entry + three sidebar entries | 0.2 |
| 9 | Test files (6, incl. the 15-case page suite) | 1.2 |
| 10 | Epic 44 props agreement, review polish | 0.2 |
| **Total** | | **6.0** (story estimate: 6 days — **the least reliable number in the epic**; steps 2–3 carry the variance) |

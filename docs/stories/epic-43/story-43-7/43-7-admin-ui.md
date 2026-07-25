# Story 43-7: Action Catalog Admin UI

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

As a **tenant admin** (SaaS) or **the sole user** (self-hosted),
I want one page that lists every action Tamma can take, grouped, showing at a level I choose which ones the system does by itself and which wait for a person — including the ones already automated, greyed but still editable,
So that I can set the policy by clicking a row rather than by knowing what number to type, and so lowering the automation floor later needs no redesign.

## Priority

P0 — The product requirement is a UI requirement: *"the admin can set at each level what groups or
individual actions are set to automate and what for users to do, even those automated at 70 should be listed
and greyed out so a future lower automation is doable."* Everything below this story is machinery for it.

## READ THIS FIRST: this is the least reliable estimate in the epic

**Three React primitives this page needs have no in-repo precedent.** Not "a similar thing exists that needs
extending" — nothing composes these behaviours in React today:

1. **A row-level toggle.** `packages/dashboard/src/components/common/Toggle.tsx:13-32` is a
   `flex items-center justify-between py-3` **full-width label + description + switch row**, used in exactly
   three places, all `components/settings/security/SecuritySettingsPanel.tsx:62,68,74`. Its switch core
   (`<button type="button" role="switch" aria-checked disabled className="… opacity-50 cursor-not-allowed">`,
   `:20-29`) is exactly right; its layout is not. **No React table in this repo renders a toggle inside a
   row.**
2. **A grouped / collapsible table.** Grouping today means **separate Cards** (`AgentsOverview.tsx`,
   `SystemPromptEditor.tsx`); collapsing means a bespoke ▲/▼ button (`ResolutionTestPanel.tsx`) or a raw
   `<details>` (`RunDetailPage.tsx`). Nothing composes group headers + expand/collapse + per-row interactive
   controls.
3. **A dimmed row with a why-disabled tooltip.** Every disabled affordance in React here **swaps the control
   for a static `<Badge>`** (`UsersTab.tsx`), which destroys the affordance rather than dimming it. The only
   real shown-but-disabled-with-an-explanation precedent in the entire repo is **Blazor**:
   `apps/tamma-elsa/src/Tamma.Studio/Pages/Admin/Alerts/AlertRules.razor:48-77`. It is **ported**, not
   invented — but porting across frameworks is not the same as reusing a component.

The 6-day estimate assumes all three land cleanly. Treat any slip here as expected, not exceptional, and
flag it in the first two days rather than the last.

**Do NOT bend `components/monitoring/DataTable.tsx`.** It is a self-contained sort/filter/paginate/
column-visibility table over **flat, read-only rows** (`DataTableColumn<T>` with `accessor`/`render`,
`:13-40`), imported only by monitoring pages. Grouping, per-row mutation and dimming are not extensions of
it; bending it would regress its only consumer. `GroupedTable` is **new code where reuse was hoped for** —
stated plainly rather than presented as a refactor.

**Coordinate with Epic 44's story 44-6, which needs the same row-toggle and grouped-table primitives.** Both
go in `components/common/`, and 44-6 must be in the room when their props are frozen — a second bespoke copy
is the failure mode.

## Architectural Context (READ FIRST)

**Target app: `packages/dashboard`.** It is the deployed admin SPA (app.tamma.dev, `docker/Dockerfile.dashboard`,
`docker/nginx-proxy.conf.template:81-178`). `packages/dashboard-user` has no docker service and no deploy
step — only a CI test line — so shipping there would need new infrastructure.

**The sidebar manifest is static and swapped wholesale.**
`packages/dashboard/src/components/layout/Sidebar.tsx:93` does
`const navGroups = isAdmin ? ADMIN_NAV_GROUPS : MEMBER_NAV_GROUPS`, with **no per-item permission
predicate**. Its `Administration` group (`:78-87`) lists exactly three items: `/admin`, `/admin/prompts`,
`/admin/conventions`. **`/admin/acceptance-rules` and `/admin/secrets` are routed but absent from the
manifest** — they are deep-link-only today. This story adds all three entries.

**Correction to the design — guard choice.** The design proposes wrapping the new page in
`TenantAdminGuard`. Every existing admin page uses `AdminGuard`, including the closest sibling:
`router.tsx:204-210` wraps `AcceptanceRulesAdminPage` in `<AdminGuard>`, and `/admin/secrets` does the same
(`:237-244`). Two facts matter:
- `AdminGuard` (`guards/AdminGuard.tsx:20-35`) checks `useCurrentUser().isAdmin` and **redirects** to
  `/account` when false.
- `TenantAdminGuard` (`guards/TenantAdminGuard.tsx:23-70`) checks the tenant role, renders an **inline
  403**, and — importantly — renders a **"No active organization" screen when `tenantId` is falsy**.
In single-user mode a user without an active tenant selected would get that screen instead of the page.
Decision: **use `AdminGuard`**, matching the acceptance-rules precedent this page sits beside, and rely on
the server's 403 (Story 43-6 AC9) for the authoritative check. If a tenant-role distinction is later needed,
it is a one-line guard swap. Record the divergence from the design explicitly.

**Data flow — house pattern, no store, no draft state.** Per-domain
`services/admin/<domain>-api-client.ts` exporting `fetchJSON` + a method object
(`acceptance-rules-api-client.ts`), consumed by a per-page hook with load-on-mount / mutate-then-reload
(`hooks/admin/useAcceptanceRules.ts`). Zustand only for cross-page state. **Every other admin page
hand-rolls dirty state** with `structuredClone` + `JSON.stringify` compare (`AgentsOverview.tsx`,
`PhaseRoleMatrix.tsx`, `SecuritySettingsPanel.tsx`). This page does **not**: each control PUTs its single
field immediately and refreshes (the `AlertRules.razor:289-305` posture). That skips the reimplementation
**and** structurally prevents the full-object-reset bug — which is exactly what 43-6's single-field DTOs
were designed for.

**Two hard sourcing rules.**
- **Never import `components/prompts/prompt-constants.ts`.** Its `ACTIONS` array (`:39-50`) hardcodes ten
  stale names — `plan`, `implement`, `summarize`, `triage`, `debug` — against real wires like
  `implement-feature`, `implement-fix`, `triage-intake`. Everything comes from `GET /api/actions/catalog`.
- **Hand-write the TS types against the actual server shape and pin them.** Cautionary precedent:
  `services/admin/conventions-api-client.ts` types `getActions` as `string[]` while the server returns
  `[{role, actions[]}]` — latent only because nothing calls it.

## The greyed-row contract (the product requirement, precisely)

- **Every catalog member renders at every level.** Nothing is absent from the table because of where the
  floor currently sits.
- The admin picks a **preview level** `L`. A row greys iff it is automated at `L`
  (`automatedAtLevel`, computed server-side by the same method the gate calls, so the greying rule cannot
  drift from the enforcement rule).
- **A greyed row's control stays visible and fully editable.** Greying communicates "automated right now";
  it locks nothing. Setting a threshold that only matters at a future lower floor is the entire point of the
  requirement.
- **The admin never types a number.** Flipping a row automated → human writes `MinAutonomy = L + 1`;
  human → automated writes `MinAutonomy = L`. A numeric input exists only behind an "advanced" affordance.
- The preview control is **display-only** and must be visually and structurally separated from the dial
  readout. Conflating "the dial I am setting" with "the level I am previewing" is the most likely UX failure
  on this page.

## Acceptance Criteria

1. **Route + guard.** `/admin/actions` is registered in `router.tsx` as a child of the existing
   `AuthGuard`+`AppLayout` parent, wrapped in `AdminGuard` (matching `/admin/acceptance-rules` at
   `:204-210`), rendering `ActionCatalogAdminPage`.

2. **Sidebar manifest gains THREE entries.** `Sidebar.tsx`'s `Administration` group (`:78-87`) gains
   `{ to: '/admin/actions', label: 'Action Catalog' }` **and** the two currently deep-link-only pages:
   `{ to: '/admin/acceptance-rules', label: 'Acceptance Rules' }`, `{ to: '/admin/secrets', label: 'Secrets' }`.
   A test asserts all three render for an admin user.

3. **`components/common/RowToggle.tsx`** — a compact switch extracted from `Toggle.tsx:20-29`'s core
   (`role="switch"`, `aria-checked`, `aria-label`, `disabled` → `opacity-50 cursor-not-allowed`), with no
   full-width label/description layout. **`Toggle` is re-implemented over it** so its three call sites
   (`SecuritySettingsPanel.tsx:62,68,74`) are unchanged — asserted by a snapshot/DOM test on the existing
   panel.

4. **`components/common/GroupedTable.tsx`** — group header rows with expand/collapse, per-group and per-row
   control slots, keyboard-operable disclosure (`aria-expanded`, `aria-controls`). Generic over the row type.
   **Not** built on `DataTable.tsx`; `DataTable.tsx` is not modified and its monitoring consumers are
   untouched.

5. **`components/common/DimmedRow.tsx` + `components/common/InfoTooltip.tsx`** — `aria-disabled="true"` on
   the `<tr>`, `opacity-60`, and a why-tooltip explaining the dim, ported from
   `Tamma.Studio/Pages/Admin/Alerts/AlertRules.razor:48-77`. **Inner controls remain enabled and
   interactive** (the greyed-row contract). A test asserts a dimmed row's threshold control still fires
   `onChange`.

6. **`services/admin/action-catalog-api-client.ts`** — `fetchJSON` + method object covering every Story 43-6
   route, with hand-written types pinned to the server DTOs. A test asserts each PUT body's key set equals
   the single-field DTO's key set exactly (`putThreshold_body_keyset_equals_the_exported_DTO_keyset`) — the
   client-side half of the anti-`acceptorRequirement` guarantee.

7. **`hooks/admin/useActionCatalog.ts`** — load-on-mount with `Promise.all` over catalog + dial + policy;
   mutate-then-reload; **no draft state, no dirty tracking**; the preview level is local React state that
   **never triggers a write** and **never triggers a refetch** (the dimming recomputes from data already
   held).

8. **`ActionCatalogAdminPage` composition** — a read-only `DialHeader` (deep-linking to
   `/admin/acceptance-rules` where the dial is actually set), a clearly-captioned display-only
   `LevelPreviewControl`, a `ProvenanceLegend` (`platform-ceiling` | `always-escalate-legacy` |
   `action-override` | `group-override` | `system-default`), the `GroupedTable`, and a
   `PendingAuthorizationsPanel`.

9. **`ThresholdControl` is a three-state segmented control** — *Automated* (`MinAutonomy = dial.min`) /
   *Human below this level* (`= preview + 1`) / *Always human* (`= dial.alwaysHuman`) — plus an "advanced"
   numeric input behind a disclosure. **Bounds come from the `dial` payload, never from literals.**
   For a row with `escalatableToHuman === false` (every `automation:*` member) it renders **two-state**,
   because a sweeper cannot suspend for a person and a mid-range value there would silently behave as Deny.

10. **`enforcementSites === 0` renders a visible "not enforced" badge** with a tooltip. The page must not
    imply protection that no seam provides.

11. **A `ConfirmDialog` (`components/common/ConfirmDialog.tsx`) fronts every lowering change on a
    `Destructive`-risk action**, and every **group-scope** threshold change that would lower a `Destructive`
    member. A group PUT that silently un-gates several destructive members is the failure this guards.

12. **Tests** (`pages/admin/actions/__tests__/ActionCatalogAdminPage.test.tsx` + per-primitive tests):
    `renders_every_catalog_member_at_every_level`,
    `greys_rows_automated_at_previewed_level`,
    `keeps_threshold_control_editable_on_greyed_row`,
    `dimmed_rows_recompute_on_slider_move_without_refetch`,
    `slider_bounds_come_from_dial_payload_not_literals`,
    `level_preview_does_not_issue_a_write`,
    `flipping_a_row_writes_L_plus_1_or_L_and_never_a_typed_number`,
    `group_threshold_change_applies_to_unoverridden_members_only`,
    `action_row_badges_overrides_group_and_resets_to_group`,
    `lowering_destructive_threshold_requires_confirm`,
    `automation_rows_render_two_state_control`,
    `zero_enforcement_sites_renders_not_enforced_badge`,
    `member_sees_403`,
    `existing_Toggle_call_sites_are_unchanged`.

13. **The `deploy-control` group description states the LLM-tool-loop caveat in the UI, not only in a doc.**
    Production deploy is an LLM tool loop dispatched as a generic `llm-call` with `enableTools=true` —
    gating the deploy effect gates the **stage transition**; the deploy itself happens inside the loop.
    An admin reading "Deployment control" must see that.

## Dependencies

- **Story 43-6 (Admin API + RBAC)** — every shape this page binds to. **Blocking.** The DTO key sets are
  pinned on both sides.
- **Story 43-1** — `AutonomyDial` reaches the UI through `GET /api/actions/dial`; no bound is hardcoded here.
- **Story 43-3** — group titles, descriptions and risk classes come from `GET /api/actions/catalog`.
- **Existing, verified:** `Sidebar.tsx:61-93`; `router.tsx:186-244`; `guards/AdminGuard.tsx:20-35`;
  `guards/TenantAdminGuard.tsx:23-70`; `components/common/{Toggle,ConfirmDialog,Badge,Card,Slider}.tsx`;
  `components/monitoring/DataTable.tsx:13-40` (**not** extended);
  `services/admin/acceptance-rules-api-client.ts` + `hooks/admin/useAcceptanceRules.ts` (the shape to copy);
  `Tamma.Studio/Pages/Admin/Alerts/AlertRules.razor:48-77` (the dimmed-row port source).
- **Coordinates with:** Epic 44 story 44-6 — same `RowToggle` + `GroupedTable`. Freeze their props jointly.

## Out of Scope

- **Editing the dial itself.** `DialHeader` is read-only and deep-links to `/admin/acceptance-rules`.
- **A platform-ceiling editor.** Ceiling rows are rendered as provenance and are not writable (43-6 D8).
- **Modifying `DataTable.tsx`** or any monitoring page.
- **`packages/dashboard-user`.** No docker service, no deploy step.
- **A level×action matrix view.** 31 levels × ~153 actions is unusable and admits non-monotone policy.
- **Migrating the legacy always-escalate entries.** Rows with `source: always-escalate-legacy` render with a
  deep link and a "migrate to a catalog row" affordance that writes the equivalent row and deliberately does
  **not** auto-delete the legacy entry — the deletion stays in the acceptance-rules UI.
- **Any enforcement behaviour.** Story 43-9.

## Estimated Effort

6 days — **and this is the epic's least reliable estimate** (see the first section).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

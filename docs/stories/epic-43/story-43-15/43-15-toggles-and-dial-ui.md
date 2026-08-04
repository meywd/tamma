# Story 43-15: Toggle Encoding and the Dial as Detents With a Diff Preview

Status: drafted

Implements: Story 43-11 **Amendment 2, sections E (toggle encoding, level-ownership predicate) and H (detents + diff preview)**, and the Supersessions note closing OQ6.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **admin who switched a specific action on, and later moves the dial**,
I want my explicit choice to survive dial moves visibly — never silently killed by a dial drop, never silently resurrected by a dial return — and I want moving the dial to show me exactly what changes before I commit,
So that "less automation please" is a reviewed decision and the dial is informed consent, not a smooth slider over invisible consequences.

## Priority

P1 — Amendment 2-E state-machined 43-11's original toggle encoding (`min_autonomy = dial-at-mint`) and found it fails the product rule twice (silent kill on dial drop, path-dependent resurrection), and verified that **group-scope rows bypass level ownership in both directions** as the resolver is built. The 409/greying rules of 43-11 AC8/AC12 are wrong until this lands.

## Architectural Context (READ FIRST)

- **Storage is unchanged**: a toggle is one `action_assignments` row (`apps/tamma-elsa/src/Tamma.Data/Entities/ActionAssignment.cs:60-75` — `target_kind='action'`, nullable `MinAutonomy`, no numeric CHECK). What changes is the **value written and the predicate read**.
- **The encoding fix (Amendment 2-E)**: a toggle is stored as **`min_autonomy = AutonomyDial.Min`** — "automated, period" — so the row's meaning is a constant function of the dial. Mint-time dial provenance goes in the audit event, not the arithmetic. Zero schema change, zero resolver change (`AutonomyGateEvaluator`'s `max()`/`??` ladder, `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:11-17`, already resolves it correctly).
- **The predicate fix (closes OQ6)**: `levelOwned` — and the 409 on `PUT …/threshold` — keys on **what the ladder WITHOUT the action row resolves** (shipped level, group rows, and platform ceiling all included), not on the shipped level alone. That closes the group-row bypass: a group row lowering a whole group below the dial no longer re-opens per-action editing on rows the ladder already automates, and DELETE's fallback is computed against the same ladder-without-the-row.
- **What the API ships today** (43-11 AC8/AC12 baseline, amended here): `GET /api/actions/policy?level=NN` returns `editable = true` unconditionally (`apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs:145-148`, S3 comment); `PUT /api/actions/policy/actions/{ns}/{key}/threshold` at `:187-200`; the dial is published at `GET /api/actions/dial` (`:57`). The machinery rows take no threshold at all (43-13).
- **Dial-lower flow (Amendment 2-E)**: lowering the dial leaves toggles standing **visibly** — the flow enumerates surviving toggles (rows at `Min` whose shipped level is now above the new dial) and offers bulk revoke. "Less automation please" is an explicit review, not a silent side-effect.
- **Detents + diff preview (Amendment 2-H)**: the dial UI renders the **discrete meaningful positions** (the distinct shipped levels over the 156 dial rows), not a smooth 1–100 slider — "a smooth slider over 13 meaningful positions is false UI". Selecting a new detent shows a diff: "raising 70 → 75 automates: N actions", each with its last-30-day fire count and approve rate, computed from the grant table (`action_authorizations`) and the per-effect mediation event families (`GIT.PR_MERGED.*`, `AGENT_DISPATCH.RUN_TRIGGERED.*`, outbox rows — Amendment 2-H's verified free channels). Where telemetry is empty (the H chicken-and-egg), the count column renders "no data", never zero.
- **The UI lands in `packages/dashboard`.** The 43-7 admin actions page is **not built** (no `ActionCatalogAdminPage` exists in the tree; the only shipped admin surface is acceptance-rules — `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx`). Per 43-11's Dependencies note, this story's UI ACs fold into the 43-7 build if it has not started; if it has, they amend it. Coordinate before scheduling.

## Acceptance Criteria

1. **Toggle-on writes `min_autonomy = AutonomyDial.Min`** (never the current dial value); the audit event carries the mint-time dial. 43-11 AC8's "only legal value is the caller's current dial" is superseded: the PUT body's `minAutonomy` must equal `AutonomyDial.Min`, anything else is 400, and the story updates 43-11's AC text with a pointer here.
2. **A toggle survives dial moves, visibly.** Test sequence: toggle on at dial 70 → lower dial to 60 → the action is still automated (row at `Min` wins) and the policy view flags it `toggleAboveDial: true`; raise back to 70 → no path-dependent state change. The Amendment 2-E failure sequence (silent kill / silent resurrect) is the named regression this test exists to prevent.
3. **`levelOwned` and the 409 key on the ladder-without-the-action-row.** With a group row at `Min` covering an action whose shipped level is above the dial, `PUT …/threshold` on that action returns 409 `ACTION_POLICY.LEVEL_OWNED` (the ladder already automates it) — the group-row bypass test from Amendment 2-E's verification, inverted into a pin. Symmetrically, an action whose shipped level is ≤ dial but which a group/ceiling row holds shut is **editable** (not level-owned).
4. **DELETE falls back against the same ladder**: deleting an action row where a group row exists resolves to the group row's outcome, and the response names the surviving source (`group` | `shipped` | `ceiling`).
5. **The diff API exists**: `GET /api/actions/policy/diff?from=L1&to=L2` returns the actions whose automated-state differs between the two dial positions, each with `shippedLevel`, direction, last-30-day fire count and approve rate (null when no telemetry). Symmetric: `from > to` returns the de-automated set plus the surviving toggles.
6. **The dial UI is detents with a preview**: the control offers exactly the distinct shipped levels (plus current position); choosing one renders the diff from AC5 and requires an explicit confirm. No free-form 1–100 input remains for the base dial.
7. **The dial-lower flow enumerates surviving toggles** with a bulk-revoke action (deletes the listed rows in one call, audited individually). Declining the revoke keeps the toggles and the confirmation records that choice.
8. **The policy view fields land**: `shippedLevel`, `levelOwned` (per AC3's predicate), `editable = !levelOwned`, `reason`, `toggleAboveDial` — replacing the unconditional `editable = true` at `ActionPolicyEndpoints.cs:148`. `PolicyView_MarksLevelOwnedRowsNonEditable` and a group-row variant pin it.
9. **`enabled` stays orthogonal and always writable** (43-11 M3 rule 3) — regression-pinned.
10. **`dotnet test` and dashboard tests green; no schema change.**

## Dependencies

- **Story 43-11** — the level model, M3/M4, AC8/AC12 this story amends in place (with pointers, not silent contradiction — the 43-11 AC15 convention).
- **Story 43-5** — `action_assignments` + resolver. Landed; not extended.
- **Story 43-7** — the admin UI host page; see the coordination note above. UI ACs (6, 7) land wherever 43-7 lands.
- **Story 43-14** — the grant table feeding approve rates; the diff renders "no data" without it, so not blocking.
- **Story 43-13** — machinery rows take no toggle; the policy view excludes them from detent math (156 dial rows, not 197).
- **Verified in tree**: `ActionAssignment.cs:60-75`; `AutonomyGateEvaluator.cs:11-17`; `ActionPolicyEndpoints.cs:57,145-148,187-200`; `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx` (the only shipped rules UI; no actions admin page exists).

## Out of Scope

- A platform-ceiling write path (still out per 43-6).
- Group-row UX beyond the bypass-closing predicate — group rows stay the admin bulk tool.
- The telemetry emitters themselves (`.ALLOWED` count events, Seam B decision events, the actionKey index — Amendment 2-H's other ACs live with the seam work in 43-9's lane, not here; this story only reads what exists).

## Estimated Effort

4 days — 1 for encoding + predicate + DELETE fallback with tests, 1 for the diff API, 2 for the detent UI, preview, and dial-lower flow (or their fold-in to 43-7).

## Change Log

| Date       | Version | Changes                                                                  | Author |
| ---------- | ------- | ------------------------------------------------------------------------ | ------ |
| 2026-08-02 | 1.0.0   | Initial story — toggle encoding at Min, ladder-without-row predicate, detents + diff preview, dial-lower review (43-11 Amendment 2 E/H) | Claude |

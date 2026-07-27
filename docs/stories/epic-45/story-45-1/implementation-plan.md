# Implementation Plan — Story 45-1: The Contract the Tests Certified Wrong

## Scope & Deliverable

When this story is done, `SubscribeResponse` in `packages/dashboard-user/src/api/pricing.ts` is a
field-for-field mirror of the C# `PlanAssignmentResponse`, the downgrade warning in
`UpgradePlanModal` actually renders (it renders nothing today), the `direction` and
`scheduledEffectiveAt` facts the server already computes reach the customer, `ApiClient` grows a
`patch` method so the one PATCH in the app stops bypassing the shared refresh-on-401 path, and the
test fixtures are copied from the C# DTO rather than invented. No server change.

## Pre-Reading

- `docs/stories/epic-45/README.md` — Gap 4
- `packages/dashboard-user/src/api/pricing.ts:114-126` — the speculative `SubscribeResponse`
- `packages/dashboard-user/src/api/pricing.ts:26-41` — `METRIC_KEYS` + `metricKeyLabel`, and the
  comment explaining why `metricKey` arrives as an ordinal on one path and a string on another
- `packages/dashboard-user/src/components/pricing/UpgradePlanModal.tsx:160-185` — the submit handler
- `packages/dashboard-user/src/components/pricing/UpgradePlanModal.test.tsx:150-180` — the fixture to
  replace
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs:280-292` — `Subscribe`, and the
  `ToPlanAssignmentResponse` hand-off
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs:884-896` — the projector
- `apps/tamma-elsa/src/Tamma.Api/Dtos/Admin/AdminTenantDtos.cs:135-148` — `PlanAssignmentResponse`
  and `PlanAssignmentWarningItem`. **This file is the source of truth for AC1 and AC6.**
- `packages/dashboard-user/src/api/client.ts:58-73` — `put`/`delete`, the shape `patch` copies
- `packages/dashboard-user/src/api/client.ts:88-113` — the refresh-on-401 path the PATCH is missing
- `packages/dashboard-user/src/api/alerts.ts:169-195` — `updateTenantChannel` and its bare `fetch`
- `packages/dashboard-user/src/api/client.test.ts` — the existing 401-refresh test shape to mirror
- **All referenced paths exist.** This story creates no new file.

## Design Decisions

- **D1 — Make the DTO's fields required, not optional.** The old interface declared seven optional
  members of which four were never sent and two that *were* sent were undeclared. Optionality is
  exactly what made a wrong contract typecheck. Required members mean the next server change that
  drops or renames a field surfaces at compile time in the consumer. The cost is that a genuinely
  optional future field must be declared optional deliberately — which is the correct amount of
  friction.

- **D2 — Mirror the C# record exactly, including field order, and cite it in a comment.** Do not
  "improve" the shape on the way across (no renaming `planVersion` to `version`, no flattening
  `warnings` into strings). Every historical divergence here started as a small client-side
  improvement. The TS interface carries `// Mirrors PlanAssignmentResponse — Dtos/Admin/AdminTenantDtos.cs:135-148`
  so a grep from either side finds the other.

- **D3 — `metricKey` normalizes through the existing `metricKeyLabel`, not a new helper.**
  `pricing.ts:26-41` already documents and solves this: the entitlements endpoint returns snake_case
  strings, a `PlanSnapshot` read returns the numeric ordinal because no string-enum converter is
  registered. `PlanAssignmentWarningItem.MetricKey` is on the second path, so it arrives as an
  ordinal. Reusing the helper keeps one place to fix when a converter is eventually registered;
  adding a second is how `METRIC_KEYS` drifts from the C# enum's declaration order.

- **D4 — Do not rename `Warnings` server-side.** `PlanAssignmentResponse` is shared with the admin
  plan-assignment path (`AdminTenantsEndpoints.cs:884`). Renaming it to match one client's guess is a
  larger change touching an admin surface, with no benefit and a real risk of breaking the admin
  dashboard's pricing page. The client was wrong; fix the client.

- **D5 — `direction` is the server's answer and the client must not re-derive it.** A plan change can
  raise one entitlement and lower another; "is this an upgrade?" has no correct client-side answer
  from price ordering. The server computes `Direction` precisely so the client does not have to
  guess. Surfacing it is a one-line render and it prevents a whole category of future bug where the
  UI cheerfully calls a mixed change an upgrade.

- **D6 — `ApiClient.patch` rather than a second bare `fetch`.** `alerts.ts:180` carries the comment
  "(no apiClient.patch)" — the author knew, and worked around it. The workaround costs the
  refresh-on-401 retry on the one call it applies to, and it spawned a duplicated base-URL resolver
  (`alerts.ts:247-253`). Adding the eight-line method removes both, and the next PATCH inherits it.

## Implementation Steps

1. **Replace `SubscribeResponse`** — `pricing.ts:114-126`. Delete the seven speculative optional
   members; add `PlanAssignmentWarning` and the seven required members of AC1, with the D2 citation
   comment. Delete the stale `violations` comment at `:124`.
2. **Update `tenantPricingApi.subscribe`'s return type** — `pricing.ts:148-149`. No signature change,
   the generic parameter now names the corrected interface.
3. **Rewrite the warning branch** — `UpgradePlanModal.tsx:171-172`. Read `resp.warnings`; render one
   line per item via a small local `formatWarning(w: PlanAssignmentWarning): string` that calls
   `metricKeyLabel(w.metricKey)` and omits either number when null. Keep it a `setWarning` string
   list rather than a new component — the modal is already 307 lines and this is three lines of it.
4. **Surface `direction`** — the post-submit message reads `resp.direction` instead of inferring.
   Check what values the server emits (`AdminTenantsEndpoints.cs` around `:884`) before writing the
   label map; do not guess the casing.
5. **Surface `scheduledEffectiveAt`** — when non-null, append "takes effect <date>" to the
   confirmation. Format with `toLocaleString()`, matching `DashboardHome.tsx:118`'s existing
   convention in this package.
6. **Add `ApiClient.patch<T>()`** — `client.ts`, immediately after `put` (`:58-69`). Identical body:
   spread `init`, set `method: 'PATCH'`, merge `Content-Type: application/json`, serialize a defined
   body, delegate to `this.request`. It inherits refresh-on-401 by construction.
7. **Rewrite `updateTenantChannel`** — `alerts.ts:169-195`. Keep the `hasPlaintextCredential`
   pre-flight (`:174-179`) unchanged; replace the bare `fetch` block with
   `apiClient.patch<ChannelDto>(...)`. Delete the now-unused `apiClientBaseUrl()` at `:247-253` and
   confirm nothing else references it.
8. **Rewrite the modal test fixture** — `UpgradePlanModal.test.tsx:150-180`. Build the mock from the
   C# record, add the five cases in AC6, and add the source-citation comment. **Delete the
   `violations` fixture entirely** — leaving it as a skipped case preserves the wrong shape for
   someone to copy.
9. **Add the PATCH refresh test** — `alerts.test.ts`. Three sequential `fetch` responses: 401, then a
   200 from `/api/v1/auth/refresh`, then a 200 from the retried PATCH. Assert call count, call order
   and the resolved `ChannelDto`. Mirror `client.test.ts`'s existing setup rather than inventing one.
10. **Run the suite and the typecheck.** `pnpm --filter @tamma/dashboard-user test` and
    `… run typecheck`. Step 1 makes previously-optional members required, so any other consumer of
    `SubscribeResponse` now fails to compile — there should be exactly one (`UpgradePlanModal`), and
    if there is a second, that is a second silent divergence this story found.

## Data & Migrations

None. No schema change, no migration. The server DTO is unchanged (D4).

## Events

None emitted by the client. The server already emits whatever `Subscribe` emits; this story does not
touch that path.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `UpgradePlanModal` — `No_warnings_renders_no_warning_banner` | `warnings: []` → no warning element |
| 2 | `UpgradePlanModal` — `One_warning_renders_metric_usage_and_limit` | `{metricKey: 3, currentUsage: 12, newLimit: 5}` → text naming `seats`, `12`, `5` |
| 3 | `UpgradePlanModal` — `Warning_with_null_usage_omits_the_number` | no literal `null` in the DOM |
| 4 | `UpgradePlanModal` — `Warning_metric_ordinal_is_labelled` | ordinal `3` renders as `seats`, via `metricKeyLabel` — pins D3 |
| 5 | `UpgradePlanModal` — `Downgrade_direction_is_shown` | `direction: "downgrade"` reaches the DOM; not inferred from price |
| 6 | `UpgradePlanModal` — `Scheduled_effective_date_is_shown_when_present` | non-null → date rendered; null → nothing |
| 7 | `alerts.test.ts` — `Patch_channel_refreshes_and_retries_on_401` | 3 `fetch` calls in order; resolves — AC8 |
| 8 | `alerts.test.ts` — existing channel-update cases | still green through `ApiClient` |
| 9 | `client.test.ts` — `Patch_sends_json_content_type_and_credentials` | the new method behaves as `put` does |
| 10 | Full suite | 103 existing + ~8 new, all green |
| 11 | `pnpm --filter @tamma/dashboard-user run typecheck` | exit 0 — catches any second `SubscribeResponse` consumer |

**The fixture in tests 1–6 is copied from `Dtos/Admin/AdminTenantDtos.cs:135-148`, not written from
the story.** That is the whole lesson of this story: the previous fixture was written from an
assumption and the assumption is what the suite then certified.

## Definition of Done

- `SubscribeResponse` has seven required members matching `PlanAssignmentResponse` name-for-name, with
  the citation comment.
- **`violations` appears nowhere in `packages/dashboard-user`** (grep-checked in review) — deleted,
  not deprecated.
- The downgrade warning renders in a manual run against a real API, not only in jsdom.
- `direction` and `scheduledEffectiveAt` are visible in the confirmation.
- `ApiClient.patch` exists; `alerts.ts` contains **no bare `fetch`** (grep-checked); the duplicated
  `apiClientBaseUrl()` is deleted.
- ~111 tests green; typecheck exit 0.
- No file under `apps/tamma-elsa/` changed (grep-checked) — D4.

## Dependencies & Sequencing

- **Blocked by:** nothing.
- **Blocks:** nothing hard. Land before 45-5 makes the app reachable.
- **Shared-edit register:** `src/api/client.ts` is also touched by nobody else in this epic;
  `src/api/alerts.ts` by 45-0 (the `ListAlertsParams` widening — a different interface in the same
  file, trivial to merge). Sequence 45-0 first if both are in flight.
- **Reporting:** the outcome belongs in Epic 34-9's retrospective. A story shipped a screen whose
  tests asserted a server shape that never existed; the interesting part is not the bug but that the
  suite could not have caught it.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **`direction`'s wire values are guessed and the label map is wrong** — the exact strings are not stated in any doc. | Step 4 says read `AdminTenantsEndpoints.cs` before writing the map. If the values are not obvious from the projector, render the raw value rather than mapping it — an unmapped-but-honest string beats a wrong label. |
| **`metricKey` arrives as a string on this path, not an ordinal**, and `metricKeyLabel` passes strings through unchanged — so a wrong assumption is invisible. | Test 4 pins the ordinal case explicitly; add a string case too. `metricKeyLabel` handles both by design (`pricing.ts:38-41`), so the render is correct either way — but the test records which one we actually saw. |
| **Making the fields required breaks a consumer we have not found.** | Step 10's typecheck is the detector. There should be exactly one consumer; a second is a finding, not a blocker. |
| **The 401-refresh test is flaky** because it drives three sequential `fetch` mocks. | Mirror `client.test.ts`'s existing setup verbatim rather than writing a new harness — that test is already green and stable in CI. |
| **Someone re-adds a bare `fetch` for the next non-GET/POST verb.** | The DoD grep-check names it, and `ApiClient` now covers GET/POST/PUT/PATCH/DELETE — there is no remaining verb to justify one. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–2 (the interface, the citation, the return type) | 0.25 |
| Steps 3–5 (warning render, `direction`, `scheduledEffectiveAt`) | 0.25 |
| Steps 6–7 (`ApiClient.patch`, rewrite `updateTenantChannel`, delete the duplicate resolver) | 0.25 |
| Steps 8–9 (rewrite the fixture from the C# DTO; the 401-refresh test) | 0.5 |
| Step 10, manual verification against a real API, review | 0.25 |
| **Total** | **1.5** |

Half the story is tests, and that is correct: the code defect is four lines, and the reason it shipped
is that the test agreed with it.

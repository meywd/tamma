# Story 45-1: The Contract the Tests Certified Wrong — `violations` → `warnings`, and the PATCH That Skips Refresh

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

As a **tenant owner about to downgrade my plan**,
I want the warning that I am about to drop below my current usage to actually appear,
So that I do not discover the seat or agent limit by hitting it, and so the test suite stops
asserting that a field the server has never sent renders correctly.

## Priority

P0 — Wave 0. Ships standalone. It is a *correctness* fix in the one screen Epic 34-9 built, and it
must land before the app is reachable, because the failure mode is silent and customer-facing.

## Architectural Context (READ FIRST)

- **`UpgradePlanModal.tsx:171-172` reads a field the server does not send:**
  ```tsx
  if (resp.violations && resp.violations.length > 0) {
    setWarning(`Subscribed with warnings: ${resp.violations.join('; ')}`);
  }
  ```
  `SubscribeResponse.violations?: string[]` is declared at `src/api/pricing.ts:125` with a comment
  claiming it is "the flagged-violation list surfaced as a non-blocking warning (34-4)".
- **What the server actually returns.** `PricingEndpoints.cs:289-290` →
  `AdminTenantsEndpoints.ToPlanAssignmentResponse(...)` (`Endpoints/Admin/AdminTenantsEndpoints.cs:884-896`)
  → `PlanAssignmentResponse` (`Dtos/Admin/AdminTenantDtos.cs:135-142`):
  ```csharp
  public record PlanAssignmentResponse(
      Guid TenantId, Guid PlanId, int PlanVersion, string Status,
      string Direction,
      IReadOnlyList<PlanAssignmentWarningItem> Warnings,
      DateTime? ScheduledEffectiveAt);
  ```
  Wire body (default camelCase): `{ tenantId, planId, planVersion, status, direction, warnings, scheduledEffectiveAt }`.
  Each warning is an **object** — `PlanAssignmentWarningItem { metricKey, currentUsage, newLimit }`
  (`AdminTenantDtos.cs:145-148`) — not a string.
- **Why this survived review: the test asserts the wrong shape too.**
  `UpgradePlanModal.test.tsx:160` mocks `violations: ['seats over limit']` and `:178` asserts
  `Subscribed with warnings: seats over limit` renders. The test and the code agree with each other
  and both disagree with the server. **Every field on `SubscribeResponse` is optional**
  (`pricing.ts:116-126`), so nothing throws, nothing type-errors, and the warning silently renders
  nothing in production.
- **Three more fields diverge, all silently:** `version` (server: `planVersion`), `planSlug`,
  `planName`, `message` — none sent. And two fields the server *does* send are undeclared:
  `direction` and `scheduledEffectiveAt`. `direction` is the field that tells the customer whether
  this is an upgrade or a downgrade, and it is being dropped on the floor.
- **The second defect, unrelated but in the same file family.**
  `src/api/alerts.ts:181-195` builds its PATCH with a bare `fetch` rather than the shared
  `ApiClient`, with a comment admitting it ("no apiClient.patch"). Consequence: that one call misses
  the single-shot refresh-on-401 every other call inherits (`client.ts:88-113`), so a channel edit
  after the access token expires fails with a raw `PATCH failed 401` instead of refreshing and
  retrying. It also re-implements the base-URL resolver byte-for-byte at `alerts.ts:247-253`.

## Acceptance Criteria

1. **`SubscribeResponse` matches `PlanAssignmentResponse` field-for-field.** In
   `src/api/pricing.ts`, replace the speculative interface with the server's actual shape:
   ```ts
   export interface PlanAssignmentWarning {
     metricKey: number | string;   // ordinal on read; normalize via metricKeyLabel()
     currentUsage: number | null;
     newLimit: number | null;
   }
   export interface SubscribeResponse {
     tenantId: string;
     planId: string;
     planVersion: number;
     status: string;
     direction: string;
     warnings: PlanAssignmentWarning[];
     scheduledEffectiveAt: string | null;
   }
   ```
   **Non-optional.** The previous interface's all-optional members are precisely what let four
   mismatches pass silently; making the fields required means the next divergence is a compile error.

2. **`metricKey` is normalized through the existing helper.** `pricing.ts:38-41` already ships
   `metricKeyLabel(metricKey: number | string)` for exactly this — the C# enum serializes as an
   ordinal on a read path and as a snake_case string on the entitlements path, and `pricing.ts:26-36`
   documents it. Warning rendering calls it; it does **not** get a second normalizer.

3. **`UpgradePlanModal` renders a real warning.** `UpgradePlanModal.tsx:171-172` reads
   `resp.warnings` and renders one line per item naming the metric, the current usage and the new
   limit — e.g. `seats: you are using 12, the new plan allows 5`. A warning with `currentUsage: null`
   or `newLimit: null` renders without the unavailable half rather than printing `null`.

4. **`direction` is surfaced, because it is the fact the customer needs.** The modal distinguishes an
   upgrade from a downgrade in its post-submit message using the server's `direction` rather than
   inferring it client-side from price ordering. Inferring it was never possible correctly — a plan
   change can raise one limit and lower another — which is why the server computes it.

5. **`scheduledEffectiveAt` is surfaced when non-null.** A plan change that takes effect at the end of
   the billing period and a plan change that takes effect now are different things, and the customer
   currently sees neither. When present, the confirmation says when.

6. **The test is rewritten against the real DTO, not the imagined one.**
   `UpgradePlanModal.test.tsx:155-180` mocks the **exact** `PlanAssignmentResponse` wire body,
   including `warnings` as objects. Add cases for: no warnings (nothing rendered), one warning
   (rendered with metric label and both numbers), a warning with `currentUsage: null` (renders
   without it), `direction: "downgrade"`, and a non-null `scheduledEffectiveAt`.
   **The mock fixture is copied from the C# DTO, and the test file carries a comment naming
   `Dtos/Admin/AdminTenantDtos.cs:135-148` as its source** — so the next person who changes the DTO
   has a string to grep for.

7. **`updateTenantChannel` goes through `ApiClient`.** Add `patch<T>()` to `ApiClient`
   (`src/api/client.ts`) beside `put` (`:58-69`) — same body/header handling, inheriting the
   refresh-on-401 path — and rewrite `alerts.ts:181-195` to use it. Delete the duplicated
   `apiClientBaseUrl()` at `alerts.ts:247-253`; it exists only to serve the bare `fetch`.

8. **A test proves the PATCH now refreshes.** `alerts.test.ts` gains a case: first PATCH returns 401,
   the refresh call returns 200, the retried PATCH returns the channel. Asserts three `fetch` calls in
   that order and a resolved promise. `client.test.ts` already has this shape for `post` — mirror it.
   Without this test the fix is invisible, because the happy path is unchanged.

9. **No new server-side change.** The API is correct; the client was wrong about it. If a reviewer
   argues the server *should* send `violations`, the answer is that `PlanAssignmentResponse` is
   shared with the admin plan-assignment path (`AdminTenantsEndpoints.cs:884`) and renaming it to suit
   one client is a strictly larger change with no benefit.

## Technical Notes

- **Why this is P0 rather than a cleanup.** The downgrade warning is the only thing standing between
  a customer and silently losing capacity they are currently using. `PlanPricingPage.tsx` gates the
  mutation to owner-or-sole-user (per commit `0de428e`), so the person clicking it is the person who
  will be surprised.
- **The `violations` name did come from somewhere.** Story 34-4's own vocabulary uses "violation" for
  the entitlement check. The server chose `Warnings` for the response DTO because the check is
  non-blocking. Two names for one concept, one of them never on the wire — worth a line in the
  eventual 34-x retrospective, not worth renaming a shared DTO now.
- **`ApiClient` gaining `patch` is additive** and has no other caller today. It is added because the
  alternative — a second bare `fetch` next release — is how the first one got there.
- The `EstimateResponse` contract was **checked and is correct**: `PricingEndpoints.cs:90-102`
  projects exactly the seven fields `pricing.ts:96-105` declares, with no `costBasisUsd` and no
  `marginUsd`. The 34-5 estimate-leak rule holds. No change.

## Dependencies

- **Blocked by:** nothing. The endpoints all exist and are unchanged.
- **Blocks:** nothing hard. Should land before 45-5 makes the app reachable — a silent billing
  warning is worse once real customers can see the screen.
- **Related:** Epic 34-9 built this screen; this is a defect in its delivery, found by the Epic 45
  audit rather than by its own tests. Worth a note in that epic's retrospective.

## Blocks / Blocked by

- **Blocks:** nothing.
- **Blocked by:** nothing.

## Out of Scope

- Renaming `PlanAssignmentResponse.Warnings` server-side (AC9).
- Any change to `GET /api/pricing/estimate`, its DTO, or `CostEstimateWidget` — checked, correct.
- `EntitlementBar` and `GET /api/pricing/entitlements` — checked, correct
  (`ResolvedEntitlementsDto` ↔ `pricing.ts:45-62`).
- Adding a proper `patch` to every other call site — there are no others; `updateTenantChannel` is the
  only PATCH in the app.
- The admin app's ~20 copies of `fetchJSON` and their lack of 401 handling. Different package,
  sanctioned per-package convention
  (`docs/superpowers/plans/2026-06-17-32-13-agent-management-and-benchmark-dashboards-plan.md:23`).

## Estimated Effort

**1.5 days.** The interface and the two call sites are half a day. The other day is the tests: AC6
rewrites a fixture that was wrong in a way nobody noticed, and AC8 adds a 401-refresh case that
requires driving three sequential `fetch` responses. Both are the parts that stop this recurring.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |

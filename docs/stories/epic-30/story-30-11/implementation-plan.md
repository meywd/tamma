# Implementation Plan — Story 30-11: Tenant Offboarding & Data Portability

> **This story is mostly composition.** Almost everything it needs exists or is drafted elsewhere —
> teardown (30-9), export (37-7), suspension status (`TenantStatusEvaluator`, dead branch),
> subscription cancel (`SubscriptionService`), scheduling (41-30). The engineering risk is not building
> those; it is **not rebuilding them**. Every design decision below is written to keep the surface it
> adds small enough that the four upstream stories can land in any order without a rewrite.

## Scope & Deliverable

One Elsa workflow (`tenant-offboarding`), one tenant-facing API triple, one tenant-scoped export
selector reusing 37-7's plumbing, the first production writer of `Status = "suspended"`, and two Epic
43 catalog members. **No teardown logic, no second export engine, no second cancel path, no bespoke
scheduler.**

## Pre-Reading

- `docs/stories/epic-30/30-9-deprovisioning-workflow.md` — **all of it**, especially AC2's activity
  sequence (Freeze → Drain → PurgeCabinet → ExecuteDeprovision → ClearRouting → ArchiveTenantRow →
  Notify), AC3's confirmation token, **AC6** (*"Tenant-admin cannot self-deprovision… feature flagged
  for future self-service"* — the sentence this story implements), AC9's disclaimed retention purge,
  and the "Data retention vs deletion" three-class section
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeleteTenantWorkflow.cs` (`delete-tenant`, `:70`;
  `DeleteRequestedEventName`, `:65`, `:81-85`) and `TenantDeleteRequestedTrigger.cs` (the
  `platform_events` bridge; `CoolingOff` **5 min**, `:42`; the force-delete waiver; the
  operator-cancel re-check immediately before dispatch) — **the terminal hand-off, and the
  cooling-off this story must not be confused with**
- `apps/tamma-elsa/src/Tamma.Api/Services/TenantStatus/TenantStatusEvaluator.cs:38-39,76,186-195` —
  the complete, never-reached `402 tenant_suspended` branch; `TammaModelConfiguration.cs:274` — the
  CHECK constraint that already permits the value; `TenantStatusInvalidationListener` — the cache
  invalidation this story must trigger
- `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanAssignmentService.cs` (`CancelAsync`, the
  scheduled-downgrade path) and `apps/tamma-elsa/src/Tamma.Api/Services/Billing/SubscriptionService.cs`
  + `Endpoints/Billing/SubscriptionEndpoints.cs` (`/cancel`) — **the cancel path to reuse**
- `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` — `DeletedAt`, `ProvisioningState`, `Settings`
  (jsonb), and the absence of any offboarding column
- `docs/stories/epic-37/story-37-7/` — the DSAR export whose plumbing this story reuses; note its scope
  is a **data subject**, not a tenant
- `docs/stories/epic-41/story-41-30/` — the scheduled-trigger seam that AC4's expiry should eventually
  ride
- `docs/stories/epic-35/` story 35-8 — the dunning state machine that is the **other** writer of
  `suspended`
- `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md` — why the *self-service* half of
  this story has a surface problem that is not this story's to solve
- **NOT FOUND (verified):** any `offboard` identifier in the C# tree; any tenant-scoped data export;
  any writer of `Status = "suspended"`; any `docs/runbooks/tenant-offboarding.md` (30-9 lists it as a
  deliverable; `docs/runbooks/` contains one file, unrelated)

## Corrections to the story

1. **CONFIRMED — 30-9 AC6 is the gap statement, and no follow-up story exists.** The sentence
   *"feature flagged for future self-service"* names a flag that was never specified, in a story that
   is itself `in-progress` with only in-saga compensation built.

2. **CONFIRMED — `suspended` is unreachable.** The evaluator, the response shape and the CHECK
   constraint all ship; the only other mentions are the constant's declaration and a comment in
   `MarkTenantActiveActivity.cs:54`. This story is the first writer. **That makes AC6's integration
   test genuinely novel** — the branch has never been exercised — and it should be written early,
   because a dead branch that has never run is exactly where a wrong assumption hides.

3. **NEW — `Tenant` has no column for any of this, and `Settings` is the wrong place to put it.**
   Offboarding state (requested-at, requested-by, reason, grace-ends-at, bundle-uri, prior-status,
   workflow-instance-id) is a **lifecycle record**, not a setting: it is queried by an expiry sweep,
   audited, and must survive a `Settings` rewrite by an unrelated feature. **D1: a
   `tenant_offboardings` control-plane table**, one row per request, with a partial unique index
   permitting one *active* request per tenant. Rejected alternative: seven nullable columns on
   `tenants`, which makes "has this tenant ever offboarded and cancelled?" unanswerable.

4. **NEW — AC2's "cancellable" has a hard boundary that must be a database fact, not a race.** Between
   "grace expired" and "deprovisioning dispatched" there is a window in which a cancel and a hand-off
   can both believe they won. **D4: the hand-off is a conditional `UPDATE … WHERE status = 'grace'`
   returning row count** — 1 means we own the transition, 0 means the customer cancelled first. The
   cancel endpoint does the mirror-image update. Postgres arbitrates; no lock, no ordering assumption.

5. **NEW — AC3's "a failed export never proceeds to deletion" cannot mean "block forever".** An export
   that fails permanently (a corrupt document, a storage outage) would strand the tenant in an
   un-offboardable state. **D3: three outcomes — `ready`, `failed_recoverable` (retry on the next
   tick, bounded), `failed_permanent` (the workflow **suspends and escalates to a platform operator**,
   who can waive the bundle explicitly).** The invariant is "never *silently* proceed", not "never
   proceed".

6. **NEW — the grace expiry must not be an Elsa `Delay` in the long run, and must be one in the short
   run.** A 30-day `Delay` bookmark is durable (Elsa re-arms it after a restart —
   `WaitForPRMergedActivity` and friends prove the pattern) but it is invisible as a schedule: an
   operator cannot list "which tenants expire this week" without walking workflow instances. **D5: the
   authoritative expiry is `tenant_offboardings.grace_ends_at`**, a queryable column; the `Delay`
   bookmark is the *actuator* until 41-30's seam can drive it from a per-tenant cadence. Both read the
   same column, so the swap is a registration, not a redesign.

7. **NEW — the `suspended` collision with 35-8 is real and must be resolved before either ships.** Two
   independent writers of one status field, with no `suspension_reason`, produce: a tenant suspended
   for non-payment requests offboarding, cancels it, and is restored to… `active`, unpaid.
   **D6: add `suspension_reason` (`billing` | `offboarding`) alongside the status write, and restore to
   the *prior recorded status*, never to a hardcoded `active`.** This story ships the column and the
   discipline; 35-8 adopts it. Flag it to 35-8's owner — **do not edit 35-8 from here.**

## Design Decisions

- **D1 — `tenant_offboardings` (control plane).** `id`, `tenant_id`, `requested_by_user_id`, `reason`,
  `status` (`requested|exporting|grace|handing_off|completed|cancelled|failed`), `prior_tenant_status`,
  `grace_ends_at`, `bundle_uri?`, `bundle_expires_at?`, `export_outcome?`, `workflow_instance_id?`,
  `deprovision_instance_id?`, `created_at`/`updated_at`/`cancelled_at?`.
  `UNIQUE (tenant_id) WHERE status NOT IN ('completed','cancelled','failed')` — one active request per
  tenant, enforced by the database rather than by a read-then-write check.
  Residency: **control plane** — it must survive the tenant schema being dropped, which is the whole
  point of the record. **Excluded from the destructive startup DROP list**
  (`Tamma.Api/Program.cs:3243-3282`), same call as 41-30 D9 and 43-5.
  Also add `suspension_reason` to `tenants` (D6).

- **D2 — `tenant-offboarding` workflow.** `DefinitionId = "tenant-offboarding"`,
  `[ResumeBehavior(ResumeMode.Both)]`. Inputs: `tenantId`, `requestedByUserId`, `reason`,
  `graceDays?` (default 30). Outputs: `status`, `bundleUri?`, `deprovisionInstanceId?`.
  Graph:
  `ReadInputs → RecordRequest → CancelSubscription → BuildExportBundle → exportOk(FlowDecision)`
  → *(failed_permanent)* `EmitExportFailed → WaitForOperatorWaiver` (suspend) → join;
  *(ready)* `EmitExportReady → SuspendTenant → SetGraceEnds → WaitForGraceExpiry(Delay, D5) →
  TryTakeHandoff(D4) → tookHandoff(FlowDecision)`
  → *(False, customer cancelled)* `ExposeOutput(cancelled)`;
  *(True)* `DispatchDeprovision → EmitHandedOff → ExposeOutput(completed)`.
  **Zero `Finish`** — every exit is a typed `ExposeOutput`.
  **No teardown activity of any kind** (AC5).

- **D3 — the export bundle, three outcomes** (Correction 5). New
  `BuildTenantExportBundleActivity` in `Tamma.Activities/TenantLifecycle/`. It **selects**; it does not
  serialise, sign or store — those are 37-7's. Selection: the tenant's `document_instances` + lineage,
  its `domain_events` slice, its work items (Epic 44, when present), its prompt/convention overrides.
  Deliberately **not** included: billing records, audit records and `platform_events` — 30-9's
  retention class says those are *retained by the platform*, and exporting them to a departing customer
  is a different (and legally distinct) decision. Say so in the bundle manifest so the customer knows
  what they did not get and why.
  Until 37-7 lands, the activity writes a manifest + a bounded JSONL dump to the same signed-URL
  mechanism `SecretRevealService`'s token pattern already establishes, and the story's ACs say the
  bundle is *partial*.

- **D4 — the hand-off is a conditional UPDATE, not a lock** (Correction 4).
  `UPDATE tenant_offboardings SET status='handing_off' WHERE id=@id AND status='grace'` — rows affected
  is the answer. The cancel endpoint runs
  `UPDATE … SET status='cancelled' WHERE id=@id AND status IN ('requested','exporting','grace')`.
  Exactly one can win. The API returns a typed `offboarding_not_cancellable` (409) when it loses, never
  a 500.

- **D5 — `grace_ends_at` is authoritative; the `Delay` is the actuator** (Correction 6). When 41-30's
  seam exists, register a per-tenant trigger that polls `grace_ends_at` and resumes the bookmark; until
  then the `Delay` fires it. Both read D1's column. An operator can always answer "who expires this
  week" with one query, which is the property the `Delay`-only design lacks.

- **D6 — `suspension_reason` and restore-to-prior** (Correction 7). Suspending records
  `prior_tenant_status` in D1's row and sets `tenants.suspension_reason = 'offboarding'`. Cancelling
  restores `prior_tenant_status` and clears the reason. **Never restore to a literal `active`.**
  Trigger `TenantStatusInvalidationListener` on both transitions so the 402 branch engages and
  disengages promptly.

- **D7 — the API, per mode** (AC1).
  `POST /api/v1/orgs/{tenantId}/offboarding` · `DELETE /api/v1/orgs/{tenantId}/offboarding` ·
  `GET /api/v1/orgs/{tenantId}/offboarding`.
  - **single-user:** the sole user may do all three.
  - **SaaS:** **`tenant_owner` only** for POST/DELETE — not `tenant_admin`, not `member` (403).
    Closing the account is a different class of decision from administering it. GET is
    owner + admin.
  - The platform-admin path (30-9's token-gated endpoints) is **unchanged and remains available**; this
    is an additional door, not a replacement.

- **D8 — Epic 43: both members are always-human by default.**
  `effect:tenant.offboard.request` and `effect:tenant.offboard.cancel`, group `platform-automation`.
  Ship at `AutonomyDial.AlwaysHuman`. This is one of the few places where the epic's
  "behaviour-preserving defaults" rule (everything at `Min`) should be deliberately broken, because
  today the behaviour is *"impossible"*, and the nearest safe reproduction of impossible is
  always-human — not `Min`. Say that in the descriptor comment, as 43-3 requires for a contested
  assignment.

- **D9 — rejected alternative: extend `DeleteTenantWorkflow` with a "self-service" input mode.** It
  would put a customer-facing, day-scale, cancellable lifecycle inside a workflow whose cooling-off is
  five minutes and whose trigger is a platform event published by an admin endpoint — and whose
  cancel semantics (`TenantDeleteRequestedTrigger`'s operator-cancel re-check) are operator-shaped.
  Two audiences, two clocks, two cancel paths in one graph.

## Implementation Steps

1. **Precondition check (no code).** `dotnet build` green. Confirm: `delete-tenant` +
   `TenantDeleteRequestedTrigger`; `TenantStatusEvaluator`'s suspended branch and the CHECK constraint;
   `SubscriptionService`/`PlanAssignmentService` cancel; the DROP list. Check 30-9, 37-7 and 41-30 —
   record which are landed, because D2/D3/D5 each have a named substitute.

2. **CREATE** `Tamma.Data/Entities/TenantOffboarding.cs`; **MODIFY** `Tenant.cs`
   (`SuspensionReason`), `ControlPlaneDbContext.cs`, `TammaModelConfiguration.cs` (the partial unique
   index, the `status` CHECK). **CREATE the control-plane migration.** **MODIFY** `Tamma.Api/Program.cs`
   — **do not** add the table to the DROP list.

3. **WRITE AC6's suspension integration test FIRST** (Correction 2) — set a tenant to `suspended` by
   hand, assert the 402 `tenant_suspended` body and that `TenantStatusInvalidationListener` clears the
   cache. **This branch has never run; find out what it actually does before building on it.**

4. **CREATE** `Tamma.Activities/TenantLifecycle/RecordOffboardingRequestActivity.cs`,
   `SuspendTenantActivity.cs`, `RestoreTenantStatusActivity.cs`,
   `BuildTenantExportBundleActivity.cs` (D3), `TryTakeOffboardingHandoffActivity.cs` (D4), and
   `TenantOffboardingEvents.cs` (the six constants; `.CANCELLED`, `.FAILED` LOUD).

5. **CREATE** `Tamma.ElsaServer/Workflows/TenantOffboardingWorkflow.cs` (D2) and
   `Helpers/TenantOffboardingHelper.cs` (pure: `ComputeGraceEnd`, `BuildBundleManifest`,
   `MapExitToStatus`).

6. **CREATE** `Tamma.Api/Endpoints/Tenants/TenantOffboardingEndpoints.cs` (D7); **MODIFY**
   `Program.cs` to map them.

7. **Epic 43 registration** (D8) — two `ExternalEffect` members at `AlwaysHuman`, with the contested-
   assignment comment; extend 43-3's expected-set and totals. Coordinate the `platform-automation`
   bump with 41-30 and 41-32.

8. **CREATE** `docs/runbooks/tenant-offboarding.md` — 30-9 lists it as a deliverable and it does not
   exist. Cover: how to waive a permanently-failed export (D3), how to cancel on a customer's behalf,
   and what is retained vs deleted (30-9's three classes).

9. **CREATE the tests**; full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean.

## Data & Migrations

One control-plane migration: `tenant_offboardings` + `tenants.suspension_reason`. Control-plane
residency is load-bearing — the record must outlive the tenant schema it describes. Excluded from the
destructive startup DROP list. No tenant-schema migration, so the missing
migrate-all-provisioned-tenants sweep (Epic 44-1) is not a blocker.

## Test Plan

- **`TenantSuspendedStatusTests`** (step 3, **AC6**) — the first exercise of the 402 branch. Written
  before anything depends on it.
- **`TenantOffboardingHandoffRaceTests`** (Testcontainers, **AC2**, D4) — concurrent cancel and
  hand-off ⇒ exactly one wins, the loser gets the typed 409, and the row lands in exactly one terminal
  state. Run it both orders.
- **`TenantOffboardingActiveRequestUniquenessTests`** — a second `POST` while one is active ⇒ 409 from
  the **partial unique index**, not from a read-then-write check (kill the row's status and assert a
  new request then succeeds).
- **`BuildTenantExportBundleActivityTests`** (**AC3**, D3) — the three outcomes; the manifest names
  what was excluded and why; a `failed_permanent` result reaches the operator-waiver suspend and
  **never** advances to suspension.
- **`TenantOffboardingWorkflowStructureTests`** (**AC5, AC8**) — `OfType<Finish>()` empty; exactly one
  `DispatchWorkflow` whose literal id is the deprovision target; **no** `DROP SCHEMA` string, no
  schema/role/cabinet activity type anywhere in the graph; one `ComputeReEntryPositionActivity`;
  `[ResumeBehavior(Both)]`; `ResumableStandardStructuralTests` green with no allowlist entry.
- **`TenantOffboardingEndpointsTests`** (**AC1, AC7**, D7) — SaaS: `member` ⇒ 403, `tenant_admin` ⇒
  403 on POST/DELETE and 200 on GET, `tenant_owner` ⇒ 200; cross-tenant ⇒ 403/404; single-user: the
  sole user ⇒ 200. Plus: the request path calls `SubscriptionService`'s cancel (**AC7**) and no other
  cancel path exists in the diff.
- **`TenantOffboardingExecutionTests`** (Testcontainers) — happy path to `completed` with the
  deprovision dispatch observed; cancel during grace ⇒ status restored to the **recorded prior value**
  (not a literal `active`) and `suspension_reason` cleared (**D6**); restart during grace ⇒ the
  bookmark re-arms and `grace_ends_at` is unchanged (**AC4**, D5).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — owner-only, own tenant | 6 (D7) | endpoint tests |
| 2 — cancellable throughout grace, typed error after hand-off | 4, 6 (D4) | handoff race tests |
| 3 — bundle before grace; a failed export never silently proceeds | 4 (D3) | bundle activity tests |
| 4 — durable, queryable grace expiry | 2, 5 (D5) | execution restart case |
| 5 — dispatches the teardown, writes none | 5 (D2) | structure test negative pins |
| 6 — writes `suspended`; the 402 branch is exercised | 3, 4 (D6) | `TenantSuspendedStatusTests` |
| 7 — one cancel path, the landed one | 5, 6 | endpoint test + diff assertion |
| 8 — `[ResumeBehavior(Both)]`, 39-10 green | 5 | `ResumableStandardStructuralTests` |

## Risks & Mitigations

- **Deleting a paying customer's data is the highest-consequence action in the product**, and this
  story adds a *self-service* door to it. Mitigations, in order of load-bearing-ness: the export bundle
  is produced **first** and blocks on failure (AC3); a 30-day reversible grace window (AC4); owner-only
  RBAC (AC1); always-human catalog defaults (D8); the terminal step is a *dispatch* to a workflow that
  itself has 30-9's guarantees.
- **The `suspended` branch has never run** (Correction 2). Mitigation: step 3 tests it before anything
  is built on it. If it turns out to be wrong, that is a finding, not a blocker discovered late.
- **The 35-8 collision is a design conflict, not a merge conflict** (D6). Mitigation: ship
  `suspension_reason` and restore-to-prior here; **flag it to 35-8's owner and do not edit 35-8.**
- **Scope creep toward "build the export engine".** Mitigation: D3 says the activity *selects*;
  37-7 serialises/signs/stores. If 37-7 slips, the story ships a partial bundle and **says so in its
  own ACs** rather than quietly growing an export subsystem.
- **The self-service promise has no surface.** `packages/dashboard-user` is undeployed. Mitigation: the
  story states it, the API is independently usable, and this story does not take on shipping an app.
- **30-9 is `in-progress` with only in-saga compensation.** Mitigation: dispatch `delete-tenant` in the
  interim, recorded in the workflow's doc comment and in the runbook, so the substitution is visible
  rather than assumed permanent.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check + upstream status survey | 0.25 |
| 2 | Entity + `suspension_reason` + model config + migration | 0.75 |
| 3 | The suspended-branch test, first | 0.5 |
| 4 | Five activities + events | 1.5 |
| 5 | Workflow + helper | 1.25 |
| 6 | API + per-mode RBAC | 1.0 |
| 7 | Epic 43 registration | 0.25 |
| 8 | Runbook | 0.25 |
| 9 | Tests (race, uniqueness, bundle, structure, endpoints, execution) | 1.5 |
| **Total** | | **7.25** (story estimate 6–7 days — **revise to 7**; the handoff race and the never-run suspended branch are both larger than they look) |

## Blocks / Blocked by

- **Blocked by (soft, each with a named substitute):** 30-9 (terminal dispatch → `delete-tenant`),
  37-7 (export plumbing → partial bundle), 41-30 (grace expiry → Elsa `Delay`).
- **Must agree with:** **35-8** on the `suspended` state machine (D6).
- **Blocks:** nothing.
- **Related, and NOT in scope:** the scheduled retention purge of `platform_events` (30-9 AC9
  disclaims it; 37-5 covers `audit_records` only; **nothing owns it**) — a 41-30 consumer, and the
  right home is Epic 37's retention family.
- **Shared-file register:** `ControlPlaneDbContext.cs` + `TammaModelConfiguration.cs` +
  `Migrations/ControlPlane/` (with 41-30, 41-32 — serialize); `Tamma.Api/Program.cs` DROP list
  (write the exclusion comment once, with 41-30/41-32); 43-3's `platform-automation` expected set
  (with 41-30, 41-32 — one bump).

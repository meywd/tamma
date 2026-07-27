# Story 30-11: Tenant Offboarding & Data Portability — the customer-initiated exit

Status: drafted

> **Note on file layout.** Epic 30's stories 30-1…30-10 use the flat
> `30-N-<slug>.md` + `30-N-<slug>-impl-plan.md` convention. This story uses the
> newer house layout (`story-30-11/` + `implementation-plan.md`) that epics 39–44 standardised on.
> Deliberate; noted so a reader does not think a file is missing.

## User Story

As a **tenant owner**, I want to close my account through Tamma — get my data out, understand exactly
what will be deleted and what is retained, and have a window in which I can change my mind — so that
leaving is a supported product path rather than an email to a platform administrator.

## Priority

**P2.** Not on any critical path. It becomes P1 the moment SaaS has paying
customers who can sign up self-serve (`packages/dashboard-user` already ships the whole
signup→billing journey; see Dependencies).

## The gap, verified

**Every tenant-destruction path in the platform is platform-admin-only.**

- `POST /api/admin/tenants/{id}/actions/delete`, `/cancel-delete`, `/cleanup`
  (`Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs`) — admin surface, admin policy.
- `DeleteTenantWorkflow` (`delete-tenant`) fires off a `TENANT.DELETE.REQUESTED` platform event
  bridged by `TenantDeleteRequestedTrigger`, with a **5-minute** operator cooling-off
  (`TenantDeleteRequestedTrigger.cs:42`). That is an *operator undo window*, not a customer grace
  period.
- `CleanUpFailedTenantWorkflow` (`clean-up-failed-tenant`) tears down a half-provisioned tenant.
- **Story 30-9** owns the deprovisioning saga — and its **AC6 says in terms**: *"Tenant-admin cannot
  self-deprovision; must contact platform admin (feature flagged for future self-service)."* No
  follow-up story was ever written for that flag. `grep -ri offboard` over the C# tree returns **zero
  hits**.

**And there is no way to get the data out.**

- `grep` for `export` / `GDPR` / `DSAR` / `portability` / `takeout` across `Tamma.Api` finds **no data
  export path**. The only GDPR artifacts in code are forward-looking constants:
  `SensitiveActionCatalog.cs:97-99` (`GDPR.DSAR.REQUESTED`, commented *"forward-looking — Story
  37-10/37-11"*) and `AuditCategory.Export`.
- The three drafted export stories each cover a **different, narrower** thing: **37-7** is a *data
  subject* (a person) DSAR; **37-4** is signed **audit-record** export; **36-8** is **analytics
  rollups**. None exports a tenant's *working data* — its documents, work items, plans, reviews,
  event lineage.
- 30-9's `NotifyActivity` emails the owner *"a summary of what was deleted vs what was retained"* —
  after the fact, and it never offers the data.

**And `suspended` is a dead branch.** `TenantStatusEvaluator` ships a complete
`402 Payment Required / tenant_suspended` response (`TenantStatusEvaluator.cs:38-39,76,186-195`) and
the value is in the DB CHECK constraint (`TammaModelConfiguration.cs:274`) — but **nothing in the
codebase ever writes `Status = "suspended"`.** An offboarding grace period is the first legitimate
writer, and Story 35-8's dunning is the second.

## New workflow, and why

`tenant-offboarding` is a **new** Elsa workflow, not an amendment to `delete-tenant` or a second entry
into 30-9's `DeprovisionTenantWorkflow`. All three of the epic's new-vs-amend tests point the same way:

- **The trigger differs** — a tenant owner in their own dashboard, not a platform operator with a
  confirmation token (30-9 AC3).
- **The produced artifact differs** — 30-9 produces a teardown; this produces a **portability bundle
  first**, and the teardown is its last step.
- **The lifecycle differs** — request → confirm → export → **grace period (days, reversible)** →
  suspend → deprovision. 30-9's cooling-off is five minutes and exists to catch a fat-fingered
  operator. A customer's grace period is a product decision measured in days and must be *cancellable
  by the customer*.

It **composes** rather than duplicates: the terminal step **dispatches 30-9's
`DeprovisionTenantWorkflow`** (or, until that lands, `delete-tenant`), and the export step reuses
37-7's export machinery where it overlaps. This story writes no teardown logic of its own.

## Scope

1. **`tenant-offboarding` workflow** — the six-step lifecycle above, resumable, cancellable at every
   step before the terminal one.
2. **A tenant-facing surface** — `POST /api/v1/orgs/{tenantId}/offboarding` (request),
   `DELETE` (cancel), `GET` (status + what-will-happen preview). Owner-only in SaaS; the sole user in
   single-user mode.
3. **The portability bundle** — a tenant-scoped export of the tenant's own working data
   (documents + lineage, work items once Epic 44 lands, plans/reviews, its slice of `domain_events`),
   written to a signed, expiring download. **Explicitly reuses 37-7's export plumbing**; this story
   owns the *tenant-scoped selection*, not a second export engine.
4. **The grace period** — configurable, default 30 days, with the tenant in `suspended` status
   (finally giving `TenantStatusEvaluator`'s 402 branch a writer) and a scheduled expiry that hands off
   to deprovisioning.
5. **Billing interlock** — an offboarding request cancels the subscription through the landed
   `SubscriptionService` rather than inventing a second cancel path.
6. **Epic 43 governance** — `effect:tenant.offboard.request` / `.cancel`, both **always-human** by
   default. This is not an action an agent should ever take unattended.

## Explicitly out of scope

- **The teardown itself** — 30-9 owns it. This story dispatches it.
- **Per-person GDPR DSAR / right-to-erasure** — 37-7 and 37-8. A *tenant* leaving and a *person*
  exercising a data right are different requests with different scopes and different legal clocks.
- **Scheduled retention purge of `platform_events`** — 30-9 AC9 disclaims it ("flagged for Epic 17
  follow-up"), 37-5 covers `audit_records` only, and **nothing owns it**. It is a genuine, separate
  gap; it is a *recurring* job and therefore a consumer of **41-30**'s scheduled-trigger seam, not part
  of this one-shot lifecycle. Named here so it stays visible.
- **Dunning-driven suspension** — Story 35-8. Both this story and 35-8 want to write
  `Status = "suspended"`; they must agree on the state machine (see Dependencies), but 35-8's payment
  path is not this story's.

## Events

`TENANT.OFFBOARDING.REQUESTED` (data: `reason`, `graceEndsAt`), `.EXPORT_READY` (data: `bundleUri`,
`expiresAt`), `.CANCELLED` (LOUD — a customer changed their mind, and someone should notice),
`.GRACE_EXPIRED`, `.HANDED_OFF` (to deprovisioning, with the dispatched instance id),
`.FAILED` (LOUD). All tagged `tenantId`, `correlationId`.

## Acceptance Criteria

1. A tenant owner can request offboarding from their own tenant, and **cannot** request it for another
   tenant. In SaaS, `tenant_admin` and `member` both get 403 — closing the account is the owner's
   decision alone.
2. **Cancellable throughout the grace period**, by the tenant, without an operator. Cancelling restores
   `Status` to its prior value and emits `.CANCELLED`. After the hand-off to deprovisioning it is no
   longer cancellable, and the API says so with a typed error rather than a 500.
3. **The export bundle is produced and made available before the grace period starts**, and the
   workflow does not proceed to suspension until the bundle is ready or has failed with a recorded
   reason. **A failed export never silently proceeds to deletion.**
4. The grace window is configurable (default 30 days), persisted, and its expiry survives a process
   restart — it is **not** an in-memory timer.
5. The terminal step **dispatches** 30-9's `DeprovisionTenantWorkflow` (falling back to `delete-tenant`
   until 30-9 lands) and this workflow writes **no** teardown logic of its own. A structure test pins
   that it contains no `DROP SCHEMA`, no schema/role activity and no cabinet purge.
6. `Status = "suspended"` is written by this workflow, and the existing 402 `tenant_suspended` branch
   is exercised end-to-end by an integration test — **the first test that branch has ever had.**
7. The subscription is cancelled through the landed `SubscriptionService` / `PlanAssignmentService`
   path, not a second one.
8. `[ResumeBehavior(Both)]`; the 39-10 structural test is green without an allowlist entry.

## Dependencies

- **Blocked by (soft):** **30-9** for the terminal dispatch. Until it lands, dispatch `delete-tenant`
  and record the substitution in the workflow's own doc comment.
- **Blocked by (soft):** **37-7** for the export plumbing. Until it lands, the bundle is scoped to what
  is reachable today (documents + lineage + the tenant's `domain_events` slice) and the story says so
  rather than claiming completeness.
- **Needs a scheduled trigger for grace expiry** — **41-30**'s seam is the right mechanism (a
  per-tenant, durable, at-most-once cadence). Until it lands, AC4's expiry rides an Elsa `Delay`
  bookmark, which is resumable and correct but not observable as a fleet-wide schedule. Note the
  substitution; do not build a bespoke scheduler.
- **Must agree with 35-8 on the `suspended` state machine.** Both write it, for different reasons, and
  a tenant suspended for non-payment that then requests offboarding must not end up in an ambiguous
  state. Settle `suspension_reason` before either ships.
- **Interlocks with `packages/dashboard-user`** — the customer surface for this lives in the SaaS
  customer app, which per `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md` **has no
  Dockerfile, no compose service, no image, no deploy step and no domain.** This story's API is usable
  without it; its *self-service* promise is not. **Epic 45 now owns shipping that app**, so this is a
  scheduled dependency rather than an unscheduled one — but it is still a dependency, and this story
  states it plainly rather than inheriting it silently, exactly as that finding asks 39-19 and 44-6 to
  do.
- **Epic 43** — the two catalog members, both always-human by default.

## Estimated Effort

**6–7 days** (workflow 2, export bundle 2, API + RBAC 1, grace/suspension state machine 1, tests 1.5) —
**excluding** 30-9 and 37-7, on which the full-fidelity version depends.

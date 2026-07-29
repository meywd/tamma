# Story 44-2: Work-Item & Project API, RBAC, `tracker_preferences`, Action-Catalog Descriptors

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

As a **tenant member** (single-user or SaaS),
I want to create, read, update, filter and assign work items and projects over an HTTP API whose authorization is correct in **both** operating modes,
So that the tracker is usable by the dashboard, by the CLI, by an agent, and by anything else that speaks HTTP — with the same RBAC discipline every other admin-config surface in the platform already has.

## Priority

P0 — Wave 0. 44-3, 44-4, 44-6, 44-7 and 44-8 are all consumers of this surface.

## Architectural Context (READ FIRST)

- **The canonical admin-config stack to copy is acceptance-rules (Story 39-5, the freshest full instance):**
  - DTOs: `apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs` — one file per feature at `Dtos/<Feature>/<Feature>Dtos.cs`
  - Endpoints: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs:21` — `public static class`, every handler `public static async Task<IResult>`, mode branch `modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid` at `:36` and `:74`
  - Service: `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs:23` — **paired methods**, `…Async(Guid? userId)` / `…ForTenantAsync(Guid tenantId)`: `ResolveAsync:51`/`ResolveForTenantAsync:67`, `UpsertAsync:150`/`UpsertForTenantAsync:169`, `DeleteAsync:191`/`DeleteForTenantAsync:201`
  - Mapping: `apps/tamma-elsa/src/Tamma.Api/Program.cs:2728-2734` — group with `RequireAuthorization("AuthenticatedAny")`, **literal routes before parameterized** (`/defaults` at `:2731` before `/{documentTypeKey}` at `:2732`), writes carrying `.RequireAuthorization("AcceptanceRulesManage")`, rate limits `ConfigRead`/`ConfigWrite` (`:2739-2746`)
- **RBAC is three-place lockstep.** A permission string in `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs:12-95` (18 keys today, `<noun>:manage` convention mapping to `["admin","owner"]`), a policy in the `AddAuthorization` block (`Program.cs`; `AcceptanceRulesManage` at `:1618` with its rationale at `:1615-1617`), and the policy name in the roster array at `:1724-1726`. **`SettingsManage` must not be reused** — it is `["owner"]` only and would 403 a `tenant_admin`; that is exactly why every feature has its own `<Feature>Manage`.
- **`PlatformOwnerAccess` (`Program.cs:1538`) is not `OwnerAccess`.** Every signed-up user auto-owns their personal tenant (`:1908-1913`), so `OwnerAccess` admits everyone. Nothing in this story is platform-scoped anyway.
- **Mode detection:** `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs:67` — explicit `Tamma:Mode`, else inferred SaaS from `Tamma:TenantSharedSecret` / `ConnectionStrings:ControlPlane`, else `SingleUser`.
- **The assignee-eligibility seam is a fail-closed stub today.** `apps/tamma-elsa/src/Tamma.Api/Services/Access/ITaskAudienceResolver.cs:45-56` returns the initiator only, and its sole production consumer `Tamma.Api/Services/Channels/ChannelOutboxService.cs:143` **hardcodes `InitiatorUserId: null`**, so `EligibleAudienceAsync` returns empty unconditionally. Nothing may assume it works.
- **Epic 43's drift harness is bidirectional and CI-blocking** (`docs/stories/epic-43/README.md:186-190`, D2 at `:395`): a mutating route without a catalog entry is unmergeable once 43-8 arms. The `issue-tracking` `ActionGroup` already exists in the 15-member partition (`:104-107`) and is near-empty.
- **The 43-0 bug class to not repeat:** the acceptance-rules edit dialog builds its PUT body without `acceptorRequirement` and the API defaults the missing field, so every admin save silently resets it (`epic-43/README.md:380-383`). **Single-field PATCHes, never defaulted full-body PUTs.**

## Acceptance Criteria

1. **DTOs** in `apps/tamma-elsa/src/Tamma.Api/Dtos/Tracker/TrackerDtos.cs` — request and response records for project, work item, and preference, every property `[JsonPropertyName]`d. Vocabulary fields are wire strings, never enum ordinals.

2. **Endpoints** in `Tamma.Api/Endpoints/TrackerEndpoints.cs`, mapped in `Program.cs` beside the acceptance-rules group:
   - `GET/POST /api/projects`, `GET/PATCH/DELETE /api/projects/{projectId:guid}`
   - `GET/POST /api/work-items`, `GET/PATCH /api/work-items/{id:guid}`, `GET /api/work-items/by-key/{key}`, `DELETE /api/work-items/{id:guid}`
   - `POST /api/work-items/{id:guid}/assign`, `POST /api/work-items/{id:guid}/status`
   - `GET /api/work-items/assignable` — the assignee picker source
   - `GET/PUT/DELETE /api/tracker/preferences`
   Literal segments are mapped **before** parameterized ones (`/by-key/{key}` and `/assignable` before `/{id:guid}`).

3. **Mutations are single-field PATCHes with explicit nullability**, never a defaulted full-body PUT. An absent field means "unchanged"; a `null` field means "clear". A test drives a PATCH containing only `title` and asserts every other column is byte-unchanged — the 43-0 regression guard.

4. **RBAC, three places, both modes.**
   - `tracker:view` = `["member","admin","owner"]` and `tracker:manage` = `["admin","owner"]` added to `Permissions.Matrix`.
   - Policies `TrackerView` and `TrackerManage` added to the `AddAuthorization` block and to the roster array at `Program.cs:1724-1726`.
   - **Project and iteration structure** (`POST/PATCH/DELETE /api/projects`) requires `TrackerManage`. **Work-item CRUD, status and assignment** requires `TrackerView` — a `member` can file and move their own work, which is the point of a tracker.
   - In single-user mode both policies admit the sole user (per the Operating Modes rule).

5. **Mode-correct scoping, with parallel service methods.** `TrackerService` exposes `…Async(Guid? userId)` / `…ForTenantAsync(Guid tenantId)` pairs for **preferences only** (the per-principal configuration). Work-item and project reads/writes are tenant-schema scoped and carry no mode split — a test asserts no work-item service method takes a `userId` scoping parameter (epic Decisions D6).

6. **`GET /api/work-items/assignable` resolves through `ITaskAudienceResolver.EligibleAudienceAsync` when it returns a non-empty set, and falls back to tenant membership when it does not** — returning a `source` discriminator (`audience-resolver` | `tenant-membership`) so the UI can say which it is showing. **It must not render an empty picker**, which is what a naive call gets today (the stub is a total no-op). In single-user mode it returns the sole user.

7. **Visibility filtering is applied in SaaS and is a no-op in single-user.** `GET /api/work-items` filters by `ITaskAudienceResolver.CanSeeAsync` **only when the resolver is not the known stub**; while the stub is registered the list is tenant-scoped and a `visibilityMode` field on the response says `tenant` rather than `per-user`. A test pins both branches. Honest degradation, never a silently empty backlog.

8. **`tracker_preferences` uses the parallel never-joined surfaces** from 44-1 AC6, with `GET` resolving `principal override → system default` and `DELETE` removing the row so the default takes over (the `AcceptanceRulesService.DeleteAsync` posture).

9. **Optimistic concurrency.** Every mutation accepts `If-Match` carrying the row `Version` and returns `409` on mismatch; responses carry `ETag`. A test drives a lost-update scenario and asserts the second writer gets 409, not a silent overwrite.

10. **Action-catalog descriptors.** Every mutating route in AC2 is declared in Epic 43's catalog under `ActionGroup.issue-tracking` with a `DefaultMinAutonomy` reproducing today's behaviour (automated at the baseline dial). If Epic 43's core has not landed, the descriptors ship as a data file plus a test asserting one entry per mutating route, so 43-3 consumes them rather than re-deriving them.

11. **Rate limiting and validation.** `ConfigRead` on the group, `ConfigWrite` on mutations, per `Program.cs:2739-2746`. Project keys validated with 44-0's `WorkItemRef.IsValidProjectKey`; vocabulary fields parsed through the Core extensions and rejected loud (`400` naming the field and the accepted wire set), never coerced.

## Technical Notes

- `GET /api/work-items` is the workhorse: filter by project, status set, kind set, assignee, iteration, parent, external-linked, and free text over title; ordered by `Rank`; keyset-paged on `(Rank, Id)`. Offset paging is not offered — a board reorders constantly and offset paging duplicates and skips rows under concurrent writes.
- `DELETE /api/work-items/{id}` returns `409` when children exist, naming them, because 44-1's `ParentId` FK is `RESTRICT`. Cascading a whole epic's subtree on one click is unrecoverable.
- The endpoint class is `TrackerEndpoints`, not `WorkItemEndpoints`, so projects, work items and (later) iterations share one mapping site rather than three.
- Do not add a `GET /api/tasks`-shaped route. That path belongs to 39-19's decision inbox and the two must remain distinguishable in a route table.

## Dependencies

- **Story 44-0** (vocabularies, `WorkItemRef` validation) — blocking.
- **Story 44-1** (repositories, `tracker_preferences`, `Version`) — blocking.
- **Story 39-20** — `ITaskAudienceResolver`'s real implementation. **Not blocking**: AC6/AC7 are written to degrade honestly against the stub and to light up when 39-20 lands, with no code change beyond the DI registration 39-20 itself performs.
- **Epic 43** — blocking only if 43-8's ratchet arms before this lands; AC10 covers both orders.


**Added obligation (2026-07-28, conformance review):** the API write path enforces
estimate/scale coherence via `EstimateScale.AllowsEstimate` (shipped in 44-0 —
`Tamma.Core/Tracking/EstimateScale.cs`): a work-item write carrying an `Estimate` under a
project whose scale is `not_used` is a 400, not a silent store. The pure rule already
exists; this story owns calling it at the boundary.

## Out of Scope

- Hierarchy validation and reparenting rules — 44-3.
- Ranking endpoints and the `BacklogOrdering` apply seam — 44-3.
- Iteration endpoints and the board projection — 44-4.
- Events — 44-5. This story's handlers call the service; the service gains its event emission in 44-5 without an endpoint change.
- Any UI — 44-6.
- Bulk import / external calls — 44-8.

## Estimated Effort

5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-29 | 1.1.0   | Amendment section added (see below). Records the four implementation deviations never written down at the time — `(Rank, Key)` keyset (plan D7), descriptors as real `ActionCatalog` members rather than the planned data file, `TrackerManage` on the preference writes (absent from AC4) — plus the adversarial-review round: `DELETE /api/work-items/{id}` tightened to `TrackerManage` (no ownership plane exists), `Version` made a real EF concurrency token on projects and preferences (AC9 was false for both), the `If-Match` precondition plumbed into the repositories so it is atomic with the write, FK-violation races on both deletes mapped to the documented 409, six catalog SiteKeys corrected to carry route constraints (and 44-2's own harness made strict), the `HandleNull` rationale corrected, and estimate/scale coherence extended to the project write. Status intentionally left `drafted` — conformance is a separate round. | Claude |

---

## Amendment — 2026-07-29 (implementation deviations + adversarial-review round)

The story text above is the DRAFT. This section records where the shipped code
deliberately differs from it, and what the adversarial review of the shipped
slice changed. Nothing here is a silent deviation: each item states what the
draft said, what shipped, and why. **Status is deliberately NOT flipped in this
amendment** — the conformance round is separate.

### A. Deviations taken while implementing (recorded late — the omission is itself a finding)

**A1. Keyset cursor is `(Rank, Key)`, not `(Rank, Id)`.** The Technical Notes say
"keyset-paged on `(Rank, Id)`" and plan D7 changed it. `Key` is the tie-break
because it is the column the SQL `ORDER BY` already uses (`COLLATE "C"`), so
paging cannot disagree with the ordering; ordering by `Rank, Id` would have
required a second, unindexed sort key.

**⚠️ This tie-break rests on an UNENFORCED invariant — a hard constraint on
44-3.** `(Rank, Key)` is only a total order if **ranks are unique within a
project**, and nothing enforces that: `IWorkItemRepository.SetRanksAsync`
(44-3's seam) validates rank FORMAT (`Rank.IsValid`) and nothing else. Today the
invariant holds incidentally — ranks are minted per project by appending to the
project's current max, and a cross-project rekey is refused
(`TRACKER.CROSS_PROJECT_REKEY`), so a rekey cannot reorder a tied pair. **The
day a duplicate rank exists inside one project**, a rekey changes the `Key`
half of the tie-break and can move a row across a page boundary that has already
been served — a skipped or duplicated row, intermittently. The story's original
`(Rank, Id)` would not have that failure mode, because `Id` is immutable.
**44-3 must either enforce rank uniqueness within a project (unique index or
validation in `SetRanksAsync`) or the cursor must move back to an immutable
tie-break.**

**A2. Descriptors ship as real `ActionCatalog` members, not a data file.** AC10
allows either ("if Epic 43's core has not landed, the descriptors ship as a data
file"). 43-2's core HAD landed, so the ten descriptors are `ExternalEffect`
members in `ActionCatalog.Descriptors.cs`, and `ActionVocabularyCountTests` moves
25 → 35. The data-file branch of AC10 is unused.

**A3. `PUT`/`DELETE /api/tracker/preferences` require `TrackerManage`.** AC4 does
not mention the preferences routes at all. They are gated admin+ because in SaaS
the preference row is TENANT-wide configuration — there is no per-user plane —
so a `member` editing it changes everyone's defaults. This follows the
prompt/convention/acceptance-rules store precedent. `GET` stays at
`TrackerView`.

### B. Adversarial-review round — 2026-07-29

**B1. (MAJOR) `DELETE /api/work-items/{id}` moved from `TrackerView` to
`TrackerManage`.** AC4's normative clause puts "work-item CRUD" at
`TrackerView`, and its justification says a member must be able to move "their
own work". **There is no ownership plane.** `TrackerService` checks neither
`CreatedByUserId` nor `AssigneeUserId` on any route, and AC7's honest
degradation makes the list tenant-wide, so at `TrackerView` any tenant `member`
could irreversibly hard-delete ANY work item in the tenant. Compounding: the
delete is a HARD delete this story's own descriptor grades
`Destructive`/`reversible: false`, and **44-2 emits no events at all** (44-5 owns
emission) — so the loss would be unrecoverable AND unaudited.

The recoverable writes (create / patch / status / assign) stay at `TrackerView`:
AC4's clause covers them and a bad patch is repairable. The destructive route is
admin-gated until an ownership plane (39-20's resolver) or the 44-5 audit trail
lands. The justification comments in `Permissions.cs`, `Program.cs` and
`TrackerEndpoints.cs` were rewritten to stop implying an ownership scoping that
does not exist. Pinned by `TrackerRbacTests.Member_may_not_hard_delete_a_work_item`
and the extended `Tenant_admin_is_not_403d`.

**B2. (MAJOR) AC9 was FALSE for projects and preferences.**
`ProjectEntity.Version` and `TrackerPreference.Version` were plain ints, not EF
concurrency tokens (only work items had one). Proved: two concurrent PATCHes
both sending `If-Match: 1` both returned `200`, and the first writer's rename
was silently reverted. Both are now `.IsConcurrencyToken()`. `dotnet ef
migrations has-pending-model-changes` is clean for BOTH contexts — for a plain
`int` this is model metadata only and needs no migration (as 44-1 established).

**B3. (MODERATE) The precondition is now ATOMIC with the write on every
mutation.** The token alone was insufficient: the service reads, checks
`RequireVersion`, and the repository then RE-READS in a fresh context, so
`W2.read(v1) → W1 completes(v2) → W2.repo-read(v2) → W2 writes v3` passed the
service check and never tripped the token. The caller's `If-Match` now rides into
the repository (`expectedVersion`), where it pins the concurrency token's
ORIGINAL value so the UPDATE/DELETE itself carries `WHERE "Version" = @expected`.
Applied to project patch/delete, work-item patch/status/assign/delete, and the
preference upsert.

**One deliberate asymmetry.** A caller that supplies NO `If-Match` has opted out
of the precondition (`TryReadIfMatch` documents this). Work items and projects
are strict regardless — 44-1 chose that so a lost write cannot drop
`PreviousKeys` history. The preference UPSERT is convergent instead: with no
precondition it re-reads and re-applies (bounded) rather than 409ing, because
"an upsert converges" is its documented contract and 44-1's
`Concurrent_first_upserts_for_one_principal_converge_on_a_single_row` pins it.
With a precondition it is strict like everything else.

**B4. (MODERATE) The delete pre-checks surfaced as 500, not the documented 409.**
`DeleteWorkItem` and `DeleteProject` pre-query their blocking children and then
delete; a row created in that gap trips the RESTRICT FK, and only `TammaError`
was caught, so `PostgresException` 23503 escaped as an unhandled 500 — despite
`ProjectRepository.DeleteAsync`'s own comment asserting "the caller (44-2) maps
the constraint violation to a 409". That mapping is now written, following
`CreateProject`'s existing 23505 pattern.

**B5. (MODERATE) Six of the ten catalog `SiteKey`s did not match their live route
patterns.** The live patterns carry route constraints
(`/api/projects/{projectId:guid}`); the SiteKeys omitted them. 43-8's
`GovernedEndpointBindingSweepTests` compares `RawText` ORDINALLY and does not
strip constraints, so all six would have been rejected the moment 43-9 bound
them. 44-2's own `Every_mutating_route_has_a_descriptor` passed only because it
applied a lenient `Normalize()` — two harnesses disagreeing, with the lenient one
guarding the descriptors. The SiteKeys now carry the constraints, and 44-2's test
compares STRICTLY (the `Normalize` helper is deleted) so they cannot drift apart
again. 43-8's sweeps were not touched and stay green; no count pin moved.

**B6. (MODERATE) The `HandleNull` rationale on `Optional<T>` was wrong.** It
claimed STJ short-circuits a JSON `null` to `default` (unset) without the
override. It does not: STJ's default is `HandleNullOnRead = !CanBeNull`, and
`Optional<T>` is a non-nullable struct, so `Read` already receives the null
token. The behaviour was never at risk. The override is KEPT (explicitness, and
defence against a future change that makes the type nullable-shaped, at which
point the STJ default flips) and the comment now states the true mechanism.

**B7. (MINOR) Visibility keys on the CREATOR, not the assignee — recorded, not
fixed.** `TrackerService` builds `TaskRef(tenantId, item.CreatedByUserId, …)`, so
once 39-20's real resolver lands, an item ASSIGNED TO the viewer but created by
someone else is filtered out of that viewer's own list. **Not fixed here because
it is not a one-liner:** `TaskRef` carries exactly ONE principal axis
(`InitiatorUserId`) and Story 39-20 owns that shape — there is no assignee axis
to add, and passing an assignee AS the initiator would lie to the resolver.
**Constraint on 39-20:** widen `TaskRef` (or add an assignee-aware overload) in
the same change that swaps the DI registration. Today's behaviour is pinned by
`Visibility_is_keyed_on_the_creator_not_the_assignee`, which is written to FAIL
when 39-20 lands, as the reminder.

**B8. (MINOR) Estimate/scale coherence now applies to the PROJECT write too.** It
was enforced on work-item writes only, so an admin could set `estimateScale` to
`not_used` on a project already holding estimated items — the same
representable-and-meaningless state the work-item rule refuses, entered through
the other door. `PatchProjectAsync` now refuses it (`TRACKER.ESTIMATE_NOT_ALLOWED`,
naming the blocking items). Same Core rule (`EstimateScale.AllowsEstimate`),
second call site.

**B9. (MINOR) The lost-update test was sequential.** `Lost_update_is_409` lets
writer one COMPLETE before writer two starts, so it would pass against a pure
check-then-write with no atomic guard — which is exactly what projects and
preferences had. It is kept (it pins the ETag/409 wire contract) and joined by
tests that actually discriminate: deterministic repository-seam tests reproducing
the B3 interleaving, and genuinely concurrent handler-level tests
(`Task.WhenAll`, both writers' reads preceding either write) asserting exactly
one winner — for work items, projects AND preferences.

### C. Recorded, not fixed

**C1. The keyset cursor is plain base64url with no MAC.** Acceptable: the cursor
carries `(Rank, Key)` and no authorization data, and it is decoded inside a
request already scoped to the caller's tenant — so forging one only re-positions
the caller within their own tenant's page sequence. A malformed cursor already
fails loud (`TRACKER.INVALID_CURSOR`) rather than silently restarting at page 1.
It is NOT a capability token and must not become one; if a future cursor ever
carries a filter or a scope, it needs a MAC.

**C2. The ownership plane itself.** B1 gates the destructive route; it does not
create ownership. Every tenant member can still see and edit every work item.
Closing that is 39-20 (visibility) plus a deliberate decision about whether edit
should be ownership-scoped at all — not this story.

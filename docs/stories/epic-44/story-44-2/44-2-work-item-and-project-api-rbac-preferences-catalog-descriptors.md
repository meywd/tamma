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

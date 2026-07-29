# Implementation Plan — Story 44-2: Work-Item & Project API, RBAC, `tracker_preferences`, Action-Catalog Descriptors

> ## ⚠️ PLAN-VS-SHIPPED BANNER — 2026-07-29 (conformance round)
>
> **This plan is the DRAFT.** Several things below name artifacts that do not
> exist. Read this banner before trusting any name in the Test Plan, step 9 or
> the Definition of Done. The story's Amendment section is the normative record
> of what shipped; this banner only covers the plan's own stale names.
>
> **1. Nine of the Test Plan's fixture names do not exist.** The plan spreads 21
> tests across nine fixtures — `TrackerModeTests`, `TrackerAssigneeTests`,
> `TrackerVisibilityTests`, `TrackerConcurrencyTests`, `TrackerListTests`,
> `TrackerValidationTests`, `TrackerDeleteTests`, `TrackerCatalogDescriptorTests`,
> `TrackerRouteOrderTests`. **None were created.** Everything consolidated into
> three real fixtures under `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/`:
> `TrackerEndpointsTests` (behaviour, Testcontainers), `TrackerRbacTests`
> (three-place RBAC + policy roster), `TrackerServiceContractTests` (pure
> reflection over `ITrackerService`, no Docker). Searching for a plan fixture
> name finds nothing; search the three real ones.
>
> **2. "21 tests green" (DoD) understates the shipped suite** — the three
> fixtures carry ~60 `[Test]` attributes between them (56 before this
> conformance round, +4 from it: `TrackerEndpointsTests` 44,
> `TrackerRbacTests` 11, `TrackerServiceContractTests` 5), because the
> adversarial and conformance rounds added concurrency, RBAC, catalog and
> If-Match coverage the plan never enumerated. The DoD number is a floor, not a
> count; do not use it as a pin.
>
> **3. Step 9 / D10's `TrackerActionDescriptors.cs` was never created.** 43-2's
> core HAD landed, so the descriptors ship as real `ExternalEffect` members in
> `Tamma.Core/Actions/ActionCatalog.Descriptors.cs`. Recorded as amendment A2;
> the data-file branch of AC10 is unused.
>
> **4. ⚠️ Test 16 shipped as a DIFFERENT test, and the substitution mattered.**
> The plan specifies `TrackerListTests.Keyset_paging_is_stable_under_reorder` —
> "page, **re-rank** mid-set, page again — no dup, no skip". What shipped was
> `TrackerEndpointsTests.Keyset_paging_is_stable_under_concurrent_insertion`, an
> **INSERTION** test: a new row lands at the front between pages. Insertion and
> re-rank are not the same mutation, and the difference is not cosmetic —
> **amendment A1 warns that a rekey changes the `Key` half of the `(Rank, Key)`
> tie-break and can move a row across an already-served page boundary**, i.e.
> the one test that would exercise A1's own stated failure mode is exactly the
> one that was replaced. The gap went unrecorded until the 2026-07-29
> conformance round.
>
> **Closed, partly.** The conformance round added the specified re-rank test —
> `TrackerEndpointsTests.Keyset_paging_is_stable_under_reorder` — driving
> `IWorkItemRepository.SetRanksAsync` mid-paging (44-2 ships no ranking ROUTE:
> `PatchWorkItemRequest` has no `rank` field and 44-3 owns the endpoints, so the
> repository seam is the only surface, exactly as the insertion test already
> drives its intruder). It asserts the honest guarantee — rows that did not move
> are never duplicated and never skipped; a row dragged across the cursor is
> legitimately re-delivered or missed, which is inherent to keyset paging.
>
> **⚠️ CONSTRAINT ON 44-3 — what is still NOT covered.** A1's failure mode is a
> COMPOUND: a duplicate rank inside one project *plus* a rekey. Only the
> premise is now pinned, by
> `TrackerEndpointsTests.Duplicate_ranks_within_a_project_are_accepted_today`,
> which proves `SetRanksAsync` validates rank FORMAT only and will happily
> collide two ranks. The compound itself is not driven, because the rekey path
> (`IWorkItemRepository.RekeyAsync`) has no 44-2 route and re-keying is 44-3's
> surface. **44-3 must either enforce rank uniqueness within a project (unique
> index or validation in `SetRanksAsync`) or move the cursor to an immutable
> tie-break — and must add the duplicate-rank + rekey paging test when it does.**
> Until then the invariant holds only incidentally, as A1 states.

## Scope & Deliverable

When this story is done the tracker has an HTTP surface: projects and work items are creatable, readable, filterable, patchable, assignable and status-movable; `tracker_preferences` resolves per-principal in both operating modes over parallel never-joined repository surfaces; `tracker:view` / `tracker:manage` exist in all three RBAC places; every mutation is a single-field PATCH with `If-Match`/`ETag` optimistic concurrency (never a defaulted full-body PUT — the 43-0 bug class); the assignee picker degrades honestly against 39-20's no-op stub instead of rendering empty; and every mutating route carries an Epic 43 catalog descriptor in the `issue-tracking` group.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §6 (ownership per mode), Decisions D6, and the boundary rows for 39-20 and Epic 43
- `docs/stories/epic-44/story-44-1/implementation-plan.md` — D5 (why no XOR on work items), D10 (`Version` is an int)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs:21-120` — the endpoint-class shape and the mode branch at `:36`, `:74`
- `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs:23-217` — paired `…Async` / `…ForTenantAsync` methods; `DeleteAsync:191` (the "delete → fall back to default" posture)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:2700-2712` — the convention-store group, incl. the three-tier split with a `PlatformOwnerAccess` admin sub-group at `:2709`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:2728-2746` — the acceptance-rules group: `AuthenticatedAny`, literal-before-parameterized, `AcceptanceRulesManage` on writes, `ConfigRead`/`ConfigWrite`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:1538` (`PlatformOwnerAccess` + rationale `:1528-1537`), `:1615-1618` (`AcceptanceRulesManage` + why not `SettingsManage`), `:1724-1726` (the roster array)
- `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs:12-95` — the 18-key matrix and the `<noun>:manage` → `["admin","owner"]` convention
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs:14-95` — `TammaMode`, `ITammaModeProvider`, `Resolve`
- `apps/tamma-elsa/src/Tamma.Api/Services/Access/ITaskAudienceResolver.cs:12-56` — the interface and the fail-closed stub
- `apps/tamma-elsa/src/Tamma.Api/Services/Channels/ChannelOutboxService.cs:135-174` — the sole consumer, and the hardcoded `InitiatorUserId: null` at `:143` that makes fan-out a no-op
- `docs/stories/epic-43/README.md:100-115` (groups), `:186-215` (the bidirectional CI-blocking harness), `:380-383` (the 43-0 defaulted-body bug)
- **All referenced paths exist.** NOT FOUND (this story creates them): `Dtos/Tracker/`, `Endpoints/TrackerEndpoints.cs`, `Services/Tracker/`.

## Design Decisions

- **D1 — One endpoint class, `TrackerEndpoints`, covering projects + work items + (in 44-4) iterations.** Three classes would mean three mapping sites and three places to get the literal-before-parameterized ordering wrong. `AcceptanceRulesEndpoints` covers rules, defaults and resets in one class for the same reason.

- **D2 — Single-field PATCH with explicit tri-state, never a defaulted full-body PUT.** This is the 43-0 bug class stated as a rule *(historical — **FIXED by Story 43-0 on 2026-07-29**, in this same wave; kept here in the past tense because the rule is why it must not recur)*: the acceptance-rules dialog **omitted** `acceptorRequirement` from its PUT body and the API **defaulted** it, so **every admin save silently reset `design` from human-required to any**. 43-0 fixed both sides — the DTO field is nullable and an omitted `acceptorRequirement` preserves the stored value, and the client sends the field. The DTO uses a tri-state wrapper (absent / null / value) so "not sent" and "explicitly cleared" are distinguishable at the model-binding layer — `System.Text.Json`'s `JsonElement`-per-field or an `Optional<T>` struct, decided at implementation, but the *contract* is fixed here and AC3's byte-unchanged test enforces it.
  `PUT` survives on `/api/tracker/preferences` only, where the body genuinely is the whole resource.

- **D3 — `tracker:view` gates work-item CRUD; `tracker:manage` gates project and iteration structure.** A tracker in which a `member` cannot file a bug or move their own card is not a tracker. But a `member` renaming a project key, deleting a project, or closing an iteration affects everyone's identifiers, so structure is `["admin","owner"]`. The split is the same one 39-20 draws between `tasks:assign` (admin) and task *completion* (any eligible holder).
  **Both policies are new; neither reuses `SettingsManage`** (`["owner"]`, would 403 a `tenant_admin` — the exact reason `AcceptanceRulesManage` exists, `Program.cs:1615-1617`). Three-place lockstep: `Permissions.Matrix`, the `AddAuthorization` block, and the roster array at `:1724-1726`.

- **D4 — Mode branching lives in the endpoint, service methods are paired, and only preferences have a pair.** `AcceptanceRulesEndpoints.cs:36` branches `modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid` and dispatches to `…ForTenantAsync`; that is copied verbatim in shape for `/api/tracker/preferences`. **Work items and projects get no pair** — they are tenant-schema content and the tenant is already resolved by the connection, so a `userId` scoping parameter would be a second ownership plane with no reader (44-1 D5). AC5's reflection test pins this so a later "for symmetry" refactor fails the build.

- **D5 — `GET /api/work-items/assignable` degrades honestly and says so on the wire.** `ITaskAudienceResolver.EligibleAudienceAsync` returns empty today for *every* input, because the only implementation is the initiator-only stub and its only caller passes `InitiatorUserId: null` (`ChannelOutboxService.cs:143`). Calling it naively yields an empty assignee dropdown, which reads as a bug and generates a support ticket.
  So: call it; if the result is non-empty, return it with `source: "audience-resolver"`; if empty, fall back to tenant membership with `source: "tenant-membership"`, and let 44-6 render a one-line note. When 39-20 lands and replaces the DI registration, the branch flips with **no code change here**. Single-user returns the sole user and `source: "single-user"`.

- **D6 — Visibility filtering is opt-in on resolver capability, not on mode.** Same reasoning, sharper stakes: applying `CanSeeAsync` while the stub is registered filters *every* work item out of *every* list, because the stub keys entirely on `InitiatorUserId`. The endpoint therefore checks whether the registered resolver is the known stub type and, if so, returns a tenant-scoped list with `visibilityMode: "tenant"`. It is a type check, not a feature flag, so it self-clears when 39-20 swaps the registration. **A test pins both branches** — one with the stub, one with a fake real resolver — so the day 39-20 lands, per-user filtering is already proven.

- **D7 — Keyset paging on `(Rank, Id)`, no offset paging.** A board reorders constantly. Offset paging over a mutating ordered set duplicates and skips rows, and the bug is intermittent and unreproducible. The cursor is opaque (base64 of the tuple) so the shape can change without a wire break.

- **D8 — `If-Match`/`ETag` over the `Version` int (44-1 D10), 409 on mismatch.** Two people dragging the same card is the normal case, not the edge case. `409` and not `412`: the caller is authorized and the request is well-formed; the resource moved. `412` is reserved for genuinely precondition-shaped semantics and Epic 43 already establishes `409` as this codebase's "the system will not do that right now" code (`epic-43/README.md:306`).

- **D9 — Vocabulary parsing rejects loud, at the DTO boundary, naming the accepted set.** `EnumWire.TryParse` is ordinal and case-sensitive (`EnumWire.cs:65`); a `400` reading `kind 'Epic' is not valid; accepted: epic, story, task, spike` is worth more than a silent lower-casing that changes what got stored. **`bug` and `chore` are not kinds** — they are `TriageIssueType` members (44-0 AC1), so a request sending `kind: bug` gets the same loud rejection with a pointer to the `issueType` field. Priority additionally accepts the shipped aliases via `TriageVocabulary.TryParsePriority` (`critical`, `medium`) because those wires already exist in triage payloads and rejecting them would break 44-8's import; **priority is nullable end-to-end** — an absent `priority` stores `null` ("nobody prioritised this") and is *not* defaulted to `normal` (44-0 AC11).

- **D10 — Catalog descriptors ship as data now, wired later.** Epic 43's core (`Tamma.Core/Actions/`) does not exist yet, and this story cannot block on an epic that is `contexted` with zero code. So: a `TrackerActionDescriptors` data file enumerating one entry per mutating route with its `ActionGroup.issue-tracking` membership and a behaviour-preserving `DefaultMinAutonomy`, plus a **reflection test asserting one descriptor per mutating `Map*` in the tracker group**. When 43-2/43-3 land they consume the file. If they land first, the file binds to the real types instead and the test is unchanged. Either order works and neither leaves the routes ungoverned when 43-8 arms.

- **D11 — `DELETE /api/work-items/{id}` returns 409 with the child list, never cascades.** 44-1's `ParentId` FK is `RESTRICT` for the reason stated there: silently deleting an epic's subtree is unrecoverable. The 409 body names the blocking children so the UI can offer "reparent" or "delete N children" explicitly.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Dtos/Tracker/TrackerDtos.cs`** — `ProjectResponse`, `CreateProjectRequest`, `PatchProjectRequest`, `WorkItemResponse`, `CreateWorkItemRequest`, `PatchWorkItemRequest` (tri-state per D2), `AssignRequest`, `SetStatusRequest`, `WorkItemListResponse` (`items`, `nextCursor`, `visibilityMode`), `AssignableResponse` (`members`, `source`), `TrackerPreferencesResponse` / `UpsertTrackerPreferencesRequest`. All `[JsonPropertyName]`d; all vocabulary fields `string`.

2. **CREATE `Tamma.Api/Services/Tracker/ITrackerService.cs` + `TrackerService.cs`** — project and work-item operations (no mode split, D4), plus the preference pair `GetPreferencesAsync(Guid? userId)` / `GetPreferencesForTenantAsync(Guid tenantId)` and their upsert/delete siblings. Vocabulary parse/validate at the boundary (D9). `Version` check + bump on every write (D8).

3. **CREATE `Tamma.Api/Services/Tracker/TrackerAssigneeResolver.cs`** — D5's three-branch resolution, injecting `ITaskAudienceResolver`, `ITammaModeProvider` and the tenant-membership repository. Returns `(members, source)`.

4. **CREATE `Tamma.Api/Endpoints/TrackerEndpoints.cs`** — a `public static class`, one `public static async Task<IResult>` per route, mode branch only in the preference handlers.

5. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`** — add `["tracker:view"] = ["member","admin","owner"]` and `["tracker:manage"] = ["admin","owner"]` to `Matrix` (`:12`).

6. **MODIFY `Program.cs` — the `AddAuthorization` block.** Add `TrackerView` and `TrackerManage` beside `AcceptanceRulesManage` (`:1618`), carrying the same "not `SettingsManage`, because that is owner-only and would 403 a tenant_admin" comment. Add both names to the roster array at `:1724-1726`.

7. **MODIFY `Program.cs` — map the group** near `:2728`:
   ```csharp
   var tracker = app.MapGroup("/api").RequireAuthorization("AuthenticatedAny").RequireRateLimiting("ConfigRead");
   tracker.MapGet ("/work-items/assignable",   TrackerEndpoints.ListAssignable);   // literal first
   tracker.MapGet ("/work-items/by-key/{key}", TrackerEndpoints.GetByKey);         // literal first
   tracker.MapGet ("/work-items",              TrackerEndpoints.ListWorkItems);
   tracker.MapGet ("/work-items/{id:guid}",    TrackerEndpoints.GetWorkItem);
   tracker.MapPost("/work-items",              TrackerEndpoints.CreateWorkItem)
          .RequireAuthorization("TrackerView").RequireRateLimiting("ConfigWrite");
   // …PATCH /{id}, POST /{id}/assign, POST /{id}/status → TrackerView
   // …projects POST/PATCH/DELETE                        → TrackerManage
   ```

8. **MODIFY `Program.cs` — DI**: `AddScoped<ITrackerService, TrackerService>()` and `AddScoped<TrackerAssigneeResolver>()` beside the acceptance-rules service registrations (`:418-422`). Repositories were registered by 44-1 in `Tamma.Data/DependencyInjection.cs`.

9. **CREATE `Tamma.Api/Services/Tracker/TrackerActionDescriptors.cs`** — D10's data file, one entry per mutating route: `{ routeTemplate, method, actionKey, group: "issue-tracking", defaultMinAutonomy, risk }`.

10. **CREATE tests** under `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/`.

## Data & Migrations

None — 44-1 owns the schema. This story adds no column.

## Events

None emitted here. 44-5 adds emission **inside `TrackerService`**, so no endpoint or DTO changes when it lands. Recorded so a reviewer does not ask why a mutation has no event.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `TrackerEndpointsTests.Patch_touches_only_the_sent_field` | **the 43-0 regression guard** — PATCH `{title}`, every other column byte-unchanged |
| 2 | `TrackerEndpointsTests.Patch_null_clears_and_absent_preserves` | the D2 tri-state, per nullable field |
| 3 | `TrackerRbacTests.Member_may_create_and_move_a_work_item` | `tracker:view` suffices |
| 4 | `TrackerRbacTests.Member_may_not_create_or_delete_a_project` | 403 on `TrackerManage` routes |
| 5 | `TrackerRbacTests.Tenant_admin_is_not_403d` | the `SettingsManage` trap, pinned |
| 6 | `TrackerRbacTests.Policy_names_are_in_the_roster` | three-place lockstep |
| 7 | `TrackerModeTests.SingleUser_preferences_key_on_user_id` | `TammaMode.SingleUser` → `…Async(userId)` |
| 8 | `TrackerModeTests.SaaS_preferences_key_on_tenant_id` | → `…ForTenantAsync(tenantId)` |
| 9 | `TrackerModeTests.Preference_planes_never_join` | a user row is invisible to the tenant surface |
| 10 | `TrackerModeTests.No_work_item_service_method_takes_a_user_scope` | reflection over `ITrackerService` — pins D4 |
| 11 | `TrackerAssigneeTests.Empty_resolver_falls_back_to_membership` | `source: "tenant-membership"`, **non-empty list** |
| 12 | `TrackerAssigneeTests.Real_resolver_wins` | fake non-empty resolver → `source: "audience-resolver"` |
| 13 | `TrackerVisibilityTests.Stub_resolver_yields_tenant_scope` | `visibilityMode: "tenant"`, list **not** empty |
| 14 | `TrackerVisibilityTests.Real_resolver_filters_per_user` | fake real resolver → only visible rows |
| 15 | `TrackerConcurrencyTests.Lost_update_is_409` | two PATCHes, same `If-Match` → second 409 |
| 16 | `TrackerListTests.Keyset_paging_is_stable_under_reorder` | page, re-rank mid-set, page again — no dup, no skip |
| 17 | `TrackerValidationTests.Bad_vocabulary_is_400_naming_the_set` | `kind: "Epic"` rejected ordinally |
| 18 | `TrackerValidationTests.Priority_aliases_are_accepted` | `critical` → urgent, `medium` → normal |
| 19 | `TrackerDeleteTests.Delete_with_children_is_409_listing_them` | D11 |
| 20 | `TrackerCatalogDescriptorTests.Every_mutating_route_has_a_descriptor` | reflection over the group's `Map*` calls vs the data file — **D10** |
| 21 | `TrackerRouteOrderTests.Literals_precede_parameterized` | `/assignable` and `/by-key/{key}` resolve, not swallowed by `/{id:guid}` |

Tests 1–2, 15–19 use the WebApplicationFactory + Testcontainers shape already used by `Tamma.Api.Tests/AcceptanceRules/`.

## Definition of Done

- 21 tests green.
- `tracker:view` / `tracker:manage` present in `Permissions.Matrix`, the `AddAuthorization` block **and** the roster array (all three; a reviewer checks the third, which is the one that gets forgotten).
- No `PUT` on any work-item or project route.
- `GET /api/work-items/assignable` returns a non-empty list against the shipped stub (test 11) — the acceptance bar for "degrades honestly".
- `TrackerActionDescriptors` has one entry per mutating route (test 20).
- No route named `/api/tasks*` is introduced (grep-checked; 39-19 owns that path).

## Dependencies & Sequencing

- **Blocked by:** 44-0, 44-1.
- **Blocks:** 44-3, 44-4, 44-6, 44-7, 44-8, 44-9.
- **Adjacent, non-blocking:** 39-20 (D5/D6 flip with no edit here when its DI registration lands); Epic 43 (D10 works in either order).
- **Shared-edit register:** `Program.cs` — the `AddAuthorization` block (`:1500-1726`) and the roster array are shared with **43-6**, which adds `actions:manage` in the same three places. Coordinate; the two changes are adjacent lines.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The assignee picker ships empty.** The most likely failure: someone calls `EligibleAudienceAsync` and trusts it. | D5's fallback plus test 11, which asserts a **non-empty** list against the real shipped stub. The test fails if the fallback is removed. |
| **Visibility filtering ships and hides everything.** Same root cause, worse blast radius — an empty backlog reads as data loss. | D6's stub-type check plus tests 13/14 pinning both branches. |
| **Three-place RBAC drifts.** The roster array is the place that gets forgotten. | Test 6. |
| **The tri-state PATCH is over-engineering for v1.** | It is not: the 43-0 bug is a shipped, live example of the exact failure, in this codebase, on the most recently built admin surface. Test 1 is named for it. |
| **Epic 43 lands after this and re-derives the descriptors differently.** | D10's data file is the artifact 43-3 consumes; the epic README's boundary row asks 43-3 to pre-reserve the keys either way. |
| **`Program.cs` merge conflicts with 43-6.** | Shared-edit register; both changes are additive lines in the same two blocks. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–2 (DTOs, service, tri-state, concurrency) | 1.5 |
| Step 3 (assignee resolver, three branches) | 0.5 |
| Steps 4, 7 (endpoints + mapping + route ordering) | 1.0 |
| Steps 5–6, 8 (RBAC three places, DI) | 0.5 |
| Step 9 (catalog descriptors) | 0.25 |
| Step 10 (21 tests) | 1.0 |
| Review | 0.25 |
| **Total** | **5.0** |

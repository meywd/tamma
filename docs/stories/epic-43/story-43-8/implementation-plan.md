# Implementation Plan — Story 43-8: Drift Harnesses

## Scope & Deliverable

When this story is done, the action catalog is bound to the **running application** in both directions by five reflection harnesses plus one grafted assertion, and a new consequential call site cannot be merged without a governance decision.

Concretely: one metadata type (`ActionGateMetadata`) with two authoring shapes (`.Governs(ActionKey)` for minimal APIs, `[Governs]` for the one controller); `GovernedEndpointCoverageTests` sweeping **all ~205 mutating endpoints** off a booted `EndpointDataSource` including controller actions and hubs; `GovernedEndpointBindingTests` proving each binding's `SiteKey` matches the route it is attached to; `[PerformsEffect]` on the 17 mutating `TammaApiClient` methods with a bidirectional test and a 19-entry counted read-only list; `BackgroundActorCoverageTests` over the built `IServiceCollection` of **both** hosts, handling the factory-registered listener and the extension-method-registered `PlatformTaskWorker`; `UnattributedActivityTests` seeded with the 13 `SecretsRotation/Activities/` files; one added assertion in `TaxonomyDriftBuildTests`. Four ratchets, each shrink-only **and** staleness-checked **and** count-pinned. Plus `enforcementSites` in the admin response so a row governing nothing does not render as governed.

**No Roslyn analyzer ships.** See D1.

## Pre-Reading

- `docs/stories/epic-43/story-43-8/43-8-drift-harnesses.md` — this story (ACs are source of truth)
- `docs/stories/epic-43/README.md` — "Drift prevention" (the bidirectionality rule, the analyzer rejection, the honest holes) and the Decisions table (D2: unclassified is allowed at runtime, unmergeable in CI)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — **the** ratchet mechanics: justified allowlist, keyword-classified justifications, staleness. Note `:255-271` — the shrink-only property is prose, not an assertion. That is the defect this story does not inherit.
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — assembly reflection over compiled graphs; the `Input`-delegate materialization at `:694-740`; the eligibility check at `:226-242` (step 8 grafts one assertion beside it); the four anti-no-op tripwires at `:110,:269-282,:363-394,:396-428`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — `:1806` `MapControllers`; `:3384-3385` the two `MapHub`; `:1385` the factory-overload `AddHostedService`; `:1410` `AddPlatformTaskWorker`; the 200 mutating `Map*` calls
- `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/PlatformTaskServiceCollectionExtensions.cs:44-47` — `TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PlatformTaskWorker>())`, the registration with no `AddHostedService` line
- `apps/tamma-elsa/src/Tamma.Api/Controllers/MentorshipController.cs` — the repo's **only** controller; `[HttpPost]` at `:53,141,164,187`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` — 36 public `Task`-returning methods; the 17 mutating ones enumerated in AC4
- `apps/tamma-elsa/src/Tamma.Activities/SecretsRotation/Activities/` — the 13 files with no `[Activity]`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:103,403` — `UseWorkflowsApi()`, the out-of-process surface named in AC10(c)
- `docs/stories/epic-43/story-43-9/implementation-plan.md` — the consumer of `.Governs`; keep the metadata shape in lockstep
- **NOT FOUND (authored by prerequisite stories, no code yet):** `Tamma.Core/Actions/` in its entirety — `ActionKey`, `ActionNamespace`, `ActionCatalog`, `ActionDescriptor.SiteKey`, `ExternalEffect`, `BackgroundActor` (Story 43-2); `NotDiRegisteredTools` (Story 43-4); the `/api/actions` response DTOs (Story 43-6). See Blocked by.

## Design Decisions

- **D1 — No Roslyn analyzer. Decided on measurement, not preference.** An analyzer was designed to assert `.Governs(...)` on every mutating `Map*` and rejected. Re-verified on 2026-07-25: `Program.cs` holds **200** mutating `Map*` calls and **79 of them terminate `);` on the same line** — no fluent chain, therefore nothing for a syntax walker to inspect: a **~40% structural miss rate**. Independently, `app.MapControllers()` (`:1806`), the two `app.MapHub<…>` (`:3384-3385`) and Elsa's `UseWorkflowsApi()` (`ElsaServer/Program.cs:103,403`, a **different process**) are invisible to syntax analysis outright. An analyzer with a 40% blind spot is **worse than none**: it would be believed, and a completeness guarantee that is not complete is precisely the failure mode this epic exists to prevent. CI blocks the merge either way; the loss is **local-build feedback**, and that loss is stated in the epic README, in AC10(f) and in the harness doc-comments rather than papered over. *(An analyzer could still be added later for the 121 chain-terminated calls as a convenience — but it must never be described as the guarantee.)*

- **D2 — Reflect over the built `IServiceCollection`, never over source text.** `PlatformTaskWorker` has **no `AddHostedService` line anywhere** — it is registered by `TryAddEnumerable` inside `PlatformTaskServiceCollectionExtensions.cs:44-47`. A source-grep sweep misses it entirely; a descriptor sweep sees it with a non-null `ImplementationType`. Conversely `TenantStatusInvalidationListener` (`Program.cs:1385`) *is* an `AddHostedService` line but has a **null** `ImplementationType`. The two failure modes are complementary and only descriptor reflection plus an explicit factory-pair list covers both. See Corrections to the design.

- **D3 — One metadata type, two authoring shapes, because two authoring styles genuinely exist.** `ActionGateMetadata` is the single thing every harness and the Story 43-9 filter read. `.Governs(ActionKey)` is a `RouteHandlerBuilder` extension (minimal APIs, 200 sites); `[Governs(ns, key)]` is an attribute (the 4 `MentorshipController` `[HttpPost]` actions). Forcing controllers through the builder API is not possible, and forcing minimal APIs through an attribute is not either. Two shapes, one metadata — so there is exactly one thing to get right in 43-9.

- **D4 — Coverage predicate is ALL mutating endpoints, not the `EngineServiceOnly` subset.** Restricting to mediation routes would put 199 of ~205 mutating endpoints outside the harness *by construction*, and the harness would pass forever while the surface it claims to guard grows. The cost is a large day-one `KnownUngovernedEndpoints` (~188 entries) — which is honest, ratcheted, and visible, rather than invisible.

- **D5 — Hub `/negotiate` is exempted as a NAMED, COUNT-PINNED class; controllers are NOT exempted.** SignalR's `/negotiate` endpoints are POST by protocol and carry no application effect; they are exempted through `ExemptEndpointClasses` with a `Count.Should().Be(2)` pin, so adding a third hub fails the build and forces a decision. Controller actions are structurally annotatable via `[Governs]`, so they are governed like anything else — the earlier design's hard-fail on "structurally un-annotatable endpoints" is resolved by *providing the annotation*, not by widening an exemption.

- **D6 — `SiteKey` correspondence for routes; explicitly absent for methods, and the test says so.** `GovernedEndpointBindingTests` asserts `descriptor.SiteKey == $"{method} {routePattern}"`, which is what stops `.Governs(SomeUnrelatedMember)` from silencing the coverage test. The same check is **impossible** for a `[PerformsEffect]`-attributed C# method — nothing verifies the declared key matches what the method actually calls. That is stated in the harness doc-comment (AC10(b)) so a green suite is not read as a stronger guarantee than it is.

- **D7 — Every ratchet gets a count pin. This is the fix, not an embellishment.** `ContractBindingTests.cs:255-271` documents shrink-only as a *property* — there is no assertion, so an addition passes. Each of the four ratchets here pairs (a) staleness (an entry that now passes fails, so the list drains), (b) justification classification, and (c) `Count.Should().Be(N)` (so growth fails). All three, or the ratchet is decorative.

- **D8 — Bidirectionality is asserted per plane, and the failure message names the remedy.** code→catalog failures say "add a catalog entry for X or justify it in <ratchet>"; catalog→code failures say "**delete** the catalog entry for X — nothing performs it". The second message matters: without it the natural reflex is to invent a site to satisfy the test, which manufactures a phantom capability.

- **D9 — `enforcementSites` is this story's requirement even though Story 6 serializes it.** The harness *knows* which actions have bindings; it is the only component that does. It exposes that set (`ActionEnforcementSites`, computed from `ActionGateMetadata` + `[PerformsEffect]` + `[Governs]` + the Seam D/E registrations) so Story 6 can serialize it and Story 7 can render "not enforced anywhere yet". On day one this is ~17 of ~205 routes; a UI that renders the other rows as governed would be lying, and the lie would be load-bearing for an admin.

- **D10 — Placeholders are not catalogued.** The one authoring judgment call: a workflow, route or executor that does not yet do the thing gets **no catalog entry** until it does. A placeholder entry passes both directions and governs nothing, which is worse than a gap — a gap is visible in `enforcementSites`.

## Corrections to the design

1. **`PlatformTaskWorker` is registered as an `IHostedService`, contrary to "no `AddHostedService` line at all" being read as "invisible to a descriptor sweep".** Verified: `PlatformTaskServiceCollectionExtensions.cs:44-47` does `services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PlatformTaskWorker>())`, called from `Program.cs:1410`. The descriptor's `ImplementationType` is **non-null**, so descriptor reflection **does** see it — only a source-grep would miss it. Practical effect: D2's choice of mechanism is load-bearing, and the `KnownFactoryRegisteredServices` special case is needed for exactly **one** service (`TenantStatusInvalidationListener`, `Program.cs:1385`), not two.

2. **`InlineToolLoopRunner` line numbers are off by one from the design.** The validator block closes at **`:281`** (design said `:259-281` — correct), `executableToolCalls` is computed at **`:330`** (design: `:329-332`) and the `EnableParallelTools` fork is at **`:335`** (design: `:334`). Not this story's code, but 43-9 sites its seam by these numbers and both plans must agree.

3. **`TammaApiClient` has 36 public `Task`-returning methods, not "17 mutating plus a handful".** `KnownReadOnlyClientMethods` therefore seeds at **19**, and at least four of those 19 are **not obviously read-only**: `RecordProviderFailureAsync:421`, `RecordProviderSuccessAsync:431`, `CreateProviderAsync:471`, `ExecuteProviderAsync:480`, `DisposeProviderAsync:490`. They mutate provider-session and telemetry state but produce no *external* effect. Each must carry an explicit justification of that form (`internal-session-lifecycle-no-external-effect`), not be waved through — otherwise the read-only list becomes the place mutating methods go to hide.

4. **The design's "~205 mutating routes" is a sum, not a single grep.** `Program.cs` has exactly **200** mutating `Map*`; the remainder are `MentorshipController`'s 4 `[HttpPost]` plus the 2 hub `/negotiate` endpoints (≈206). The harness must derive the number from `EndpointDataSource` at runtime and **pin it**, rather than restating a literal that will drift the first time a route is added.

5. **`MentorshipController` is the only controller in the repo.** `ls src/Tamma.Api/Controllers/` returns one file. The `[Governs]` attribute path therefore has exactly 4 day-one call sites — cheap to land, and the mechanism exists so the *second* controller cannot arrive ungoverned.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Infrastructure/ActionGateMetadata.cs`** (AC1, D3) — `sealed record ActionGateMetadata(ActionKey Action)`. No behaviour; it is a marker read by the harnesses and by 43-9's filter.

2. **CREATE `apps/tamma-elsa/src/Tamma.Api/Infrastructure/GovernsExtensions.cs`** (AC1) —
   ```csharp
   public static RouteHandlerBuilder Governs(this RouteHandlerBuilder b, ActionKey action)
       => b.WithMetadata(new ActionGateMetadata(action));   // 43-9 adds .AddEndpointFilter here
   public static RouteGroupBuilder Governs(this RouteGroupBuilder b, ActionKey action) => …;
   ```
   **CREATE `GovernsAttribute.cs`** — `[AttributeUsage(AttributeTargets.Method)] sealed class GovernsAttribute(ActionNamespace ns, string key)` implementing the metadata-surfacing interface so it lands in `Endpoint.Metadata`. Apply it to the 4 `MentorshipController` `[HttpPost]` actions (`:53,141,164,187`).

3. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** — attach `.Governs(...)` to the routes Story 43-9 will enforce (the 17 mutating mediation routes; see 43-9's plan for the exact list). No filter yet — metadata only, so this story is behaviour-neutral and can land independently.

4. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/GovernedEndpointCoverageTests.cs`** (AC2, D4/D5/D7/D8) — `WebApplicationFactory` boot, `EndpointDataSource.Endpoints` walk. Per endpoint: read `HttpMethodMetadata`; if it intersects `{POST,PUT,PATCH,DELETE}` (or the endpoint is in `GovernedGetEndpoints`, seeded with the `secret.reveal` route), require `ActionGateMetadata` **or** a `KnownUngovernedEndpoints` entry. `ExemptEndpointClasses` holds the 2 `/negotiate` endpoints with `Count.Should().Be(2)`. Also assert `TotalMutatingEndpointCount` against a pin (Correction 4) so the sweep cannot silently stop seeing endpoints. Failure messages per D8.

5. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs`** (AC11, D7) — `IReadOnlyList<(string Method, string Pattern, string Justification)>`, seeded (~188 entries) in one reviewed diff. Ratchet: stale entries (endpoint now carries metadata) fail; justification runs through the `ContractBindingTests` keyword classifier; `Count.Should().Be(N)`. Seed `POST /api/v1/governance/evaluate` with `gate-evaluation-endpoint-cannot-gate-itself` (43-9 adds the route; if 43-9 lands second, that entry lands with it and the count pin is bumped in the same commit).

6. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/GovernedEndpointBindingTests.cs`** (AC3, D6) — for each endpoint with `ActionGateMetadata`: key resolves in `ActionCatalog.ByKey`; `descriptor.SiteKey == $"{method} {routePattern}"`. Doc-comment states the method-plane limitation.

7. **CREATE `apps/tamma-elsa/src/Tamma.Core/Actions/PerformsEffectAttribute.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs`** (AC4) — attribute the 17 mutating methods at the verified lines. Attribute lives in Core because `Tamma.Activities` references Core and the test asserts against `ExternalEffect`.

8. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Actions/ExternalEffectCallSiteTests.cs`** (AC4, D8, Correction 3) — reflect `typeof(TammaApiClient)` public instance `Task`-returning methods (36). Forward: each `[PerformsEffect]` names a real `ExternalEffect` with a descriptor. Reverse: each unattributed method is in `KnownReadOnlyClientMethods` (seed **19**, `Count.Should().Be(19)`), with the five session/telemetry methods carrying explicit `internal-session-lifecycle-no-external-effect` justifications. Catalog→code: every `ExternalEffect` member is either attributed on a method **or** justified as bound at a non-client site (Seam C route, Seam D actor) — failure message says *delete the member* if neither.

9. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/BackgroundActorCoverageTests.cs`** (AC5, D2, Correction 1) — two fixtures: `WebApplicationFactory` (Tamma.Api) and a `Host.CreateApplicationBuilder`-based fixture mirroring `ElsaServer/Program.cs` registrations. Enumerate `IServiceCollection` descriptors with `ServiceType == typeof(IHostedService)`. For each: resolve implementation type from `ImplementationType`, else from `KnownFactoryRegisteredServices` (one entry: `TenantStatusInvalidationListener`), else **fail** naming the descriptor. Map to `automation:*`. Bidirectional against `BackgroundActor` members. Assert `PlatformTaskWorker` is present (a regression pin for D2 — if someone converts the registration and it vanishes, the sweep must go red, not quiet).

10. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Actions/UnattributedActivityTests.cs`** (AC6) — reflect `typeof(TammaApiClient).Assembly` for types assignable to Elsa's `IActivity` (or deriving `CodeActivity`/`Activity`) with no `[Activity]`; assert each is in `UnattributedActivities`, seeded with the **13** `SecretsRotation/Activities/` types, staleness + `Count.Should().Be(13)`.

11. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`** (AC7) — beside `:226-242`, assert the materialized action wire resolves to `ActionKey(ActionNamespace.AgentAction, wire)` in `ActionCatalog.ByKey`. ~15 lines; no new enumeration, no new fixture.

12. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionEnforcementSites.cs`** (AC9, D9) — computes, per `ActionKey`, the concrete enforcement sites known statically (`ActionGateMetadata`-carrying endpoints, `[PerformsEffect]` methods, `[Governs]` controller actions, plus the Seam D/E registrations 43-9 declares). **MODIFY** Story 6's action DTO to carry `enforcementSites`. **CREATE `ActionEnforcementSitesTests.cs`** — asserts a member with no site reports an empty array and that the day-one governed-route count matches the `KnownUngovernedEndpoints` complement (so the two numbers can never drift apart silently).

13. **CREATE `docs/stories/epic-43/drift-harness-holes.md`** *(or, preferred, put it in the harness doc-comments only — AC10 requires the harness, the doc is optional)*: the six holes verbatim, each next to the harness that cannot see it.

14. **Run** `dotnet test` (full suite — the ratchets are only meaningful green) and `dotnet ef migrations has-pending-model-changes` (must stay clean; this story adds no entities).

## Test Plan

NUnit + FluentAssertions; `WebApplicationFactory` for the endpoint sweeps; no Testcontainers (nothing here touches a database).

- **`GovernedEndpointCoverageTests`** — `EveryMutatingEndpoint_IsGovernedOrJustified`; `GovernedGetEndpoints_AreCovered`; `NegotiateEndpoints_AreExactlyTwo`; `TotalMutatingEndpointCount_IsPinned`; `StaleUngovernedEntry_Fails`; `UngovernedList_CountIsPinned`; `ControllerActions_AreInScope` (asserts the 4 `MentorshipController` POSTs appear in the sweep — a regression pin against someone re-adding a controller exemption). **Covers AC2, AC8, AC11.**
- **`GovernedEndpointBindingTests`** — `EveryBoundEndpoint_ResolvesInTheCatalog`; `SiteKey_MatchesRoutePattern`; `WrongBinding_IsDetected` (a deliberately mis-bound endpoint in a test-only app fixture must fail — proves the check is not a no-op). **Covers AC3.**
- **`ExternalEffectCallSiteTests`** — `EveryAttributedMethod_NamesARealEffect`; `EveryUnattributedMethod_IsJustifiedReadOnly`; `ReadOnlyList_CountIsPinned` (19); `EveryEffectMember_HasAPerformingSite` (catalog→code, failure message says *delete*); `SessionLifecycleMethods_CarryExplicitJustification`. **Covers AC4, AC8, AC10(a) via doc-comment assertion on presence.**
- **`BackgroundActorCoverageTests`** — `ApiHost_EveryHostedService_MapsToAnAutomationMember`; `ElsaHost_…` (same); `FactoryRegisteredServices_AreResolved` (null-`ImplementationType` descriptor is mapped, not skipped); `UnmappableDescriptor_Fails`; `PlatformTaskWorker_IsSeen` (D2 regression pin); `EveryAutomationMember_HasARegistration`. **Covers AC5.**
- **`UnattributedActivityTests`** — `EveryActivityType_CarriesTheAttributeOrIsAllowlisted`; `Allowlist_CountIsPinned` (13); `StaleEntry_Fails`. **Covers AC6.**
- **`TaxonomyDriftBuildTests`** (modified) — the added assertion; verify the existing tripwires still fail when the enumerator is stubbed to empty. **Covers AC7.**
- **`ActionEnforcementSitesTests`** — `MemberWithNoSite_ReportsEmpty`; `GovernedRouteCount_EqualsSweepMinusUngoverned`. **Covers AC9.**
- **Ratchet meta-test** `RatchetDisciplineTests` — for each of the four ratchets, assert all three properties are actually implemented (staleness fails, justification classified, count pinned). This is the test that stops a future ratchet shipping with only two of three. **Covers AC8.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — metadata + two authoring shapes | 1, 2, 3 | `GovernedEndpointBindingTests`, `ControllerActions_AreInScope` |
| 2 — coverage over all mutating endpoints | 4, 5 | `GovernedEndpointCoverageTests` (full class) |
| 3 — binding correctness on the route plane | 6 | `SiteKey_MatchesRoutePattern`, `WrongBinding_IsDetected` |
| 4 — bidirectional mediation-client coverage | 7, 8 | `ExternalEffectCallSiteTests` (full class) |
| 5 — both hosts, both registration shapes | 9 | `BackgroundActorCoverageTests` (full class) |
| 6 — unattributed activities visible | 10 | `UnattributedActivityTests` |
| 7 — dispatch pairs resolve in the catalog | 11 | modified `TaxonomyDriftBuildTests` |
| 8 — ratchets shrink-only + stale + counted | 5, 8, 9, 10 | `RatchetDisciplineTests` |
| 9 — `enforcementSites` exposed | 12 | `ActionEnforcementSitesTests` |
| 10 — holes recorded in the harnesses | 4, 6, 8, 9, 10 (doc-comments), 13 | Reviewer check: each harness names what it cannot see |
| 11 — allowlist seeded with real justifications | 5 | `UngovernedList_CountIsPinned`, keyword classifier |

## Risks & Mitigations

- **`WebApplicationFactory` boot cost and fragility.** Booting the real app in a test to walk `EndpointDataSource` is the only way to see controllers and hubs, but it drags in DI validation — and Story 43-2's fail-loud `BuildIndex` means a catalog gap becomes a *test-host boot failure*, not a clean assertion. Mitigation: one shared fixture per assembly; assert the boot failure explicitly in a dedicated test so its message is legible rather than surfacing as 40 unrelated red tests.
- **~188-entry `KnownUngovernedEndpoints` is a large reviewed diff.** Mitigation: generate it once from the sweep, then hand-write the justifications in grouped passes (by route group); the keyword classifier rejects placeholders, so the review has a floor.
- **The allowlist enforces nothing until draining starts.** A seeded-with-everything ratchet is a snapshot, not a gate — except that the **count pin** makes any *new* ungoverned route fail immediately. That is the property that matters on day one; draining is separately funded (epic open question 5).
- **Count pins are churn.** Every legitimately-added route bumps a number. Accepted deliberately: the bump is the reviewer's prompt to ask "should this be governed?", which is the entire point. The alternative (no pin) is the `ContractBindingTests` defect.
- **Catalog→code failures invite fake sites.** A developer facing "no performing site for `effect:x`" may add a trivial call site instead of deleting the member. Mitigation: D8's failure wording leads with *delete*; D10 forbids cataloguing placeholders; code review on catalog diffs.
- **`SiteKey` is unverifiable for attributed methods.** Real, unclosable here (D6). A `[PerformsEffect(Wrong)]` on a `TammaApiClient` method passes everything. Mitigation: the 17 sites are few and reviewed once; the limitation is stated in the harness.
- **MCP has no drift signal at all.** Adding an MCP server or a tool on an existing server changes nothing any harness can see. Not mitigated — recorded (AC10(d)), and it is why the "flip to fail-closed when unclassified goes silent" endgame is unsound for that class specifically.
- **Test-time, not build-time.** D1's accepted cost. CI blocks the merge; a local build does not.

## Blocks / Blocked by

- **Blocked by Story 43-2** (catalog core: `ActionKey`, `ActionCatalog`, `ActionDescriptor.SiteKey`, `ExternalEffect`, `BackgroundActor`). Hard — every harness reflects against it. Can be developed in parallel against 43-2's declared type shapes, but does not compile before it.
- **Blocked by Story 43-3** for the catalog→code direction to be meaningful (a member with no group cannot exist; the totality check is 43-3's).
- **Soft dependency on Story 43-4** — `NotDiRegisteredTools` is authored there; this story only asserts its ratchet discipline in `RatchetDisciplineTests`.
- **Coordinates with Story 43-6** — `enforcementSites` field name, single source, agreed once.
- **Blocks Story 43-9.** Seam C is `.Governs` **plus** the filter; the metadata and the coverage harness must exist first, otherwise 43-9 attaches enforcement to a surface nothing verifies. 43-9 also adds one `KnownUngovernedEndpoints` entry and bumps the count pin.
- **Parallel with Stories 43-5, 43-6, 43-7** — no shared files.

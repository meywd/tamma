# Story 43-8: Drift Harnesses — Bidirectional Reflection Over Real Call Sites

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

As a **platform owner accountable for what the system may do unattended**,
I want every consequential call site in the codebase to be bound to a catalog entry, and every catalog entry bound to a call site, enforced by reflection over the **running application** rather than over a second declaration,
So that a new mutating route, a new mediation method, a new background actor or a new unattributed activity **cannot be merged** without a governance decision — and so that a capability that is deleted from the code cannot leave a phantom row in the admin UI implying it is still governed.

## Priority

P0 — This is the epic's whole guarantee. Story 2's fail-loud `BuildIndex` is **bidirectional against an enum this epic authored**, so it is vacuous for the 55 members of `ToolAction` / `ExternalEffect` / `BackgroundActor`. Those three planes are bound to reality by **this story alone**. If 43-8 slips, the `effect:*` / `automation:*` / `tool:*` half of the catalog has no drift protection at all (epic README, Drift prevention; design §12 risk 6).

## Architectural Context (READ FIRST)

### The checks are bidirectional, and that is not an aspiration

The catalog is **derived from the code** during Stories 2–4, so on the day it is written both directions hold trivially. The value is what happens afterwards:

- **code → catalog**: a new mutating route / mediation method / hosted service with no catalog binding fails the build. This is the "new capability ships unclassified" case.
- **catalog → code**: a catalog member with no performing site fails the build, and the failure message says **delete the entry**. This is the "capability was removed but the admin UI still shows a governed row" case — a row an admin can set to `AlwaysHuman` believing they have blocked something that no longer exists.

Neither direction is optional. A one-directional check produces a catalog that only grows.

The **one judgment call at authoring time** is not to catalogue a placeholder as a real capability: a workflow or route that does not yet do the thing gets **no entry** until it does. A placeholder entry passes both directions and governs nothing.

### There is no Roslyn analyzer, and the reason is measured

An analyzer (`TAMMA00x`, `Tamma.Activities.Guardrails` shape) was designed and **rejected on evidence**, not on taste. The numbers, re-verified in this repo on 2026-07-25:

- `apps/tamma-elsa/src/Tamma.Api/Program.cs` contains **382** `Map{Get,Post,Put,Patch,Delete}(` calls, of which **200 are mutating** (`MapPost`/`MapPut`/`MapPatch`/`MapDelete`).
- **79 of those 200 terminate `);` on the same line** — no fluent chain, therefore no `InvocationExpressionSyntax` continuation for an analyzer to inspect for a `.Governs(...)` call. That is a **~40% structural miss rate** on the largest governed surface.
- `app.MapControllers()` (`Program.cs:1806`), `app.MapHub<OrchestratorChannelHub>` / `MapHub<UserChannelHub>` (`Program.cs:3384-3385`) and Elsa's `elsa.UseWorkflowsApi()` / `app.UseWorkflowsApi()` (`Tamma.ElsaServer/Program.cs:103,403`) are **invisible to syntax analysis entirely** — the first two expand at runtime from attributes and hub metadata, and the third registers routes **in a different process**.

> **Dated note (2026-07-29, conformance round) — the 200/79/~40% figures are unmeasured today.**
> `grep -cE '\.Map(Post|Put|Patch|Delete)\(' apps/tamma-elsa/src/Tamma.Api/Program.cs` returns
> **231**, not 200 (and **430**, not 382, for all five verbs) — the file grew after 2026-07-25. The
> derived "79 of those 200 terminate `);` on the same line" and the "~40% structural miss rate" are
> therefore both stale, and the miss rate has **not** been re-derived here: doing it honestly means
> re-running the same same-line-termination classification over the current 231, and stating the
> method, which is more than a `grep -c`. What is NOT affected is the decision: the three
> syntax-invisible surfaces in the bullet above (`MapControllers`, the two `MapHub` registrations,
> Elsa's out-of-process `UseWorkflowsApi`) are unchanged and are on their own sufficient to reject an
> analyzer that claims completeness. The verdict stands on those; the percentage is decoration and
> should be read as "a large fraction, last measured 2026-07-25 at ~40% of 200".

**An analyzer with a 40% blind spot is worse than none.** The epic's guarantee is *completeness*; a completeness guarantee that is not complete is exactly the failure this epic exists to prevent, and it would be believed. CI blocks the merge either way. What is genuinely lost is **local-build feedback**: a developer who skips `dotnet test` can push an ungoverned route and only learn on CI. That is recorded as an accepted cost, not hidden (epic README, Drift prevention; design §12 risk 4).

### The surfaces, verified

| Surface | Where | Count | Verified |
|---|---|---|---|
| Minimal-API mutating routes | `Tamma.Api/Program.cs` | 200 *(→ **231** on 2026-07-29; see the dated note above)* | `grep -cE '\.Map(Post\|Put\|Patch\|Delete)\('` |
| Attribute-routed controller actions | `Tamma.Api/Controllers/MentorshipController.cs` — the **only** controller in the repo | 4 `[HttpPost]` at `:53,141,164,187` (+ 4 `[HttpGet]`) | file read |
| SignalR negotiate endpoints | `Program.cs:3384-3385` | 2 hubs → 2 `/negotiate` POSTs *(+ 2 transport endpoints; 4 exempt in total — see the AC2 note)* | file read |
| Engine mediation client | `Tamma.Activities/LlmCall/TammaApiClient.cs` | **36** public `Task`-returning methods, **17** mutating | `grep -nE '^\s+public (virtual )?(async )?Task'` |
| Background actors (`IHostedService`) | both hosts | 25 total | see below |
| Activities missing `[Activity]` | `Tamma.Activities/SecretsRotation/Activities/` | **13 files**, none carrying `[Activity]` — but only **9** are `IActivity` classes (see the AC6 note) | `ls` |

**Two registration shapes that a naive sweep misses, both real:**

1. **Factory overload** — `Program.cs:1385` registers `TenantStatusInvalidationListener` via `AddHostedService(sp => sp.GetRequiredService<…>())`. `ServiceDescriptor.ImplementationType` is **null** for this descriptor; only `ImplementationFactory` is set. A sweep keyed on `ImplementationType` silently skips it.
2. **`TryAddEnumerable` inside an extension method** — `PlatformTaskWorker` (`Tamma.Api/Services/PlatformTasks/PlatformTaskWorker.cs:112`, `: BackgroundService`) has **no `AddHostedService` line anywhere in `Program.cs`**. It is registered at `Tamma.Api/Services/PlatformTasks/PlatformTaskServiceCollectionExtensions.cs:44-47` as `services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PlatformTaskWorker>())`, reached from `Program.cs:1410` via `AddPlatformTaskWorker`. **See "Corrections to the design" — this one is caught by descriptor reflection, and is the reason the harness must reflect the built `IServiceCollection` rather than grep source.**

### House patterns this story reuses (do not invent new ones)

- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the ratchet: justified-allowlist entries, keyword-classified justification strings, **staleness** (an entry that now passes fails as stale). Its shrink-only property at `:255-271` is a **comment, not an assertion** — additions are undetectable. This story fixes that by pairing every ratchet with a **count pin**.
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — assembly reflection over compiled workflow graphs; `EnumerateAllDispatchPairs`; the four anti-no-op tripwires at `:110,:269-282,:363-394,:396-428`.
- `WebApplicationFactory` boot + `EndpointDataSource` walk — the only way to see controller actions and hub endpoints as endpoints.

## Acceptance Criteria

1. **Binding surface: one metadata type, two authoring shapes.**
   `Tamma.Api/Infrastructure/ActionGateMetadata.cs` carries the `ActionKey`. Two ways to attach it, because two route-authoring styles exist and neither can be forced into the other:
   - `RouteHandlerBuilder.Governs(ActionKey)` — an extension in `Tamma.Api/Infrastructure/GovernsExtensions.cs` that adds `ActionGateMetadata` **and** (Story 43-9) the enforcement filter. Used by minimal APIs.
   - `[Governs(ActionNamespace.Effect, "…")]` — an attribute on a controller action method, surfaced as endpoint metadata by the framework's attribute-to-metadata pipeline. Used by `MentorshipController`'s four `[HttpPost]` actions.
   Both produce the *same* metadata object, so every harness and the Story 43-9 filter read exactly one thing.

> **Harness names (dated note, 2026-07-29, conformance round — read this before AC2–AC7).** Every
> harness listed in the ACs below landed under a `…SweepTests` name. The AC names are kept verbatim
> above and below; the mapping is recorded once here rather than by editing five ACs. See also
> amendment **§A5**.
>
> | AC | Name in the AC | Name in the tree | Project |
> |----|----------------|------------------|---------|
> | AC2 | `GovernedEndpointCoverageTests` | `GovernedEndpointCoverageSweepTests` (+ `KnownUngovernedEndpoints.cs`, `GovernanceHostFixture.cs`) | `Tamma.Api.Tests/Actions/` |
> | AC3 | `GovernedEndpointBindingTests` | `GovernedEndpointBindingSweepTests` | `Tamma.Api.Tests/Actions/` |
> | AC4 | `ExternalEffectCallSiteTests` | `MediationClientEffectSweepTests` | `Tamma.Activities.Tests/Actions/` |
> | AC5 | `BackgroundActorCoverageTests` | `BackgroundActorRegistrationSweepTests` (registration plane) + `BackgroundActorCatalogSweepTests` (class plane) | `Tamma.Api.Tests/Actions/`, `Tamma.Activities.Tests/Actions/` |
> | AC6 | `UnattributedActivityTests` | `UnattributedActivitySweepTests` | `Tamma.Activities.Tests/Actions/` |
> | AC7 | (an assertion inside `TaxonomyDriftBuildTests`) | `DispatchPairCatalogSweepTests` — a separate fixture | `Tamma.Activities.Tests/Actions/` |
>
> AC4's ratchet is likewise named `KnownReadOnlyClientMethods` in the AC text and
> **`KnownNonEffectClientMethods`** in the tree (the tree's name is the better one: the list is
> "performs no catalogued external effect", which is not the same as "read-only").
>
> The `…Sweep` suffix is **not** load-bearing. Each harness's doc-comment used to justify it by
> saying the suffix makes "the epic's CI drift filter (`~Drift|~Sweep|~Catalog`)" select the fixture.
> No such filter exists — `.github/workflows/ci.yml:248` runs `dotnet test "$proj"` per project with
> no `--filter`, and the string appears nowhere in `docs/`. Those three doc-comments were corrected
> in this round (`GovernedEndpointBindingSweepTests`, `GovernedEndpointCoverageSweepTests`,
> `DispatchPairCatalogSweepTests`).

2. **`GovernedEndpointCoverageTests` — the load-bearing harness (code → catalog, all endpoints).**
   In `tests/Tamma.Api.Tests/Actions/`. Boots the real app with `WebApplicationFactory`, walks `EndpointDataSource.Endpoints`, and for **every** endpoint whose `HttpMethodMetadata` includes POST/PUT/PATCH/DELETE — **all ~205, not just the `EngineServiceOnly` subset** — plus an explicit named list of governed GETs (`effect:secret.reveal`), asserts the endpoint carries **either** `ActionGateMetadata` **or** a `KnownUngovernedEndpoints` entry with a non-empty justification. Controller actions and hub endpoints are **in scope, not skipped**; the two SignalR `/negotiate` endpoints are exempted as a **named, count-pinned class** (`ExemptEndpointClasses`), never by a wildcard.
   *(Corrected 2026-07-29, conformance round: **four** SignalR endpoints are exempt, not two. Each
   `MapHub` produces a `/negotiate` endpoint **and** a transport endpoint that declares no HTTP
   method (so it accepts POST). `GovernedEndpointCoverageSweepTests.IsExempt` exempts both kinds and
   `ExemptEndpointClass_isCountPinned` pins them separately at 2 + 2. The exemption is reasonable —
   both are protocol surface carrying no application effect — and it is still a named, count-pinned
   class, never a wildcard; but "hub endpoints are in scope, not skipped" is true only of a hub's
   application surface, not of its transport endpoints. Controller actions ARE in scope and are not
   exempt. Also "all ~205" is stale: the in-scope surface is derived at runtime and pinned at **237**
   by `InScopeEndpointCount_isPinned` — see the AC9 and Architectural Context notes.)*

3. **`GovernedEndpointBindingTests` — the binding is the right one (route plane).**
   For every endpoint carrying `ActionGateMetadata`, the referenced `ActionKey` must resolve in `ActionCatalog.ByKey` **and** that descriptor's `SiteKey` must equal `$"{method} {routePattern}"`. Without this, `.Governs(anyExistingMember)` silences the coverage test with a wrong binding — a governed-looking route pointing at an unrelated action. The test **must state in its failure message** that it cannot do the same for attributed C# methods (see AC10).

4. **`ExternalEffectCallSiteTests` — bidirectional over the mediation client.**
   `[PerformsEffect(ExternalEffect.X)]` is applied to the **17 mutating `TammaApiClient` methods**: `CallLlmAsync:109`, `CreateBranchAsync:128`, `CreatePullRequestAsync:136`, `MergePullRequestAsync:144`, `UpdateIssueStatusAsync:152`, `CreateReleaseAsync:167`, `DeleteBranchAsync:217`, `TriggerTestsAsync:249`, `UpdateJiraTicketAsync:283`, `DispatchAgentRunAsync:300`, `QueueSlackNotificationAsync:386`, `SendEmailAsync:403`, `AppendEventsAsync:530`, `PersistDocumentAsync:579`, `SetDocumentStatusAsync:609`, `PostChannelOutboxAsync:663`, `AppendPlatformEventsAsync:710`. The test asserts **both** directions: every attributed method names a real `ExternalEffect` member with a catalog descriptor, **and** every public `Task`-returning method on `TammaApiClient` that is *not* attributed appears in a shrink-only `KnownReadOnlyClientMethods` list carrying a count pin. Total public `Task`-returning methods is **36**, so the read-only list seeds at **19** — pinned.
   *(Name corrected 2026-07-29, conformance round: the tree ships **`KnownNonEffectClientMethods`**,
   not `KnownReadOnlyClientMethods` — the tree's name is the more accurate one, since the list means
   "performs no catalogued external effect", not "reads nothing". The harness itself is
   `MediationClientEffectSweepTests`, not `ExternalEffectCallSiteTests`; see the harness-name table
   above AC2. Per amendment §A1 carve-out 3 the 17 `[PerformsEffect]` attributes were NOT applied —
   the mapping is a test-side table — and per §A4/F12 discovery no longer filters on return type, so
   "every public `Task`-returning method" reads as "every public instance method".)*

5. **`BackgroundActorCoverageTests` — both hosts, both awkward registration shapes.**
   Reflects `IHostedService` registrations off the **built `IServiceCollection`** for `Tamma.Api` (via `WebApplicationFactory`) and `Tamma.ElsaServer` (via a host-builder fixture), and asserts each maps to an `automation:*` catalog member.
   *(Corrected 2026-07-29, conformance round — an AC-level carve-out §A1 omitted. **No ElsaServer
   host-builder fixture exists.** `Tamma.Api.Tests` does not reference `Tamma.ElsaServer` and cannot
   boot its Elsa/EF composition, so that host's hosted services are never reflected off a built
   collection at all. In their place the six actors concerned are a HAND-WRITTEN, count-pinned set —
   `BackgroundActorRegistrationSweepTests.ActorsRegisteredInTheElsaServerHostOnly`
   (`:89-97`, pinned `HaveCount(6)` at `:361`) — so a new ElsaServer actor still fails a pin, but the
   registration itself is asserted by nobody; only the CLASS-level
   `BackgroundActorCatalogSweepTests` sees those types. This limit is written in that harness's
   doc-comment and was **not** in this story. A second, smaller limit lives in the same file:
   four `Tamma.Api` actors sit behind configuration branches the test host does not take
   (`ActorsNotRegisteredInTheTestHost`), so in a deployment where those branches are on, four
   background actors run that this sweep never examined.)* The **factory registration at `Program.cs:1385`** (null `ImplementationType`) is handled by a named `KnownFactoryRegisteredServices` pair list — `(descriptor position or factory-declaring member) → actor key` — never by skipping null-`ImplementationType` descriptors. Bidirectional: every `automation:*` member must map to a registered service or a justified `NotRegisteredActors` entry.

6. **`UnattributedActivityTests` — the first mechanism that can see them.**
   Reflects `Tamma.Activities` for types deriving from `CodeActivity`/`Activity` that carry **no `[Activity]` attribute**, asserted against a shrink-only allowlist seeded with the verified **13 files** in `Tamma.Activities/SecretsRotation/Activities/`.
   *(Corrected 2026-07-29, conformance round — a second AC-level carve-out §A1 omitted. The tree pins
   **9**, not 13: `UnattributedActivitySweepTests.Baseline_countIsPinned` asserts
   `HaveCount(9)`, because 4 of the 13 FILES are not `IActivity` implementations —
   `RotationActivityBase` (abstract), `DrainRotationAuditEmitter`, `RotationAuditDrainScope`,
   `RotationWorkflowState`. Thirteen files, nine activity CLASSES; the sweep counts classes, which
   is the thing that can carry `[Activity]`. The plan's step 10 `Count.Should().Be(13)` is wrong for
   the same reason. The explanation was written in the harness's seeding note and NOT in this
   story.)* Implemented as a **test, not an analyzer** — the property is reflection-visible, so a test is strictly cheaper and equally complete.

7. **One added assertion in `TaxonomyDriftBuildTests`.**
   The existing harness already materializes each `DispatchWorkflow`'s real `(role, action)` pair by invoking the `Input` delegate against a synthetic `ExpressionExecutionContext` (`:694-740`). Beside the eligibility check at `:226-242`, add: the materialized action wire must resolve to `ActionKey(ActionNamespace.AgentAction, wire)` in `ActionCatalog.ByKey`. ~15 lines. Its four existing anti-no-op tripwires protect the new assertion for free.
   *(Corrected 2026-07-29, conformance round: the assertion did NOT land inside
   `TaxonomyDriftBuildTests`. It landed as a separate fixture,
   `Tamma.Activities.Tests/Actions/DispatchPairCatalogSweepTests`, which REUSES that file's
   `internal EnumerateAllDispatchPairs()`. So the enumeration is still the compiled-graph one and the
   four tripwires still guard IT — but they live in a different fixture and cannot fail this one, so
   "protect the new assertion for free" is no longer literally true. The gap was closed deliberately
   at authoring time: the new fixture carries its own tripwire,
   `The_dispatch_enumeration_is_not_empty`, plus
   `Discrimination_anUncataloguedActionWireWouldNotResolve`.)*

8. **Every ratchet is shrink-only, staleness-checked AND count-pinned.**
   Four ratchets ship: `KnownUngovernedEndpoints`, `KnownReadOnlyClientMethods`, `NotDiRegisteredTools` (authored in Story 4, consumed here), `UnattributedActivities`. For each: (a) an entry that now passes fails as **stale**; (b) justification strings are non-empty and keyword-classified (`ContractBindingTests` idiom); (c) a **count pin** `X.Count.Should().Be(N)` so an *addition* fails the build. (c) is not optional — `ContractBindingTests.cs:255-271`'s shrink-only property is a comment, and additions are otherwise undetectable.

9. **Honesty about day-one coverage — `enforcementSites` on the API response.**
   On the day this story lands the catalog governs roughly **17 of ~205 mutating routes** (the mediation set Story 43-9 binds); ~188 ship in `KnownUngovernedEndpoints`.
   *(Numbers corrected 2026-07-29, conformance round. The tree: **0** routes governed — not 17; the
   17 mediation bindings are Story 43-9's Seam C and are explicitly deferred (§A1 carve-out 2), and
   `GovernedEndpointBindingSweepTests.NoProductionRouteIsBoundYet_isTheDayOneState` pins the zero.
   The in-scope mutating surface is **237**, not ~205 (`KnownUngovernedEndpoints.PinnedInScopeCount`,
   derived at runtime by `InScopeEndpointCount_isPinned` rather than restated from a grep). And
   **237** — every one of them — ship in `KnownUngovernedEndpoints` (`PinnedCount = 237`), not ~188.
   The honesty requirement AC9 states is therefore MORE pressing than the AC text implies, not less:
   today every catalog row has zero enforcement sites, and `enforcementSites` still does not exist
   (§A1 carve-out 4).)* The Story 6 admin API response for each action therefore exposes `enforcementSites: string[]` — the concrete sites that will actually evaluate the gate for that action — and the Story 7 UI renders an explicit "not enforced anywhere yet" state for an empty array. **A catalog row with zero enforcement sites must not render as governed.** Without this the UI implies coverage that does not exist, which is the same class of lie the epic is built to prevent.

10. **The holes are written down in the harness, not only in a doc.**
    Each harness carries a doc-comment naming what it cannot see, so a reader of a *passing* test is not misled: (a) `[PerformsEffect]` / `.Governs` bind a **site, not an effect** — a new capability grown inside an already-governed method passes everything; (b) `SiteKey` correspondence works for routes and **cannot** work for attributed C# methods; (c) Elsa's `UseWorkflowsApi()` surface is in another process and is not in `EndpointDataSource`; (d) MCP is one coarse member with **no drift signal at all** — adding a server or a tool on an existing server changes nothing here; (e) the TypeScript sidecar is ungoverned past the proxy route; (f) enforcement is **test-time, not build-time**.

11. **`KnownUngovernedEndpoints` is seeded and drained, and its justifications are real.**
    Seeding is a reviewed, one-time diff: every entry names *why* the route is not yet governed and, where applicable, which story governs it. Entries whose justification is a bare placeholder fail the keyword classifier. The gate-evaluation mediation route added by Story 43-9 (`POST /api/v1/governance/evaluate`) enters the list with the justification `gate-evaluation-endpoint-cannot-gate-itself`.

## Dependencies

- **Story 43-2 (catalog core)** — `ActionKey`, `ActionCatalog.ByKey`, `ExternalEffect`, `BackgroundActor`, `ActionDescriptor.SiteKey`. **Blocking**; everything here reflects against it.
- **Story 43-4 (tool-vocabulary validator)** — authors `NotDiRegisteredTools`; this story only asserts its ratchet discipline. Soft.
- **Story 43-6 (admin API)** — owns the response shape that AC9's `enforcementSites` lands in. Coordinate the field name in lockstep; this story owns the *requirement*, 43-6 the serialization.
- **Story 43-9 (seams)** — consumes `.Governs` (it attaches the filter) and adds `POST /api/v1/governance/evaluate` to the allowlist. **43-8 lands first**; 43-9's Seam C is `.Governs` plus a filter, and the metadata must already exist.
- **Existing, verified:** `ContractBindingTests` ratchet mechanics, `TaxonomyDriftBuildTests` reflection + tripwires, `WebApplicationFactory` test hosts, `EndpointDataSource`.

## Out of Scope

- **A Roslyn analyzer.** Rejected on measurement (Architectural Context). Not deferred — decided.
- **Closing the holes in AC10.** They are recorded, not fixed. `file_write` path granularity, the two unmerged shell denylists, MCP's absent drift signal and the Elsa workflow API are named as real holes; each needs its own change.
- **Draining `KnownUngovernedEndpoints` to zero.** The ratchet guarantees it only shrinks; the work of governing ~188 routes is not part of this story and is an open question in the epic README.
- **Any enforcement.** This story adds metadata and tests. The filter, the gate and the 409 are Story 43-9.

## Estimated Effort

5 days

## Amendments — as-landed deviations (2026-07-29)

> Added after adversarial review finding **F10**. Everything below was true of the tree at
> the moment 43-8's harnesses landed but was recorded **only in test doc-comments**, so a
> reader of this story could not learn any of it — while **Story 43-9's plan assumes the
> metadata is already attached**. `Status:` is deliberately NOT flipped here; the
> conformance round is a separate pass.

### A1. What landed: harnesses, not annotations

43-8 landed as a **metadata-and-harness-only** change. That is a defensible choice — it keeps
the story **behaviour-neutral** (no route changes behaviour, no gate evaluates, nothing can
regress in production) and it is **honestly pinned** by
`GovernedEndpointBindingSweepTests.NoProductionRouteIsBoundYet_isTheDayOneState`, which
asserts that zero routes carry a binding today. But the following **seven AC-level carve-outs
were deferred and were not written down anywhere a story reader would look**. Each was
verified absent from the tree on 2026-07-29.

> **Table extended 2026-07-29 (conformance round).** Rows 6 and 7 were omitted by the first
> pass of this amendment. Both are AC-level: the AC names a mechanism or a number that the tree
> does not have, and both were explained only inside a harness doc-comment. Rows 1–5 were
> independently re-verified in this round and all five still hold, in both directions.

| # | AC | What the AC required | State in the tree | Why deferred |
|---|----|---------------------|-------------------|--------------|
| 1 | **AC1, step 2** | `[Governs(ns, key)]` on `MentorshipController`'s 4 `[HttpPost]` actions | `GovernsAttribute` **type exists, 0 usages** | Mentorship session lifecycle has **no catalog member** (it is baselined `no-catalog-member` in `KnownUngovernedEndpoints`). There is nothing to bind it *to*; inventing a member to satisfy the annotation would manufacture a phantom capability, which the story's own architectural context forbids. |
| 2 | **AC1, step 3** | `.Governs` on the 17 mediation routes | **0 call sites in `src/`** | Attaching a binding is Seam C of **Story 43-9**, which attaches the enforcement filter in the same call so annotating and enforcing stay one action. 43-8 deliberately landed the metadata *shape* first so 43-9's binding is visible to a harness the moment it lands. |
| 3 | **AC4, step 7** | `[PerformsEffect]` on the 17 `TammaApiClient` methods | attribute **type ships unused (0 usages)**; the mapping is a **test-side table** (`MediationClientEffectSweepTests.EffectPerformingSites`) | See **A2** — the recorded justification does not hold, and this one has a real consequence for 43-9. |
| 4 | **AC9, step 12** | `ActionEnforcementSites` + `enforcementSites` on the admin action DTO | **0 hits anywhere** | The admin response shape is Story 43-6's file. With zero bound routes the array would be empty for every action, so the field's *value* is trivially known; its **absence**, however, means the UI has no way to render "not enforced anywhere yet", which is precisely the lie AC9 exists to prevent. **This is the carve-out with the largest honesty cost and should be closed by 43-6/43-9, not deferred again.** |
| 5 | **AC8** | a `RatchetDisciplineTests` meta-test asserting all four ratchets have (a) staleness, (b) classification, (c) a count pin | **absent** | The three properties are asserted **per ratchet** in each owning fixture (`GovernedEndpointCoverageSweepTests.Baseline_countIsPinned` / `…_justificationsAreClassified` / the staleness arms of `EveryMutatingEndpoint_IsGovernedOrJustified`, and the equivalents in `MediationClientEffectSweepTests` and `BackgroundActorRegistrationSweepTests`). What is missing is the **meta**-assertion that a FUTURE ratchet also has all three — so a fifth ratchet can ship with only two of the properties and nothing notices. |
| 6 | **AC5** | coverage of BOTH hosts, `Tamma.ElsaServer` "via a host-builder fixture" | **no such fixture exists.** `Tamma.Api.Tests` does not reference `Tamma.ElsaServer` and cannot boot its Elsa/EF composition, so no ElsaServer registration is ever reflected off a built `IServiceCollection`. In its place: a HAND-WRITTEN set, `BackgroundActorRegistrationSweepTests.ActorsRegisteredInTheElsaServerHostOnly` (`:89-97`), count-pinned at **6** (`:361`) | Booting the engine host from the API test assembly means an assembly reference and an Elsa/EF composition this project does not have. The pinned name set keeps the blind spot **bounded and loud** — a new ElsaServer actor fails the pin — but nothing asserts that host's REGISTRATIONS; those six types are covered only at CLASS level by `BackgroundActorCatalogSweepTests`. Recorded in that harness's doc-comment, **not** in the story. |
| 7 | **AC6** | the baseline "seeded with the verified **13 files**" (plan step 10: `Count.Should().Be(13)`) | the tree pins **9** (`UnattributedActivitySweepTests.Baseline_countIsPinned`) | Thirteen FILES, nine `IActivity` CLASSES: `RotationActivityBase` is abstract and `DrainRotationAuditEmitter` / `RotationAuditDrainScope` / `RotationWorkflowState` are not activities. The sweep counts what can carry `[Activity]`, so 9 is correct and `Be(13)` would have been unsatisfiable. Explained in the harness's seeding note, **not** in the story. |

### A2. F11 — the `[PerformsEffect]` justification does not hold, and 43-9 is affected

`MediationClientEffectSweepTests`'s doc-comment justifies carrying the effect→method mapping
as a **test-side table** rather than as `[PerformsEffect]` attributes on the grounds that
"applying the 17 attributes to `TammaApiClient` itself is a source edit to a file another
in-flight story is extending."

**That justification is factually wrong.** `Tamma.Activities/LlmCall/TammaApiClient.cs` is not
in this commit's diff at all — no in-flight story was editing it.

The table is a **fine drift mechanism** on its own terms: it is bidirectional, its method names
are resolved by reflection (a rename fails the build), and
`EveryAttributedMethod_AgreesWithTheTable` guarantees an attribute can never disagree with it,
so entries can graduate one at a time. What the table **cannot** do is be consumed by
production code — it lives in a test assembly. Concretely:

- **Story 43-9's filter** cannot ask a `TammaApiClient` method which effect it performs.
- **AC9's `enforcementSites`** cannot enumerate method-plane enforcement sites.

**Therefore 43-9 must apply the 17 `[PerformsEffect]` attributes first** (a mechanical change —
the sweep already proves each mapping and will reject a disagreeing attribute), and only then
consume them.

### A3. What Story 43-9 must do first, in order

1. Apply `[PerformsEffect(ExternalEffect.X)]` to the 17 mutating `TammaApiClient` methods named
   in AC4. The sweep validates each against the table as it lands; the table then retires
   member by member.
2. Attach `.Governs(key)` **+ the enforcement filter** to the 17 mediation routes, deleting each
   route's `KnownUngovernedEndpoints` entry and decrementing `PinnedCount` in the same commit.
   `NoProductionRouteIsBoundYet_isTheDayOneState` will go red on the first binding — **delete
   that test in the same commit**; it is a day-one pin, not a permanent invariant.
   *(Reconciled 2026-07-29, conformance round: that test's own failure message used to say
   "update this pin to the new count", which contradicted this step. The message now says
   **delete**, matching here. Delete is the resolution because the test's NAME asserts zero
   bindings — raising the number would make the name a lie. Note also that decrementing
   `PinnedCount` alone is not enough: a bound route leaves the in-scope surface but is no longer
   baselined, so `PinnedInScopeCount` stays put while `PinnedCount` drops. The two are equal only
   while zero routes are bound; after the first binding they diverge, and
   `InScopeEndpointCount_isPinned` / `Baseline_countIsPinned` must be reconciled independently.)*
3. Add `POST /api/v1/governance/evaluate` to the baseline with the justification
   `gate-evaluation-endpoint-cannot-gate-itself` (AC11), and delete
   `GovernedEndpointCoverageSweepTests.PreProvisionedJustificationKeyword_isStillUnused`, which
   pins that arm's 0 uses today.
   *(Added 2026-07-29, conformance round: this step must bump **both** pins. A new route is new
   in-scope mutating surface AND a new baseline entry, so `PinnedInScopeCount` 237 → 238 **and**
   `PinnedCount` 237 → 238 in the same commit — `InScopeEndpointCount_isPinned` and
   `Baseline_countIsPinned` are separate assertions and both go red otherwise. The original step
   named only the baseline.)*
4. Coordinate `enforcementSites` with 43-6 (carve-out #4) **before** any UI renders an action as
   governed.
5. Decide separately whether `MentorshipController` gets a catalog member (carve-out #1); if it
   does not, the `[Governs]` attribute type stays unused and that should be stated, not left
   looking like an oversight.

### A4. Other landed deviations recorded by this pass

- **F15** — the ungoverned baseline's family grouping hid the agent-provider **credential**
  writes and **escalation resolution** behind a generic `no-catalog-member: agent / workflow /
  document orchestration write` paraphrase. Those four entries now carry their own
  justification lines; `PinnedCount` is unchanged (237) because no entry was added or removed.
- **F16** — the two `POST /api/kb/mcp/servers/{id}/start|stop` baseline entries claimed to be
  "the C# half of the catalogued `effect:mcp.tool.invoke` member". They were never bindable to
  it: the member's `SiteKey` was an **alternation**, which matches no registered route. Their
  justifications now read `no-catalog-member: MCP-SERVER LIFECYCLE`, and the invocation route
  `POST /api/kb/mcp/tools/invoke` is recorded as the one route the member names.
- **F17** — `GovernsExtensions`'s `RouteGroupBuilder.Governs` overload was **removed**. Under
  `SiteKey` equality at most one route in a group can ever match its `ActionKey`, so the helper
  guaranteed N−1 binding failures; it had zero call sites. AC1 names only the
  `RouteHandlerBuilder` shape, so nothing in the story is lost.
- **F12** — `MediationClientEffectSweepTests` discovered client methods with
  `typeof(Task).IsAssignableFrom(m.ReturnType)`, making a `ValueTask`-returning mediation
  method **completely invisible** (proved by mutation: a real
  `public ValueTask<bool> ZzNukeProductionAsync()` left all 13 tests green). Discovery no longer
  filters on return type. AC4's wording — "every public `Task`-returning method" — should be
  read as **"every public instance method"** from here on.

### A5. Harness names, ratchet names, and a CI filter that does not exist (2026-07-29)

Every harness landed under a `…SweepTests` name; the ACs name them without the suffix. The
mapping is recorded **once**, in the note above AC2, rather than by editing five ACs — AC4's
`ExternalEffectCallSiteTests` → `MediationClientEffectSweepTests` is the largest rename, and
AC5 splits into two fixtures (`BackgroundActorRegistrationSweepTests` for the registration
plane, `BackgroundActorCatalogSweepTests` for the class plane) because the two ask different
questions. AC4's ratchet is `KnownNonEffectClientMethods` in the tree, not
`KnownReadOnlyClientMethods`; the tree's name is the more accurate one and was kept.

**The suffix was justified by a mechanism that does not exist.** Three harness doc-comments
claimed the `…Sweep` name was needed so "the epic's CI drift filter (`~Drift|~Sweep|~Catalog`)"
would select the fixture — `GovernedEndpointBindingSweepTests:46`,
`GovernedEndpointCoverageSweepTests:54`, `DispatchPairCatalogSweepTests:28`. There is no such
filter: `.github/workflows/ci.yml:248` runs `dotnet test "$proj" --no-build -c Release …` once
per project directory, with no `--filter` anywhere, and the string occurs nowhere in `docs/`.
The fixtures run because they are in `tests/Tamma.Api.Tests` and `tests/Tamma.Activities.Tests`,
not because of their names. AC10 makes these doc-comments load-bearing — a reader of a passing
test is entitled to trust what the comment says the harness relies on — so all three
justifications were corrected in this round. The suffix itself is kept; it is a convention, and
nothing depends on it.

## Change Log

| Date       | Version | Changes                                                                                     | Author |
| ---------- | ------- | ------------------------------------------------------------------------------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation                                                                        | Claude |
| 2026-07-29 | 1.1.0   | Amendments §A1–A4: five deferred AC carve-outs, F11's void justification, F12/F15/F16/F17 fixes | Claude |
| 2026-07-29 | 1.2.0   | Conformance round: §A1 extended to **seven** carve-outs (AC5's absent ElsaServer host-builder fixture; AC6's 13 files → 9 activity classes); §A3 reconciled with the day-one pin's failure message and extended with the `PinnedInScopeCount` bump; new §A5 (harness/ratchet name mapping + the non-existent CI drift filter); dated corrections on AC2 (4 exempt SignalR endpoints, not 2), AC4 (ratchet name), AC5, AC6, AC7 (separate fixture, tripwires not inherited), AC9 (**0** governed / **237** in scope / **237** baselined, not 17 / ~205 / ~188); Architectural Context + plan D1 flagged: 231 mutating `Map*` today, so the ~40% miss rate is unmeasured | Claude |

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

**An analyzer with a 40% blind spot is worse than none.** The epic's guarantee is *completeness*; a completeness guarantee that is not complete is exactly the failure this epic exists to prevent, and it would be believed. CI blocks the merge either way. What is genuinely lost is **local-build feedback**: a developer who skips `dotnet test` can push an ungoverned route and only learn on CI. That is recorded as an accepted cost, not hidden (epic README, Drift prevention; design §12 risk 4).

### The surfaces, verified

| Surface | Where | Count | Verified |
|---|---|---|---|
| Minimal-API mutating routes | `Tamma.Api/Program.cs` | 200 | `grep -cE '\.Map(Post\|Put\|Patch\|Delete)\('` |
| Attribute-routed controller actions | `Tamma.Api/Controllers/MentorshipController.cs` — the **only** controller in the repo | 4 `[HttpPost]` at `:53,141,164,187` (+ 4 `[HttpGet]`) | file read |
| SignalR negotiate endpoints | `Program.cs:3384-3385` | 2 hubs → 2 `/negotiate` POSTs | file read |
| Engine mediation client | `Tamma.Activities/LlmCall/TammaApiClient.cs` | **36** public `Task`-returning methods, **17** mutating | `grep -nE '^\s+public (virtual )?(async )?Task'` |
| Background actors (`IHostedService`) | both hosts | 25 total | see below |
| Activities missing `[Activity]` | `Tamma.Activities/SecretsRotation/Activities/` | **13 files**, none carrying `[Activity]` | `ls` |

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

2. **`GovernedEndpointCoverageTests` — the load-bearing harness (code → catalog, all endpoints).**
   In `tests/Tamma.Api.Tests/Actions/`. Boots the real app with `WebApplicationFactory`, walks `EndpointDataSource.Endpoints`, and for **every** endpoint whose `HttpMethodMetadata` includes POST/PUT/PATCH/DELETE — **all ~205, not just the `EngineServiceOnly` subset** — plus an explicit named list of governed GETs (`effect:secret.reveal`), asserts the endpoint carries **either** `ActionGateMetadata` **or** a `KnownUngovernedEndpoints` entry with a non-empty justification. Controller actions and hub endpoints are **in scope, not skipped**; the two SignalR `/negotiate` endpoints are exempted as a **named, count-pinned class** (`ExemptEndpointClasses`), never by a wildcard.

3. **`GovernedEndpointBindingTests` — the binding is the right one (route plane).**
   For every endpoint carrying `ActionGateMetadata`, the referenced `ActionKey` must resolve in `ActionCatalog.ByKey` **and** that descriptor's `SiteKey` must equal `$"{method} {routePattern}"`. Without this, `.Governs(anyExistingMember)` silences the coverage test with a wrong binding — a governed-looking route pointing at an unrelated action. The test **must state in its failure message** that it cannot do the same for attributed C# methods (see AC10).

4. **`ExternalEffectCallSiteTests` — bidirectional over the mediation client.**
   `[PerformsEffect(ExternalEffect.X)]` is applied to the **17 mutating `TammaApiClient` methods**: `CallLlmAsync:109`, `CreateBranchAsync:128`, `CreatePullRequestAsync:136`, `MergePullRequestAsync:144`, `UpdateIssueStatusAsync:152`, `CreateReleaseAsync:167`, `DeleteBranchAsync:217`, `TriggerTestsAsync:249`, `UpdateJiraTicketAsync:283`, `DispatchAgentRunAsync:300`, `QueueSlackNotificationAsync:386`, `SendEmailAsync:403`, `AppendEventsAsync:530`, `PersistDocumentAsync:579`, `SetDocumentStatusAsync:609`, `PostChannelOutboxAsync:663`, `AppendPlatformEventsAsync:710`. The test asserts **both** directions: every attributed method names a real `ExternalEffect` member with a catalog descriptor, **and** every public `Task`-returning method on `TammaApiClient` that is *not* attributed appears in a shrink-only `KnownReadOnlyClientMethods` list carrying a count pin. Total public `Task`-returning methods is **36**, so the read-only list seeds at **19** — pinned.

5. **`BackgroundActorCoverageTests` — both hosts, both awkward registration shapes.**
   Reflects `IHostedService` registrations off the **built `IServiceCollection`** for `Tamma.Api` (via `WebApplicationFactory`) and `Tamma.ElsaServer` (via a host-builder fixture), and asserts each maps to an `automation:*` catalog member. The **factory registration at `Program.cs:1385`** (null `ImplementationType`) is handled by a named `KnownFactoryRegisteredServices` pair list — `(descriptor position or factory-declaring member) → actor key` — never by skipping null-`ImplementationType` descriptors. Bidirectional: every `automation:*` member must map to a registered service or a justified `NotRegisteredActors` entry.

6. **`UnattributedActivityTests` — the first mechanism that can see them.**
   Reflects `Tamma.Activities` for types deriving from `CodeActivity`/`Activity` that carry **no `[Activity]` attribute**, asserted against a shrink-only allowlist seeded with the verified **13 files** in `Tamma.Activities/SecretsRotation/Activities/`. Implemented as a **test, not an analyzer** — the property is reflection-visible, so a test is strictly cheaper and equally complete.

7. **One added assertion in `TaxonomyDriftBuildTests`.**
   The existing harness already materializes each `DispatchWorkflow`'s real `(role, action)` pair by invoking the `Input` delegate against a synthetic `ExpressionExecutionContext` (`:694-740`). Beside the eligibility check at `:226-242`, add: the materialized action wire must resolve to `ActionKey(ActionNamespace.AgentAction, wire)` in `ActionCatalog.ByKey`. ~15 lines. Its four existing anti-no-op tripwires protect the new assertion for free.

8. **Every ratchet is shrink-only, staleness-checked AND count-pinned.**
   Four ratchets ship: `KnownUngovernedEndpoints`, `KnownReadOnlyClientMethods`, `NotDiRegisteredTools` (authored in Story 4, consumed here), `UnattributedActivities`. For each: (a) an entry that now passes fails as **stale**; (b) justification strings are non-empty and keyword-classified (`ContractBindingTests` idiom); (c) a **count pin** `X.Count.Should().Be(N)` so an *addition* fails the build. (c) is not optional — `ContractBindingTests.cs:255-271`'s shrink-only property is a comment, and additions are otherwise undetectable.

9. **Honesty about day-one coverage — `enforcementSites` on the API response.**
   On the day this story lands the catalog governs roughly **17 of ~205 mutating routes** (the mediation set Story 43-9 binds); ~188 ship in `KnownUngovernedEndpoints`. The Story 6 admin API response for each action therefore exposes `enforcementSites: string[]` — the concrete sites that will actually evaluate the gate for that action — and the Story 7 UI renders an explicit "not enforced anywhere yet" state for an empty array. **A catalog row with zero enforcement sites must not render as governed.** Without this the UI implies coverage that does not exist, which is the same class of lie the epic is built to prevent.

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
- **Draining `KnownUngovernedEndpoints` to zero.** The ratchet guarantees it only shrinks; the work of governing ~188 routes is not funded by this story and is an open question in the epic README.
- **Any enforcement.** This story adds metadata and tests. The filter, the gate and the 409 are Story 43-9.

## Estimated Effort

5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

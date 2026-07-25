# Implementation Plan — Story 43-2: Catalog Core

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.Core/Actions/` holds one closed, compile-checked, `[Wire]`-encoded vocabulary naming every consequential action Tamma can take: `ActionNamespace` (6), `ActionKey` (composite, round-trip-tested), `ActionRisk` (4), the four new enums `ToolAction` (8), `ExternalEffect` (22), `BackgroundActor` (25), `PlatformTaskKind` (8), and `GitSubcommand` (14, replacing a private `HashSet` in `GitOperationsTool`), plus `ActionDescriptor` and an `ActionCatalog` whose `BuildIndex` throws at static init on any of seven inconsistency classes and is touched eagerly at boot in **both** hosts. Bidirectional keyset-equality tests bind the `agent-action` and `document-type` planes to their existing canonical enums. Every count is re-derived from the tree and pinned.

No storage, no endpoint, no group assignment, no default values, no enforcement.

## Pre-Reading

- `apps/tamma-elsa/src/Tamma.Core/Tamma.Core.csproj` — **zero `<ProjectReference>`**, one `<PackageReference>` (`System.Text.Json` 8.0.6). The reason the catalog lives here and the reason it cannot touch a DB.
- `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs:1-45` — `WireAttribute`; `EnumWire<TEnum>`'s static-ctor validation (exactly one `[Wire]` per member, distinct wires, **ordinal case-sensitive** parse). Read the header note: the file lives in `Tamma.Core` but keeps the `Tamma.Api.Services.Agents` namespace (Story 27-19).
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentJson.cs:58` — `internal sealed class WireEnumJsonConverter<TEnum>`. **Internal.** See D5.
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs` — 80 `[Wire(` members; header comment claims a different figure (**stale — see C1**)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:43-163` (the enum-referenced literal table — the posture `Descriptors.cs` copies) and `:170-171` (`s_rolesForAction = BuildRolesForAction()` — the projected-index idiom)
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs` (10 members) + `DocumentTypeRegistry.cs` — the fail-loud index precedent
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:120-147` (`ValidateEscalationClass` — the shipped switch-on-kind-delegate-to-registry shape this story generalizes) and `:204-210` (`EscalationClassKind`'s two wire strings, which `ActionNamespace` must preserve byte-for-byte)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs:20-25` (the `HashSet<string> AllowedSubcommands`), `:29-31` (the `Description` restating the same 14 names), `:78-82` (the validation + error message)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-765` — the six `AddSingleton<IToolExecutor, …>` registrations + `TryAddSingleton<IToolExecutorRegistry, …>`; `:415-422` — the deliberately-unregistered seventh (`GetAcceptanceRulesTool`, see 43-0)
- `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandlerRegistry.cs:25-40` (`RegisteredTypes`), `:50-78` (the duplicate-throwing ctor)
- `apps/tamma-elsa/src/Tamma.Core/Audit/` — `SensitiveActionCatalog` (53 codes × 11 categories), the optional `SensitiveActionCode` join target
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — the strongest drift-guard in the repo; the keyset-equality style AC12 copies
- `apps/tamma-elsa/tests/…/ConventionSeedDriftTests.cs:12-20` — "codegen is a deliberate repo non-goal" (why no source generator)
- `docs/stories/epic-43/README.md` — the sixteen-vocabulary table, the model, decisions D1–D3/S1–S6
- `docs/stories/epic-43/story-43-1/…` — `AutonomyDial` (this story's only hard prerequisite)

## Corrections to the design

- **C1 — `AgentAction` is 80, and the file's own header comment is stale.** Verified by counting `[Wire(` in `Tamma.Core/Agents/AgentAction.cs`: **80**. The header prose says 79. The design's table says 80 (correct). Fix the comment in this story's commit — a stale count in the canonical vocabulary is exactly the drift the epic indicts, and it is one line.
- **C2 — `WireEnumJsonConverter` is `internal`** (`DocumentJson.cs:58`), which the design's code sketch does not mention. It is usable from `Tamma.Core.Actions` because that is the same assembly; it constrains nothing here, but it does mean an enum of this family **cannot** be declared outside `Tamma.Core` while carrying the attribute. Recorded so a later story does not try to put `ActionGroup` or a follow-on enum in `Tamma.Api`. See D5.
- **C3 — `AllowedSubcommands` spans `GitOperationsTool.cs:21-25` for the declaration but the same 14 names are restated a *second* time in the tool's `Description` at `:29-31`, and a *third* time in the error message at `:81`** (that one derived from the set, so it is fine). The design names only the `HashSet`. Replacing the `HashSet` without deriving the `Description` leaves a restatement that will drift the first time a subcommand is added. Step 5 derives both.
- **C4 — `PlatformTaskHandlerRegistry` implements `IPlatformTaskHandlerRegistry`, not `IPlatformTaskHandler`.** A naive `grep ": IPlatformTaskHandler"` returns 9 hits; one is the registry. **8** genuine handlers: `RetireSecretVersionTaskHandler`, `ActivateScheduledPlanTaskHandler`, `MoveTenantTaskHandler`, `CranlProvisionPlatformTaskHandler`, `ProvisionTenantV2TaskHandler`, `CranlDeprovisionPlatformTaskHandler`, `BillingWebhookFollowupTaskHandler`, `CreateBillingCustomerTaskHandler`. Matches the design's 8. Note every one sources its `TaskType` from a **constant on another type** (`RetireScheduler.TaskType`, `MoveTenantTaskPayload.TaskType`, `CranlTenantProviderV2.ProvisioningTaskType`, …), so the pin must be written against those constants, not against string literals.
- **C5 — the `ExternalEffect` and `BackgroundActor` counts are the design's hypotheses, and this story's job is to falsify or confirm them.** The design derives 22 and 25 from greps run during synthesis. Step 1 re-runs both derivations; any delta is recorded here and the count pin takes the derived value. Do **not** author to the number.

## Design Decisions

- **D1 — Composite `ActionKey`, not a flat enum.** A flat ~153-member enum duplicates 90 wire strings (80 `AgentAction` + 10 `DocumentTypeKey`) into a second vocabulary — the epic's own indictment, committed by the artifact meant to prevent it. The composite is not a new idiom: `AcceptanceRules.ValidateEscalationClass` (`:120-147`) already switches on a kind and delegates key parsing to the owning registry, and its two wire strings are persisted in `acceptance_rules_overrides`. `ActionNamespace` preserves them exactly, which is what makes the `agent-action:`/`document-type:` key space a **strict superset** of live data and 43-3's `AlwaysEscalate` absorption a floor rather than a migration.
- **D2 — Home is `Tamma.Core/Actions/`, a new namespace, using the real `Tamma.Core.Actions` name.** `AgentAction` and `EnumWire` sit in `Tamma.Core` under the legacy `Tamma.Api.Services.Agents` namespace for compatibility reasons that do not apply to new code. New types get the honest namespace; consumers add one `using`.
- **D3 — `Descriptors.cs` is a hand-written array literal with enum-referenced keys, not codegen and not reflection-at-startup.** `AgentAction.Deploy.ToWire()` rather than `"deploy"` means a renamed enum member is a **compile error**, not a runtime miss. Codegen is a stated repo non-goal (`ConventionSeedDriftTests.cs:12-20`). Building descriptors reflectively from `[Wire]` would remove the compile check and make the descriptor set unreviewable in a diff — the group assignment (43-3) is exactly the thing that must be reviewable line by line.
- **D4 — `ActionGroup` ships in THIS story as a declaration-only enum; 43-3 owns the assignment.** `ActionDescriptor.Group` is non-nullable and typed, so it cannot compile without the enum. Two options were considered: (a) a one-member stub here, replaced wholesale in 43-3 — rejected: it makes every descriptor line churn twice and makes 43-3's diff unreadable; (b) **ship the full 15-member `ActionGroup` enum here as a pure `[Wire]` declaration with no membership semantics, and let 43-3 assign, project `ByGroup`, and add totality/disjointness/`GROUP_EMPTY`.** Taken: the enum is 15 lines of vocabulary, the *judgment* is the assignment, and splitting them this way makes 43-3's diff exactly the thing needing review. Descriptors authored here carry a provisional group with a `// 43-3` marker; a test asserts the marker count equals the descriptor count until 43-3 lands, so provisional values cannot leak into a release.
- **D5 — All catalog enums live in `Tamma.Core` because `WireEnumJsonConverter` is `internal`** (C2). Not a preference — an assembly constraint. Documented at the top of `ActionNamespace.cs` so the next enum in this family is not born in `Tamma.Api`.
- **D6 — `ActionKey.Parse` splits on the FIRST `:`, ordinal, and is fail-loud.** `git_operations.read` contains a `.` but no `:`; nothing in any key vocabulary contains a `:`, and a first-`:` split keeps the parser total even if one later does. Casing is ordinal-strict, matching `EnumWire`'s deliberate posture ("non-canonical casing in persisted data is rejected, not silently accepted"). `TryParse` exists for the API layer (43-6 returns 400 on a bad wire; it must not need to catch).
- **D7 — `BuildIndex` fails loud at static init, in both hosts, eagerly.** The `PromptFileLoader` posture, proven at 101 files. Eager touch matters: a lazily-initialized `FrozenDictionary` would first throw inside whatever request happened to reach the gate, producing a 500 with a stack trace pointing at the caller rather than a boot failure pointing at the catalog. **Both** hosts — the engine plane runs its own composition and a catalog broken only there would be found at the first Seam-E call. Accepted cost (epic risk 5): adding an `AgentAction` member without a descriptor stops the app, including `WebApplicationFactory` test hosts. That is the guarantee, stated rather than softened.
- **D8 — Seven distinct throw codes, one test each, rather than one generic `ACTION.CATALOG.INVALID`.** The failure lands at boot on a developer who has just added an enum member; the message is the entire remediation UX. Codes: `DUPLICATE_KEY`, `MISSING_DESCRIPTOR`, `ORPHAN_DESCRIPTOR`, `INVALID_DEFAULT`, `EMPTY_METADATA`, `DUPLICATE_SITE_KEY`, `UNKNOWN_NAMESPACE_KEY`. (`GROUP_EMPTY` and the totality/disjointness codes are 43-3's.)
- **D9 — Keyset equality is asserted as SET equality, both directions, for the two derived planes only — and the self-referential nature of the other three is written into the test file.** `{descriptors where Ns=AgentAction}.Keys == AgentAction wire set` catches both a missing descriptor and an orphan. For `ExternalEffect`/`BackgroundActor`/`ToolAction` the same test compares the enum to descriptors *this story also wrote*, which proves internal consistency and nothing about reality. The test file says so in a header comment naming Story 43-8 as the only mechanism that binds those 55 members to real call sites. Pretending otherwise is worse than the gap.
- **D10 — Counts are derived, then pinned; the design's figures are hypotheses.** Step 1 re-runs each derivation and records the command used in a comment beside the pin, so the next person can re-run it. If `ExternalEffect` comes out 21 or 23, the catalog takes the derived number and C5 is updated — the design is not authority over the tree.
- **D11 — `GitSubcommand` replaces the `HashSet` and derives the `Description` (C3), but does NOT change the permitted set and does NOT resolve a gate.** Fourteen in, fourteen out, count-pinned. The `read|write` grade ships as data on the enum for 43-4 to consume. Changing the permitted set inside a vocabulary refactor would hide a policy change in a mechanical diff.
- **D12 — `SensitiveActionCode` is an optional string join, not a required mapping.** The epic's open question 3 (does legal need one artifact with SOC2 mappings across all ~153 members?) is unsettled, and settling it enlarges scope materially. An optional field keeps `SensitiveActionCatalog` the compliance artifact and the action catalog the authorization artifact, joined where a join exists. If the answer later comes back "one artifact", the field becomes required — a widening, not a rewrite.

## Implementation Steps

1. **Re-derive every count and freeze it** (AC14, D10, C5). Before writing a type: count `[Wire(` in `AgentAction.cs` (expect 80) and `DocumentTypeKey.cs` (expect 10); enumerate mutating `EngineServiceOnly` routes plus the four extra surfaces and the deploy split in `Tamma.Api/Program.cs` (expect 22); enumerate `AddHostedService` across `Tamma.Api/Program.cs` and `Tamma.ElsaServer/Program.cs` plus `PlatformTaskWorker` (expect 25); enumerate `: IPlatformTaskHandler` excluding the registry (expect 8, per C4); enumerate `IToolExecutor` implementations (expect 7 → 8 members after the git split). Record each command and result in the PR description and as comments beside the pins. **Update `AgentAction.cs`'s stale header count (C1).**

2. **CREATE `Tamma.Core/Actions/ActionNamespace.cs`, `ActionKey.cs`, `ActionRisk.cs`** (AC1–AC3, D1/D5/D6). `ActionNamespace` carries the D5 note about `WireEnumJsonConverter` being internal. `ActionKey.Parse` throws `TammaError` `ACTION.KEY.INVALID` (severity high, non-retryable) with the offending wire in context.

3. **CREATE `Tamma.Core/Actions/ToolAction.cs`, `ExternalEffect.cs`, `BackgroundActor.cs`, `PlatformTaskKind.cs`** (AC4–AC7) at the counts derived in step 1. Each member's XML doc names its real site (route + method, `IToolExecutor` class, `IHostedService` class, `TaskType` constant). `BackgroundActor`'s factory-registered member (the `AddHostedService` overload with a null `ImplementationType`) carries a comment flagging it for 43-8's sweep. `PlatformTaskKind`'s wires are written against the `TaskType` **constants** (C4), not literals.

4. **CREATE `Tamma.Core/Actions/ActionGroup.cs`** (D4) — the 15 `[Wire]` members as a pure declaration, with a header stating that membership, projection and totality are Story 43-3's.

5. **CREATE `Tamma.Core/Actions/GitSubcommand.cs`; MODIFY `Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs`** (AC8, D11, C3) — 14 `[Wire]` members with a `read|write` grade; delete `AllowedSubcommands` (`:21-25`); `:78` validates via the enum; `:81`'s message derives from it (already does); **`:29-31`'s `Description` is derived** rather than restating the names. Count pin + a test asserting the permitted set is byte-identical to the pre-refactor `HashSet` contents.

6. **CREATE `Tamma.Core/Actions/ActionDescriptor.cs`** (AC9) — the record per the story, with `DefaultMinAutonomy` documented as `[AutonomyDial.Min, AutonomyDial.AlwaysHuman]` and a doc line forbidding literals.

7. **CREATE `Tamma.Core/Actions/ActionCatalog.cs` + `ActionCatalog.Descriptors.cs`** (AC10–AC11, D3/D7/D8) — the array literal (one line per member, enum-referenced keys, provisional `Group` values marked `// 43-3`), `ByKey`, `Get`/`TryGet`, `UnclassifiedFallback = AutonomyDial.AlwaysHuman`, and `BuildIndex` with the seven codes. Titles and summaries are written now (they are UI copy in 43-7 and a boot-failure requirement here); keep them one line each.

8. **MODIFY `Tamma.Api/Program.cs` and `Tamma.ElsaServer/Program.cs`** (AC13, D7) — one eager static read of `ActionCatalog.ByKey.Count` during composition, with a comment explaining that the read exists to force static init at boot rather than at first gate call.

9. **CREATE the test suite** under `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/` (see Test Plan) — count pins, wire pins, round-trip, the two keyset-equality tests, seven `BuildIndex` failure tests, `EveryDefault_IsOverridableOverTheApi`, the `EscalationClassKind` byte-parity pin, and the provisional-group marker test.

10. **Verify** — `dotnet build`, `dotnet test`, `dotnet ef migrations has-pending-model-changes` clean. Then **boot both hosts** and confirm the eager touch runs: temporarily add an `AgentAction` member with no descriptor and confirm the app refuses to start with `ACTION.CATALOG.MISSING_DESCRIPTOR` naming the member; revert. Record in the PR.

## Data & Migrations

**None.** `Tamma.Core` has no project references and therefore no EF dependency; nothing in this story is persisted. Storage (`action_assignments`, `action_authorizations`) is Story 43-5, and it is control-plane resident and deliberately excluded from the destructive startup DROP list — noted here only so this story is not mistaken for the place where that decision is implemented. `dotnet ef migrations has-pending-model-changes` must stay clean.

## Events

None emitted or consumed. Catalog membership is compile-time data; gate outcomes (`ACTION.GATE.*`) arrive with 43-5's audit service and 43-9's seams.

## Test Plan

All NUnit + FluentAssertions in `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/`.

- **`ActionVocabularyCountTests`** — one pin per enum (`ActionNamespace` 6, `ToolAction` 8, `ExternalEffect` 22, `BackgroundActor` 25, `PlatformTaskKind` 8, `GitSubcommand` 14, `ActionGroup` 15) plus `TotalCatalogMembers` (working 153). Each pin's comment carries step 1's derivation command. **Covers AC14.**
- **`ActionWirePinTests`** — every member's exact wire string, per enum. Renaming a wire is then a deliberate reviewed diff. **Covers AC4–AC8.**
- **`ActionKeyTests`** — `ToWire`/`Parse` round-trip for **every catalogued member**; first-`:` split; `ACTION.KEY.INVALID` on no-colon, empty namespace, empty key, unknown namespace wire; ordinal casing rejected (`Agent-Action:deploy`, `agent-action:Deploy`). **Covers AC2, D6.**
- **`ActionNamespaceCompatibilityTests`** — `ActionNamespace.AgentAction.ToWire() == EscalationClassKind.AgentAction.ToWire()` and the `DocumentType` pair, asserted against `AcceptanceRules.cs:204-210`'s values. **Covers AC1**; this is the pin that keeps live persisted data a subset.
- **`ActionCatalogKeysetTests`** — set equality (both directions) for the `agent-action` and `document-type` planes against `EnumWire<AgentAction>` / `EnumWire<DocumentTypeKey>`; self-referential equality for the three new planes, with a **header comment naming Story 43-8 as the only real binding for those 55 members** (D9). **Covers AC12.**
- **`ActionCatalogBuildIndexTests`** — one test per throw code (D8), each constructing a deliberately-bad descriptor array through an internals-visible test seam and asserting the code **and** that the message names the offending member. **Covers AC11.**
- **`ActionCatalogDefaultsSanityTests.EveryDefault_IsOverridableOverTheApi`** — every `DefaultMinAutonomy` satisfies `AutonomyDial.IsValidThreshold`. **Covers AC15.** (The *values* are 43-3's; this asserts only that they are writable.)
- **`ActionCatalogProvisionalGroupTests`** — until 43-3 lands, asserts every descriptor carries the provisional marker; 43-3 deletes this test in the same commit that assigns groups (D4). Prevents provisional values shipping silently.
- **`GitSubcommandTests`** — 14 members; the permitted set equals the pre-refactor `HashSet` contents byte-for-byte (a literal list in the test, the only place the old strings survive); every member has a `read|write` grade; `GitOperationsTool.Description` contains each wire exactly once and no name the enum lacks. **Covers AC8, C3.**
- **`ActionDescriptorMetadataTests`** — no empty `Title`/`Summary`/`SiteKey`; `SiteKey` uniqueness where required; `EscalatableToHuman` is `false` for every `automation:*` member (43-9's Seam D depends on it; asserting it here means the property is true before anything relies on it).
- **Boot verification (step 10, manual + recorded)** — both hosts start; an intentionally-missing descriptor stops the app with the right code. **Covers AC13.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `ActionNamespace`, wires preserved | 2 | `ActionNamespaceCompatibilityTests` |
| 2 — `ActionKey` parse/round-trip | 2 | `ActionKeyTests` |
| 3 — `ActionRisk` | 2 | `ActionWirePinTests` |
| 4–7 — the four new enums at derived counts | 1, 3 | `ActionVocabularyCountTests`, `ActionWirePinTests` |
| 8 — `GitSubcommand` replaces the `HashSet`, `Description` derived | 5 | `GitSubcommandTests` (set-parity + description) |
| 9 — `ActionDescriptor` | 6 | compiles; `ActionDescriptorMetadataTests` |
| 10 — `ActionCatalog` + enum-referenced descriptors | 7 | `dotnet build` (rename an enum member → compile error) |
| 11 — fail-loud `BuildIndex`, 7 codes | 7 | `ActionCatalogBuildIndexTests` |
| 12 — bidirectional keyset equality | 9 | `ActionCatalogKeysetTests` |
| 13 — eager touch, both hosts | 8 | step 10's boot verification, recorded in the PR |
| 14 — counts re-derived and frozen | 1 | pins + derivation comments; corrections section updated |
| 15 — every default is API-writable | 9 | `EveryDefault_IsOverridableOverTheApi` |

## Risks & Mitigations

- **`BuildIndex` at static init is a boot failure for a forgotten descriptor** (epic risk 5) — and it bites test hosts. Mitigation: intentional and stated; the seven distinct codes name the offending member; step 10 rehearses the failure so the message quality is verified, not assumed. Do not soften to a log-and-continue — a catalog that silently omits a member is the epic's core failure mode.
- **`BuildIndex` is vacuous for 55 of 153 members** (epic risk 6). The three enums this story authors validate against themselves. Mitigation: stated in `ActionCatalogKeysetTests`'s header and in the story; **if 43-8 slips, the `effect`/`automation`/`tool` half of the catalog has no drift protection** — that dependency is explicit, not implied.
- **The design's counts (22, 25) may not survive re-derivation.** Mitigation: D10/C5 — derive first, author second, record deltas. The risk of authoring to the number is a catalog that is wrong on day one and pinned wrong forever.
- **Provisional group values leak into a release if 43-3 slips.** Mitigation: D4's marker test fails while any provisional value remains; 43-3 deletes it in the assignment commit.
- **~153 hand-written descriptor lines is a large reviewable diff and reviewer fatigue is real.** Mitigation: one line per member, enum-referenced, sorted by namespace then wire; titles/summaries kept to one line; the *judgment-bearing* field (`Group`) is deliberately deferred to 43-3 so this diff is mechanical and that one is not.
- **Replacing `AllowedSubcommands` touches a live security check.** Mitigation: D11 — set unchanged, byte-parity test against a literal copy of the old contents, and the gate resolution deliberately deferred to 43-4 so this change has no behavioural surface.
- **`SensitiveActionCode` may need to become required** if the epic's open question 3 resolves toward one compliance artifact (D12). Mitigation: optional now; widening later is additive.

## Blocks / Blocked by

- **Blocked by 43-1** (hard) — `AutonomyDial.Min` / `.AlwaysHuman` must exist before `ActionDescriptor` or `UnclassifiedFallback` can be written without literals.
- **Blocked by 43-0** (soft) — deleting `ResolveToolsActivity` and resolving `GetAcceptanceRulesTool` removes two false candidates before `ToolAction`'s 8 are chosen.
- **Blocks 43-3** (group assignment + defaults + totality), **43-4** (tool reconciliation + boot validator + `GitOperationsTool` gate resolution), **43-5** (storage keyed by `ActionKey`), **43-6** (admin API), **43-7** (admin UI), **43-8** (drift harnesses — and the only real binding for 55 members), **43-9** (the five seams).
- **Parallelizable with 43-8's harness scaffolding** once the enums exist (43-8 can be developed against the vocabulary before descriptors are final).

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | Re-derive and freeze all six counts; fix `AgentAction`'s stale header | 0.5 |
| 2 | `ActionNamespace`, `ActionKey`, `ActionRisk` | 0.4 |
| 3 | The four new enums with per-member site documentation | 1.0 |
| 4 | `ActionGroup` declaration | 0.1 |
| 5 | `GitSubcommand` + `GitOperationsTool` refactor + description derivation | 0.4 |
| 6–7 | `ActionDescriptor`, `ActionCatalog`, ~153 descriptor lines, `BuildIndex` + 7 codes | 1.5 |
| 8 | Eager touch, both hosts | 0.1 |
| 9 | Test suite (counts, wires, round-trip, keysets, 7 failure tests, metadata, git parity) | 0.9 |
| 10 | Build/test/boot verification incl. the deliberate-failure rehearsal | 0.2 |
| **Total** | | **5.1** (story estimate: 5 days) |

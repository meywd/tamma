# Story 43-2: Catalog Core — the Union Vocabulary, `ActionKey`, and a Fail-Loud Index

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

As the **platform**, I want one addressable, closed, compile-checked vocabulary naming every consequential action Tamma can take — spanning agent work phases, document types, tools, external effects, background automation and platform tasks —
So that an admin can later be shown a single governed list instead of sixteen unrelated vocabularies, and so that adding a new capability without classifying it becomes a build failure rather than a silent ungoverned surface.

## Priority

P0 — the spine of Epic 43. Nothing downstream exists without it: 43-3 assigns its members to groups, 43-4 validates tool names against it, 43-5 stores rows keyed by `ActionKey`, 43-6 serves it, 43-7 renders it, 43-8 checks it against real call sites, 43-9 gates on it.

## Architectural Context (READ FIRST)

### Why a composite key and not a flat enum

Consequential capability is spread across sixteen unrelated vocabularies (epic README table). Two of them — `AgentAction` (80 members) and `DocumentTypeKey` (10) — are already canonical, drift-guarded, and persisted. A flat ~153-member `TammaAction` enum would **copy all 80 `AgentAction` wire strings into a second vocabulary**: the exact drift this epic exists to prevent, created by the artifact meant to prevent it.

The composite shape is not novel — **it is already shipped and validated**. `AcceptanceRules.ValidateEscalationClass` (`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:120-147`) switches on an `EscalationClassKind` and delegates key parsing to the owning registry. This story is that switch with four more arms.

```csharp
public readonly record struct ActionKey(ActionNamespace Ns, string Key) {
    public string ToWire() => $"{Ns.ToWire()}:{Key}";   // "agent-action:deploy", "tool:file_write"
}
```

`ActionNamespace` deliberately **preserves the two wire strings `EscalationClassKind` already uses** (`agent-action`, `document-type`, at `AcceptanceRules.cs:204-210`), so `agent-action:` and `document-type:` keys are a strict superset of a vocabulary already persisted in `acceptance_rules_overrides`. That is what makes 43-3's absorption of `AlwaysEscalate` a floor rather than a migration.

### Why `Tamma.Core`

`apps/tamma-elsa/src/Tamma.Core/Tamma.Core.csproj` has **zero `<ProjectReference>`** — verified; its only `<PackageReference>` is `System.Text.Json` 8.0.6. It is therefore the only assembly reachable from `Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer` and `Tamma.Api` alike. `AgentAction` lives there for exactly this reason (`Tamma.Core/Agents/AgentAction.cs`, whose header records the Story 27-19 move out of `Tamma.Api` to break a cycle — while *keeping* the `Tamma.Api.Services.Agents` namespace to avoid churning callers).

**Consequence, stated up front:** Core cannot touch a database. The catalog is pure declaration; storage (43-5) and the gate implementation live elsewhere. This is the shipped `IAcceptanceRulesResolver` split (`interface + pure evaluator in Core, EF-backed impl in Tamma.Api`).

### The existing `[Wire]` machinery this story reuses verbatim

`EnumWire.cs` (in `Tamma.Core`, namespace `Tamma.Api.Services.Agents`) provides `WireAttribute` and `EnumWire<TEnum>`: a bidirectional `FrozenDictionary` pair built and **validated in a static constructor** — every member must carry exactly one `[Wire]`, all wire strings must be distinct, parsing is **case-sensitive ordinal** so non-canonical casing in persisted data is rejected rather than silently accepted. The new enums adopt this without modification.

`WireEnumJsonConverter<TEnum>` exists at `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentJson.cs:58` — note it is **`internal sealed`**. Attributing a public enum with `[JsonConverter(typeof(WireEnumJsonConverter<ActionNamespace>))]` from inside `Tamma.Core` is legal and works across assembly boundaries at runtime (the runtime instantiates by reflection), because the attribute is applied within the declaring assembly. This is a real constraint on *where the enums live*, not a blocker.

### What each namespace keys on, and what is genuinely new

| `ActionNamespace` | Key vocabulary | Count | Status |
|---|---|---|---|
| `agent-action` | `AgentAction` wire | **80** (verified: 80 `[Wire(` in `Tamma.Core/Agents/AgentAction.cs`) | exists, strongest drift guard in repo |
| `document-type` | `DocumentTypeKey` wire | **10** (verified) | exists, fail-loud registry |
| `tool` | `ToolAction` wire | 8 | **NEW** |
| `effect` | `ExternalEffect` wire | 22 | **NEW** |
| `automation` | `BackgroundActor` wire | 25 | **NEW** |
| `platform-task` | `PlatformTaskKind` wire | 8 | **NEW** |

**Working total 153. Story 43-2's first task is to re-derive every count from the tree and freeze the pin** — treat 153 as the design's figure, not a guarantee. Two counts are independently corroborated here: `AgentAction` = 80 (the enum's own header comment says 79 — **the comment is stale**), and `PlatformTaskKind` = 8 (eight `: IPlatformTaskHandler` implementations, each with a `TaskType`, excluding `PlatformTaskHandlerRegistry` itself which implements the *registry* interface).

`ToolAction`'s 8 members are the 7 `IToolExecutor` names with `git_operations` **split by subcommand class** — `git_operations.read` and `git_operations.write` — so `git push` is independently gateable. The split is driven by a new `[Wire] GitSubcommand` enum **replacing the private `HashSet<string> AllowedSubcommands` at `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs:21-25`** (14 members: `status, diff, log, add, commit, push, branch, checkout, stash, show, fetch, pull, rev-parse, ls-files` — verified verbatim). This is the **only** argument-bound split in the epic, and it is cheap because the subcommand parse already exists at `GitOperationsTool.cs:78`.

### The fail-loud index posture

`ActionCatalog.BuildIndex` throws at static init. That is the `PromptFileLoader` posture — already proven in this repo at 101 files (a taxonomy cell without a file, or a file outside the taxonomy, refuses to start). Its cost is stated as a risk, not hidden: **adding an `AgentAction` member without a descriptor becomes a boot failure**, and it bites `WebApplicationFactory` test hosts too.

The by-group index is **projected, never hand-maintained** — the `RolePhaseMap` idiom (`apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:170-171`, `s_rolesForAction = BuildRolesForAction()`).

**Known limitation, recorded not hidden:** `BuildIndex` is **vacuous for 55 of 153 members** — the three enums this story authors (`ExternalEffect`, `BackgroundActor`, `ToolAction`) validate only against themselves. Those planes are bound to reality solely by Story 43-8's reflection harnesses. If 43-8 slips, that half of the catalog has no drift protection.

## Acceptance Criteria

1. **`ActionNamespace`** — `apps/tamma-elsa/src/Tamma.Core/Actions/ActionNamespace.cs`, a `[Wire]` enum of exactly six members with wire strings `agent-action`, `document-type`, `tool`, `effect`, `automation`, `platform-task`, JSON-converted via `WireEnumJsonConverter`. A test pins that `agent-action` and `document-type` are **byte-identical** to `EscalationClassKind`'s wire strings (`AcceptanceRules.cs:204-210`), so the superset property cannot silently break.

2. **`ActionKey`** — `readonly record struct ActionKey(ActionNamespace Ns, string Key)` with `ToWire()`, `Parse(string)` (ordinal split on the **first** `:`, fail-loud `TammaError` code `ACTION.KEY.INVALID`) and `TryParse(string, out ActionKey)`. Round-trip is tested for every catalogued member. Casing is ordinal — `Agent-Action:deploy` is rejected, matching `EnumWire`'s posture.

3. **`ActionRisk`** — `[Wire]` enum: `read-only`, `mutating`, `command`, `destructive`. (This is Epic 42's `ToolPermissionClass` relocated and generalized; 43-10 records the supersession.)

4. **`ToolAction` — 8 members**, count-pinned and wire-pinned: `file_read`, `file_write`, `search_code`, `shell_execute`, `run_tests`, `get_acceptance_rules`, `git_operations.read`, `git_operations.write`. The member set is **derived from the tree** (the `IToolExecutor` implementations, including the deliberately-unregistered `get_acceptance_rules` — see 43-0), not from the design doc.

5. **`ExternalEffect` — 22 members**, count-pinned, each carrying its `SiteKey`. Derived by re-running the route sweep against `Program.cs`, not copied: the design's list (17 mutating `EngineServiceOnly` routes + `mcp.tool.invoke`, `secret.reveal`, `process.spawn`, `deploy.promote-prod`, `deploy.rollback`) is the starting hypothesis and **any discrepancy is recorded in the plan's corrections section**, not silently reconciled.

6. **`BackgroundActor` — 25 members**, count-pinned, one per `AddHostedService` across both hosts plus `PlatformTaskWorker` (a `BackgroundService` with no `AddHostedService` line, catalogued explicitly so it is not invisible). Re-derived from the tree. The factory-overload registration (no `ImplementationType`) is noted in the descriptor comment so 43-8's reflection sweep knows to special-case it.

7. **`PlatformTaskKind` — 8 members**, pinned against `IPlatformTaskHandlerRegistry.RegisteredTypes`. The registry already throws on duplicate task types at construction (`PlatformTaskHandlerRegistry`'s ctor), so this pin composes with an existing guarantee rather than duplicating one.

8. **`GitSubcommand` — 14 members**, `[Wire]`, each carrying a `read | write` grade (`status/diff/log/show/rev-parse/ls-files/fetch/branch` read; `add/commit/push/checkout/stash/pull` write). It **replaces** `GitOperationsTool.cs:21-25`'s private `HashSet<string> AllowedSubcommands`: the tool's validation at `:78` and its error message at `:81` now read from the enum, and the tool's `Description` (`:30`) is derived rather than restated. The permitted set is unchanged — this is a refactor with a count pin, not a policy change. **`GitOperationsTool` resolving the subcommand into a gate decision is Story 43-4**; this story only replaces the vocabulary.

9. **`ActionDescriptor`** — `sealed record` with `Key`, `Group` (an `ActionGroup` — the enum ships in 43-3; see Dependencies for the seam), `Risk`, `Reversible`, `Title`, `Summary`, `DefaultMinAutonomy` (documented range `[AutonomyDial.Min, AutonomyDial.AlwaysHuman]`, **written as named constants, never literals**), `SiteKey`, `SensitiveActionCode?` (an optional join into `SensitiveActionCatalog.ByCode`), `EscalatableToHuman`.

10. **`ActionCatalog` + `ActionCatalog.Descriptors.cs`** — `ByKey` as a `FrozenDictionary<ActionKey, ActionDescriptor>`; `Get` throwing `ACTION.CATALOG.UNKNOWN_MEMBER`; `TryGet`; `UnclassifiedFallback = AutonomyDial.AlwaysHuman`. `Descriptors.cs` is a `static readonly ActionDescriptor[]` literal, one line per member, **compile-checked by enum reference** (`AgentAction.Deploy.ToWire()`, never `"deploy"`) — the `RolePhaseMap.cs:43-163` posture verbatim. **No source generator** (`ConventionSeedDriftTests.cs:12-20` states codegen is a deliberate repo non-goal).

11. **Fail-loud `BuildIndex`** with distinct error codes, each covered by its own test: duplicate `ActionKey`; an `AgentAction` member with no descriptor; a descriptor for a non-existent `AgentAction`/`DocumentTypeKey`/`ToolAction`/… wire; a `DefaultMinAutonomy` failing `AutonomyDial.IsValidThreshold`; an empty `Title`/`Summary`/`SiteKey`; a duplicate `SiteKey` where uniqueness is required. (`ACTION.CATALOG.GROUP_EMPTY` and the totality/disjointness checks arrive with 43-3.)

12. **Bidirectional keyset-equality drift tests** for the two namespaces keyed on existing vocabularies: `{descriptors with Ns=AgentAction}.Keys == AgentAction wire set` and likewise for `DocumentTypeKey` — set equality, not subset, so both a missing descriptor **and** an orphan descriptor fail. For the three new enums the equivalent test is self-referential and is labelled as such in the test file (per the "vacuous for 55 members" limitation).

13. **Eager touch in both hosts.** `ActionCatalog`'s static init is forced at startup in `Tamma.Api` and `Tamma.ElsaServer` (a single static read in composition), so a bad catalog fails at boot rather than at the first gate call. Both hosts, not one.

14. **Counts re-derived and frozen.** Every count in AC4–AC8 is derived from the tree during implementation and recorded as a pinned constant with the derivation method in a comment. Discrepancies against the design's figures are recorded in the plan's "Corrections to the design", not silently absorbed.

15. **`EveryDefault_IsOverridableOverTheApi`** — a test asserting no `DefaultMinAutonomy` sits outside `IsValidThreshold`, so every shipped default is a value an admin could also write. (Story 39-23 AC2 requires every gating rule be replaceable over the API; a default the API would reject is a rule with no off switch.)

## Dependencies

- **Blocked by 43-1** — `AutonomyDial.Min` / `.AlwaysHuman` must exist; `ActionDescriptor.DefaultMinAutonomy` and `UnclassifiedFallback` reference them by name and must never be literals.
- **Soft-blocked by 43-0** — it deletes `ResolveToolsActivity` (a third dead tool vocabulary) and resolves `GetAcceptanceRulesTool`'s status. Authoring `ToolAction` while four candidate tool vocabularies exist invites the wrong 8.
- **Interlocks with 43-3** — `ActionDescriptor.Group` is typed `ActionGroup`, which 43-3 authors. **Seam:** this story ships `ActionGroup` as a *stub with a single member* (or, preferably, 43-3's enum lands first as a pure declaration and 43-3 does the assignment). The plan picks one and records it as a design decision; the descriptors' `Group` values are 43-3's work either way.
- **Blocks:** 43-3, 43-4, 43-5, 43-6, 43-7, 43-8, 43-9.
- **Existing, verified in place:** `EnumWire` / `WireAttribute`, `WireEnumJsonConverter` (`DocumentJson.cs:58`), `AgentAction` (80), `DocumentTypeKey` (10) + `DocumentTypeRegistry`, `RolePhaseMap`'s projected-index idiom, `IPlatformTaskHandlerRegistry` (8 handlers, duplicate-throwing), `SensitiveActionCatalog`, `TammaError`.

## Out of Scope

- **Group assignment and shipped defaults** — 43-3. This story ships the `DefaultMinAutonomy` *field* and its validation; the ~153 *values* are 43-3's judgment call.
- **Tool-vocabulary reconciliation and the boot validator** — 43-4. This story does not touch the registry-vs-agent-config name disagreement (`file_read` vs `Read`), which is a **privilege expansion**, not a cleanup.
- **`GitOperationsTool` gate resolution** — 43-4. Here the enum only replaces the `HashSet`.
- **Storage, resolution, principal resolution, audit** — 43-5. Core has no database.
- **Any endpoint** — 43-6. (43-1 already created the `/api/actions` group with the dial route.)
- **The `AlwaysEscalate` absorption / `TryPreGate` bridge** — 43-5's evaluator.
- **Enforcement at any seam** — 43-9.
- **A per-server or per-tool MCP vocabulary.** MCP is one coarse member (`effect:mcp.tool.invoke`); server and tool names arrive in the request body and are not enumerable. The hole is recorded, not closed.

## Estimated Effort

5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

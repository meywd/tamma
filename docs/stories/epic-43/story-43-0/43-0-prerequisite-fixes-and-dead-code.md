# Story 43-0: Prerequisite Fixes and Dead Code — the `acceptorRequirement` Reset, a Mistyped Client, and Two Orphan Tool Vocabularies

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

As a **platform/tenant admin editing acceptance rules**,
I want a save from the admin dialog to preserve every field the API models — not silently reset the ones the dialog does not know about —
So that `design`'s shipped `acceptorRequirement = human` survives an unrelated edit, and so that Epic 43's catalog is not layered on top of a surface that already loses policy on write.

Secondarily, as a **developer about to author the action catalog**, I want the two orphan tool vocabularies and the mistyped registry client removed or resolved first, so the catalog is derived from vocabularies that are actually reachable.

## Priority

P1 — **ships standalone, before anything else in Epic 43.** The `acceptorRequirement` reset is a live data-loss bug on a shipped admin surface and is worth landing on its own merits. The dead-code items are prerequisites: Story 43-2 enumerates tool names as a closed vocabulary, and it cannot do that honestly while a third dead tool vocabulary (`ResolveToolsActivity`) and an unreachable `IToolExecutor` (`GetAcceptanceRulesTool`) exist with names that match neither the registry nor each other.

## Architectural Context (READ FIRST)

### (1) The live bug: every admin save resets `acceptorRequirement`

Three files, one silent loss:

- `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx:70-105` — `const body = useMemo<AcceptanceRules>(...)` builds an object literal with exactly **eight** properties: `autonomyLevel`, `maxRevisionRounds`, `maxValidationRepairAttempts`, `ambiguityEscalationThreshold`, `alwaysEscalate`, `reviewerSelection`, `decisionGuidance`, `routingGuidance`. `acceptorRequirement` is absent. There is no spread of `initial`, so nothing carries it through.
- `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts:74-83` — `export interface AcceptanceRules` declares those same eight fields and no `acceptorRequirement`. TypeScript therefore **cannot** flag the omission: the literal is complete with respect to the (wrong) interface. `AcceptanceRulesUpsertRequest = AcceptanceRules` (`:97`), so the PUT body type is the same wrong shape.
- `apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs:23-24` — the DTO's trailing parameter is
  `[property: JsonPropertyName("acceptorRequirement")] AcceptorRequirement AcceptorRequirement = AcceptorRequirement.Any`.
  The default is deliberate and documented ("*trailing + defaulted so a body written before the field existed still binds, to `any` (today's behavior)*") — it exists for **stored** legacy bodies, but it also silently absorbs a live client that forgot the field.

Net effect: a PUT from the dialog binds `AcceptorRequirement.Any`, `ToRules()` (`:28-39`) maps it through, and the row is written with `any`. `AcceptanceDefaults.For` ships `design` with `AcceptorRequirement.Human`; **the first admin save of `design` for any unrelated reason destroys that.** The write path validates (`AcceptanceRulesService`) but validation does not object to `any` — it is a legal value.

### (2) The mistyped conventions registry client

`packages/dashboard/src/services/admin/conventions-api-client.ts:160`:

```ts
getActions: () => fetchJSON<string[]>('/conventions/registry/actions'),
```

The endpoint does **not** return `string[]`. `ConventionStoreEndpoints.RegistryActions()` (`apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionStoreEndpoints.cs:169-179`) projects `RolePhaseMap.EligibleActions` into `RoleActionsResponse(Role, Actions)` — declared at `apps/tamma-elsa/src/Tamma.Api/Dtos/Conventions/ConventionDtos.cs:67` as `sealed record RoleActionsResponse(string Role, IReadOnlyList<string> Actions)`. The wire shape is `[{ role, actions[] }]`, i.e. actions **per role**, not a flat action list.

The client is currently **unreferenced** — the only occurrence of `getActions` anywhere under `packages/dashboard/src` is its own declaration. It is a landmine with a compile-time lie attached: the first consumer that writes `actions.map(a => a.toUpperCase())` type-checks and crashes at runtime. The correct flat list already exists at `RegistryResponse.actions` (`conventions-api-client.ts:80`) via the `/conventions/registry` aggregate.

### (3) `ResolveToolsActivity` — a third dead tool vocabulary

`apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` (226 lines, `[Activity("Tamma.LlmCall", "Resolve Tools", …)]` at `:18-23`, `class ResolveToolsActivity : CodeActivity<List<ResolvedTool>>` at `:24`).

**Zero references outside the file.** The only mention anywhere else in the tree is a doc comment: `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:16` ("*registering it in the set `ResolveToolsActivity` discovers would inject it*"). No workflow instantiates it, no test covers it, nothing DI-registers it.

It is not merely unused — it ships **its own third tool-name vocabulary**, hardcoded in a `switch` in its built-in map, including `"read_file"` at `:188`. That name matches neither of the two live vocabularies:

| Vocabulary | Names | Where |
|---|---|---|
| `IToolExecutor` registry (what actually executes) | `file_read`, `file_write`, `search_code`, `shell_execute`, `run_tests`, `git_operations` | 6 `AddSingleton<IToolExecutor, …>` at `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-763` |
| per-role agent config (what is advertised to the model) | `Read`, `Write`, `Bash`, … | agent configs |
| `ResolveToolsActivity` built-ins (dead) | `read_file`, … | `ResolveToolsActivity.cs:~150-226` |

Leaving it in place means Story 43-2's `ToolAction` enum would be authored beside a fourth candidate list. Deleting it is a strict simplification with no behavioural surface.

### (4) `GetAcceptanceRulesTool` — registered as a factory, resolved by nobody

`apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:27` — `public sealed class GetAcceptanceRulesTool : IToolExecutor`, `ToolName => "get_acceptance_rules"` (`:51`).

**Correction to the design brief:** this is *not* an accidental non-registration. `apps/tamma-elsa/src/Tamma.Api/Program.cs:415-422` documents it explicitly:

> `// … The GetAcceptanceRulesTool itself is NOT registered as an IToolExecutor (Design Decision D6) — the factory mints principal-bound instances per tenant-agent session.`

and registers `AddScoped<GetAcceptanceRulesToolFactory>()` at `:422`. The tool takes `(IAcceptanceRulesResolver, Guid? userId, Guid? tenantId, ILogger?)` — it is principal-bound by construction, so a singleton `IToolExecutor` registration would be *wrong*, not merely different.

The real defect is one layer up: **`GetAcceptanceRulesToolFactory.Create` has no production caller.** The only `Create` call sites are `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesToolParityTests.cs:37,97`. The intended consumer is Story 39-17's tenant-agent session assembly, which has not landed. So today the tool is registered-but-unreachable: no agent can ever call `get_acceptance_rules`.

So "DI-register or delete" is a false dichotomy. The resolution is: **keep, do not singleton-register, and make the gap explicit and testable** — because Story 43-2 catalogues `tool:get_acceptance_rules` and Story 43-4's boot validator will otherwise flag it as a catalogued tool with no registration. Its exemption must be a named, justified, shrink-only allowlist entry from day one, not a silent hole.

### House patterns this story reuses

- Round-trip preservation testing: the acceptance-rules endpoint tests already assert wire→domain→wire fidelity (`apps/tamma-elsa/tests/Tamma.Api.Tests/…/AcceptanceRulesEndpointsTests.cs`).
- Shrink-only justified allowlists with staleness detection: `ContractBindingTests.cs` (`KnownContractViolations` — entries may only be removed; a stale entry fails the build).

## Acceptance Criteria

1. **`acceptorRequirement` survives a dialog save (TS side).** `AcceptanceRules` in `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts:74-83` gains `acceptorRequirement: AcceptorRequirement` (a new exported union type `'any' | 'human'` — verified against `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:254-258`, which declares exactly two members, `[Wire("any")] Any` and `[Wire("human")] Human`), and `RulesEditDialog.tsx`'s `body` memo includes it, seeded from `initial.acceptorRequirement` and listed in the memo's dependency array.

2. **The dialog can edit it, not merely echo it.** `RulesEditDialog.tsx` renders an `acceptorRequirement` control (select/segmented, alongside the existing reviewer-selection controls) with helper text stating what each value means. Echoing-without-editing would fix the data loss but leave a field that only the API can set — a second instance of the same class of trap.

3. **Server-side regression pin.** `AcceptanceRulesEndpointsTests.Upsert_PreservesAcceptorRequirement` — PUT a body with `acceptorRequirement: "human"`, GET it back, assert `human`; and a second case PUTs a body **omitting** the field and asserts the documented legacy default (`any`) still applies, so the DTO's deliberate default is pinned as intentional rather than deleted.

4. **Client-side regression pin.** A dashboard test (`RulesEditDialog` / `AcceptanceRulesAdminPage` suite, Vitest) renders the dialog over a resolved payload carrying `acceptorRequirement: 'human'`, changes an unrelated field (e.g. `maxRevisionRounds`), saves, and asserts the captured `onSave` body contains `acceptorRequirement: 'human'`. This is the test that would have caught the bug.

5. **Field-completeness guard.** One test asserts the TS `AcceptanceRules` field set equals the C# `AcceptanceRulesUpsertRequest` field set — realized as a C# test that reflects the DTO's `JsonPropertyName` values and compares them against a checked-in list which the dashboard test also imports/asserts against, OR (simpler, acceptable) a C# test pinning the DTO's exact wire-property set so that adding a field to the DTO fails until the pin — and the pin's comment names the dashboard client as the second place to update. A future 10th field must not be able to repeat this bug silently.

6. **`getActions` is correctly typed or removed.** `conventions-api-client.ts:160` either (a) returns `RoleActionsResponse[]` — a new exported `interface RoleActions { role: string; actions: string[] }` — with the JSDoc corrected to "actions per role", or (b) is deleted and callers directed to `RegistryResponse.actions`. Given zero current consumers, (a) with the corrected type is preferred (the endpoint is real and useful); the decision is recorded in the plan. Either way, no declaration in the tree claims `/conventions/registry/actions` returns `string[]`.

7. **`ResolveToolsActivity` deleted.** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` is removed. `ResolvedTool` is **not** deleted with it — verified live at `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:287`, consumed by `CallLlmActivity.cs:249-253`, `CallLlmInlineActivity.cs:534-537` and `LlmCallModels.cs:417`. Only the activity file goes. The dangling reference in `GetAcceptanceRulesTool.cs:16`'s doc comment is rewritten. The solution builds and `dotnet test` is green.

8. **`GetAcceptanceRulesTool` resolved as documented, not left ambiguous.** Program.cs's D6 comment (`:415-419`) is extended to state the *current* consequence in one sentence: the factory has no production caller until Story 39-17, so `get_acceptance_rules` is unreachable at runtime today. A test (`GetAcceptanceRulesToolReachabilityTests`) pins the factory's DI registration and asserts the tool is deliberately absent from the `IToolExecutor` registry, referencing this story. The entry that Story 43-4's boot validator will need (`NotDiRegisteredTools`, shrink-only) is specified here in prose so 43-4 does not have to re-derive the justification.

9. **No behaviour change beyond the bug fix.** No route added, no DTO field added or removed, no migration. `dotnet ef migrations has-pending-model-changes` stays clean.

## Dependencies

- **None.** This story is a leaf: it depends on nothing in Epic 43 and nothing in Epic 43 has landed.
- **Feeds:** Story 43-2 (`ToolAction` is authored against exactly two surviving tool vocabularies, not four), Story 43-4 (the tool-vocabulary boot validator inherits the `get_acceptance_rules` justification), and the whole epic's premise that the acceptance-rules admin surface is trustworthy enough to layer on.

## Out of Scope

- **Reconciling the two *live* tool vocabularies** (registry `file_read`/`file_write`/`shell_execute` vs. per-role agent config `Read`/`Write`/`Bash`). That is a **privilege expansion**, not a cleanup — those tools currently cannot execute for roles advertising Claude-Code names — and it is Story 43-4's whole subject with its own boot validator and alias table. This story only deletes the *dead* third one.
- **Wiring `GetAcceptanceRulesToolFactory` into a production call site.** That is Story 39-17's tenant-agent session assembly.
- **Modelling `AcceptorRequirement` in the action catalog.** It remains a second "pin this to a human" concept coexisting with the catalog's thresholds (recorded as a risk in the epic). Folding it in means touching the document-lifecycle acceptance path and is deliberately not attempted.
- **Any `AutonomyDial` work** — Story 43-1, even though it edits three of the same files.

## Estimated Effort

2 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

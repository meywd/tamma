# Story 43-0: Prerequisite Fixes and Dead Code — the `acceptorRequirement` Reset, a Mistyped Client, and Two Orphan Tool Vocabularies

Status: done — implemented 2026-07-29. One deliberate deviation from the plan's **D1**, recorded in full
under "Corrections applied while implementing (2026-07-29)" below: the fix is on BOTH sides, not the client
only — the API no longer invents `acceptorRequirement` for a body that omits it.

> **Read the scope boundary before quoting this story's headline.** The fix covers
> `PUT /api/acceptance-rules/{documentTypeKey}`. A `PUT /api/acceptance-rules/base` can STILL erase the
> shipped human acceptor floor on `design`, `sprint-plan` and `threat-model` — not through defaulting, but
> through 39-5's tier-2 WHOLESALE shadowing, which predates this story and is a recorded follow-up. See
> **"Amendment — 2026-07-29" → A1**.

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

## Corrections applied while implementing (2026-07-29)

*[Amendment 2026-07-29 — the fix is two-sided; D1 is superseded. The original D1 text is kept in
`implementation-plan.md` as the historical record.]*

**D1 said:** fix the TypeScript only, keep the DTO's non-nullable
`AcceptorRequirement AcceptorRequirement = AcceptorRequirement.Any`, and pin that default as intentional —
on the reasoning that the default exists to bind "stored legacy bodies" and removing it would turn a silent
loss into an outage.

**What shipped instead:** both sides.

1. `AcceptanceRulesUpsertRequest.AcceptorRequirement` is now `AcceptorRequirement?`, defaulting to `null` =
   *"the caller did not say"*. `ToRules` takes the currently-effective requirement as a required argument
   (there is no parameterless overload), and `AcceptanceRulesEndpoints.Upsert` resolves it — the override
   row if one exists, else `AcceptanceDefaults.For(type)` — before mapping. An omitted field is therefore
   **preserved**, never invented. An explicitly stated `"any"` still lowers the floor: silence and intent
   are now distinguishable, which is the whole point.

2. The dashboard sends the field (interface + memo + a `<select>` control), as AC1/AC2 required.

**Why D1's reasoning did not survive contact with the code.** The "legacy stored body" safety net is not on
this DTO at all — persistence round-trips the DOMAIN record
(`AcceptanceRulesService.UpsertAsync` → `AcceptanceRulesJson.Serialize`; `Materialize` →
`AcceptanceRulesJson.Deserialize`), and it is
`Tamma.Core.Documents.Policy.AcceptanceRules.AcceptorRequirement { get; init; } = AcceptorRequirement.Any`
that binds a pre-39-13 row. That property is untouched. `AcceptanceRulesUpsertRequest` only ever binds
INBOUND PUT bodies, so making it nullable cannot affect a single stored row — D1's stated cost was not real,
and the story's own framing ("defaulted bodies ARE the bug class") wins.

Two further facts weighed here, both dated after the story was drafted:

- **The repo now has a precedent that says exactly this.** `ActionPolicyEndpoints` (Story 43-6, shipped)
  opens with: *"Every write endpoint takes ONE nullable-required field: a body missing the field is a 400,
  NEVER a defaulted write"* — and names the rule **"the 43-0 bug class"**. A 400 is the right shape for a
  single-field write; for this whole-object PUT the equivalent is preserve-on-absent, which keeps every
  legacy 8-field client working while making silent policy reset impossible.
- **The blast radius was larger than the story said.** The story names `design`. Since 41-1b/41-1c,
  `sprint-plan` and `threat-model` also ship `AcceptorRequirement.Human`
  (`AcceptanceDefaults.For`), so the pre-fix dialog silently stripped the human-acceptance requirement from
  **three** document types, not one.

**Effect on AC3.** Case (a) (stated `human` round-trips) is unchanged. Case (b) is now pinned as
*`Upsert_omitting_acceptorRequirement_on_an_any_type_stays_any`*: for a type whose effective requirement is
`any`, an omitting body still writes `any` — the documented pre-39-13 behavior is preserved for every type
that never had a human floor. What changed is only that omission means "keep what is in force" instead of
the literal constant `any`. The regression pin the bug actually needed is
*`Upsert_omitting_acceptorRequirement_preserves_shipped_human_floor`*.

**AC8 partially deferred (already satisfied elsewhere).** The `Program.cs` D6 comment extension and
`GetAcceptanceRulesToolReachabilityTests` were NOT added: Story 43-4 landed first and already carries the
exemption as a shrink-only, count-pinned, justification-bearing entry —
`ToolCatalogAllowlists.NotDiRegisteredTools` (one entry, `tool:get_acceptance_rules`), pinned by
`ToolCatalogAllowlistTests` and enforced at boot by `ActionCatalogStartupValidator`. That is a strictly
stronger guard than the proposed test, so the "Handoff to 43-4" section is **consumed, not pending**. What
43-0 did add is the pointer: `GetAcceptanceRulesTool`'s class doc now explains why a singleton
`IToolExecutor` registration would be wrong and names the allowlist.

## Amendment — 2026-07-29 (adversarial review of the shipped slice)

### A1. SCOPE BOUNDARY: the fix covers the PER-TYPE route only. The base route can still erase the human floor. (must-read)

**This story's headline is "an admin save no longer resets `acceptorRequirement`".
That is true of `PUT /api/acceptance-rules/{documentTypeKey}`. It is NOT true of
`PUT /api/acceptance-rules/base`,** and the gap is not an omission-handling bug —
preserve-on-absent works correctly on the base route too (it carries the BASE
row's own in-force requirement forward). The gap is **tier-2 wholesale
shadowing**, which is 39-5's D1/D2 resolution semantics and predates this story.

**The mechanism.** `AcceptanceRulesService.ResolveAsync` resolves WHOLESALE:
tier 1 is the per-type override row, tier 2 is the principal BASE override row,
tier 3 is `AcceptanceDefaults.For(type)`. There is no field merge. So the moment
a base override row exists, it shadows tier 3 **entirely** — including the
per-type `AcceptorRequirement.Human` floors that `design`, `sprint-plan` and
`threat-model` ship (and `threat-model`'s `security` reviewer selection).

Consequence, proved: **one** `PUT /api/acceptance-rules/base` whose body omits
`acceptorRequirement` writes a base row carrying the BASE row's in-force value
(`any`), and from then on `design`, `sprint-plan` and `threat-model` all resolve
to `any` — their human floor is gone, without any of them having been written.
Worse, a subsequent OMITTING per-type save then reads that degraded value as
"what is in force" and bakes it into a type row, at which point deleting the base
row no longer restores the floor.

**Not a regression, and not UI-reachable today.** The semantics are 39-5's, not
43-0's; and the admin page renders only the ten per-type rows, so nothing in the
shipped UI issues a base PUT. But it is the same user-visible failure this story
claims to have closed, reachable by anything that speaks HTTP.

**Decision: recorded as a FOLLOW-UP, not fixed here — with the reason.** The
instruction to "apply the same in-force-preservation to the base route if it is
genuinely the same shape" was evaluated and **it is not the same shape.**
Preserve-on-absent carries forward *the value in force for the row being
written*; the base row is ONE row standing in for ten document types with three
different floors, so there is no single value to carry forward that would protect
them. Closing this requires changing what tier 2 MEANS — either merging
`AcceptorRequirement` per-type instead of shadowing it, or making the floor a
`max()` across tiers rather than a wholesale pick. That is a deliberate change to
39-5 D1/D2's wholesale-row contract, affects every field (not just this one), and
belongs in a story that owns the resolution semantics. Doing it inside 43-0 would
change resolution behaviour for every existing stored base row without a story
saying so.

**What a follow-up must decide:** whether tier 2 stays wholesale (and the base
route grows a guard that REFUSES to lower a floor below any shipped per-type
floor), or tier 2 becomes a per-field merge for `AcceptorRequirement`
specifically. Either closes it; they are not equivalent and the choice is a
product one.

### A2. A corrupt stored row is now a 400, not a 500 (fixed)

**This commit introduced a new 500 on a shipped admin surface.** Story 43-0 made
`Upsert` READ before writing (that is how an omitted field is preserved). The
read goes through `AcceptanceRulesService.Materialize` →
`AcceptanceRulesJson.Deserialize`, which throws `TammaError`
`ACCEPTANCE_RULES.INVALID` on an out-of-range body (caught → 400) **or
`JsonException` on malformed JSON — which the endpoint's `catch (TammaError)` did
not cover, so it escaped as a 500.** Because per-type resolution falls through to
the base row, ONE corrupt base row made `PUT` fail for EVERY document type.
Before this commit `Upsert` never read, so overwriting the row WAS the repair.

**Fixed:** the catch is widened to `JsonException` on both `Upsert` and
`GetResolved`, returning `400 ACCEPTANCE_RULES.STORED_ROW_UNREADABLE` whose
message names the fall-through (the corrupt row may be `base` even though the
caller addressed a type) and the repair path.

**The repair path, documented:** `DELETE /api/acceptance-rules/{key}` — DELETE
never reads the body — drops to the next tier, then `PUT` the wanted rules. A PUT
alone can no longer repair a corrupt row, precisely because since 43-0 it must
read the in-force value in order to preserve it. Pinned by
`Upsert_over_a_malformed_stored_row_is_400_naming_the_problem_not_500`,
`One_malformed_BASE_row_makes_every_type_400_not_500`,
`Get_resolved_over_a_malformed_stored_row_is_400_not_500` and
`Delete_then_put_recovers_from_a_malformed_stored_row`.

*Adjacent, NOT changed:* `ListEffective` catches `InvalidOperationException` /
`NpgsqlException` / `DbUpdateException` and degrades to shipped defaults. A
corrupt row still throws past it. Widening that catch was deliberately NOT done —
it would silently serve defaults over a corrupt row, masking the corruption
instead of reporting it, which is the opposite of D3's "a corrupt row throws,
never degrades".

### A3. Comment corrections

- **`RulesEditDialog.test.tsx`** — the whole-body test's comment claimed a future
  tenth field "fails here". It cannot: the expected key list is a hardcoded
  literal, so a tenth field forgotten in the memo leaves BOTH the memo and the
  literal at nine and the assertion still passes. What actually catches it is
  `tsc` (the memo is typed `useMemo<AcceptanceRules>`) plus the C#
  `AcceptanceRulesUpsertRequestFieldSetTests` (reflection over the DTO). Comment
  corrected to say what the test does catch: the memo dropping a field the
  interface still declares — the original 43-0 defect shape.
- **`AcceptanceRulesDtos.cs`** — documented `null` as "the caller did not say"
  but never stated that a client SENDING `null` is treated identically to
  omitting the field. It is: this is a plain `AcceptorRequirement?` reduced with
  `?? current`, not a tri-state, so "clear this field" is not expressible. Now
  stated, with the contrast to Story 44-2's `Optional<T>` tri-state (built in the
  same commit for exactly the cases this member does not need, because an
  always-present enum floor has no cleared state).

### A4. Wiki corrected — `ResolveToolsActivity` was documented as live surface

`wiki.tamma.dev` is live and still described the activity this story DELETED as
existing surface, including Story 42-3 being specified as "extends
`ResolveToolsActivity`". Corrected in both the wiki and its
`apps/wiki-site/public/content/` mirrors: `Epics/Epic-42-Tool-Layer.md`
(4 places), `Architecture.md`, `Workflow-LLM-Call.md`, `Roadmap.md`. Each now
states plainly that Story 43-0 deleted it on 2026-07-29, that **nothing replaced
it** (tool selection is not a workflow activity), and where the work has to land
instead — `IToolExecutorRegistry` and the API-side tool loop
(`InlineToolLoopRunner` / `ParallelToolExecutor`). Epic 42 is backlog, so this is
a re-siting of unstarted work, not a lost implementation. Historical story
documents under `content/stories/` are left as written — they are dated records
of past state, and several already say "deleted by Story 43-0".

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-29 | 1.1.0   | Implemented. D1 superseded — the API side no longer defaults an omitted `acceptorRequirement` (preserve-on-absent); `ResolveToolsActivity` deleted; 43-4's story text reconciled; AC8 recorded as satisfied by 43-4's allowlist. | Claude |
| 2026-07-29 | 1.2.0   | Adversarial-review round (see "Amendment — 2026-07-29"). FIXED: a malformed stored row is now a typed 400 on `Upsert`/`GetResolved` instead of a 500 this commit introduced, with the DELETE-then-PUT repair path documented and tested. RECORDED as follow-up with reasoning: tier-2 wholesale shadowing means a `PUT .../base` omitting `acceptorRequirement` still erases the human floor on `design`/`sprint-plan`/`threat-model` — pre-existing 39-5 D1/D2 semantics, not the same shape as preserve-on-absent, and closing it changes what tier 2 means. Comment corrections in `RulesEditDialog.test.tsx` and `AcceptanceRulesDtos.cs`. Wiki + mirrors corrected: `ResolveToolsActivity` no longer documented as live surface. | Claude |

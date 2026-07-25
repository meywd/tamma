# Implementation Plan — Story 43-0: Prerequisite Fixes and Dead Code

## Scope & Deliverable

When this story is done: an acceptance-rules save from the admin dialog round-trips **every** field the API models — `acceptorRequirement` is in the TS interface, in the PUT body, editable in the dialog, and pinned by a server test and a dashboard test that each fail on the pre-fix code; a field-set pin makes a future 10th DTO field unable to repeat the omission silently; `/conventions/registry/actions` is no longer misdeclared as `string[]` anywhere in the tree; `ResolveToolsActivity.cs` (226 lines, zero callers, a third dead tool vocabulary) is deleted; and `GetAcceptanceRulesTool`'s registered-but-unreachable status is documented at the registration site and pinned by a test, with the justification Story 43-4's boot validator will consume written down here rather than re-derived there.

No migration, no route change, no new DTO field. One behaviour change: saves stop destroying `acceptorRequirement`.

## Pre-Reading

- `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx:70-105` — the eight-field `body` memo; note there is no `...initial` spread and no dependency on `initial.acceptorRequirement`
- `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts:74-83` (`interface AcceptanceRules`), `:97` (`type AcceptanceRulesUpsertRequest = AcceptanceRules`) — why `tsc` cannot see the omission
- `apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs:12-40` — the nine-parameter positional record; `:21-24` the trailing defaulted `AcceptorRequirement` and its "so a body written before the field existed still binds" comment; `:28-39` `ToRules()`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:60-66` (`AcceptorRequirement` property + its "OPTIONAL and additive" doc), `:254-258` (the two-member enum), `:84-100` (`Validate()` — note it does **not** object to `any`, so the loss is invisible to validation)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionStoreEndpoints.cs:160-179` — `RegistryRoles` (really `string[]`) vs `RegistryActions` (really `RoleActionsResponse[]`)
- `apps/tamma-elsa/src/Tamma.Api/Dtos/Conventions/ConventionDtos.cs:61,67` — `RoleActionCell(string Role, string Action)` vs `RoleActionsResponse(string Role, IReadOnlyList<string> Actions)`; three registry endpoints, three different shapes
- `packages/dashboard/src/services/admin/conventions-api-client.ts:78-83` (`RegistryResponse` — where the honest flat `actions: string[]` already lives), `:155-165` (`conventionRegistryApi`)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` — whole file; `:18-23` the `[Activity]` attribute, `:24` the class, `:150-226` the hardcoded built-in tool map incl. `"read_file"` at `:188`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:287` — `ResolvedTool`, **kept** (consumed at `CallLlmActivity.cs:249-253`, `CallLlmInlineActivity.cs:534-537`, `LlmCallModels.cs:417`)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:415-422` — the D6 comment + `AddScoped<GetAcceptanceRulesToolFactory>()`; `:753-765` — the six `AddSingleton<IToolExecutor, …>` registrations and `TryAddSingleton<IToolExecutorRegistry, …>`
- `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:16` (the dangling `ResolveToolsActivity` doc reference), `:27` (`: IToolExecutor`), `:51` (`ToolName => "get_acceptance_rules"`), `:125-152` (the factory)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesToolParityTests.cs:37,97` — the only `Create` call sites in the tree
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the shrink-only justified-allowlist + staleness idiom the 43-4 handoff entry must follow

## Corrections to the design

The design's §11 row 0 is right about the bug and the delete, and wrong in two places:

- **C1 — `conventions-api-client.ts:101` is the wrong line.** `getActions` is at **`:160`**. Line 101 is inside `conventionsApi.get`. The typing claim itself is correct: declared `string[]`, actually `RoleActionsResponse[]` (`{role, actions[]}`).
- **C2 — "`GetAcceptanceRulesTool`: DI-register or delete" is a false dichotomy.** The non-registration is *deliberate and documented* (`Program.cs:415-419`, "Design Decision D6"), and it is *correct*: the tool's constructor is principal-bound (`GetAcceptanceRulesTool.cs:39-49`), so a singleton `IToolExecutor` registration would bind the wrong principal for every caller. The actual defect is one level up — **`GetAcceptanceRulesToolFactory.Create` has zero production call sites** (only `AcceptanceRulesToolParityTests.cs:37,97`), because its consumer (Story 39-17's tenant-agent session assembly) has not landed. So the tool is reachable by no agent today. Resolution: keep, document the runtime consequence at the registration site, pin it with a test, and pre-write the 43-4 validator exemption. See D4.
- **C3 — `ResolvedTool` must survive the delete.** The design says "delete `ResolveToolsActivity.cs`" without qualifying the model type. `ResolvedTool` lives in a *different* file (`LlmCallModels.cs:287`) and has three live consumers. Deleting it would break `CallLlmActivity` and `CallLlmInlineActivity`.

## Design Decisions

- **D1 — Fix the TS interface first, the dialog second; do not "fix" the C# default.** The tempting server-side fix is to make `AcceptorRequirement` non-defaulted so an omitting body 400s. Rejected: the default's stated purpose is binding *stored legacy bodies* (`AcceptanceRulesDtos.cs:21-23`), and removing it turns a silent data loss into a hard failure for rows written before 39-13 — trading one bug for an outage. The defect is entirely on the client: a PUT body type that does not model the resource. Fix the type, and **pin the default as intentional** (AC3's second case) so a later reader does not "clean it up".
- **D2 — Editable, not merely preserved.** Threading `initial.acceptorRequirement` through the memo fixes the loss in ~2 lines. Shipping only that leaves a policy field that exists in the DTO, exists in the domain, ships a non-default value for `design`, and is settable by nobody through the UI — the exact shape of the bug one release later, when someone adds a tenth field and copies the nine-field literal. The dialog gets a control. Cost: one `<select>` beside the reviewer-selection block, ~15 lines.
- **D3 — The field-completeness guard is a C# wire-property pin, not a cross-language codegen check.** AC5 offers two shapes; take the cheap honest one: a NUnit test reflecting `AcceptanceRulesUpsertRequest`'s constructor parameters, reading each `JsonPropertyName`, and asserting the resulting ordered set equals an explicit literal list of nine strings. Adding a DTO field fails it; the failure message names `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts` as the second place to update. A generated-types pipeline would be strictly better and is a repo non-goal (`ConventionSeedDriftTests.cs:12-20` states codegen is deliberately not used). The guard is a tripwire with a pointer, not a proof.
- **D4 — `GetAcceptanceRulesTool` is kept, documented, and pre-exempted.** Per C2. Concretely: (a) extend the `Program.cs:415-419` comment with one sentence naming the missing consumer and the story that supplies it (39-17); (b) add `GetAcceptanceRulesToolReachabilityTests` asserting the factory resolves from DI **and** that `get_acceptance_rules` is absent from the `IToolExecutor` registry's names — so the day someone singleton-registers it, a test explains why not to; (c) write the exemption entry verbatim in this plan (below, "Handoff to 43-4") so 43-4 seeds its shrink-only `NotDiRegisteredTools` list without re-deriving the reasoning. Deleting the tool was considered and rejected: it has a parity test asserting its output matches the API payload byte-for-byte, and 39-17 is a live consumer-in-flight, not a hypothetical.
- **D5 — `getActions` is retyped, not deleted (AC6 option (a)).** The endpoint is real, correct and useful (`RegistryActions()` is the only per-role projection of `RolePhaseMap.EligibleActions` on the wire). Deleting the client would mean the next consumer re-derives the shape from C# — the same mistake with a longer path. Retype to a new exported `RoleActions` interface, fix the JSDoc to "actions per role", and add a one-line comment pointing readers wanting a flat list at `RegistryResponse.actions`. Zero current consumers means zero blast radius either way, so this is a readability call, decided toward keeping the honest client.
- **D6 — Delete `ResolveToolsActivity` in the same commit as the doc-comment fix at `GetAcceptanceRulesTool.cs:16`.** The comment is the file's only external mention; leaving it dangling makes the delete look incomplete in review. Note it is an Elsa `[Activity]`, so the delete removes one entry from the 218-activity attribute-decorated set — harmless (nothing references it by `DefinitionId` or type), but it is the sort of thing an activity-count pin would catch. Search for a count assertion before deleting; if one exists, update it in the same commit and call it out in the PR description.
- **D7 — No `AcceptorRequirement` modelling in the catalog, stated in the story's Out of Scope and repeated here.** `AcceptorRequirement` is a second "pin this to a human" concept that will coexist with the catalog's thresholds. Folding it in touches the document-lifecycle acceptance path and is out of scope for a 2-day prerequisite. Recorded as an epic-level risk, not silently deferred.

## Implementation Steps

1. **MODIFY `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts`** — add `export type AcceptorRequirement = 'any' | 'human';` beside the other wire unions, and `acceptorRequirement: AcceptorRequirement;` to `interface AcceptanceRules` (`:74-83`). `AcceptanceRulesUpsertRequest` (`:97`) inherits it. Expect `tsc` to now fail on `RulesEditDialog.tsx`'s memo — that failure is the proof the interface was the root cause.

2. **MODIFY `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx`** — add `const [acceptorRequirement, setAcceptorRequirement] = useState<AcceptorRequirement>(initial.acceptorRequirement)` beside the existing `useState` block (`:55-65`); add `acceptorRequirement` to the `body` literal (`:78-100`) and to the memo dependency array (`:101-105`); render a labelled `<select>` with the two options and helper text ("`human` — a person must accept this document type regardless of the autonomy level; `any` — the autonomy dial decides"). Place it adjacent to the reviewer-selection controls, not next to the slider (it is an acceptance-identity knob, not a dial knob).

3. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/…/AcceptanceRulesEndpointsTests.Upsert_PreservesAcceptorRequirement`** (append to the existing endpoints test class): case (a) PUT `acceptorRequirement: "human"` → GET → `human`; case (b) PUT a body with the property omitted → GET → `any`, with an inline comment naming `AcceptanceRulesDtos.cs:21-24` and stating the default is deliberate legacy-body binding, not an oversight.

4. **CREATE the dashboard regression test** in the acceptance-rules dashboard suite: render `RulesEditDialog` over a resolved payload with `acceptorRequirement: 'human'`, change `maxRevisionRounds`, click save, assert the captured `onSave` body's `acceptorRequirement === 'human'`. Add a second case asserting the new control changes the submitted value.

5. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/…/AcceptanceRulesUpsertRequestFieldSetTests.cs`** (D3) — reflect the record's primary-constructor parameters, project `JsonPropertyName`, assert set-equality against the nine expected wire names, with the failure message: *"AcceptanceRulesUpsertRequest gained/lost a field. Update `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts` `interface AcceptanceRules` AND `RulesEditDialog.tsx`'s body memo, then update this pin. See Story 43-0."*

6. **MODIFY `packages/dashboard/src/services/admin/conventions-api-client.ts`** (D5) — add `export interface RoleActions { role: string; actions: string[] }`; retype `:160` to `getActions: () => fetchJSON<RoleActions[]>('/conventions/registry/actions')`; fix the JSDoc to `` `GET /api/conventions/registry/actions` — actions per role (NOT a flat list; for that use `getRegistry().actions`) ``.

7. **DELETE `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:16`** (D6) — rewrite the doc comment to describe the factory pattern without naming the deleted type. Before deleting, `grep -rn "ResolveToolsActivity\|Resolve Tools"` across `src/` **and** `tests/` to confirm zero remaining references, and check for an activity-count pin (`grep -rn "218\|ActivityCount\|activities.Count" tests/`) — update it in the same commit if present.

8. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs:415-419`; CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/GetAcceptanceRulesToolReachabilityTests.cs`** (D4) — comment gains: *"NOTE (Story 43-0): the factory has no production call site yet — its consumer is Story 39-17's tenant-agent session assembly — so `get_acceptance_rules` is currently unreachable at runtime. Do not 'fix' this by registering the tool as a singleton `IToolExecutor`: the constructor is principal-bound and a singleton would bind the wrong principal."* Test asserts (i) `GetAcceptanceRulesToolFactory` resolves from a built service provider, (ii) no registered `IToolExecutor` reports `ToolName == "get_acceptance_rules"`.

9. **Verify green** — `pnpm --filter @tamma/dashboard test` + `pnpm lint` + `tsc`; `dotnet build` + `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean.

## Data & Migrations

None. No entity, no column, no EF configuration touched. `dotnet ef migrations has-pending-model-changes` must stay clean — assert it in the PR checklist.

## Events

None emitted or consumed by this story. Note the acceptance-rules write path already emits its own events via `AcceptanceRulesEventsService`; preserving `acceptorRequirement` changes the *payload* of those events (the field stops being wrongly `any`) but adds no new event type.

## Test Plan

NUnit + FluentAssertions server-side; Vitest + Testing Library dashboard-side.

- **`AcceptanceRulesEndpointsTests.Upsert_PreservesAcceptorRequirement`** (server, WebApplicationFactory) — case (a) `human` round-trips; case (b) omitted → `any` (pins the legacy default as intentional). **Covers AC3.** Must fail on pre-fix code only if driven through the dashboard body shape — so case (a) is written against the *literal nine-field JSON* the fixed client sends, and a third case posts the *old eight-field body* and asserts `any`, documenting the exact pre-fix behaviour.
- **`RulesEditDialog` preservation test** (dashboard) — save after an unrelated edit preserves `human`; the new control changes the submitted value. **Covers AC1, AC2, AC4.** This is the test that would have caught the bug; verify it is red against `git stash`ed source before landing.
- **`AcceptanceRulesUpsertRequestFieldSetTests`** (server, reflection) — nine wire names, set-equality, pointer-bearing failure message. **Covers AC5.**
- **`GetAcceptanceRulesToolReachabilityTests`** (server, DI) — factory resolves; `get_acceptance_rules` absent from the executor registry. **Covers AC8.**
- **Build-level** — `tsc` clean after step 1+2 (the interface change is the compile-time forcing function); `dotnet build` clean after step 7 proves zero residual references to the deleted activity. **Covers AC6, AC7.**
- **Regression sweep** — full `dotnet test` and full dashboard suite; specifically confirm `AcceptanceRulesToolParityTests` (the only `Create` consumer) still passes after step 8. **Covers AC9.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `acceptorRequirement` in TS interface + PUT body | 1, 2 | `tsc`; dashboard preservation test |
| 2 — editable in the dialog | 2 | dashboard control-changes-value case |
| 3 — server round-trip pin + legacy-default pin | 3 | `Upsert_PreservesAcceptorRequirement` (3 cases) |
| 4 — client regression pin | 4 | dashboard test red on stashed source, green after |
| 5 — field-completeness guard | 5 | `AcceptanceRulesUpsertRequestFieldSetTests` |
| 6 — `getActions` correctly typed | 6 | `tsc`; reviewer check vs `ConventionDtos.cs:67` |
| 7 — `ResolveToolsActivity` deleted, `ResolvedTool` kept | 7 | `dotnet build`; grep shows zero references |
| 8 — `GetAcceptanceRulesTool` documented + pinned | 8 | `GetAcceptanceRulesToolReachabilityTests`; comment review |
| 9 — no behaviour change beyond the fix | all | `has-pending-model-changes` clean; full suite green |

## Handoff to 43-4

Seed 43-4's shrink-only `NotDiRegisteredTools` allowlist with exactly one entry, justification pre-written:

> `get_acceptance_rules` — `Tamma.Api/Services/AcceptanceRules/GetAcceptanceRulesTool.cs:27`. Deliberately not a singleton `IToolExecutor` (Program.cs D6): the constructor is principal-bound (`userId`/`tenantId`), so a singleton would bind the wrong principal. Minted per tenant-agent session by `GetAcceptanceRulesToolFactory`. **Removable once Story 39-17 supplies the production call site** — at that point the tool becomes reachable and the validator should assert the factory is *consumed*, not that the tool is *registered*.

## Risks & Mitigations

- **The dialog fix is cosmetically small and reviewers may not see why the interface change matters.** Mitigation: land the interface change (step 1) as the first commit so the `tsc` failure on the memo is visible in the diff's history; state in the PR description that the eight-field literal type-checked *because* the interface was wrong.
- **Someone later "cleans up" the trailing DTO default.** Mitigation: AC3 case (b) pins it, and the test comment states the reason (legacy stored bodies) at the assertion.
- **Deleting an `[Activity]` type could break an unseen pin.** Mitigation: step 7's pre-delete grep across `src/` and `tests/`, including a search for activity-count assertions. Low likelihood — the type is referenced by exactly one doc comment.
- **A behaviour-preserving read of "preserve the field" ships without the control (D2 skipped under time pressure).** Mitigation: AC2 is a separate criterion with its own test case, so dropping it is a visible scope cut, not an omission.
- **Two of this story's files (`RulesEditDialog.tsx`, `acceptance-rules-api-client.ts`) are also edited by Story 43-1.** Mitigation: land 43-0 first (it is smaller and independently valuable); 43-1's edits are to the slider constants block (`:20-21,165-176`), disjoint from the `body` memo and the interface's `acceptorRequirement` line. If they land concurrently, expect a trivial merge, not a semantic conflict.

## Dependencies & Sequencing

- **Blocked by:** nothing. This is the epic's only story with no prerequisite.
- **Blocks:** 43-2 (authors `ToolAction` against a tool-vocabulary landscape with the dead third list removed), 43-4 (inherits the `get_acceptance_rules` exemption above). Neither is *hard*-blocked — both could proceed — but doing 43-0 first removes a false candidate vocabulary from 43-2's derivation and a re-derivation from 43-4.
- **Ships standalone.** The `acceptorRequirement` fix is releasable on its own with no Epic 43 context; if the epic slips entirely, this story should still land.
- **Sequencing within the story:** 1 → 2 → 4 (TS chain, single compile loop) in parallel with 3 → 5 → 8 (C# chain); 6 and 7 independent; 9 last.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | TS interface + dialog state, body memo, control, helper text | 0.4 |
| 3 | Server round-trip test (3 cases) | 0.25 |
| 4 | Dashboard regression test (2 cases, red-then-green verification) | 0.35 |
| 5 | Field-set reflection pin | 0.2 |
| 6 | `getActions` retype + `RoleActions` interface + JSDoc | 0.15 |
| 7 | Delete `ResolveToolsActivity`, doc-comment fix, reference sweep | 0.25 |
| 8 | `Program.cs` comment + reachability test | 0.25 |
| 9 | Full-suite verification, PR write-up | 0.15 |
| **Total** | | **2.0** (story estimate: 2 days) |

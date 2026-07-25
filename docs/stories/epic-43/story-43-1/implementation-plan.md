# Implementation Plan — Story 43-1: `AutonomyDial` — One Constant, Published, Drift-Tested

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs` is the sole owner of the validated autonomy range in the entire tree: the one production check (`AcceptanceRules.cs:85-86`) calls `AutonomyDial.IsValidLevel` and interpolates its message; `GET /api/actions/dial` publishes `{min, max, alwaysHuman, default, current, source}`; `RulesEditDialog.tsx`'s two constants are **deleted** and the slider bounds plus helper text bind to the payload; the corrupt-row test vector that silently defuses on a downward widen is re-vectored to `Max + 1000`; both silent coverage loops derive from `ValidLevels()`; the four hardcoded bounds `[TestCase]`s become a derived `[TestCaseSource]`; the shipped `RoutingGuidance` prose that reaches an agent stops asserting "autonomy 70"; a comparison-shaped drift test with a shrink-only allowlist fails any future restatement; and 13 documentation files stop presenting `70–100` as permanent.

**After this story, `AutonomyDial.Min = 50` is a one-line diff** — no test edit, no UI edit, no doc edit, no migration.

## Pre-Reading

- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:29-32` (the XML doc restating the bound), `:84-100` (`Validate()`; note `:85-86` rejects, never clamps), `:254-258` (`AcceptorRequirement` — the adjacent enum, untouched here)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:28-52` — `DefaultAutonomyLevel = 70` at `:31` (**stays a literal**), `DefaultRoutingGuidance` at `:48-52` (**rewritten**), and the static-ctor validation posture noted in the class doc
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRulesJson.cs` — `Materialize`, the read-side `Validate()` call that makes a widened bound take effect on reads too
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs` — where `TammaError` becomes a 400; the pattern the new dial endpoint's error posture mirrors (it has none — it is a pure read)
- `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx:20-21` (the constants), `:160-177` (the slider label, `min`/`max` at `:168-169`, helper text at `:175`)
- `packages/dashboard/src/pages/admin/acceptance-rules/__tests__/AcceptanceRulesAdminPage.test.tsx:114-122` — `it('constrains the autonomy dial to 70–100')`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceRulesModelTests.cs:22-28` — the four bounds `[TestCase]`s
- `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesServiceTests.cs:103-117` — `ResolveAsync_throws_on_corrupt_rules_json`, `AutonomyLevel = 5` at `:110`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceContractTests.cs:90-105` — `for (var level = 70; level <= 100; level++)` at `:98`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceGuardrailsTests.cs:180-195` — `rng.Next(70, 101)` at `:186`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs:23,29,96` — the default pins and the **content-blind** `RoutingGuidance.Should().NotBeNullOrWhiteSpace()`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the shrink-only allowlist + staleness ratchet the drift test copies
- `docs/stories/epic-43/README.md` §"The dial becomes one constant", and its decision **D3** (model carries no lower bound; `[70,100]` stays one named constant)

## Corrections to the design

- **C1 — `GetAcceptanceRulesTool.cs:53-56` does not restate the level.** design.md §10 row 4 lists it alongside `AcceptanceDefaults.cs:48-52` and `AcceptanceRules.cs:30` as carrying the same "autonomy 70" wording. Verified: the tool's `Description` (`:53-56`) names no number at all. Only **two** shipped prose sites restate it — `AcceptanceRules.cs:30` (XML doc, developer-facing) and `AcceptanceDefaults.cs:48-52` (`DefaultRoutingGuidance`, **model-facing**). Step 6 edits two sites, not three.
- **C2 — `RulesEditDialog`'s helper text is a *third* restatement, not a derivation.** design.md §10 treats `:174-176` as "helper text" to be bound. In the code it is the string literal `70 = supervised baseline · 100 = full auto` (`:175`) — it does not reference `MIN_AUTONOMY`/`MAX_AUTONOMY` at all. Deleting the constants therefore does **not** make the helper text follow; it must be separately rewritten to interpolate the payload. Missing this ships a UI that says "70 = supervised baseline" under a slider that starts at 50.
- **C3 — the `AcceptanceRulesAdminPage.test.tsx` assertion is at `:115-122`, inside a test declared at `:114`** (`it('constrains the autonomy dial to 70–100', …)`). The **test name** also restates the bound and must be renamed, not only the assertion body.
- **C4 — confirmed absences (design asserts these; verified true).** No DataAnnotations `[Range]` on any autonomy property in `apps/tamma-elsa/src/`; no CHECK constraint (no migration under `Tamma.Data/Migrations/` mentions autonomy — the rules persist as JSON in `acceptance_rules_overrides.RulesJson`, so SQL never sees the integer); no TypeScript range validation (`acceptance-rules-api-client.ts` contains neither `70` nor `100`). These absences are a **design invariant to preserve**, not merely a fact: AC12 forbids 43-2/43-3/43-5/43-6 from adding any of them.

## Design Decisions

- **D1 — `AutonomyDial` lives in `Tamma.Core/Documents/Policy/`, beside `AcceptanceRules`, not in `Tamma.Core/Actions/`.** Its first and today only consumer is the acceptance-rules domain; `Tamma.Core/Actions/` does not exist until 43-2, and creating an empty namespace to host a constant would make this story depend on a story that depends on it. Core is reachable from all four other assemblies (zero `ProjectReference`s), so the placement costs nothing in reach. 43-2's `ActionDescriptor` references it across namespaces with a `using`.
- **D2 — `AlwaysHuman = Max + 1`, derived, and the asymmetry is stated in the XML doc.** `AlwaysHuman` is a *legal threshold value* meaning "a person decides at every level in the validated range" — not a nullable, not a magic number. Deriving it from `Max` means "widening is one line" holds **downward only**: raising `Max` to 120 would silently reinterpret every stored `101` as an ordinary threshold. The epic's requirement is only ever about lowering the floor, so this is not the asked case — but the doc comment says so explicitly rather than implying unconditional safety. `IsValidThreshold` therefore accepts `[Min,Max] ∪ {AlwaysHuman}` and rejects `Max + 2`, which is the assertion that makes the sentinel a closed set rather than an open tail.
- **D3 — the model carries no lower bound below `Min`** (epic D3, binding). `AutonomyDial` exposes no `AbsoluteMin`, no `[0,100]` concept, no "widened range" flag. `[70,100]` is one named constant pair; widening is editing `Min`. A second "the model allows 0 but this deployment validates 70" layer was considered and rejected: it re-creates the two-places problem inside the constant that exists to eliminate it, and it would make `ValidLevels()` ambiguous (which range does the UI render?).
- **D4 — `DefaultAutonomyLevel` is NOT derived from `Min`.** `AcceptanceDefaults.cs:31` keeps `= 70`. Writing `= AutonomyDial.Min` is superficially DRY and semantically wrong: it would make widening the range silently move every deployment's shipped default to the new floor — a behaviour change smuggled inside a "one-line" edit. Range and default are different concerns. `AcceptanceDefaultsDriftTests.cs:96` already pins the default against the named constant, so the coupling is unnecessary as well as harmful.
- **D5 — the drift guard is comparison-shaped, C#-only, and shrink-only-allowlisted.** A bare-literal scan for `70`/`100`/`101` is rejected on evidence: Story 43-3 ships `ActionCatalog.Descriptors.cs` with ~153 `DefaultMinAutonomy` values, so a literal scan either fails the build permanently or is allowlisted into uselessness — the guard would collide with the very catalog it exists to protect. The regexes key on *bound-check syntax* (`Autonomy\w*\s*(is\s+)?[<>=!]+\s*(70|100|101)` and its mirror, plus `AutonomyLevel is [<>]`), which has near-zero false-positive rate. **Not extended to `.ts`/`.tsx`**: after step 5 there is nothing to scan (the constants are deleted and the values arrive over the wire), and the dashboard test asserting against the mocked payload is the TS-side guard. The rationale is written **into the test file**, because the next person's instinct will be to "strengthen" it into a literal scan.
- **D6 — `GET /api/actions/dial` ships here, creating Epic 43's route group early.** It is one read-only endpoint with no new permission (any caller who can read acceptance rules can read the dial; the values are constants plus that caller's own resolved level). Deferring it to 43-6 would mean the UI cannot unhardcode until 43-6 — leaving `MIN_AUTONOMY = 70` alive across five stories, which is exactly the drift this story exists to end. Route-ordering discipline for the group (literals before parameterized) is established here so 43-6 inherits it rather than retrofitting it. `current`/`source` come from the caller's resolved **base** acceptance rules (the `base` document-type key), not per-type — the dial is one number per principal.
- **D7 — the UI fetches the dial once at the admin-page level and passes it into the dialog as a prop.** Not a fetch inside `RulesEditDialog` (which would fire per row-open) and not a global singleton (which would be untestable). The page already loads resolved rules; the dial rides that load. **Fallback posture:** if the dial payload is absent, the dialog renders the slider **disabled** with a "loading policy bounds" note rather than defaulting to `[0,100]` or `[70,100]`. Rendering an unbounded slider is worse than rendering none — it produces a body the server 400s, and defaulting to a literal re-introduces the constant this story deletes.
- **D8 — the shipped `RoutingGuidance` rewrite happens NOW, not when the range widens.** This prose is an input to a model that makes acceptance decisions, and `AcceptanceDefaultsDriftTests.cs:29` pins it **non-empty only** — the drift test cannot catch a semantic lie. A widened deployment would ship an agent instruction referencing a baseline that no longer exists. Rewriting it later would also mean editing a model-facing prompt as part of a "one-line" change, which contradicts the story's whole claim. A new content assertion (no bare dial literal in the shipped guidance) makes the property enforced rather than merely fixed once.
- **D9 — the corrupt-row vector becomes `AutonomyDial.Max + 1000`, not `Min - 1` or `AlwaysHuman + 1`.** `Min - 1` defuses on exactly the widen this story anticipates. `AlwaysHuman + 1` (=102) is only 2 away from a legal value and could plausibly become meaningful if the sentinel design changes. `Max + 1000` (=1100) is invalid under any downward widen and under any plausible sentinel scheme, and it reads unmistakably as "deliberately absurd" to the next maintainer. The inline comment carries the lesson — the value alone does not.
- **D10 — the two silent coverage loops are converted, not deleted or parameterized further.** `AcceptanceContractTests.cs:98` becomes `foreach (var level in AutonomyDial.ValidLevels())` and `AcceptanceGuardrailsTests.cs:186` becomes `rng.Next(AutonomyDial.Min, AutonomyDial.Max + 1)`. Note the second is inside a randomized termination trial: widening to `[0,100]` roughly triples its state space, which is desirable (it is the *point*) but will lengthen the run. If the trial count is tuned to wall-clock, note it in the PR; do not reduce coverage to compensate.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs`** (AC1, D1/D2/D3):

   ```csharp
   /// THE validated autonomy dial range. Widening downward is a ONE-LINE change here
   /// (Min); nothing else in production C#, SQL or TypeScript may restate a bound.
   /// NOTE: AlwaysHuman is derived from Max, so the one-line claim holds DOWNWARD only —
   /// raising Max would reinterpret every stored 101 as an ordinary threshold.
   /// Pinned by AutonomyDialSingleSourceTests.
   public static class AutonomyDial
   {
       public const int Min = 70;
       public const int Max = 100;
       public const int AlwaysHuman = Max + 1;
       public static bool IsValidLevel(int l)     => l >= Min && l <= Max;
       public static bool IsValidThreshold(int t) => (t >= Min && t <= Max) || t == AlwaysHuman;
       public static IEnumerable<int> ValidLevels() => Enumerable.Range(Min, Max - Min + 1);
   }
   ```

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs`** (AC2) — `:85-86` becomes `if (!AutonomyDial.IsValidLevel(AutonomyLevel)) throw Invalid(nameof(AutonomyLevel), $"AutonomyLevel must be within [{AutonomyDial.Min}, {AutonomyDial.Max}]; got {AutonomyLevel}.");`. Rewrite `:30`'s XML doc: *"How much the orchestrator decides itself — higher means the orchestrator decides more by itself. Validated against `AutonomyDial`."*

3. **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceRulesModelTests.cs:22-28`** (AC8) — replace the four `[TestCase]`s with `[TestCaseSource(nameof(AutonomyBoundsCases))]` yielding `(AutonomyDial.Min - 1, false)`, `(Min, true)`, `(Max, true)`, `(Max + 1, false)`; rename the method to `AutonomyLevel_is_bounded_by_AutonomyDial`.

4. **MODIFY `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesServiceTests.cs:110`** (AC6, D9) — `AutonomyLevel = 5` → `AutonomyLevel = AutonomyDial.Max + 1000`, with:
   `// Deliberately absurd: invalid under ANY downward widen of AutonomyDial.Min. The`
   `// previous vector (5) becomes LEGAL at [0,100] — this test would have kept passing`
   `// while testing nothing. See Story 43-1.`
   **MODIFY `AcceptanceContractTests.cs:98`** → `foreach (var level in AutonomyDial.ValidLevels())`. **MODIFY `AcceptanceGuardrailsTests.cs:186`** → `rng.Next(AutonomyDial.Min, AutonomyDial.Max + 1)` (AC7, D10).

5. **CREATE `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AutonomyDialSingleSourceTests.cs`** (AC10, D5) — the three comparison regexes over `apps/tamma-elsa/src/**/*.cs`, `AutonomyDial.cs` excluded, shrink-only allowlist (`KnownAutonomyBoundRestatements`, empty at landing) with staleness detection copied from `ContractBindingTests`; plus `AlwaysHuman_is_strictly_above_Max`, `Min_is_less_than_Max`, `IsValidThreshold_accepts_AlwaysHuman_and_rejects_Max_plus_2`, `ValidLevels_spans_Min_to_Max_inclusive`. Header comment carries D5's rationale verbatim (why not a literal scan; why not `.ts`).

6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:48-52`** (AC9, D8) — rewrite `DefaultRoutingGuidance` to name no number: *"At the deployment's baseline autonomy level, assign nearly every acceptance decision to a human role. As the autonomy level rises, decide more routine, unambiguous, fully-approved documents yourself and assign only the contested or high-impact ones. Always assign — never reject or hard-accept — anything you are not confident the rules unambiguously permit."* `:31` `DefaultAutonomyLevel = 70` is **left alone** (D4). **MODIFY `AcceptanceDefaultsDriftTests.cs`** — keep `:29`'s non-empty pin, add `RoutingGuidance_names_no_dial_literal` (regex `\b(70|100|101)\b` over the shipped string).

7. **CREATE the dial endpoint** (AC3, D6) — a `DialResponse(int Min, int Max, int AlwaysHuman, int Default, int Current, string Source)` DTO under `apps/tamma-elsa/src/Tamma.Api/Dtos/Actions/`; a handler resolving the caller's base acceptance rules for `Current`/`Source`; `MapGet("/api/actions/dial", …)` registered in a new `/api/actions` route group in `apps/tamma-elsa/src/Tamma.Api/Program.cs`, with a comment establishing the literal-before-parameterized ordering rule for 43-6. Add `DialEndpoint_ReportsAutonomyDialConstants` to the API tests.

8. **MODIFY `packages/dashboard/src/services/admin/`** — add a dial api-client function + response type (`{ min, max, alwaysHuman, default, current, source }`) and the hook/loader the admin page uses.

9. **MODIFY `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx`** (AC4, D7, C2) — **delete** `:20-21`; add a `dial` prop; slider `min={dial.min} max={dial.max}`; **rewrite the helper text at `:175`** to `{dial.min} = supervised baseline · {dial.max} = full auto`; disabled-slider fallback when `dial` is absent. **MODIFY the admin page** to fetch the dial alongside resolved rules and pass it down.

10. **MODIFY `packages/dashboard/src/pages/admin/acceptance-rules/__tests__/AcceptanceRulesAdminPage.test.tsx:114-122`** (AC5, C3) — rename the test off `70–100` (e.g. `constrains the autonomy dial to the published range`), mock the dial endpoint, assert `slider.min === String(mockDial.min)` / `slider.max === String(mockDial.max)`; add a case with a **different** mocked `min` (e.g. `50`) asserting the slider follows — the assertion that proves the binding is real rather than coincidental.

11. **Documentation pass, 13 files** (AC11) — `wiki/Document-Lifecycle.md:125,148,155,165`, `wiki/Architecture.md:191`, `wiki/Workflow-Document-Lifecycle.md:67,90`, `wiki/Home.md:84`, `wiki/Roadmap.md:128,140`, `wiki/Stories.md:622,712`, `wiki/Epics/Epic-39-Document-Lifecycle.md:28,65`, `wiki/Epics/Epic-41-Full-Team-Workflows.md:12,22,23,32`, `wiki/Epics/Epic-42-Tool-Layer.md:33,46,60,68,100,107`, and `packages/dashboard/src/pages/admin/acceptance-rules/AcceptanceRulesAdminPage.tsx:6,33` prose. `:155`'s "Validated 70–100 — it is a dial, not a mode" → "validated within the range published by `AutonomyDial` / `GET /api/actions/dial` — it is a dial, not a mode". **DO NOT TOUCH `docs/PRD.md:14,16,30,165`** (the 70% completion-rate KPI).

12. **Verify** — `dotnet build` + `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean; `pnpm --filter @tamma/dashboard test` + `tsc` + `pnpm lint`. **Then the proof:** on a scratch branch set `AutonomyDial.Min = 50`, re-run the full C# and dashboard suites, and confirm **zero** failures and zero required edits. Revert. Record the result in the PR description — this is the story's actual acceptance evidence.

## Data & Migrations

None. The bound has never had a DB representation: acceptance rules persist as JSON in `acceptance_rules_overrides.RulesJson`, so SQL never sees the integer, and no migration mentions autonomy (verified). **AC12 forbids adding one** — a CHECK constraint would live in a migration snapshot and become a permanent second place that no `AutonomyDial` edit can reach. `dotnet ef migrations has-pending-model-changes` must stay clean.

## Events

None emitted or consumed. The dial endpoint is a pure read. Note that rewriting `DefaultRoutingGuidance` (step 6) changes the *content* of prompts sent to acceptance-deciding agents; it adds no event but is a model-facing behaviour change and should be called out in the PR description.

## Test Plan

NUnit + FluentAssertions server-side; Vitest + Testing Library dashboard-side.

- **`AutonomyDialSingleSourceTests`** (Core, new) — `No_autonomy_comparison_restates_a_bound` (three comparison regexes over `src/**/*.cs`, `AutonomyDial.cs` excluded, shrink-only allowlist with staleness); `AlwaysHuman_is_strictly_above_Max`; `Min_is_less_than_Max`; `IsValidThreshold_accepts_AlwaysHuman_and_rejects_Max_plus_2`; `ValidLevels_spans_Min_to_Max_inclusive`. **Covers AC10, AC1.** Verify red: temporarily add `if (x is < 70 or > 100)` to any src file and confirm the scan names it.
- **`AcceptanceRulesModelTests.AutonomyLevel_is_bounded_by_AutonomyDial`** (Core, converted) — derived `[TestCaseSource]`. **Covers AC8, AC2.**
- **`AcceptanceRulesServiceTests.ResolveAsync_throws_on_corrupt_rules_json`** (Api, re-vectored) — `Max + 1000`. **Covers AC6.** Its correctness is proven by step 12's `Min = 50` run: the pre-fix vector would pass-while-testing-nothing there; the new one still throws.
- **`AcceptanceContractTests` / `AcceptanceGuardrailsTests`** (Core, converted loops) — unchanged assertions, derived bounds. **Covers AC7.** Under step 12's `Min = 50` run these must exercise 51 levels rather than 31, with no failure; note the guardrails trial's runtime.
- **`AcceptanceDefaultsDriftTests.RoutingGuidance_names_no_dial_literal`** (Core, new) — regex `\b(70|100|101)\b` over the shipped guidance string. **Covers AC9.** The existing non-empty pin at `:29` stays.
- **`DialEndpoint_ReportsAutonomyDialConstants`** (Api, new) — payload `min`/`max`/`alwaysHuman` equal the constants; `default` equals `AcceptanceDefaults.DefaultAutonomyLevel`; `current`/`source` reflect the caller's resolved base rules. **Covers AC3.**
- **`AcceptanceRulesAdminPage` dial tests** (dashboard, converted + new) — renamed bounded-slider test asserting the mocked payload; the `min: 50` variant proving the binding is live; a fallback case asserting the slider is disabled (not unbounded, not `[70,100]`) when the dial payload is absent. **Covers AC4, AC5, D7.**
- **The widen rehearsal (step 12)** — `Min = 50`, full suites, zero failures. **Covers AC1's "one-line" claim end to end**; this is the only test of the story's actual thesis and its result belongs in the PR description.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `AutonomyDial` exists, sole owner, no lower-bound concept | 1 | `AutonomyDialSingleSourceTests` invariants; reviewer check vs epic D3 |
| 2 — the one production edit, interpolated message | 2 | `AcceptanceRulesModelTests`; message asserted in the `Max+1` case |
| 3 — `GET /api/actions/dial` | 7 | `DialEndpoint_ReportsAutonomyDialConstants` |
| 4 — UI binds; constants deleted; helper text rewritten | 8, 9 | dashboard `min: 50` variant; grep shows no `MIN_AUTONOMY` |
| 5 — admin-page test asserts the payload | 10 | renamed test + variant case |
| 6 — corrupt-row vector re-vectored, with the comment | 4 | `AcceptanceRulesServiceTests`; reviewer checks the comment is present |
| 7 — both coverage loops derived | 4 | step 12's `Min = 50` run exercises 51 levels |
| 8 — bounds cases derived | 3 | `[TestCaseSource]` |
| 9 — shipped agent-facing prose names no number | 6 | `RoutingGuidance_names_no_dial_literal` |
| 10 — comparison-shaped drift guard | 5 | red-then-green verification with an injected restatement |
| 11 — 13-file doc pass, PRD untouched | 11 | reviewer diff check; `git diff --stat docs/PRD.md` empty |
| 12 — no second bound introduced | all | grep for `[Range]` / CHECK / TS literals; reviewer checklist inherited by 43-2/3/5/6 |

## Risks & Mitigations

- **The helper text is missed (C2).** Deleting `MIN_AUTONOMY`/`MAX_AUTONOMY` does not touch `:175`'s independent literal, so a widened deployment shows "70 = supervised baseline" under a slider starting at 50. Mitigation: C2 is called out in the plan, AC4 names the helper text explicitly, and the dashboard `min: 50` variant should assert the helper text too.
- **Someone later "strengthens" the drift test into a bare-literal scan**, which then collides head-on with 43-3's ~153 `DefaultMinAutonomy` literals. Mitigation: D5's rationale is written into the test file header, not only into this plan.
- **The `Min = 50` rehearsal is skipped under time pressure.** It is the only end-to-end test of the story's thesis; everything else tests parts. Mitigation: it is step 12 and its result is required in the PR description, so its absence is visible in review.
- **Widening triples the guardrails randomized trial's state space** (D10). Mitigation: expected and desirable; note the runtime in the PR rather than trimming trials. If the suite's wall-clock budget is a real constraint, raise it as a separate concern — do not solve it by narrowing the sampled range back to a literal.
- **`AlwaysHuman = Max + 1` makes "one line" true downward only** (D2). Mitigation: stated in the XML doc and in the story; the epic's requirement is exclusively about lowering. Not mitigated further — a non-derived sentinel would be a second magic number.
- **Two files collide with Story 43-0** (`RulesEditDialog.tsx`, the acceptance-rules client). Mitigation: land 43-0 first; the regions are disjoint (43-0 touches the `body` memo and the interface, 43-1 touches the constants block, the slider, and a new dial hook).
- **The dial endpoint ships before its route group has an owner** (D6). `/api/actions` is created here with one route and filled by 43-6. Mitigation: the group's ordering rule (literals before parameterized) is established in a comment at creation, and 43-6's plan is told to extend rather than re-create the group.

## Blocks / Blocked by

- **Blocked by:** nothing. Ships standalone.
- **Soft-ordered after:** 43-0 (shared files, disjoint regions).
- **Blocks:** 43-2 (`ActionDescriptor.DefaultMinAutonomy` documented as `[AutonomyDial.Min, AutonomyDial.AlwaysHuman]`; `ActionCatalog.UnclassifiedFallback = AutonomyDial.AlwaysHuman`), 43-3 (all ~153 defaults written as `AutonomyDial.Min` / `AutonomyDial.AlwaysHuman`, never literals), 43-5 (threshold validation calls `IsValidThreshold`), 43-6 (the API rejects out-of-range thresholds through the same helper), 43-7 (the level selector renders `ValidLevels()` from the published payload).
- **Must land before** any implementation of Story 39-23's `EscalationClass.minAutonomyLevel` or Story 42-1's `ToolDescriptor.AutonomyFloor` — each would re-hardcode the bound. Both are dropped by Epic 43 (spec edits are 43-10's scope), so the duplication should never occur; this ordering is the safety net if 43-10 slips.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | `AutonomyDial` + the one production edit + XML doc | 0.2 |
| 3–4 | Test conversions: bounds cases, corrupt-row vector, two coverage loops | 0.3 |
| 5 | `AutonomyDialSingleSourceTests` (regexes, allowlist, staleness, red-verify) | 0.4 |
| 6 | `RoutingGuidance` rewrite + content pin | 0.2 |
| 7 | Dial DTO, handler, `/api/actions` group, endpoint test | 0.3 |
| 8–9 | Dashboard client/hook, dialog unhardcode, helper text, fallback | 0.3 |
| 10 | Admin-page tests (rename, payload-sourced, `min: 50` variant, fallback) | 0.2 |
| 11 | 13-file documentation pass | 0.2 |
| 12 | Full verification + the `Min = 50` rehearsal + PR write-up | 0.15 |
| **Total** | | **2.05** (story estimate: 2 days) |

# Story 43-1: `AutonomyDial` — One Constant, Published Over the Wire, Drift-Tested

Status: in-progress — the AutonomyDial constant + invariants shipped (PR #506); the 8 hardcode-site rewires, GET /api/actions/dial, dashboard unhardcode, and the doc pass remain

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform operator who will one day want a lower autonomy floor** (the epic's product requirement is literally "*even those automated at 70 should be listed and greyed out so a future lower automation is doable*"),
I want the validated autonomy range to exist in exactly one place in C#, to cross the wire so the UI binds instead of restating it, and to be guarded by a test that fails when anyone restates it,
So that widening `[70,100]` to `[50,100]` or `[0,100]` is a one-line edit with no test edit, no UI edit, no doc edit and no migration — and so that Epic 43's ~153-member catalog is not built on top of a bound that is hardcoded in eight more places.

## Priority

P0 — **ships standalone and is valuable even if the rest of Epic 43 slips.** It is also load-bearing for ordering: **two unlanded specs would each re-hardcode the bound** — Story 39-23's `EscalationClass.minAutonomyLevel` and Story 42-1's `ToolDescriptor.AutonomyFloor`. Under Epic 43's design both die, so the duplication never occurs; if either lands first the bound is in three more places. Story 43-2's `ActionDescriptor.DefaultMinAutonomy` and 43-3's ~153 default literals both reference `AutonomyDial.Min` / `AutonomyDial.AlwaysHuman` by name, so this constant must exist before they are authored.

## Architectural Context (READ FIRST)

### The one production enforcement — and what it does *not* do

`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:85-86`:

```csharp
if (AutonomyLevel is < 70 or > 100)
    throw Invalid(nameof(AutonomyLevel), $"AutonomyLevel must be within [70, 100]; got {AutonomyLevel}.");
```

Three properties matter:

1. **It REJECTS, it never clamps.** An out-of-range value is a `TammaError` (`ACCEPTANCE_RULES.INVALID`), surfaced as a 400 by `AcceptanceRulesEndpoints.cs`'s catch. There is no silent coercion anywhere to preserve.
2. **It runs on both write AND read.** `Validate()` is called on upsert (`AcceptanceRulesService`) and defensively on resolve (via `AcceptanceRulesJson` → `Materialize`). So a widened bound takes effect on every path at once — and a *narrowed* one would start rejecting stored rows on read.
3. **The message restates the bound as a literal.** Interpolating `AutonomyDial.Min`/`.Max` is not cosmetic: a stale message on a widened deployment is a support ticket.

**Verified: there is no second enforcement.**

- **No DataAnnotations `[Range]`.** The 400 comes solely from the domain `Validate()`. A grep for `Range(` against autonomy in `apps/tamma-elsa/src/` returns nothing (one binary false positive in `Tamma.Core/Documents/Types/TestSpec.cs`).
- **No DB CHECK constraint.** No migration under `apps/tamma-elsa/src/Tamma.Data/Migrations/` mentions autonomy at all. The rules are persisted as JSON in `acceptance_rules_overrides.RulesJson` — SQL never sees the integer.
- **No TypeScript validation.** `packages/dashboard/src/services/admin/acceptance-rules-api-client.ts` contains no `70` and no `100`; the client sends whatever the slider produces and relies on the slider's `min`/`max` attributes plus the server 400.

That is the good news: the bound has exactly **one** production owner and it must not gain a second. **Story 43-2/43-3 must not add a `[Range]`, a CHECK, or a TS validator** — those would each become a permanent second place.

### Everywhere the bound is currently restated

| # | Site | Nature |
|---|---|---|
| 1 | `Tamma.Core/Documents/Policy/AcceptanceRules.cs:85-86` | **the** production enforcement (literal comparison + literal message) |
| 2 | `Tamma.Core/Documents/Policy/AcceptanceRules.cs:30` | XML doc: "*(70 = supervised baseline, 100 = full auto). Validated 70–100.*" |
| 3 | `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx:20-21` | `const MIN_AUTONOMY = 70; const MAX_AUTONOMY = 100;` |
| 4 | `RulesEditDialog.tsx:168-169` | slider `min={MIN_AUTONOMY} max={MAX_AUTONOMY}` |
| 5 | `RulesEditDialog.tsx:175` | helper text: `70 = supervised baseline · 100 = full auto` — a **third** independent restatement, not derived from the constants above it |
| 6 | `packages/dashboard/src/pages/admin/acceptance-rules/__tests__/AcceptanceRulesAdminPage.test.tsx:115-122` | `it('constrains the autonomy dial to 70–100')` → `expect(slider.min).toBe('70')` |
| 7 | `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceRulesModelTests.cs:23-28` | `[TestCase(69,false)] [TestCase(70,true)] [TestCase(100,true)] [TestCase(101,false)]` on `AutonomyLevel_is_bounded_70_to_100` |
| 8 | `tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesServiceTests.cs:104-117` | **the dangerous one** — `ResolveAsync_throws_on_corrupt_rules_json` uses `AutonomyLevel = 5` as its corrupt-row vector |
| 9 | `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceContractTests.cs:98` | `for (var level = 70; level <= 100; level++)` — a **silent coverage loop** |
| 10 | `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceGuardrailsTests.cs:186` | `AutonomyLevel = rng.Next(70, 101)` — the second silent coverage loop |
| 11 | `Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:48-52` | shipped `DefaultRoutingGuidance` prose: "*At the supervised baseline (autonomy 70) assign nearly every acceptance decision to a human role…*" |
| 12 | `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs:23,96` | `r.AutonomyLevel.Should().Be(70)` / `Be(AcceptanceDefaults.DefaultAutonomyLevel)` — **default** pins, not range pins |
| 13 | 13 documentation files | `wiki/Document-Lifecycle.md`, `wiki/Architecture.md`, `wiki/Workflow-Document-Lifecycle.md`, `wiki/Home.md`, `wiki/Roadmap.md`, `wiki/Stories.md`, `wiki/Epics/Epic-39…`, `Epic-41…`, `Epic-42…`, `AcceptanceRulesAdminPage.tsx` prose, + the epic-39/41/42 story docs |

**Row 8 is the single most valuable line in this story.** `AutonomyLevel = 5` is the corrupt-row vector proving that a tampered `RulesJson` fails on *read*. At `[70,100]` it is invalid, so the test passes. **Widen to `[0,100]` and `5` becomes legal — the test silently stops testing anything and still goes green.** It must be re-vectored to something invalid under any *downward* widen.

**Rows 9 and 10 are the dangerous silent ones.** Both loops assert a property across the whole dial. After a widen they still pass — over the *old* band only. A new lower band would ship completely unexercised with nothing going red.

**Row 11 reaches an agent.** `DefaultRoutingGuidance` is shipped prose fed into acceptance decisions; `AcceptanceDefaultsDriftTests.cs:29` pins it non-empty, not by content, so the drift test does not catch it. Leaving "*(autonomy 70)*" in it is a semantic lie the moment the baseline moves — and it is a lie told *to a model that acts on it*.

**Correction to the design:** design.md §10 row 4 claims the same wording appears in `GetAcceptanceRulesTool.cs:53-56`'s `Description`. It does not — that description names no number (verified). Only sites 2 and 11 restate the level in prose that ships.

### `DefaultAutonomyLevel` is a separate concern

`AcceptanceDefaults.cs:31` — `public const int DefaultAutonomyLevel = 70;` — **stays a literal that happens to equal `Min`.** The *default* and the *range* are different concepts; coupling them would mean widening the range silently moves the shipped default for every deployment. Note `AcceptanceDefaults`' static constructor validates the base row and every per-`DocumentTypeKey` default, so a *widened* bound cannot break boot, but a *narrowed* one would refuse to start the app. Correct posture; left as is.

### Why a bare-literal drift scan is the wrong guard

A test that greps `apps/tamma-elsa/src/**/*.cs` for the literals `70`/`100`/`101` would be useless the moment Story 43-3 lands: `ActionCatalog.Descriptors.cs` will carry **~153 `DefaultMinAutonomy` literals**, most of them `AutonomyDial.Min`-valued, plus every `AlwaysHuman` row. The scan would either fail the build permanently or be allowlisted into meaninglessness. **The guard must be comparison-shaped**, keyed on the *syntax of a bound check* rather than the presence of a number.

### House patterns this story reuses

- **Shrink-only justified allowlist with staleness detection** — `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` (`KnownContractViolations`: entries may only be removed; a stale entry fails the build).
- **`[TestCaseSource]` deriving cases from a constant** — the standard way to make a bounds test track its bound.
- **Publishing a server-owned constant to the dashboard** — the existing admin api-client + hook shape under `packages/dashboard/src/services/admin/`.

## Acceptance Criteria

1. **`AutonomyDial` exists, in Core, as the sole owner of the range.** `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs` — a `public static class` with `const int Min = 70`, `const int Max = 100`, `const int AlwaysHuman = Max + 1`, plus `IsValidLevel(int)`, `IsValidThreshold(int)` (accepting `[Min,Max]` **or** exactly `AlwaysHuman`), and `ValidLevels()` yielding the inclusive range. Its XML doc states the contract: *widening downward is a one-line change here; nothing else in production C#, SQL or TypeScript may restate a bound.* The class carries **no lower-bound-below-Min concept** — `[70,100]` stays one named constant (epic decision D3).

2. **The one production edit.** `AcceptanceRules.cs:85-86` becomes `if (!AutonomyDial.IsValidLevel(AutonomyLevel)) throw Invalid(nameof(AutonomyLevel), $"AutonomyLevel must be within [{AutonomyDial.Min}, {AutonomyDial.Max}]; got {AutonomyLevel}.");` — message **interpolated**, so it cannot go stale. `AcceptanceRules.cs:30`'s XML doc is rewritten to name no number.

3. **The range crosses the wire.** `GET /api/actions/dial` returns `{ min, max, alwaysHuman, default, current, source }` — `default` from `AcceptanceDefaults.DefaultAutonomyLevel`, `current`/`source` from the caller's resolved base acceptance rules. It is the first route in Epic 43's `/api/actions` group (the group is created here; 43-6 fills it). Read-only; no new permission — readable by any authenticated caller who can already read acceptance rules.

4. **The UI binds; the constants are deleted.** `RulesEditDialog.tsx:20-21` is deleted. The slider's `min`/`max` (`:168-169`) and the helper text (`:175`) derive from the `/api/actions/dial` payload — including the helper text, which must stop being a third independent restatement (e.g. `{min} = supervised baseline · {max} = full auto`). **After this story, widening requires zero TypeScript edits.** A loading/fallback posture is specified: the dialog does not render an unbounded slider before the payload arrives.

5. **`AcceptanceRulesAdminPage.test.tsx:115-122` asserts against the mocked payload, not a literal.** The test keeps its intent (the slider is bounded) but sources `70`/`100` from the mocked dial response, so it tracks the constant.

6. **The corrupt-row vector is re-vectored.** `AcceptanceRulesServiceTests.cs:104-117`'s `AutonomyLevel = 5` becomes `AutonomyDial.Max + 1000` — invalid under **any** downward widen — with an inline comment stating why (`5` is legal at `[0,100]`; the test would silently stop testing). AC met only if the comment is present: the value alone does not carry the lesson.

7. **Both silent coverage loops derive from the constant.** `AcceptanceContractTests.cs:98` → `foreach (var level in AutonomyDial.ValidLevels())`; `AcceptanceGuardrailsTests.cs:186` → `rng.Next(AutonomyDial.Min, AutonomyDial.Max + 1)`. A widened band is then exercised automatically.

8. **The bounds test derives its cases.** `AcceptanceRulesModelTests.cs:23-28`'s four `[TestCase]`s become a `[TestCaseSource]` yielding `(Min-1,false) (Min,true) (Max,true) (Max+1,false)`, and the method is renamed off `_70_to_100`.

9. **The shipped agent-facing prose names no number.** `AcceptanceDefaults.cs:48-52`'s `DefaultRoutingGuidance` is rewritten now — e.g. *"At the deployment's baseline autonomy level, assign nearly every acceptance decision to a human role; as the level rises, decide more routine, unambiguous, fully-approved documents yourself…"*. `AcceptanceDefaultsDriftTests.cs:29`'s non-empty pin still passes; a new assertion pins that the prose contains no bare dial literal, since the existing drift test checks content not at all.

10. **`AutonomyDialSingleSourceTests` guards it, comparison-shaped.** `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AutonomyDialSingleSourceTests.cs`:
    - `No_autonomy_comparison_restates_a_bound` — scans `apps/tamma-elsa/src/**/*.cs` for comparison-shaped patterns (`Autonomy\w*\s*(is\s+)?[<>=!]+\s*(70|100|101)`, `(70|100|101)\s*[<>=!]+\s*\w*Autonomy`, `AutonomyLevel is [<>]`), failing anywhere outside `AutonomyDial.cs`, with a shrink-only allowlist using the `ContractBindingTests` staleness ratchet. **Explicitly NOT a bare-literal scan and explicitly not extended to `.ts`/`.tsx`** — the rationale (collision with 43-3's ~153 `DefaultMinAutonomy` literals; the TS constants are deleted so there is nothing to scan) is a comment in the test file, not only in this story.
    - `AlwaysHuman_is_strictly_above_Max`, `Min_is_less_than_Max`, `IsValidThreshold_accepts_AlwaysHuman_and_rejects_Max_plus_2`.
    - `DialEndpoint_ReportsAutonomyDialConstants` — the wire payload equals the constants (so a hand-written DTO default cannot drift).

11. **Documentation pass, 13 files.** Every doc restating the range is rewritten to reference the published range rather than assert `70–100` as permanent. The strongest normative statement — `wiki/Document-Lifecycle.md:155` "*Validated 70–100 — it is a dial, not a mode*" — becomes "*validated within the range published by `AutonomyDial` / `GET /api/actions/dial`*". **`docs/PRD.md:14,16,30,165` must NOT be touched** — those `70%` are the autonomous-issue-completion-rate KPI, an unrelated number.

12. **No second bound is introduced anywhere.** No DataAnnotations `[Range]`, no DB CHECK on any autonomy column, no TypeScript range validator. Stated as a reviewer checklist item and inherited by 43-2/43-3/43-5/43-6.

## Dependencies

- **None blocking.** Ships standalone; independently valuable.
- **Soft ordering with 43-0** — both stories edit `RulesEditDialog.tsx` and `acceptance-rules-api-client.ts`, in disjoint regions (43-0: the `body` memo + the `AcceptanceRules` interface; 43-1: the constants block + the slider + a new dial hook). Land 43-0 first; expect a trivial merge otherwise.
- **Blocks:** 43-2 (`ActionDescriptor.DefaultMinAutonomy` is documented as `[AutonomyDial.Min, AutonomyDial.AlwaysHuman]` and `ActionCatalog.UnclassifiedFallback = AutonomyDial.AlwaysHuman`), 43-3 (every one of ~153 defaults is written as `AutonomyDial.Min` or `AutonomyDial.AlwaysHuman`, never a literal), 43-5/43-6/43-7 (the resolver, the API's threshold validation, and the UI's level selector all read the published dial).
- **Corrects/supersedes:** Story 39-23's `EscalationClass.minAutonomyLevel` and Story 42-1's `ToolDescriptor.AutonomyFloor` — both would re-hardcode the bound. Under Epic 43 both are dropped (43-10 does the spec edits); this story must land before either is implemented.

## Out of Scope

- **Actually widening the range.** `Min` stays `70`. This story makes widening a one-line edit; it does not perform it. Widening is a product decision with a real consequence — every catalog row defaulted to `Min` becomes automated at a lower dial.
- **`AcceptanceDefaults.DefaultAutonomyLevel`.** Stays a literal `70`; the default is not the range.
- **`AcceptorRequirement`.** A separate "pin this to a human" concept; not folded into the dial (see 43-0 Out of Scope and the epic's risk list).
- **Any catalog type.** `ActionKey`, `ActionCatalog`, `ActionGroup` are 43-2/43-3. This story creates only the `/api/actions` route group as a container plus its one dial route.
- **Enforcement of any threshold.** Nothing reads the dial to decide anything yet; that is 43-9.

## Estimated Effort

2 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

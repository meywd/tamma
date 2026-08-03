# Implementation Plan — Story 43-16: Acceptance Unification (Form α)

Verified against the working tree 2026-08-03. Every file:line below was re-checked; where the
story's citation was imprecise it is corrected here, not repeated.

## Scope & Deliverable

The shipped acceptor floor stops being three hardcoded `AcceptorRequirement.Human` constants and
becomes **derived**: `Human` while `baseDial < catalogLevel(document-type:<type>)`, `Any` at or
above. The stored per-type `AcceptorRequirement` survives only as the named-type override
(explicit `any` still lowers; a base PUT still cannot lower below what the level implies). The
`max()` floor lattice is untouched — only its shipped input moves. No schema change, no new
storage, no engine change (grep confirms no consumer of `acceptorRequirement` in
`Tamma.Activities`/`Tamma.ElsaServer` — the derivation's blast radius is the resolver output and
its DTO surface only).

**AC7 is a hard product-decision gate.** At the ruling zone numbers (43-11 Amendment 3 +
re-audit), `design`/`sprint-plan`/`threat-model` acceptance sits at level **45**; the shipped
default dial is **70** (`AcceptanceDefaults.cs:33`). 45 < 70, so the derivation **automates all
three human-pinned acceptances on day one**. This plan presents both arms and decides neither —
see D4 and Blocked #1. The derivation does not merge until one arm is signed.

## Pre-Reading (verified 2026-08-03)

| File:line | Why |
|---|---|
| `docs/stories/epic-43/story-43-16/43-16-acceptance-unification-form-alpha.md` | This story; ACs are source of truth. |
| `docs/stories/epic-43/story-43-11/…md` — Amendment 2 §G (`:800-815`), Amendment 3 zone table (`:944-964`), re-audit Level-45 table (`:1220-1230`), Amendment 4 (`:1483-1507`), M6 (`:524-550`) | The ruling model: dial governs the LLM only; acceptance is always a step, the dial picks the approver; form α's exact wording, the base-row-dial caveat, the three FLAGs. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:33` (`DefaultAutonomyLevel = 70`), `:120-123` (`s_humanAcceptorRules`, Human at `:122`), `:144-147` (`s_humanProductOwnerRules`, Human at `:146`), `:162-171` (`s_securityRules`, Human at `:170`), `:206-223` (the `For` switch; Design `:214`, SprintPlan `:216`, ThreatModel `:218`) | The three constants AC1 removes and the switch arms that collapse when they go. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceFloors.cs:65-66` (`Max`), `:69-70` (`ShippedFloorFor`), `:80-96` (`ApplyShippedAcceptorFloor`; `max` applied `:85`) | The floor machinery gaining the `(type, dial)` inputs. Its class doc (`:3-55`) is the CD-1 argument and must be rewritten for the derived form. |
| `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs:63-84` (`ResolveAsync`: tier-1 exempt `:68-72`, tier-2 floored `:74-81`, tier-3 `:83`), `:86-107` (tenant twin, floored `:98-104`), `:114-131` (`ResolveBaseAsync`/`ForTenantAsync`), `:266-272` (`SystemDefault` — today NO floor call; the Human is baked into `For`) | The three call sites the dial is plumbed into. Tier 3 must start applying the floor or the three types lose their Human acceptor entirely when the constants go. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:196` | `var dial = baseRules?.Rules.AutonomyLevel ?? AutonomyDial.Min;` — THE definition of "the dial" the derivation must match (Amendment 2-G's load-bearing caveat). |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:27` (`Min = 70`), `:38` (`AlwaysHuman = 101`), `:52` (`ValidLevels()`) | AC2's quantifier. Today `ValidLevels()` = [70,100] (31 positions); after 43-11 AC1, [1,100]. The biconditional derives from the constant, so it is order-robust. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:85-86` | The literal `is < 70 or > 100` validation. Until 43-11 AC2 rewires it, **no test can store a dial below 70** — this blocks the ACCEPT-arm re-vectoring (step 11). |
| `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs:241,253,255` (three `Doc(…, min: AutonomyDial.AlwaysHuman)`), `:387-389` (mcp) | Today's catalog levels for the three types: **101**. The derivation input until 43-11's remap. |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.cs:93-120` (`DesignDocumentType_MatchesAcceptanceDefaults`) | The lockstep test AC2 rewrites into the biconditional. Its `ActionCatalog.Get(new ActionKey(ActionNamespace.DocumentType, type.ToWire()))` pattern (`:101`) is reused verbatim. |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceFloorsTests.cs` (whole file, 123 lines) | Re-vectored wholesale onto the two-arg signature. `Resolved()` helper `:18-23`; the named-trio pin `:47-58`; monotone sweep `:106-122`. |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs:140-150, 152-169, 171-183, 185-189` | The four rows that named the three Human constants (AC5's enumerated set). Note `:135` (prose asserts `Any`) and the reviewer-selection pins `:66-104` stay untouched — they are AC6's evidence. |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesEndpointsTests.cs:215-237` (preserve-shipped-floor; **inline sanity assert `:222-224` breaks in phase 1**), `:311-324` (explicit-any, AC4), `:340-374` + `:384-409` + `:419-438` (CD-1 family, arranged dials **80/85**), `:454, :494, :525` (SaaS mirrors, same dials) | The floor-test family. The arranged dials of 80/85 collide with EVERY proposed level set once 43-11's remap lands — see Blocked #2. |
| `docs/stories/epic-43/story-43-11/implementation-plan.md` step 10 + D5 | The AC9 decision table lands in `tests/Tamma.Core.Tests/Actions/ActionCatalogLevelTests.cs` (does **not** exist yet — verified). AC7's three rows extend that fixture. |
| `docs/stories/epic-43/story-43-15/implementation-plan.md:368-370` | 43-15 declares no file overlap with this story; watches only `AcceptanceRulesEndpoints.Upsert` — which this plan does not touch. |

## Design Decisions

- **D1 — The derivation lives in `AcceptanceFloors`, not in `AcceptanceDefaults.For`.**
  `ShippedFloorFor(DocumentTypeKey type, int dial)` returns
  `dial < ActionCatalog.Get(new ActionKey(ActionNamespace.DocumentType, type.ToWire())).DefaultMinAutonomy ? Human : Any`.
  `AcceptanceDefaults.For(type)` keeps its parameterless contract and now returns `Any` for every
  type. **Rejected:** deriving inside `For(type, dial)` — it would put an `ActionCatalog` read
  into `AcceptanceDefaults`' static constructor (which calls `For` for every type at `:191-192`),
  coupling two static initializers, and would break `For`'s parameterless signature used across
  the drift tests and the service. `ActionCatalog.Descriptors.cs` references `AcceptanceDefaults`
  only in comments/strings (verified by grep), so `AcceptanceFloors → ActionCatalog` creates no
  init cycle.

- **D2 — The dial is an explicit parameter at every floor call site, sourced the way the gate
  sources it.** `ShippedFloorFor(type, dial)` and `ApplyShippedAcceptorFloor(resolved, type,
  baseDial)`. Callers pass: tier 2 — the materialized base row's `Rules.AutonomyLevel` (that row
  IS what `ResolveBaseAsync` returns for this principal, i.e. exactly
  `AutonomyGateEvaluator.cs:196`'s input); tier 3 — `AcceptanceDefaults.Rules.AutonomyLevel`
  (what `ResolveBase*` returns when no base row exists). **Rejected:** reading
  `resolved.Rules.AutonomyLevel` inside the floor function. It is coincidentally correct at tiers
  2/3 today (the floored row is the base/system row), but it is exactly the wiring that lets a
  future caller hand a **per-type** row in and silently violate the Amendment 2-G caveat. The
  explicit parameter makes "which dial" a visible choice at every call site; AC3's test then
  guards the choice.

- **D3 — Tier 3 (system default) goes through `ApplyShippedAcceptorFloor`, and the
  `AcceptorRequirementFloored` flag becomes `true` on system-default resolutions of the three
  types (at dials below their level).** Today tier 3 returns Human with `Floored=false` because
  the Human is baked into `For`. **Rejected:** constructing the derived value directly inside
  `SystemDefault` with the flag false — two code paths for one rule, and it hides the derivation
  from the provenance surface the endpoint already exposes. Consequence to state in review: the
  DTO's `acceptorRequirementFloored` flips false→true on untouched-deployment reads of the three
  types. No test pins the tier-3 flag (verified: the only `AcceptorRequirementFloored` asserts
  are tier-2 `:366` and controls `:373`, `:437` — all unaffected); the dashboard reads the field
  (`RulesEditDialog.tsx`) for display only.

- **D4 — AC7 is a mechanical merge gate, and this plan does not pick the arm.** The three rows
  land in 43-11's AC9 decision table (`ActionCatalogLevelTests.cs`, 43-11 plan step 10/D5) as a
  distinct `AcceptanceDayOneLooseningDecisions` set, one row per type, each carrying
  `ACCEPT | REBASE` + signer + date. A cross-check test fails the build when a row is missing,
  marked undecided, or **stale against the catalog** (ACCEPT row while the type's
  `DefaultMinAutonomy > DefaultAutonomyLevel`; REBASE row while it is `≤ DefaultAutonomyLevel`
  or not a valid level). The branch carries the rows undecided and therefore **cannot go green
  until the product owner signs**. The two arms, with their real costs:
  - **ACCEPT** — the zone numbers stand (45). On upgrade day at dial 70, the orchestrator
    becomes the approver for design/sprint-plan/threat-model acceptance (the acceptance step
    still runs; the two runtime escape signals — ambiguity ≥ threshold, blocking-review
    violation — still pull in a person). Amendment 1 M5's "no upgrade loosens anything on day
    one" (43-11 `:515`) is formally retired and its text amended. The CD-1/floor test family
    must be re-vectored to arranged dials **below 45** (dial 40 is the discriminating position:
    routine-doc acceptances at zone 40 automate, binding docs at 45 floor Human) — which
    requires 43-11 AC1+AC2 (store a dial < 70) to have landed.
  - **REBASE** — the three catalog levels move above 70 (Amendment 1's 80/85/90 are the
    on-record candidates; a flat >70 value is equally valid). Day-one behaviour at dial 70 is
    preserved. Cost: the product owner's own Amendment 3 zone table ("Approve binding docs" =
    45 including these three) gains three signed exceptions — the zone model's uniformity
    breaks, and the re-audit FLAG rows are resolved the other way.

- **D5 — AC5's "exactly four surfaces" is honoured by pre-justifying every extra edit now.**
  Verified against the tree, the diff cannot stay inside AC5's list (see Blocked #2). The
  justified deviation set is enumerated in steps 7 and 11 and nowhere else; any edit outside
  steps 4–7 and 9–11's named tests remains a scope violation.

- **D6 — Train order: derivation first, remap second.** Against today's catalog (the three types
  at 101), the derivation is behaviour-preserving: `dial < 101` at every valid dial → floor
  `Human` everywhere, exactly today's output. If 43-11's remap lands first instead, the existing
  lockstep test `DesignDocumentType_MatchesAcceptanceDefaults` goes red in the gap (design's
  descriptor ≠ AlwaysHuman while the acceptor is still Human). The story text says "43-11 first
  or together"; the verified tree says derivation-first is the only order with no red
  intermediate state. One PR train either way; AC7's gate (D4) binds the train, not just the
  remap commit.

## Implementation Steps

**Phase 1 — the derivation (behaviour-preserving against the 101 catalog; merge still gated by
AC7/D4).**

1. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs`** (AC1) —
   remove the three Human constants: delete `s_humanAcceptorRules` (field `:81`, block
   `:117-123`) and let `DocumentTypeKey.Design` (`:214`) fall to `_ => Rules`; delete
   `s_humanProductOwnerRules` (field `:83`, block `:141-147`) and point `SprintPlan` (`:216`) at
   `s_productOwnerRules`; delete the `AcceptorRequirement = AcceptorRequirement.Human` line
   (`:170`) from `s_securityRules` (its security reviewer stays — AC6). Update the class doc
   (`:17-23`) to say the acceptor floor is derived, with a pointer to `AcceptanceFloors`.
   *Effort: 0.25 day.*

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceFloors.cs`** (AC1, D1,
   D2) — `ShippedFloorFor(DocumentTypeKey type, int dial)` per D1;
   `ApplyShippedAcceptorFloor(ResolvedAcceptanceRules resolved, DocumentTypeKey type, int
   baseDial)`; `Max` (`:65-66`) and the `max()` application shape (`:85`) unchanged. Rewrite the
   class doc: the CD-1 floor argument stands, the floor's *value* is now a function of
   `(catalog level, base dial)`, and the base-row caveat is stated in the doc with the
   `AutonomyGateEvaluator.cs:196` citation. *Effort: 0.25 day.*

3. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs`**
   (D2, D3) — `:79` and `:102`: pass the materialized base row's `AutonomyLevel` as `baseDial`;
   `SystemDefault` (`:266-272`): wrap in
   `AcceptanceFloors.ApplyShippedAcceptorFloor(…, type, AcceptanceDefaults.Rules.AutonomyLevel)`.
   Tier-1 (`:68-72`, `:94-96`) stays exempt — no edit. `ResolveBase*` untouched. *Effort: 0.25
   day.*

4. **REWRITE `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.cs:93-120`**
   (AC2) — replace `DesignDocumentType_MatchesAcceptanceDefaults` with
   `ShippedAcceptorFloor_IsTheCatalogLevelAgainstTheDial_ForEveryTypeAtEveryDial`: for every
   `DocumentTypeKey` (17) × every `AutonomyDial.ValidLevels()` position, assert
   `ShippedFloorFor(type, dial) == Human ⟺ dial < ActionCatalog.Get(document-type:<type>).DefaultMinAutonomy`.
   No other test in this file is touched (the `ShippedAlwaysHuman` table, `EveryOtherMember…`,
   the MCP and triage pins are 43-11's to move). *Effort: 0.25 day.*

5. **RE-VECTOR `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceFloorsTests.cs`**
   (AC5) — all call sites onto the two-arg/three-arg signatures.
   `TheShippedHumanFloor_CoversDesign_SprintPlan_AndThreatModel` (`:47-58`) becomes
   `TheShippedFloor_IsDerived…`: at `dial = AutonomyDial.Min` the three types floor Human and
   `Findings` floors Any (true at catalog-101 and at every proposed level set with Min = 1;
   under Min = 70 + ACCEPT-45 this case is dial-unreachable — the test derives its dial from
   `AutonomyDial.Min`, so it stays honest in every ordering). `TheFloor_IsMonotone…`
   (`:106-122`) gains the dial loop: never lowers a stated requirement, and for a fixed type the
   floor is antitone in the dial (Human below the level, Any at or above). Lattice test
   (`:31-43`) untouched. *Effort: 0.25 day.*

6. **EDIT `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs`**
   (AC5's enumerated rows) — `Design_defaults_to_a_human_acceptor` (`:140-150`) inverts to
   assert `For(Design).AcceptorRequirement == Any` with a pointer to the derivation;
   `The_41_1b_human_pinned_types_get_a_human_acceptor` (`:171-183`) likewise;
   `Design_sprint_plan_and_threat_model_are_the_only_types_with_an_acceptor_floor` (`:185-189`)
   becomes "no type ships a stored acceptor floor" (set 3 → 0);
   `Every_unpinned_type_imposes_no_acceptor_floor` (`:152-169`) extends its `TestCase` list
   14 → 17 and drops the stale reason string. The reviewer-selection tests (`:66-104`,
   including `Every_other_type_defaults_to_single_architect_unanimous` which already lists
   Design, and ThreatModel's security reviewer at `:103`) pass **unmodified** — that is AC6's
   pin. *Effort: 0.25 day.*

7. **EDIT `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesEndpointsTests.cs`
   — phase-1 minimum only** (pre-justified AC5 deviation, D5): the inline sanity assertion at
   `:222-224` (`AcceptanceDefaults.For(Design).AcceptorRequirement == Human`) is false the
   moment step 1 lands; re-point it at the resolved value
   (`_store.ResolveAsync(...)` before the PUT) or at
   `ShippedFloorFor(Design, AcceptanceDefaults.DefaultAutonomyLevel)`. Update the doc comment on
   `Upsert_explicit_any_clears_the_human_floor` (`:306-311`) to name the derived floor (AC4's
   phase-1 half — behaviourally the test already exercises "per-type explicit any lowers below
   the derived Human", since the derived floor at catalog-101 is Human at every dial). No other
   test body changes in phase 1. *Effort: 0.25 day.*

8. **Run `dotnet test`** — green, with exactly one intended observable delta: tier-3 resolutions
   of the three types now report `acceptorRequirementFloored: true` (D3). `dotnet ef migrations
   has-pending-model-changes` trivially clean — no entity is touched (AC8).

**Phase 2 — rides the 43-11 remap train; blocked on the AC7 signature (D4).**

9. **ADD the decision rows + cross-check** (AC7) in
   `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/ActionCatalogLevelTests.cs` — 43-11 plan
   step 10's fixture; if this story's commit is first in the train, mint the file with only the
   acceptance table and let 43-11 extend it. Table: three rows
   (`document-type:design|sprint-plan|threat-model`), decision enum, signer, date, reason. Test
   `AcceptanceDayOneLoosening_IsDecided_AndNotStale` per D4's staleness rules. *Effort: 0.25
   day + the wait on the signature.*

10. **ADD the base-dial caveat test** (AC3) in
    `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesEndpointsTests.cs`:
    arrange base row at a dial **below** the type's landed level, per-type row for `design`
    with its own `AutonomyLevel` **at or above** the level and acceptor materialized `human`;
    assert the resolved acceptor is `Human` — the per-type autonomy edit moved nothing. A
    wrong-wired implementation (deriving from the per-type row's dial) returns `Any` and goes
    red; removing the D2 explicit parameter re-creates exactly that wiring. This test is
    structurally vacuous while the levels are 101 (no valid dial reaches 101 — nothing
    discriminates), which is why it lands in phase 2, stated here rather than discovered later.
    *Effort: 0.25 day.*

11. **RE-VECTOR the floor-test family onto discriminating dials** (AC4 phase-2 half + D5's
    justified deviation): `:215` (arrange dial below the landed level so the preserved value is
    the derived Human), `:311` (arrange base dial below the level, then per-type explicit any
    lowers), `:340`, `:384`, `:419` and SaaS mirrors `:454`, `:494`, `:525` (their arranged
    dials 80/85 collide with 45 under ACCEPT **and** with 80/85/90 under REBASE — see Blocked
    #2; re-vector to `min(landed levels) − 5`, i.e. dial 40 under ACCEPT / 75 under REBASE-80).
    The asserted semantics are unchanged: explicit per-type any lowers; a base PUT cannot; the
    bake-in writes the floored value; `Findings` stays the un-floored control (at dial 40 its
    zone level is exactly 40 → automated → not floored, which keeps `:373` honest). Under
    ACCEPT this step **requires 43-11 AC1 (Min = 1) and AC2 (the `AcceptanceRules.cs:85-86`
    rewire)** — a dial of 40 is a 400 until both land. *Effort: 0.5 day.*

12. **Run `dotnet test`** on the assembled train (AC8).

Total: ~2.5 days + the AC7 decision wait. Matches the story's 2–3 day estimate.

## Test Plan (fail-first, red state named per test)

| Test | Where | Red state — what fails, against what |
|---|---|---|
| `ShippedAcceptorFloor_IsTheCatalogLevel…` (AC2 biconditional, step 4) | `ActionCatalogDefaultsTests.cs` | Written first: **compile-fails** (two-arg `ShippedFloorFor` does not exist). After step 1 but before step 2's derivation: **red on the three types** at every dial (`For` now returns Any; the biconditional at catalog-101 demands Human below 101). Green only when the derivation lands. It is also the lockstep replacement: at any future point, moving a document-type level without the derivation (or vice versa) goes red. |
| `TheShippedFloor_IsDerived…` + antitone sweep (step 5) | `AcceptanceFloorsTests.cs` | Same compile-fail red; after step 1 pre-derivation the trio asserts red (floor Any). The antitone half cannot fail at catalog-101 (no dial reaches 101) — its discriminating power arrives with the remap; stated, not hidden. |
| Inverted drift rows (step 6) | `AcceptanceDefaultsDriftTests.cs` | **Genuinely red against today's tree** (`For(Design).AcceptorRequirement` is Human today). Write before step 1; step 1 turns them green. The strongest fail-first evidence in the story. |
| Reviewer-selection drift tests `:66-104` (AC6) | same file | **Must pass unmodified** — the negative pin. If step 1 touches a `ReviewerSelection`, `The_41_1b_single_reviewer_types_get_their_domain_reviewer` (ThreatModel→security) goes red. |
| `Upsert_explicit_any_clears_the_human_floor` (AC4) | `AcceptanceRulesEndpointsTests.cs:311` | Phase 1: passes throughout (tier-1 exemption is untouched); its sanity context `:222-224` in the neighbouring test is the phase-1 red (fails the moment step 1 lands, fixed in step 7). Phase 2 (step 11): the re-vectored arrange (low base dial) is a **400 until 43-11 AC1+AC2 land** — red via dependency, then green. |
| CD-1 family `:340/:384/:419` + SaaS `:454/:494/:525` | same file | **Red on the train the moment 43-11's remap lands, under either AC7 arm** (arranged dial 80 ≥ 45 and ≥ 80). That red is the forcing function for step 11; it must not be silenced by deletion — the re-vectored forms assert the same CD-1 semantics at a dial below the landed level. |
| `AcceptanceDayOneLoosening_IsDecided_AndNotStale` (AC7, step 9) | `ActionCatalogLevelTests.cs` (new) | Red while any row is undecided (the shipped state of the branch), red on a stale row (ACCEPT while level > 70; REBASE while level ≤ 70 or = 101), red on a missing/extra row vs. the formerly-Human-pinned set. **This red is the merge gate** — it cannot be made green by code, only by the signed decision plus the matching catalog level. |
| Base-dial caveat test (AC3, step 10) | `AcceptanceRulesEndpointsTests.cs` | Red against a wrong-wired derivation (per-type dial in, Any out); red if D2's explicit parameter is later replaced by reading `resolved.Rules.AutonomyLevel`. Vacuous at catalog-101 — phase 2 only. |

A note the rules demand: the AC2 biconditional is **true of today's tree as a property**
(three types at 101 → Human everywhere; the rest at Min → never Human). Its fail-first value is
the compile break plus the step-1→step-2 red window, and permanently as the lockstep guard. The
tests with unconditional red-today status are the step-6 drift inversions.

## Count pins moved (current values read from the tree)

| Pin | Before → After |
|---|---|
| Stored `AcceptorRequirement.Human` defaults in `AcceptanceDefaults` (`:122,:146,:170`) | **3 → 0** |
| `AcceptanceDefaultsDriftTests.Design_sprint_plan_and_threat_model_are_the_only_types_with_an_acceptor_floor` — non-Any set | **{Design, SprintPlan, ThreatModel} → ∅** |
| `AcceptanceDefaultsDriftTests.Every_unpinned_type_imposes_no_acceptor_floor` TestCase count | **14 → 17** |
| `ActionCatalogDefaultsTests.cs:93-120` quantification | 3-type constant lockstep → **17 types × `ValidLevels()`** (17×31 today; 17×100 once 43-11 AC1 lands) |
| `AcceptanceFloorsTests` named-Human-floor pin (`:47-58`) | 3 static Human floors → 0 static; derived form pinned instead |
| NOT moved (and asserted so): `ActionVocabularyCountTests` **197**; `ActionCatalogDefaultsTests.ShippedAlwaysHuman` **4** (43-11 AC6's to delete, not this story's); `AcceptanceDefaults.DefaultAutonomyLevel` **70**; `ActionEnforcementSitesTests` **21**; panel roster **7**. | — |

## Dependencies on the batch (43-12..16, 42-10, 39-25, 40-8, 31-13)

- **43-11 — blocking, in both directions, one PR train.** Phase 2 consumes its catalog level
  remap (`Descriptors.cs:241/253/255`), its AC1 (`Min = 1`) + AC2 (`AcceptanceRules.cs:85-86`
  rewire) for any arranged dial below 70, and its AC9 decision-table fixture
  (`ActionCatalogLevelTests.cs`, not yet in the tree). In return 43-11's remap **cannot land
  before this story's derivation** without turning `DesignDocumentType_MatchesAcceptanceDefaults`
  red (D6). Order inside the train: 43-16 phase 1 → 43-11 (remap + Min + AC9 table) → 43-16
  phase 2. The AC7 signature gates the whole train.
- **43-13 (caller-kind predicate) — no ordering constraint; same-file conflict risk only.**
  Document-type rows are DUAL in the re-audit and stay dial-governed, so 43-13's
  machinery-off-the-dial work never touches this story's semantics. Both stories edit
  `ActionCatalogDefaultsTests.cs` (disjoint tests) — textual merge coordination, nothing more.
- **43-15 (toggles/dial UI) — no dependency for form α.** Form β (field deletion) is explicitly
  deferred until 43-15's toggle surface proves itself; 43-15's plan confirms no file overlap and
  this plan does not touch the `AcceptanceRulesEndpoints.Upsert` handler it watches.
- **43-12 (per-target keys) — none.** It reshapes effect/deploy keys; the derivation iterates
  `DocumentTypeKey` only.
- **43-14 (approval scopes / grant minting) — none.** Who *approves* an escalated acceptance and
  how the grant is scoped is orthogonal to *whether* the floor forces a person.
- **42-10, 39-25, 40-8, 31-13 — none.** No shared files. 39-25 is named in Out of Scope: the two
  runtime escape signals stay the only level-independent human pulls, and this story must not
  touch them.

## Risks

- **The AC7 wait is the schedule risk.** Everything in phase 2 is mechanical once the arm is
  signed; nothing in phase 2 can start honestly before it.
- **The `AcceptorRequirementFloored` flip on tier-3 rows (D3) is DTO-visible.** Dashboard shows
  provenance; an operator sees "floored" on a fresh install where they saw nothing. Cosmetic,
  but it must be in the review notes, not discovered.
- **Bake-in at save time crystallizes the derived floor.** 43-0's preserve-on-absent materializes
  the *resolved* acceptor into any per-type row saved for an unrelated reason. A row saved at
  dial 40 bakes `human`; raising the dial later does not un-bake it (stored survives, by AC1's
  own rule; deleting the row restores derived behaviour). Same posture CD-1 chose; recorded so
  nobody files it as a bug.
- **Under ACCEPT, the CD-1 tests' meaning narrows.** "A base PUT cannot lower the floor" is only
  demonstrable at dials below 45 — a range that exists solely because 43-11 AC1 landed. If the
  train ever ships Min = 70 with ACCEPT-45 levels, the CD-1 protection is untestable and
  effectively dead at every reachable dial; that combination should be treated as a train
  assembly error.
- **Static-init coupling.** `ShippedFloorFor` now triggers `ActionCatalog` initialization from
  the acceptance path. No cycle exists today (verified); a future edit that makes a descriptor
  read `AcceptanceFloors` would create one. One sentence in each class doc wards it.

## Blocked / Contradictions

1. **AC7 — REQUIRED product decision, not made here (D4).** The zone model (43-11 Amendment 3 +
   re-audit, binding) puts the three acceptances at 45; the shipped dial is 70; the shipped
   constants say Human-at-every-dial. The derivation makes these three statements unsatisfiable
   together. ACCEPT retires Amendment 1 M5's no-day-one-loosening promise (43-11 `:515` must be
   amended); REBASE breaks the zone table's uniformity the product owner set (three signed
   exceptions to "Approve binding docs = 45"). The cross-check test ships red-by-construction
   until one arm is signed. **Stop-and-decide; the plan proceeds on everything else.**
2. **AC5 vs AC4/the tree — the "exactly four surfaces" enumeration is not satisfiable.**
   Verified: (a) `AcceptanceRulesEndpointsTests.cs:222-224` asserts
   `AcceptanceDefaults.For(Design).AcceptorRequirement == Human` inline — false the moment AC1's
   constants go, so a fifth test file location is touched in phase 1; (b) the CD-1 family
   (`:340/:384/:419/:454/:494/:525`) arranges dials 80/85, which sit **at or above every
   proposed landed level** (45 under ACCEPT; 80/85 collide with REBASE-80/85 for design and
   sprint-plan), so those tests go red on the train and must be re-vectored — AC4 itself demands
   the CD-1 protection be "re-vectored, not deleted", contradicting AC5's list which omits them.
   Resolution adopted: treat AC5's escape clause ("justified in review") as the mechanism and
   pre-justify exactly steps 7 and 11's edits; nothing else.
3. **AC3's caveat test is unwritable-as-discriminating until the remap lands.** While the three
   levels are 101, no valid dial is ≥ the level, so a wrong-wired derivation and the correct one
   agree on every input. The test lands in phase 2 (step 10). Recorded rather than planned
   around silently.
4. **The story's own sequencing note is backwards.** "43-11 first … or 43-16 immediately after
   (the lockstep test fails in the gap)" — verified: the gap exists only in that order.
   Derivation-first has no red gap (D6). Adopted as a plan deviation from the story's
   Dependencies wording, flagged for the train assembler.

## Definition of Done

| AC | Step(s) | Verified by |
|---|---|---|
| 1 — constants removed, floor derived, `max()` unchanged | 1, 2, 3 | step-6 drift inversions; `AcceptanceFloorsTests` lattice test unmodified |
| 2 — biconditional over every type × every dial | 4 | `ShippedAcceptorFloor_IsTheCatalogLevel…` |
| 3 — base dial, never per-type | 2 (D2), 10 | base-dial caveat test |
| 4 — explicit-any preserved, CD-1 re-vectored | 7, 11 | `:311` + re-vectored CD-1 family |
| 5 — enumerated touch set | 4–7, 9–11 only | diff review against D5's list |
| 6 — panel path untouched | 1 (scoped edits) | drift `:66-104` pass unmodified |
| 7 — loosening decision recorded, undecided fails | 9 + the signature | `AcceptanceDayOneLoosening_IsDecided_AndNotStale` |
| 8 — green, no schema change | 8, 12 | `dotnet test`; `has-pending-model-changes` clean |

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-03 | 1.0.0   | Initial plan — form α derivation in two phases; AC7 held as an undecided product gate (D4); AC5 deviation set pre-justified against the verified tree; derivation-first train order (D6) | Claude |

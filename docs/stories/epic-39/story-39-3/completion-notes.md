# Completion Notes — Story 39-3: Document Types Batch 1

Status: implemented (Decomposition, Findings, AmbiguityAssessment, Clarification).

## AC6 — Deliberate divergences from the baseline parsers

The four typed validators are **supersets** of the fail-closed baseline parsers:
every input a baseline parser rejects, the typed `Validate` also rejects (proven by
`BaselineSubsumptionTests`). Where a baseline parser was **lenient** (accepted a
value by normalizing, pruning, or clamping it), the typed validator deliberately
**tightens** and emits a violation instead. Each divergence below is asserted as a
live test in `BaselineSubsumptionTests` (its `because` message cites this file), so a
stale entry has a failing twin in CI.

| # | Divergence | Baseline behaviour | Typed behaviour | Violation code | Affected prompt cell(s) |
|---|---|---|---|---|---|
| 1 | **Per-task sizing 2–8h** | `estimateHours` clamped `<0 → 0`; 2–8h was "a soft guide", never enforced | Rejected when outside **[2, 8]** inclusive (missing → 0 → rejected) | `SIZING_OUT_OF_RANGE` | `senior_developer/decompose-issue` |
| 2 | **Dangling `dependsOn`** | Pruned silently | Loud violation naming the missing id | `DANGLING_DEPENDS_ON` | `senior_developer/decompose-issue` |
| 3 | **Self `dependsOn`** | Pruned silently | Loud violation | `SELF_DEPENDS_ON` | `senior_developer/decompose-issue` |
| 4 | **Duplicate task id** | Kept the first, dropped the rest silently | Loud violation naming the id | `DUPLICATE_TASK_ID` | `senior_developer/decompose-issue` |
| 5 | **Dependency cycles** | Deferred to Story 2-15 (not detected) | Loud violation rendering the cycle path (`ST-2 -> ST-4 -> ST-2`) **plus** the stable `NO_PREREQUISITE_ORDER` signal | `CYCLIC_DEPENDS_ON`, `NO_PREREQUISITE_ORDER` | `senior_developer/decompose-issue` |
| 6 | **Complexity label set** | `SubtaskComplexities.Normalize` folded synonyms (`"Trivial." → low`) and defaulted unknowns to `medium` | Strict closed set `{low, medium, high}` — reject, don't normalize (D5) | `UNKNOWN_COMPLEXITY` | `senior_developer/decompose-issue` |
| 7 | **Evidence required (findings)** | `citations` read but never required | Every finding must cite ≥1 source | `MISSING_EVIDENCE` | `product_owner/research` |
| 8 | **`relevance`/`confidence` ranges (findings)** | Read unchecked (no range enforcement) | Rejected when outside [0,1] — reject, don't clamp (D6) | `RELEVANCE_OUT_OF_RANGE`, `CONFIDENCE_OUT_OF_RANGE` | `product_owner/research` |
| 9 | **Ambiguity `confidence` range** | **Clamped** to [0,1] | Rejected when outside [0,1] — reject, don't clamp (D6) | `CONFIDENCE_OUT_OF_RANGE` | `product_owner/score-ambiguity` |
| 10 | **Ambiguity type label set** | `AmbiguityTypes.Normalize` folded synonyms (`"unclear" → vague`) and defaulted unknowns to `unspecified` | Strict closed set `{vague, missing, contradictory, implicit, unspecified}` (D5) | `UNKNOWN_AMBIGUITY_TYPE` | `product_owner/score-ambiguity` |
| 11 | **Ambiguity severity label set** | `AmbiguitySeverities.Normalize` folded synonyms (`"critical" → high`) and defaulted unknowns to `medium` | Strict closed set `{low, medium, high}` (D5) | `UNKNOWN_SEVERITY` | `product_owner/score-ambiguity` |
| 12 | **Open-endedness (clarification)** | `ParseQuestions` required only ≥1 non-empty question | ≥1 non-empty question **and** not-all-closed-form (deterministic yes/no-auxiliary detector, D4) | `NO_OPEN_QUESTION` | `product_owner/clarify-requirements` |

### Producer-side reconciliation (Story 39-13)

The strict label sets (divergences 6, 10, 11) do **not** regress live traffic: Story
39-13 migration reuses the existing `SubtaskComplexities.Normalize` /
`AmbiguityTypes.Normalize` / `AmbiguitySeverities.Normalize` helpers **producer-side**,
before the typed payload is constructed, so synonyms are folded to the canonical wire
before `Validate` ever sees them. The 39-9 repair ring is the designed consumer of the
remaining tightenings (sizing, evidence, ranges).

### Boundary mapping (D8)

Text-level baseline negatives (`""`, `"   "`, `"no json here at all"`, `"{ not valid
json"`) can never reach `Validate` — the 39-2 JSON boundary throws first
(`BaselineSubsumptionTests.Text_level_negatives_throw_at_the_json_boundary`). JSON-shaped
negatives (missing summary, empty subtasks, out-of-range score, missing rationale,
all-shell items) map to named `Validate` violations. A payload whose wire **types** are
wrong (e.g. a non-numeric `score`, a string where an array is expected) deserializes to a
single `MALFORMED_PAYLOAD` violation — `AmbiguityAssessment.score` is `required`, so an
absent score also fails loud there (subsuming the baseline fail-closed on a missing score).

## Notable design choices

- **`DependencyGraphCheck` extracted up front** (`Tamma.Core/Documents/Types/`) so Story
  39-4's `Plan` reuses one copy of the cycle/dangling/self/topological checks (D10). Each
  document type supplies its own SCREAMING_SNAKE_CASE code strings via `DependencyGraphCodes`.
- **`DocumentExample.ExpectedViolationCodes`** added additively (D9); the registry drift loop
  now asserts each invalid example emits **exactly** its declared codes.
- **Clarification is one flat two-phase payload** with a `phase` discriminator (D3); `questions`
  stays an array of strings so `ClarifyParsing.ParseQuestions` still parses it, and the resolution
  phase keeps `clarifiedRequirement`/`remainingAmbiguities`/`resolved` at the root for
  `ClarifyParsing.ParseClarification`.

## Minor deviations from the implementation-plan sketches

- Payload records use **non-`required`, defaulted** properties for domain-checked load-bearing
  fields (`summary`, `subtasks`, `rationale`, task `id`, …) rather than `required` as the plan's
  illustrative sketches showed — so a missing field yields the **named** domain violation the Test
  Plan pins (`MISSING_SUMMARY`, `NO_TASKS`, `MISSING_RATIONALE`, `TASK_MISSING_ID`) instead of a
  generic deserialization failure. The one intentional `required` is `AmbiguityAssessment.Score`,
  where the plan wants an absent score to fail loud with no named code available (→ `MALFORMED_PAYLOAD`).
- `RenderContractTokenTests` and the cross-parser suites live exactly where D7 places them
  (`Tamma.Activities.Tests/Documents/Types/`); `Tamma.Core.Tests` gained **no** `ProjectReference`.

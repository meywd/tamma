# Implementation Plan — Story 39-3: Document Types Batch 1 — Decomposition, Findings, AmbiguityAssessment, Clarification

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.Core/Documents/Types/` contains four first-class typed documents — `Decomposition`, `Findings`, `AmbiguityAssessment`, `Clarification` — each as an immutable C# payload record (+ nested records), an `IDocumentType` implementation (`Validate` / `RenderContract` / `Examples`) with stable SCREAMING_SNAKE_CASE violation codes, and a `DocumentTypeRegistry` registration (count pin consciously bumped 0 → 4). A subsumption + round-trip test suite in `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Types/` proves every input the existing fail-closed parsers reject is also rejected by the new validators, and every fixture they parse flows through the typed payload and back to the *old* parsers unharmed. No parser is deleted, no workflow rewired; every deliberate tightening over the baseline is enumerated in completion notes.

## Pre-Reading

- `docs/stories/epic-39/story-39-3/39-3-document-types-batch-1-decomposition-findings-ambiguity-clarification.md` — the story (source of truth for ACs)
- `docs/stories/epic-39/README.md` — document-type table (domain rules per type), "Vocabulary static, composition dynamic"
- `docs/stories/epic-39/story-39-2/implementation-plan.md` — the 39-2 contract this story implements against (`IDocumentType`, `DocumentValidationResult`/`DocumentViolation`, `DocumentExample`, `DocumentTypeRegistry`, `DocumentJson`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/` — **NOT FOUND (expected)**: the 39-2 deliverable has not landed yet; this story is blocked on it (see Dependencies & Sequencing)
- Baseline parsers (the compatibility floor — read end-to-end):
  - `apps/tamma-elsa/src/Tamma.Activities/Decomposition/DecompositionParsing.cs` + `Models/DecompositionModels.cs` (`IssueDecomposition`, `Subtask`, `SubtaskComplexities.Normalize`)
  - `apps/tamma-elsa/src/Tamma.Activities/Research/ResearchParsing.cs` + `Models/ResearchModels.cs` (`ResearchReport`, `ResearchFinding`)
  - `apps/tamma-elsa/src/Tamma.Activities/Ambiguity/AmbiguityParsing.cs` + `Models/AmbiguityModels.cs` (`AmbiguityTypes`/`AmbiguitySeverities` closed sets + synonym folding)
  - `apps/tamma-elsa/src/Tamma.Activities/Clarify/ClarifyParsing.cs` + `Models/ClarifyModels.cs` (`ParseQuestions` three accepted shapes; `ParseClarification` flat object)
- Baseline callers (context only, untouched by AC9): `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs`, `ResearchWorkflow.cs`, `AmbiguityScoringWorkflow.cs`, `ClarifyingQuestionsWorkflow.cs`
- Round-trip corpus: `apps/tamma-elsa/tests/Tamma.Activities.Tests/Decomposition/DecompositionParsingTests.cs`, `Research/ResearchParsingTests.cs`, `Ambiguity/AmbiguityParsingTests.cs` (no ClarifyParsing test file exists — clarify fixtures come from the prompt templates' documented shapes; see D3)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the binding map whose tokens AC8 pins `RenderContract()` to; note the clarify questions cell is `(product_owner, clarify-requirements)` (wire name from `AgentAction.ClarifyRequirements` — the story's shorthand "clarify" is not a wire name)
- Prompt cells the contracts must not contradict: `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/decompose-issue.md`, `product_owner/score-ambiguity.md`, `product_owner/research.md`, `product_owner/clarify-requirements.md`, `product_owner/incorporate-answers.md` (context-scan cells are `IntentionallyUnbound` free-text — they impose no tokens)
- Style precedents: `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs`/`EnumWire.cs` (`[Wire]` enums), `apps/tamma-elsa/src/Tamma.Core/TammaError.cs`, `apps/tamma-elsa/src/Tamma.Activities/Core/JsonSlice.cs` (first-`{`-to-last-`}` idiom)
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Tamma.Core.Tests.csproj` — the test project this story extends (NUnit 3.14 + FluentAssertions, references only Tamma.Core today)

## Design Decisions

- **D1 — Names and file layout.** Payload records carry the canonical type names in `namespace Tamma.Core.Documents.Types`: `Decomposition`, `Findings`, `AmbiguityAssessment`, `Clarification`. The `IDocumentType` implementations are `DecompositionDocumentType`, `FindingsDocumentType`, `AmbiguityAssessmentDocumentType`, `ClarificationDocumentType`. One file per type holds record + nested records + document type + violation-code consts (`Types/Decomposition.cs`, `Types/Findings.cs`, `Types/AmbiguityAssessment.cs`, `Types/Clarification.cs`). Name collisions with the legacy `Tamma.Activities.*.Models` classes (`AmbiguityAssessment`, `AmbiguityItem`) are namespace-separated; the round-trip test file disambiguates with using-aliases (`using LegacyAssessment = Tamma.Activities.Ambiguity.Models.AmbiguityAssessment;`).
- **D2 — Wire shape = the baseline parser's shape, verbatim.** AC7 (old parser must still parse the re-serialized payload) fixes the JSON property names: `Decomposition` → `summary`/`subtasks[{id,title,description,acceptanceCriteria,estimateHours,complexity,dependsOn}]`; `Findings` → `topic`/`summary`/`findings[{title,summary,relevance,confidence,citations}]`/`overallConfidence` (record property is `Items` with `[JsonPropertyName("findings")]` — C# forbids a member named like its enclosing type; an optional `rank` per finding is additive); `AmbiguityAssessment` → `score`/`rationale`/`confidence`/`ambiguities[{type,description,severity,recommendation}]`. Every property carries an explicit `[JsonPropertyName]` (39-2 D8). New fields are only ever *added*; old parsers skip unknown fields.
- **D3 — Clarification is one FLAT payload with a `phase` discriminator.** `phase: "questions" | "resolution"`. `questions` stays an **array of strings** (so the old `ClarifyParsing.ParseQuestions` object shape `{"questions":[...]}` still parses it); question identity is positional: `Q-1`…`Q-n` (1-based), derived, never stored. The resolution phase adds root-level `clarifiedRequirement` / `remainingAmbiguities` / `resolved` (byte-compatible with `ClarifyParsing.ParseClarification`) plus a NEW `resolutions: [{questionId, requirement}]` array satisfying AC5's "each resolution states the clarified requirement and references its question". A nested `resolution:{...}` object was rejected because the old parser reads `clarifiedRequirement` at the root. Lifecycle progress (questions asked → answers incorporated) is carried by the 39-2 envelope state + `SupersedesDocumentId` chain — no second lifecycle (story technical note).
- **D4 — Open-endedness rule (AC5).** Baseline floor: ≥1 non-empty trimmed question (mirrors `ParseQuestions`; violation `NO_OPEN_QUESTION` when zero survive). Deliberate tightening: a deterministic closed-question detector (question starts with a yes/no auxiliary — is/are/do/does/did/can/could/will/would/should/has/have/was/were — AND contains no interrogative word or "or"-alternative) and `NO_OPEN_QUESTION` fires when **all** questions are closed-form. A question mark is not consulted. This aligns the validator with what `product_owner/clarify-requirements.md` already instructs ("open-ended (not yes/no)"); listed as a tightening in completion notes per AC6.
- **D5 — Strict closed label sets; reject, don't normalize.** `Validate` is pure over a `JsonElement` and never mutates, so the baseline's synonym folding (`SubtaskComplexities.Normalize`, `AmbiguityTypes.Normalize`, `AmbiguitySeverities.Normalize`) cannot be reproduced inside it. The validators accept exactly the canonical wires: complexity ∈ {low, medium, high}; ambiguity type ∈ {vague, missing, contradictory, implicit, unspecified} (the enumerated set `AmbiguityParsing` produces today — including `unspecified`, don't invent); severity ∈ {low, medium, high} — shipped as `[Wire]` enums (`TaskComplexity`, `AmbiguityCategory`, `AmbiguitySeverity`) per the `AgentAction.cs` pattern. Synonym spellings ("trivial", "unclear", "critical"…) become *producer-side* normalization at 39-13 migration time (the parsers already do it); the divergence is a named tightening in completion notes with cells `(senior_developer, decompose-issue)` and `(product_owner, score-ambiguity)`.
- **D6 — Reject-don't-clamp on every numeric range** (AC3 says so explicitly for Findings; applied uniformly): `relevance`/`confidence` ∈ [0,1] (`RELEVANCE_OUT_OF_RANGE`/`CONFIDENCE_OUT_OF_RANGE` — baseline read them unchecked), ambiguity `score` ∈ [0,1] (`SCORE_OUT_OF_RANGE` — baseline also rejects; parity), ambiguity `confidence` ∈ [0,1] (baseline *clamps* — tightening, listed), `estimateHours` ∈ [2,8] inclusive (`SIZING_OUT_OF_RANGE` — baseline only clamped negatives to 0 and called 2–8h "a soft guide"; the biggest deliberate tightening, several old fixtures trip it — see Test Plan).
- **D7 — Cross-parser round-trip/subsumption tests (which need the OLD `Tamma.Activities` parsers) live in `Tamma.Activities.Tests`, NOT `Tamma.Core.Tests`.** Settled decision (reconciling with 39-2's stated posture): `Tamma.Core.Tests` stays dependency-light and Docker-free — it does NOT gain a `ProjectReference` to `Tamma.Activities` (which would drag Elsa 3.5.3 + Tamma.Data transitively into the Core test project). AC7's "the *old* parser still parses" is proven where the old parsers already live: the round-trip/subsumption suite is added to `Tamma.Activities.Tests` (which already references both `Tamma.Activities` and `Tamma.Core`). The pure new-type unit tests (Validate rules, contract-token pins) stay in `Tamma.Core.Tests`. AC9's diff surface is read to admit the one cross-parser test file in `Tamma.Activities.Tests` — the plan of record, not a fallback.
- **D8 — Text-level negatives are subsumed at the deserialization boundary, not in `Validate`.** `Validate(JsonElement)` can only see parsed JSON; the baseline negatives "no json here at all" / "{ not valid json" / empty / whitespace can never *reach* it (39-2's `DocumentJson.Deserialize` / `JsonSerializer` throws first). The subsumption suite asserts these inputs throw on deserialization — a loud rejection, never a silent default — and documents the boundary mapping in the test's doc comment. JSON-shaped negatives (missing summary, empty subtasks, out-of-range score, missing rationale, all-shell items) map to named violations.
- **D9 — Invalid examples carry their expected violation codes.** AC8 requires "≥1 invalid with the expected violation codes asserted". Extend 39-2's `DocumentExample` with an additive `IReadOnlyList<string> ExpectedViolationCodes` (empty for valid examples) and strengthen the registry drift loop to assert each invalid example's `Validate` emits exactly those codes. If 39-2 lands first without the field, this is a small additive edit inside `Tamma.Core/Documents/` — a declared lockstep item, coordinated on whichever PR lands second.
- **D10 — Violation codes are `public const string` on each `<Name>DocumentType`,** SCREAMING_SNAKE_CASE, XML-documented as platform vocabulary (39-9's repair ring feeds them back to the model). Cycle detection reports the actual cycle path in `DocumentViolation.Message` (e.g. `"Cyclic dependsOn: ST-2 -> ST-4 -> ST-2"`) — the canonical "domain-phrased violation". Acyclicity failure emits BOTH `CYCLIC_DEPENDS_ON` (naming members) and `NO_PREREQUISITE_ORDER` (the stable downstream signal AC2 demands for Stories 2-15/2-16).

## Implementation Steps

1. **Precondition — 39-2 core in tree.** Verify `apps/tamma-elsa/src/Tamma.Core/Documents/` exists with `IDocumentType`, `DocumentValidationResult`, `DocumentViolation`, `DocumentExample`, `DocumentTypeRegistry`, `DocumentTypeKey`, `DocumentJson` per the 39-2 plan. If not landed, land 39-2 first — nothing here compiles without it.

2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Decomposition.cs`** — records mirror `DecompositionModels.cs` shapes (immutable records instead of mutable classes), plus the document type:

   ```csharp
   namespace Tamma.Core.Documents.Types;
   public enum TaskComplexity { [Wire("low")] Low, [Wire("medium")] Medium, [Wire("high")] High }
   public sealed record DecompositionTask
   {
       [JsonPropertyName("id")] public required string Id { get; init; }
       [JsonPropertyName("title")] public string Title { get; init; } = "";
       [JsonPropertyName("description")] public string Description { get; init; } = "";
       [JsonPropertyName("acceptanceCriteria")] public string AcceptanceCriteria { get; init; } = "";
       [JsonPropertyName("estimateHours")] public decimal EstimateHours { get; init; }
       [JsonPropertyName("complexity")] public string Complexity { get; init; } = "medium";
       [JsonPropertyName("dependsOn")] public IReadOnlyList<string> DependsOn { get; init; } = [];
   }
   public sealed record Decomposition
   {
       [JsonPropertyName("summary")] public required string Summary { get; init; }
       [JsonPropertyName("subtasks")] public required IReadOnlyList<DecompositionTask> Subtasks { get; init; }
   }
   public sealed class DecompositionDocumentType : IDocumentType
   {
       public const string MissingSummary = "MISSING_SUMMARY";        // baseline: fail-closed no summary
       public const string NoTasks = "NO_TASKS";                       // baseline: fail-closed empty subtasks
       public const string TaskMissingId = "TASK_MISSING_ID";          // baseline: shell drop → now loud
       public const string TaskEmptyShell = "TASK_EMPTY_SHELL";        // no title AND no description
       public const string DuplicateTaskId = "DUPLICATE_TASK_ID";      // baseline kept-first → now loud
       public const string DanglingDependsOn = "DANGLING_DEPENDS_ON";  // baseline pruned → now loud
       public const string SelfDependsOn = "SELF_DEPENDS_ON";          // baseline pruned → now loud
       public const string CyclicDependsOn = "CYCLIC_DEPENDS_ON";      // NEW (baseline deferred to 2-15)
       public const string NoPrerequisiteOrder = "NO_PREREQUISITE_ORDER"; // NEW, stable topo signal
       public const string SizingOutOfRange = "SIZING_OUT_OF_RANGE";   // NEW: 2–8h hard rule
       public const string UnknownComplexity = "UNKNOWN_COMPLEXITY";   // baseline normalized → now loud
       // Key = "decomposition" (DocumentTypeKey.Decomposition wire), SchemaVersion = 1,
       // PayloadClrType = typeof(Decomposition); Validate / RenderContract / Examples per below.
   }
   ```

   `Validate` deserializes via `DocumentJson.Options` (a `JsonException` → a single `MALFORMED_PAYLOAD` violation, never a throw out of `Validate`), then runs the domain checks. **Extract the graph checks UP FRONT into a shared helper `apps/tamma-elsa/src/Tamma.Core/Documents/Types/DependencyGraphCheck.cs`** (rather than inlining them in `DecompositionDocumentType`), so 39-4's `Plan` type reuses ONE copy instead of creating a second: `internal static class DependencyGraphCheck { internal static List<DocumentViolation> Check(IReadOnlyList<(string Id, IReadOnlyList<string> DependsOn)> nodes, DependencyGraphCodes codes); }` — iterative DFS with an explicit stack over the id→dependsOn adjacency detects duplicate ids / dangling / self / back-edges and walks the stack to render the cycle path (D10); Kahn's algorithm asserts topological order. The per-type violation code strings (Decomposition: `DUPLICATE_TASK_ID`/`DANGLING_DEPENDS_ON`/`SELF_DEPENDS_ON`/`CYCLIC_DEPENDS_ON`/`NO_PREREQUISITE_ORDER`; Plan: the same shapes with its own code constants) are passed in via `DependencyGraphCodes` so each document type keeps its own vocabulary. **Handoff:** 39-4 CONSUMES this helper (its step 4 becomes "skip — 39-3 shipped `DependencyGraphCheck`"); whichever of 39-3/39-4 lands first owns the file, the other reuses it — no duplicate cycle-detection implementation.

3. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs`** — `Finding` record (`title`, `summary`, `relevance`, `confidence`, `citations`, optional `rank`), `Findings` record (`topic`, `summary`, `Items` → `[JsonPropertyName("findings")]`, `overallConfidence`), `FindingsDocumentType` with codes `MISSING_SUMMARY`, `EMPTY_FINDINGS` (XML doc: *inherited baseline choice — `ResearchParsing` fails closed on an empty findings list, so an empty list is a violation, not a valid "nothing found"*), `FINDING_EMPTY_SHELL` (no title and no summary), `MISSING_EVIDENCE` (empty `citations` — AC3's evidence rule; tightening, baseline never required citations), `RELEVANCE_OUT_OF_RANGE`, `CONFIDENCE_OUT_OF_RANGE` (incl. `overallConfidence`), `DUPLICATE_RANK` / `PARTIAL_RANKS` (rank rule: either NO finding carries `rank` — list order is the ranking, the baseline behavior — or ALL do, with no duplicates).

4. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Types/AmbiguityAssessment.cs`** — `[Wire]` enums `AmbiguityCategory` (vague/missing/contradictory/implicit/unspecified) and `AmbiguitySeverity` (low/medium/high); `AmbiguityConcern` record (`type`, `description`, `severity`, `recommendation`); `AmbiguityAssessment` record (`score`, `rationale`, `confidence`, `ambiguities`); `AmbiguityAssessmentDocumentType` with codes `SCORE_OUT_OF_RANGE`, `MISSING_RATIONALE`, `CONFIDENCE_OUT_OF_RANGE`, `UNKNOWN_AMBIGUITY_TYPE`, `UNKNOWN_SEVERITY`, `AMBIGUITY_EMPTY_SHELL` (no description). **A score-any + empty `ambiguities` list is valid** (AC4 — mirror `ParseAssessment_ClearRequirement_EmptyBreakdown_IsValid`); the validator must not require items.

5. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Clarification.cs`** — per D3:

   ```csharp
   public enum ClarificationPhase { [Wire("questions")] Questions, [Wire("resolution")] Resolution }
   public sealed record QuestionResolution
   {
       [JsonPropertyName("questionId")] public required string QuestionId { get; init; }  // "Q-3" (positional)
       [JsonPropertyName("requirement")] public required string Requirement { get; init; } // the clarified statement
   }
   public sealed record Clarification
   {
       [JsonPropertyName("phase")] public required string Phase { get; init; }
       [JsonPropertyName("questions")] public IReadOnlyList<string> Questions { get; init; } = [];
       [JsonPropertyName("clarifiedRequirement")] public string? ClarifiedRequirement { get; init; }
       [JsonPropertyName("resolutions")] public IReadOnlyList<QuestionResolution> Resolutions { get; init; } = [];
       [JsonPropertyName("remainingAmbiguities")] public IReadOnlyList<string> RemainingAmbiguities { get; init; } = [];
       [JsonPropertyName("resolved")] public bool Resolved { get; init; }
   }
   ```

   `ClarificationDocumentType` codes: `UNKNOWN_PHASE`; questions phase → `NO_OPEN_QUESTION` (D4), `EMPTY_QUESTION`; resolution phase → `MISSING_CLARIFIED_REQUIREMENT` (root, mirrors `ParseClarification`'s load-bearing field), `EMPTY_RESOLUTION` (a resolution with empty `requirement`), `UNKNOWN_QUESTION_REF` (a `questionId` outside `Q-1`…`Q-n` of the payload's `questions` — AC5's unknown-id violation). XML doc records the D4 open-endedness rule verbatim.

6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** — append the four instances to the compile-time registration list the 39-2 static ctor builds from (the seam 39-2's plan explicitly left for this story). If D9's `ExpectedViolationCodes` is not yet on `DocumentExample`, **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentExample.cs`** additively here.

7. **MODIFY the 39-2 drift tests** (conscious pin bumps, AC1): `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/DocumentTypeRegistryTests.cs` — registered-count pin 0 → 4 (narrative comment: "+6 in 39-4"); the `Resolve(DocumentTypeKey.Decomposition)`-throws-`NOT_REGISTERED` assertion moves to a still-unregistered key (e.g. `Plan`). `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs` — remove the four keys from the `PendingImplementations` ratchet (leaving 6).

8. **CREATE per-type unit tests** (see Test Plan): `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Types/DecompositionDocumentTypeTests.cs`, `FindingsDocumentTypeTests.cs`, `AmbiguityAssessmentDocumentTypeTests.cs`, `ClarificationDocumentTypeTests.cs`.

9. **CREATE the cross-parser tests in `Tamma.Activities.Tests`, NOT `Tamma.Core.Tests`** (D7): **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Documents/Types/BaselineSubsumptionTests.cs`** and **`RoundTripCompatibilityTests.cs`**. `Tamma.Core.Tests` does NOT gain a `ProjectReference` to `Tamma.Activities` — it stays dependency-light/Docker-free per 39-2's posture. `Tamma.Activities.Tests` already references both `Tamma.Activities` (the old parsers) and `Tamma.Core` (the new types), so these suites reference the old parsers directly and the `*ParsingTests.cs` fixture constants can be reused in-assembly (no verbatim copy needed).

10. **CREATE `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Types/RenderContractTokenTests.cs`** — pins, per type, the quoted-token list copied from `ContractBindingTests.Bindings` (with a cross-reference comment; the map is private so tokens are duplicated deliberately — 39-16 later collapses this duplication by *generating* the contract).

11. **Author `docs/stories/epic-39/story-39-3/completion-notes.md`** — the AC6 divergence table: every deliberate tightening (sizing 2–8h, dangling/self/duplicate now loud, evidence required, ranges rejected not clamped/normalized, label sets strict, open-endedness) with the affected prompt cells named (`senior_developer/decompose-issue`, `product_owner/research`, `product_owner/score-ambiguity`, `product_owner/clarify-requirements`, `product_owner/incorporate-answers`).

12. **Verify AC9 by construction**: `git status` shows only `Tamma.Core/Documents/**`, `Tamma.Core.Tests/**`, the two cross-parser test files added under `Tamma.Activities.Tests/Documents/Types/**` (D7 — the deliberate exception, no csproj/ProjectReference change), and this story's docs directory; `dotnet test apps/tamma-elsa/tests/Tamma.Core.Tests` and `apps/tamma-elsa/tests/Tamma.Activities.Tests` both green (the latter proves `ContractBindingTests` and the parser tests are undisturbed).

## Data & Migrations

None. Persistence is Story 39-11; these types are storage-free (`Tamma.Core` only).

## Events

None emitted or consumed. `DOCUMENT.*` constants are 39-6 scope; the baseline `DECOMPOSITION.*` / `RESEARCH.*` / `AMBIGUITY.*` / `CLARIFY.*` events remain untouched with their workflows (AC9).

## Test Plan

All NUnit + FluentAssertions in `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Types/`; no Testcontainers (everything is pure). Style precedent: the baseline `*ParsingTests.cs` (fixture constants + reasoned assertion messages).

- **`DecompositionDocumentTypeTests`** (AC2): valid graph passes; duplicate id → `DUPLICATE_TASK_ID`; dangling ref → `DANGLING_DEPENDS_ON` naming the missing id; self-ref → `SELF_DEPENDS_ON`; 2-node and 3-node cycles → `CYCLIC_DEPENDS_ON` with the cycle path in the message AND `NO_PREREQUISITE_ORDER` present; estimate 1.5h / 9h / 0h / missing → `SIZING_OUT_OF_RANGE` (2h and 8h pass — inclusive bounds); `"Trivial."` complexity → `UNKNOWN_COMPLEXITY` (strict, D5); missing summary / empty subtasks → `MISSING_SUMMARY` / `NO_TASKS`.
- **`FindingsDocumentTypeTests`** (AC3): finding without citations → `MISSING_EVIDENCE`; relevance 1.5 / confidence −0.2 → out-of-range violations (rejected, not clamped); duplicate explicit ranks → `DUPLICATE_RANK`; some-but-not-all ranks → `PARTIAL_RANKS`; no ranks at all → valid (order is rank, baseline parity); empty findings list → `EMPTY_FINDINGS` (the documented inherited baseline choice).
- **`AmbiguityAssessmentDocumentTypeTests`** (AC4): score 0.0 valid (mirrors `ParseAssessment_ScoreZero_IsValid`); **low score + empty ambiguities valid** (mirrors `ParseAssessment_ClearRequirement_EmptyBreakdown_IsValid`); score 1.5/−0.2 → `SCORE_OUT_OF_RANGE`; missing rationale → `MISSING_RATIONALE`; `"unclear"` type → `UNKNOWN_AMBIGUITY_TYPE` (strict; the closed set pinned member-by-member = exactly what `AmbiguityTypes` enumerates: vague, missing, contradictory, implicit, unspecified); item without description → `AMBIGUITY_EMPTY_SHELL`.
- **`ClarificationDocumentTypeTests`** (AC5): questions phase with one open question valid; zero questions → `NO_OPEN_QUESTION`; all-closed-form set ("Is it fast?", "Should we ship?") → `NO_OPEN_QUESTION`; mixed set valid (D4's conservative rule); resolution phase without root `clarifiedRequirement` → `MISSING_CLARIFIED_REQUIREMENT`; resolution with empty `requirement` → `EMPTY_RESOLUTION`; `questionId: "Q-9"` against 3 questions → `UNKNOWN_QUESTION_REF`; bad `phase` → `UNKNOWN_PHASE`.
- **`BaselineSubsumptionTests`** (AC6): for each type, feeds every negative case from the corresponding `*ParsingTests.cs` (copied constants: `noSummary`, `emptySubtasks`, `allShells`, `emptyFindings`, `noScore`, `badScore`, out-of-range scores, `noRationale`, …) and asserts the typed path also rejects — JSON-shaped negatives via named `Validate` violations, text-level negatives via deserialization failure (D8's documented boundary). Also asserts the *lenient-spelling* divergences: inputs the baseline accepted by normalizing/pruning (`"Trivial."`, dangling `ST-99`, negative hours) now produce violations — each assertion message cites the completion-notes entry, making the AC6 list executable.
- **`RoundTripCompatibilityTests`** (AC7): for every fixture the baseline tests parse successfully (`ValidDecomposition`, `TemplateShapedDecomposition`, `messy`, `withBadDeps`, `withShells`, `ValidReport`, `TemplateShapedReport`, `noOverall`, `noTopic`, `withShell`, `ValidAssessment`, `TemplateShapedAssessment`, `clear`, `zero`, `noConf`, plus clarify samples shaped exactly per the two prompt templates): slice first-`{`/last-`}` (the `JsonSlice` idiom), deserialize into the typed payload with `DocumentJson.Options`, run `Validate` — assert either valid or *exactly* the expected documented-tightening codes (e.g. `withBadDeps` → dangling/self/sizing; `messy` → sizing + unknown-complexity) — then re-serialize and assert the **old** parser (`DecompositionParsing.ParseDecomposition` / `ResearchParsing.ParseReport` / `AmbiguityParsing.ParseAssessment` / `ClarifyParsing.ParseQuestions` + `ParseClarification`) returns non-null with key fields intact. This is the 39-12/39-13 transition-window proof.
- **`RenderContractTokenTests`** (AC8): per type, `RenderContract()` contains every quoted token its bound cells pin in `ContractBindingTests.Bindings` (decomposition 9 tokens; findings 7; ambiguity 8; clarification: the phrase `JSON array` + `"clarifiedRequirement"`, `"remainingAmbiguities"`, `"resolved"`) and every field its own validator checks; called twice → identical (determinism, 39-16's seam).
- **Registry drift (MODIFIED 39-2 tests)** (AC1): count pin = 4; the 39-2 part-(b) loop now bites live — key parses, contract non-empty, ≥1 valid + ≥1 invalid example, valid examples pass `Validate`, invalid examples emit exactly their `ExpectedViolationCodes` (D9).

## Definition of Done

| AC | Satisfied by | Verified by |
|---|---|---|
| 1 — four types registered, +4 pin bump, 39-2 drift green | Steps 2–7 | `DocumentTypeRegistryTests` (pin 4 + example loop), `WorkflowInterfaceGraphTests` (ratchet −4) |
| 2 — Decomposition rules (ids, dangling/self/cycle+path, 2–8h, topo signal) | Step 2 | `DecompositionDocumentTypeTests` |
| 3 — Findings rules (evidence, [0,1] rejected, ranked, empty-list inheritance) | Step 3 | `FindingsDocumentTypeTests` |
| 4 — AmbiguityAssessment rules (score range, closed set, clear+empty valid) | Step 4 | `AmbiguityAssessmentDocumentTypeTests` |
| 5 — Clarification two-phase rules (open question, resolution refs) | Step 5 | `ClarificationDocumentTypeTests` |
| 6 — fail-closed subsumption + divergences listed | Steps 8, 9, 11 | `BaselineSubsumptionTests` + `completion-notes.md` table (cross-cited) |
| 7 — round-trip: fixtures → typed → old parser still parses | Step 9 | `RoundTripCompatibilityTests` |
| 8 — contracts carry validator fields + binding tokens; ≥2 examples w/ codes | Steps 2–6, 10 | `RenderContractTokenTests` + registry example loop |
| 9 — no parser deletion, no rewiring, bounded diff | Steps 1–12 (nothing else planned) | Step 12 `git status` inspection + full `Tamma.Activities.Tests` run green |

## Dependencies & Sequencing

- **Hard prerequisite: 39-2** (`IDocumentType`, `DocumentValidationResult`, `DocumentExample`, `DocumentTypeRegistry`, `DocumentJson`). Not yet in tree — this story cannot start until it lands. Follow its implementation plan's names; if the landed shape differs, this plan's sketches adapt to it, not vice versa. Lockstep: the 0→4 count pin, the `PendingImplementations` ratchet, and D9's `DocumentExample.ExpectedViolationCodes`.
- **Soft prerequisite: 39-1 audit** (informal consumers of the four shapes). Its deliverable does not exist yet; this story does not block on it — the four parsers' callers are already named in the story and verified present. Any extra consumer the audit surfaces affects 39-13 migration, not these type definitions.
- **Nothing pulled forward from later stories**: no lifecycle (39-6), no events, no persistence (39-11), no prompt regeneration (39-16 — `RenderContract` only has to *agree* with today's templates, enforced by token pins). No stubs/fakes are needed beyond the copied fixture constants: the old parsers are real code referenced directly (D7).
- **Feeds**: 39-12 (pilot consumes `Decomposition`), 39-13 (family migration consumes all four + the producer-side normalization noted in D5), 39-16 (contract generation), 39-4 (repeats this recipe for batch 2).

## Risks & Mitigations

- **The 2–8h sizing rule invalidates real historical outputs** (fixtures without estimates score 0h). Mitigation: it is an explicit AC2 rule; round-trip tests pin the expected violations so the tightening is visible, and the 39-9 repair ring is the designed consumer of `SIZING_OUT_OF_RANGE`. If pilot 39-12 shows excessive repair churn, relaxing to a warning is a one-line policy change *there*, not here.
- **Strict label sets reject spellings the lenient parsers folded.** Mitigation: divergence table (Step 11) + subsumption assertions make every rejection deliberate and named; 39-13 reuses the existing `*.Normalize` helpers producer-side before payload construction, so live traffic is normalized before `Validate` ever sees it.
- **Keeping `Tamma.Core.Tests` dependency-light while still proving old-parser round-trip.** Mitigation (D7): the cross-parser round-trip/subsumption suites live in `Tamma.Activities.Tests` (which already references the old parsers + `Tamma.Core`), so `Tamma.Core.Tests` gains NO `ProjectReference` and stays Docker-free/Elsa-free; the pure new-type unit tests stay in `Tamma.Core.Tests`.
- **Clarification's flat two-phase shape guesses wrong for 39-6's revise loop.** Mitigation: the phase discriminator + positional question ids are the minimal structure satisfying AC5 and old-parser round-trip simultaneously; envelope-level `SupersedesDocumentId` (39-2 D4) carries progression, so the payload shape can gain fields additively without breaking either consumer.
- **Divergence list drifts from reality.** Mitigation: `BaselineSubsumptionTests` asserts each listed tightening *as a test*, so a stale completion-notes entry has a failing/red-flagging twin in CI.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 2 | `Decomposition` type + graph checks (cycle path rendering, Kahn) | 0.75 |
| 3–4 | `Findings` + `AmbiguityAssessment` types (+ `[Wire]` enums) | 0.75 |
| 5 | `Clarification` two-phase type | 0.5 |
| 6–7 | Registry registration, `DocumentExample` extension, 39-2 pin/ratchet bumps | 0.5 |
| 8 | Four per-type unit test classes | 1.0 |
| 9 | Subsumption + round-trip suites (in `Tamma.Activities.Tests`, reusing parser fixtures in-assembly) | 0.75 |
| 10–12 | Contract token pins, completion-notes table, AC9 verification, polish | 0.75 |
| **Total** | | **5.0** (story estimate: 4–5 days) |

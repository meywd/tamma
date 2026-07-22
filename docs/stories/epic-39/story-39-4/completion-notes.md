# Story 39-4 — Completion Notes (Document Types Batch 2)

Vocabulary complete: `DocumentTypeRegistry.All` now holds all **10** types (39-3's 4 + this story's 6: `Plan`, `Design`, `Review`, `TriageDecision`, `Diagnosis`, `TestSpec`). The `WorkflowInterfaceGraphTests` `PendingImplementations` ratchet is empty.

## Files created / modified

**Created (`src/Tamma.Core/Documents/Types/`):** `Review.cs`, `ReviewDocumentType.cs`, `Plan.cs`, `Design.cs`, `TriageDecision.cs`, `Diagnosis.cs`, `TestSpec.cs`.
**Modified:** `src/Tamma.Core/Documents/DocumentTypeRegistry.cs` (appended 6 → 10).
**Modified tests (`tests/Tamma.Core.Tests/Documents/`):** `DocumentTypeRegistryTests.cs` (pin 4 → 10; the NOT_REGISTERED-via-`plan` case became "every vocabulary key now resolves", since the vocabulary is complete), `WorkflowInterfaceGraphTests.cs` (ratchet emptied).
**Created pure tests (`tests/Tamma.Core.Tests/Documents/Types/`):** `ReviewTypeTests.cs`, `ReviewLegacyCompatTests.cs`, `PlanTypeTests.cs`, `DesignTypeTests.cs`, `TriageDecisionTypeTests.cs`, `DiagnosisTypeTests.cs`, `TestSpecTypeTests.cs`.
**Created cross-parser tests (`tests/Tamma.Activities.Tests/Documents/Types/`, D8 exception):** `ReviewCrossParserTests.cs`, `PlanCrossParserTests.cs`, `DesignCrossParserTests.cs`, `TriageDecisionCrossParserTests.cs`, `DiagnosisCrossParserTests.cs`.

No parser/helper/workflow/prompt/`*.csproj` was edited or deleted; no `ProjectReference` added to `Tamma.Core.Tests` (AC9 satisfied).

## Deliberate tightenings vs. the baselines

| Type | Baseline behaviour | 39-4 behaviour (divergence) | Affected producer / prompt cell |
|---|---|---|---|
| `Review` | `ParseRoleVerdict` / `TaskReviewWorkflow` launder parse failure into a pessimistic `"concerns"` default | `Review.FromLegacyVerdictJson` throws `DOCUMENT.REVIEW.LEGACY_UNPARSEABLE` (garbage / no verdict) or `DOCUMENT.REVIEW.UNKNOWN_DECISION` — never a defaulted document; the pessimistic-default question is settled by the lifecycle (repair ring → ValidationExhausted), not the type | `plan-review` family, `task-review`, `code-review` |
| `Review` | object-verdict `blockingIssues[]` are folded into a comment string with no fix | ingested as `Critical` issues with an **empty** `suggestedFix`, which then FAILS `Validate` (`ISSUE_MISSING_FIX`) — incomplete legacy content goes to repair, it is not laundered (D4) | `plan-review` family |
| `Review` | severity spellings vary (`critical|major|minor|suggestion`, `style`, `info`, `blocker`) | closed `ReviewSeverity {Critical,Major,Minor,Suggestion}`; legacy read aliases `style`/`info`→`Suggestion`, `blocker`→`Critical` (D2); blocking = `Critical` only | `code-review`, plan-review cells |
| `Review` | forked verdict vocabularies | closed `ReviewDecision {Approve,RequestChanges,NeedsDiscussion}`; `concerns`→`RequestChanges`, `COMMENT`→`NeedsDiscussion` (D1) | all review cells |
| `Review` | (no such rule existed anywhere) | **AC3 flagship**: `decision=approve` while any issue is `Critical` → `APPROVE_WITH_BLOCKING_ISSUES` (names the blocking issues) — the forked-verdict bug state is now unrepresentable | — |
| `Plan` | `ValidatePlan` checks only a ROOT `tasks|steps` + ROOT `fileMap|files|filesToModify` | per-task file map (`TASK_MISSING_FILE_MAP`), per-task testing (`TASK_MISSING_TESTING`), and full dependency-graph checks (dangling/self/cyclic/no-topological-order) via the shared `DependencyGraphCheck`; root `files` kept verbatim (D5) so round-trip still passes `ValidatePlan` | `plan-system-design`, `create-tasks` |
| `Design` | `ParseProposal` fail-closes only on missing `summary` | additionally: ≥1 alternative (`NO_ALTERNATIVES`), per-alternative trade-offs (`ALTERNATIVE_MISSING_TRADEOFFS`), and `recommendedAlternativeId` must match a listed alternative id (`RECOMMENDATION_UNKNOWN_ALTERNATIVE`) — additive `id` field ignored by the old reader (D7) | `propose-design` |
| `TriageDecision` | `TriagePoDecisionHelper` **clamps** out-of-vocab values to a safe default and appends a note | out-of-vocab is a **violation** (`OUT_OF_VOCABULARY`, names field+value), never a silent clamp — the clamp-and-flag behaviour moves to the visible repair/review layer (AC6). The helper's `llm-failed`/`unparsed`/`skipped` status markers do NOT enter the payload — they stay lifecycle outcomes | `triage-intake` |
| `TriageDecision` | — | **Prompt divergence recorded (no change made):** the shipped `Prompts/product_owner/triage-intake.md` instructs a DIFFERENT vocabulary (`P0..P3`, `severity`, `ownerRole`) that the helper already clamps away today. This story defines the type from the Story 26-1 vocabulary per AC6 (story wins); reconciling the prompt is 39-15/39-16 migration scope. Read aliases `critical`→`Urgent`, `medium`→`Normal` preserve the helper's documented synonym folds (D6) | `triage-intake` (unchanged here) |
| `Diagnosis` | `ParseDiagnosisResponse` reads snake_case and never range-checks confidence | camelCase canonical wire; range check `confidence ∈ [0,1]` (rejected, not clamped), unique ranks, rank/confidence order consistency, fix-must-name-files. The snake_case bridge lives ONLY in `FromLegacyJson`/`ToLegacyJson` (D4) — a camelCase re-serialization would "parse" into an empty gate-failing result | `debugging`, `blocker-diagnosis` |
| `TestSpec` | `TestCaseCreationWorkflow` inline-validate accepts a non-empty `testCases|tests` array | serializes under `testCases` (accepted token); adds `EMPTY_TEST_SPEC` (subsumes the non-empty requirement), per-case `taskId` + single `behavior`, and duplicate `(taskId, behavior)` collision flagging (`DUPLICATE_CASE_FOR_BEHAVIOR`) | `write-tests` |

## Notes on posture

- **Canonical wire is camelCase everywhere** (39-2 D8). The only snake_case in this story is inside the paired `Diagnosis.FromLegacyJson`/`ToLegacyJson` reader/writer, pinned by tests.
- **`Review` enums** carry `[JsonConverter(typeof(WireEnumJsonConverter<…>))]` so the closed vocabulary (de)serializes through `DocumentJson.Options`; an out-of-vocab decision/severity wire fails deserialization → a single `MALFORMED_PAYLOAD` violation (never a throw out of `Validate`).
- **`TriageDecision`** validates over the raw `JsonElement` (no deserialize), so a non-object payload is `MALFORMED_PAYLOAD` and each classification field yields a precise `OUT_OF_VOCABULARY` naming the field + offending value.
- **`DependencyGraphCheck`** is reused, not duplicated (D9) — `Plan` passes its own `DependencyGraphCodes`.
- **Aggregation is out of scope** (39-7): `Review` models a single reviewer's review; the optional `aggregatedFrom` field is defined here (inside 39-7's diff surface) and validated (non-empty + duplicate-free when present).

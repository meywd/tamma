# Implementation Plan — Story 39-16: Prompt Contracts Generated From Document Types (Single Source)

## Scope & Deliverable

When this story is done, the output-contract block inside every producing prompt cell (`apps/tamma-elsa/src/Tamma.Api/Prompts/{role}/{action}.md`) is machine-generated from the bound document type's `RenderContract()` — delimited by explicit `<!-- BEGIN GENERATED CONTRACT: {type-key} v{n} --> … <!-- END GENERATED CONTRACT -->` markers — by a repeatable dotnet CLI tool (`apps/tamma-elsa/tools/Tamma.PromptContractGen/`). `ContractBindingTests` is flipped: the hand-maintained token-group `Bindings` map is deleted and replaced by byte-equality between each cell's generated region and the freshly rendered contract of the type its lifecycle binding declares it produces; the dispatched-pair coverage guard survives unchanged. Golden-file tests pin one rendered block per registered document type, and the allowlist shrinks to prose-only cells plus a remove-only ratchet of not-yet-migrated stragglers. Prompt/parser drift becomes structurally impossible (one source), not merely caught (two sources compared).

## Pre-Reading

- `docs/stories/epic-39/story-39-16/39-16-prompt-contracts-generated-from-document-types.md` — the story (ACs are source of truth)
- `docs/stories/epic-39/README.md` — "Supersedes / absorbs" (this story kills the token map), "Prose stays prose" (the AC5 allowlist class), "Vocabulary static, composition dynamic"
- `docs/guides/BEFORE_YOU_CODE.md` — mandatory process
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the mechanism being flipped: `Bindings` token-group map (dies), `KnownContractViolations` ratchet (discipline reused), `IntentionallyUnbound` (restructured), `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` (retained)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — `EnumerateAllDispatchPairs` + `DispatchPair` (internal, reused by the coverage guard), the `ExpressionExecutionContext` materialization machinery, `MinExpectedDispatchPairs` tripwire style
- `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs` — front-matter format (body is verbatim after the closing `---`; markers ride inside the body untouched), fail-loud `PROMPT.SEED.*` codes; behavior is UNCHANGED by this story
- `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs` — `PromptTemplate` record + `GetRoleAction` (the equality test reads templates through this, not the file system)
- `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/decompose-issue.md` — a representative bound cell (its "Return ONLY a single JSON object…" section is what gets wrapped in markers and regenerated)
- `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj` (~L70) — the `EmbeddedResource` + `LogicalName` pattern (precedent for embedding golden files in the test project)
- `.gitattributes` — `Prompts/**/*.md text eol=lf` pin already exists (byte-equality prerequisite); extend for goldens
- Sibling plans this story compiles against (contracts, no code in tree yet): `docs/stories/epic-39/story-39-2/implementation-plan.md` (`IDocumentType.RenderContract()`/`SchemaVersion`, `DocumentTypeKey`, `DocumentTypeRegistry.Resolve/All`), `story-39-3/implementation-plan.md` + `story-39-4/implementation-plan.md` (the 10 type implementations; 39-3 step 10's `RenderContractTokenTests` which this story retires), `story-39-6/implementation-plan.md` (D2 `producerRole`/`producerAction`/`documentType` lifecycle inputs), `story-39-7/implementation-plan.md` (D3/D9 `ReviewerSelectionHelper.AllDispatchablePairs` — reviewer cells → `Review`), `story-39-12/implementation-plan.md` (D5 lifecycle-binding dispatch walk; the pilot `produces` binding), `story-39-13/implementation-plan.md` + `story-39-14/implementation-plan.md` (family bindings)
- NOT FOUND (planned by prerequisite stories, no code in tree yet): `apps/tamma-elsa/src/Tamma.Core/Documents/` (39-2/39-3/39-4: `IDocumentType`, `DocumentTypeKey`, `DocumentTypeRegistry`, the type classes), `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` (39-6), the rewritten lifecycle bindings (39-12..39-15), `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs` (39-7), `docs/stories/epic-39/story-39-15/implementation-plan.md` (not yet authored). All story-referenced paths that name existing code were verified present.

## Design Decisions

- **D1 — The binding source is code the workflows already own; no third map.** A cell is "bound" to a type iff (a) a compiled lifecycle binding workflow dispatches `DefinitionId "document-lifecycle"` with constant, materializable `producerRole`/`producerAction`/`documentType` inputs (the 39-12 D5 walk, extended to also materialize `documentType`), or (b) the cell is in 39-7's `ReviewerSelectionHelper.AllDispatchablePairs` (7 document-review + 5 diff-review pairs), all bound to `DocumentTypeKey.Review` — reviewer cells produce validated `Review` envelopes through the single-reviewer producer. Both sources are production code; the generator and the flipped test walk the SAME merged set, so there is nothing hand-maintained to drift.
- **D2 — Extract the lifecycle-binding walk into `Tamma.ElsaServer` so tool and test share one implementation.** 39-12 D5 plans its walk inside `TaxonomyDriftBuildTests` (test assembly) — a CLI tool must not reference a test project. Ship `Tamma.ElsaServer/Workflows/Introspection/LifecycleProducerBindingScanner.cs` (public static; reflection over compiled `WorkflowBase` subclasses + `Input` delegate materialization, the `TaxonomyDriftBuildTests` technique) returning `(Workflow, DispatchId, Role, Action, DocumentTypeKey)` triples, and have the test's lifecycle walk delegate to it (projecting away `documentType` for `EnumerateAllDispatchPairs`). Coordinate with the 39-12 owner: if 39-12 lands first with the walk in-test, this story moves it; if this story's scanner lands first, 39-12 consumes it. Tension noted: 39-12's plan text places the walk in-test — the story-file requirement here ("generator walks THAT binding") forces the shared location; behavior is identical.
- **D3 — Region grammar and renderer live in `Tamma.Core/Documents/PromptContractRegion.cs`, pure and file-system-free.** Marker lines: `<!-- BEGIN GENERATED CONTRACT: {key} v{version} -->` / `<!-- END GENERATED CONTRACT -->`, where `{key}` is the `DocumentTypeKey` wire string (kebab) and `{version}` is `IDocumentType.SchemaVersion` (AC7). The story's example uses `Decomposition v1`; it is prefixed "e.g." — the wire key is chosen so the marker parses through `DocumentTypeKeyExtensions.Parse` with zero casing ambiguity. The full generated region is `begin-marker + "\n" + RenderContract() + "\n" + end-marker`, LF-only. Exactly one region per bound cell; zero or ≥2 is a structural error. Markers are inert HTML comments: `PromptFileLoader` passes them through verbatim (AC2), and they ride into the rendered prompt — harmless to the model, and deliberate (the loaded `PromptTemplate.Template` is what the equality test extracts from, so no file-system access in tests).
- **D4 — Generator core is pure in `Tamma.Core`; the CLI is a thin I/O shell.** `PromptContractGenerator.Run(files, bindings)` takes `IReadOnlyDictionary<string /*relative path*/, string /*content*/>` + `IReadOnlyDictionary<(string Role, string Action), IDocumentType>` and returns rewritten contents + typed errors (missing markers on a bound cell; markers on an unbound cell; marker key ≠ bound type). The tool project wires: locate the Prompts dir (arg or git-root walk-up), collect bindings via `PromptContractBindingSource` (D1), run the core, write only changed files, exit 1 on any error. This makes AC3's idempotency and fail-loud clauses unit-testable in `Tamma.Core.Tests` without a tool-test project.
- **D5 — The equality check is a shared pure function so AC6's "drift is impossible" is demonstrable.** `PromptContractRegion.CheckEquality(templateBody, IDocumentType)` returns violation strings (no region / version-marker mismatch / byte diff at index N, with the offending line quoted) and every message embeds `PromptContractRegion.GeneratorCommand` (`dotnet run --project apps/tamma-elsa/tools/Tamma.PromptContractGen`). The flipped test loops it over real templates; the AC6 tests drive it with (a) a fake `IDocumentType` wrapping a real one but with an altered `RenderContract()`/`SchemaVersion` against the real checked-in template, and (b) the real type against a tampered template copy. No reflection-mutation tricks needed.
- **D6 — Allowlist splits into two tables with different lifetimes (AC5).** `ProseOnlyCells` (permanent, justified — free-text/success-flag/lenient consumers surviving from today's `IntentionallyUnbound`: tech-writer prose, `implement-fix`/`debug`/`address-review-comments`, `context-scan` family, `mentor-feedback`, `summarize-*`, `resolve-blocker`) and `PendingMigrationCells` (remove-only ratchet: cells with fail-closed parsers whose family migration has not landed, e.g. `triage-intake` and the write-tests/create-tasks/diagnosis cells if 39-15 lags). Staleness check mirrors `KnownContractViolations`: a pending entry whose pair now appears in the D1 binding set fails until deleted; entries may only ever be removed.
- **D7 — Golden files are checked in, embedded, and refreshed by the same generator run (AC1).** `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/ContractGoldens/{key}.v{n}.md`, embedded via the `Tamma.Api.csproj` `LogicalName` pattern. The tool refreshes goldens alongside cells (`--goldens` dir, default resolved), so a type change fans out to cells + goldens in one command; the golden test is the reviewed-diff freshness gate ("same trade-off as EF migrations", per the story). Version-stamped filenames make a `SchemaVersion` bump orphan the old golden — a stale-golden check forces its deletion.
- **D8 — AC8 lands as a two-commit PR.** Commit A: markers inserted, cells regenerated, generator + goldens + region helpers added — the EXISTING token-map test untouched and green (the "old binding map run one final time" migration assertion, in CI, against regenerated templates). Commit B: the flip — token map + `KnownContractViolations` + old test bodies deleted, equality + ratchet tests in. Both commits individually green; 39-3's `RenderContractTokenTests` (whose plan explicitly anticipates this collapse) is retired in commit B, superseded by the strictly-stronger goldens.
- **D9 — Landing-order flexibility is the ratchet, not conditional code.** The generator and test bind whatever the D1 walk discovers at build time. If this story lands right after 39-12, exactly one cell is generated and everything else sits in `PendingMigrationCells`/`ProseOnlyCells`; each subsequent family migration moves entries out of the ratchet in its own PR (their plans already touch `ContractBindingTests`). Nothing here branches on "which stories have landed".

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/PromptContractRegion.cs`** (D3, D5) — pure static class:

   ```csharp
   namespace Tamma.Core.Documents;
   public static class PromptContractRegion
   {
       public const string GeneratorCommand = "dotnet run --project apps/tamma-elsa/tools/Tamma.PromptContractGen";
       public sealed record Region(string TypeKeyWire, int Version, string InnerBlock, int StartIndex, int EndIndex);
       public static string Render(IDocumentType type);                     // markers + RenderContract(), LF-only
       public static bool TryExtract(string body, out Region? region, out string? error); // 0 or ≥2 regions → error
       public static string Splice(string body, IDocumentType type);        // deterministic, idempotent
       public static IReadOnlyList<string> CheckEquality(string body, IDocumentType type); // AC4/AC6/AC7 messages
   }
   ```

   Style precedent: `PromptFileLoader` (dumb, ordinal string handling, no regex library beyond one anchored marker pattern).

2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/PromptContractGenerator.cs`** (D4) — the pure core: `Run(files, bindings)` → `GeneratorResult(RewrittenFiles, Errors)`; per bound cell resolve `Prompts/{role}/{action}.md`, `TryExtract` (missing/duplicate markers → error naming the cell), verify marker key matches the bound type (mismatch → error), `Splice`; also scan ALL files for marker regions whose `(role, action)` is unbound → error (AC3's "markers with no bound type").

3. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Introspection/LifecycleProducerBindingScanner.cs`** (D2) and **`PromptContractBindingSource.cs`** (same directory): the scanner materializes `DispatchWorkflow("document-lifecycle")` inputs over compiled workflows (machinery per `TaxonomyDriftBuildTests`' delegate-invocation approach); `PromptContractBindingSource.All()` merges scanner triples with `ReviewerSelectionHelper.AllDispatchablePairs → DocumentTypeKey.Review`, throws `TammaError PROMPT.CONTRACT.CONFLICTING_BINDING` if one cell maps to two types, and resolves each key through `DocumentTypeRegistry.Resolve` (fail-loud on unregistered). **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`** — its lifecycle-binding walk (39-12 step 6) delegates to the scanner; its tripwire ("walk finds ≥1 binding") stays in the test.

4. **CREATE `apps/tamma-elsa/tools/Tamma.PromptContractGen/Tamma.PromptContractGen.csproj` + `Program.cs`; MODIFY `apps/tamma-elsa/Tamma.sln`** (new `tools` solution folder). Console app, net8, references `Tamma.Core` + `Tamma.ElsaServer`; ~100 lines: parse `--prompts-dir`/`--goldens-dir` (defaults via git-root walk-up from `AppContext.BaseDirectory`), load files, call `PromptContractBindingSource.All()` + `PromptContractGenerator.Run`, write changed files LF-only, refresh goldens (D7), print per-cell summary, exit non-zero on errors.

5. **MODIFY every currently-bound producing cell under `apps/tamma-elsa/src/Tamma.Api/Prompts/`** — one-time manual marker insertion wrapping the existing hand-written contract section (e.g. `senior_developer/decompose-issue.md` L20–44), preserving all front matter and surrounding prose; then run the generator once so region contents become `RenderContract()` output. **CREATE `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/ContractGoldens/{key}.v{n}.md`** per registered type (generator-written). **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Tamma.Core.Tests.csproj`** (embed goldens) and **`.gitattributes`** (add `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/ContractGoldens/**/*.md text eol=lf`). This is D8's commit A boundary: full `dotnet test` runs here with the OLD token test still in place — the AC8 migration assertion.

6. **REWRITE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`** (D8 commit B) — delete `CellContract`/`Bindings`/`One`/`AnyOf`/`KnownContractViolations` and the two old binding-satisfaction tests; keep the file, fixture name, and doc-comment narrative (rewritten for the flip). New members per Test Plan: equality loop over `PromptContractBindingSource.All()` reading templates via `SystemPrompts.GetRoleAction`; the retained coverage guard (`EnumerateAllDispatchPairs`, clauses (a)–(c) intact) now classifying against generated ∪ `ProseOnlyCells` ∪ `PendingMigrationCells` (D6); the ratchet staleness test; the two AC6 demonstrations (D5).

7. **CREATE `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/PromptContractRegionTests.cs`, `PromptContractGeneratorTests.cs`, `RenderContractGoldenTests.cs`; DELETE `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Types/RenderContractTokenTests.cs`** (D8 commit B; superseded by goldens — cross-reference comment left in the golden test).

8. **CREATE `.dev/findings/prompt-override-contract-drift.md`** — the story's non-blocking technical note: tenant/user `prompt_overrides` replace templates wholesale; a future warn-on-save when an override's contract region diverges from the type is recorded as a candidate follow-up, not built.

9. **Verify:** generator run #2 is a byte-no-op (`git status` clean); full `dotnet test` green at both commit boundaries; `dotnet ef migrations has-pending-model-changes` untouched-clean (no Data changes exist to check, listed for the house checklist).

## Data & Migrations

None. No EF entities, no tables, no migrations. The only persisted-artifact change is checked-in markdown (prompt cells, goldens) embedded as resources.

## Events

None emitted or consumed. This story is build-time tooling + tests; `PROMPT.SEED.*` startup error codes are unchanged, and no `DOCUMENT.*` events are touched.

## Test Plan

All NUnit + FluentAssertions; no Moq beyond trivial fakes, no Testcontainers (nothing here touches a database).

- **`PromptContractRegionTests`** (`Tamma.Core.Tests/Documents/`) — `Render` output shape (marker lines exact, LF-only, version from `SchemaVersion`); `TryExtract` on: no markers, one region, two regions (error), begin-without-end (error), unparseable begin marker (error); `Splice` idempotent (`Splice(Splice(x)) == Splice(x)`, byte compare); `CheckEquality`: version-marker mismatch names both versions (AC7), byte-difference message quotes the first divergent line and contains `GeneratorCommand`. **Covers AC1 (format half), AC3 (idempotency core), AC6 (message contract), AC7.**
- **`PromptContractGeneratorTests`** (`Tamma.Core.Tests/Documents/`) — synthetic file sets + fake `IDocumentType`s: bound cell with no markers → error naming the cell; marker region on an unbound cell → error; marker key ≠ bound type → error; a run rewrites ONLY region interiors — front matter and surrounding prose byte-identical (AC2); second run returns zero rewritten files (AC3). **Covers AC2, AC3.**
- **`RenderContractGoldenTests`** (`Tamma.Core.Tests/Documents/`) — for every `DocumentTypeRegistry.All` entry: an embedded golden `{key}.v{SchemaVersion}.md` exists (missing → fail naming the file), `RenderContract()` byte-equals it, and called twice → identical; every embedded golden corresponds to a registered type at its current version (stale → fail). **Covers AC1.**
- **Flipped `ContractBindingTests`** (`Tamma.Activities.Tests/Workflows/`) —
  - `EveryBoundCell_GeneratedRegionEqualsRenderedContract`: for each `PromptContractBindingSource.All()` pair, template exists in `SystemPrompts`, `CheckEquality` empty; failure output names cell + type + diff + generator command. **Covers AC4 (equality half), AC7 (marker-version live check).**
  - `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`: retained clauses (a) unclassified fails, (b) no pair in two tables, (c) stale entries fail — classification now generated ∪ `ProseOnlyCells` ∪ `PendingMigrationCells`. **Covers AC4 (coverage guard retained).**
  - `PendingMigrationCells_RatchetOnlyShrinks`: an entry whose pair is now generated-bound fails until deleted; table carries the "entries may only ever be REMOVED" doc-comment. **Covers AC5.**
  - `TypeChange_FailsEqualityUntilRegenerated` (fake type with an added field / bumped `SchemaVersion` vs the real checked-in decompose-issue template) and `HandEditInsideRegion_FailsNamingGeneratorCommand` (real type vs tampered template copy). **Covers AC6.**
  - `BindingSource_FindsAtLeastOneLifecycleBinding_AndReviewPairs`: no-op tripwire for the scanner + merge (the `MinExpectedDispatchPairs` posture). **Covers AC3's "fails loudly on … no bound type" seam + D1.**
- **Existing suites as regression gates:** `Tamma.Api.Tests/PromptStore/PromptFileLoaderTests` + `SystemPromptsTests` (unchanged, prove AC2's loader-behavior clause), full `dotnet test` at both D8 commit boundaries (**AC8** — commit A runs the old token map one final time against regenerated templates; commit B proves the flip leaves everything green).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — deterministic `RenderContract` blocks + golden files | 1, 5 (goldens), 7 | `RenderContractGoldenTests` (byte-equal + double-call), `PromptContractRegionTests` format pins |
| 2 — delimited regions; front matter + prose untouched; loader unchanged | 1, 2, 5 | `PromptContractGeneratorTests` byte-identical-outside-region; `PromptFileLoaderTests`/`SystemPromptsTests` untouched-green; step 5 diff review |
| 3 — repeatable generator, idempotent, fail-loud (no markers / no bound type) | 2, 3, 4 | `PromptContractGeneratorTests`, `PromptContractRegionTests` idempotency, step 9 no-op run, binding-source tripwire |
| 4 — token map deleted; byte-equality per bound cell; coverage guard retained | 3, 6 | Flipped `ContractBindingTests` equality loop + retained guard clauses (a)–(c) |
| 5 — allowlist shrinks to prose-only + ratcheted stragglers | 6 (D6) | `PendingMigrationCells_RatchetOnlyShrinks` + coverage-guard staleness clause |
| 6 — drift-impossible demonstrated both directions | 1 (D5), 6 | `TypeChange_FailsEqualityUntilRegenerated`, `HandEditInsideRegion_FailsNamingGeneratorCommand` |
| 7 — versioned markers; version bump forces full fan-out | 1 (D3), 6 | `CheckEquality` version clause in `PromptContractRegionTests` + live marker-version check in the equality loop |
| 8 — old map run one final time then deleted; full `dotnet test` green | 5, 6, 7, 9 (D8 two commits) | CI green on commit A (old test + regenerated cells) and commit B (flip); `RenderContractTokenTests` deletion rides commit B |

## Dependencies & Sequencing

- **Hard prerequisites (must compile before step 3/5/6):** 39-2 (`IDocumentType.RenderContract`/`SchemaVersion`, `DocumentTypeKey`, `DocumentTypeRegistry`), 39-3 + 39-4 (registered type implementations for every bound key), 39-6 (`document-lifecycle` input contract), 39-12 at minimum (first lifecycle binding to scan). Steps 1–2 (region + generator core) depend only on 39-2's `IDocumentType` and can be built against fakes the moment 39-2 lands.
- **Maximized by 39-13/39-14/39-15:** each landed family moves cells from `PendingMigrationCells` to generated; this story does not wait for them (D9). 39-7 supplies `ReviewerSelectionHelper.AllDispatchablePairs`; if it has not landed, the reviewer half of `PromptContractBindingSource` is omitted and reviewer cells sit in the ratchet — a one-line merge to add later.
- **Lockstep partners:** 39-12 owner on the shared scanner location (D2 — one implementation, whoever lands second refactors to it); 39-13/14/15 owners on ratchet burn-down entries (their plans already edit `ContractBindingTests`); 39-3 owner on retiring `RenderContractTokenTests` (their plan pre-authorizes it).
- **Stubbing:** nothing from 39-17..39-21 is touched. Tests never stub the registry — they use real types where present plus local fake `IDocumentType` records for generator/region edge cases (small in-test classes, the `DocumentTypeRegistryTests` fake pattern from 39-2's plan).

## Risks & Mitigations

- **Byte-equality is brittle to line-ending drift.** Mitigated: `.gitattributes` already pins `Prompts/**/*.md` to LF (verified present); step 5 extends the pin to goldens; `Render`/`Splice` emit LF only; `CheckEquality` detects a `\r` in-region and says "CRLF corruption — check .gitattributes" instead of a cryptic byte diff.
- **The scanner extraction (D2) races 39-12's in-test walk.** Two implementations of the same reflection walk would themselves drift. Mitigated: explicit lockstep note; the test delegates to the scanner so exactly one walk exists; the tripwire fails if the walk finds zero bindings while a lifecycle binding workflow exists.
- **Deleting the token map before all families migrate removes token protection from stragglers.** Accepted by the story (AC5's ratchet encodes it): stragglers keep their fail-closed parsers and their entries are loud, remove-only debt; commit A's final token-map run proves no regression at the moment of hand-off.
- **`RenderContract()` blocks read worse to the model than today's hand-tuned prose.** Mitigated: the story's output-style note is binding — 39-3/39-4 own block quality; regeneration diffs in commit A are reviewed cell-by-cell; prose outside the region stays hand-authored, so instruction framing is untouched.
- **Marker text colliding with template content.** `RenderContract()` containing a marker line would break extraction. Mitigated: `Render` throws if the block contains either marker string; golden tests would catch it per type.
- **Tool bit-rots because nothing runs it in CI.** Mitigated: the equality test IS the freshness gate (a needed-but-unrun generator fails the build), and the test messages embed the exact command, so the tool is exercised every time a type changes.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | `PromptContractRegion` + pure `PromptContractGenerator` core | 0.75 |
| 3 | Scanner extraction + `PromptContractBindingSource` + drift-test delegation | 0.5 |
| 4 | CLI tool project + sln wiring | 0.5 |
| 5 | Marker insertion, first regeneration, goldens, `.gitattributes`, commit-A gate | 0.5 |
| 6 | `ContractBindingTests` flip (equality, guard, ratchet, AC6 tests) | 0.75 |
| 7 | Region/generator/golden test classes + `RenderContractTokenTests` retirement | 0.75 |
| 8–9 | Findings note, no-op verification, two-commit staging, review polish | 0.25 |
| **Total** | | **4.0** (story estimate: 3–4 days) |

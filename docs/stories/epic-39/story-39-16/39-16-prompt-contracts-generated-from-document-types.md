# Story 39-16: Prompt Contracts Generated From Document Types (Single Source)

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

As a **prompt author** (and the CI gate that watches over me),
I want the output-contract block inside each producing cell's `Prompts/{role}/{action}.md` to be **generated from the document type's `RenderContract`** — the same single source the validator enforces — with `ContractBindingTests` flipped from token-presence checking to generated-block equality,
So that the prompt that tells the model what to emit and the validator that judges what it emitted can never drift apart: divergence becomes **impossible** (there is one source), not merely **caught** (two sources compared).

## Priority

P1 — The epic's closing ratchet on the PR #475 substrate. Today's `ContractBindingTests` is a good tripwire but structurally a two-source comparison (hand-maintained binding map vs template text); every migrated cell (39-12..39-15) makes the flip cheaper, and after 39-15 the flip can be near-total.

## Architectural Context (READ FIRST)

- `apps/tamma-elsa/src/Tamma.Api/Prompts/{role}/{action}.md` — the file-backed prompt cells (roles: `architect`, `developer`, `devops`, `product_owner`, `security`, `senior_developer`, `tech_writer`, `tester`), front matter carrying `variables`/`enableTools`/`maxTokens`/`version`, loaded at startup by `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs` (fail-loud taxonomy check) and resolved via `SystemPrompts` (`apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs`). **Front matter and the prose body are untouched by this story** — only the output-contract block inside the body becomes generated content.
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the mechanism being flipped. Read its doc comment first: today it (1) checks a **hand-maintained binding map** of required token groups against template text (`EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken`), (2) guards coverage by enumerating every dispatched `(role, action)` pair via `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs` and requiring bound-or-allowlisted, and (3) ratchets `KnownContractViolations` (entries may only be removed; stale entries fail). The coverage guard and the ratchet discipline SURVIVE the flip; the token-group map is what dies.
- **`RenderContract` comes from the document types** (39-2's document core defines the prompt-contract renderer; 39-3/39-4 implement it per type): each document type can render its own output contract — field list, closed enums, domain-rule statements ("no cyclic dependsOn"), and a canonical example instance — deterministically from the static C# type.
- **Cell → document type binding comes from the lifecycle migrations**: after 39-12..39-15, every producing `(role, action)` cell is bound to a document type via its lifecycle binding's `produces` declaration — the generator walks THAT binding, so there is no third hand-maintained map.
- Precedent for generated-vs-checked-in equality gating: the drift-test family (`TaxonomyDriftBuildTests`, `ContractBindingTests`) — same "fails the build naming the offender" ergonomics expected.

## Acceptance Criteria

1. **Deterministic contract rendering.** Each document type's `RenderContract()` produces a deterministic markdown block (stable field order, stable formatting; same type version → byte-identical output) containing: the JSON field contract, enum value sets, the domain rules the validator enforces (phrased as instructions), and one canonical valid example. Golden-file tests pin one rendered block per document type.

2. **Delimited generated region in templates.** Each producing cell's `Prompts/{role}/{action}.md` carries its contract inside explicit generated-region markers (e.g. `<!-- BEGIN GENERATED CONTRACT: Decomposition v1 --> … <!-- END GENERATED CONTRACT -->`). Prose before/after the region and ALL front matter remain hand-authored and untouched. `PromptFileLoader`'s behavior is unchanged (the markers are inert comments at load/render time).

3. **Generator tool.** A repeatable generator (dotnet CLI tool or MSBuild-invocable command in the solution) walks every lifecycle `produces` binding, renders each bound type's contract block, and rewrites exactly the generated regions in the corresponding `Prompts/{role}/{action}.md` files — idempotent (second run is a no-op), and it fails loudly on a producing cell with no markers or markers with no bound type.

4. **`ContractBindingTests` flipped to generated-block equality.** The token-group binding map is deleted. The binding-satisfaction half now asserts, for every bound cell: the template's generated region is **byte-equal** to the freshly rendered `RenderContract()` of the bound document type — an edited-by-hand contract block or a type change without regeneration fails the build naming the cell, the type, and the diff. The coverage guard (every dispatched pair bound-or-allowlisted, via `EnumerateAllDispatchPairs`) is retained unchanged.

5. **Allowlist shrinks to prose-only.** The explicit allowlist retains only genuinely free-text cells (tech-writer prose class per the epic's "prose stays prose" principle, plus any not-yet-migrated stragglers if this story lands before 39-15 completes — ratcheted: entries may only be removed, stale entries fail, mirroring `KnownContractViolations` discipline).

6. **Drift is impossible, demonstrated.** A test mutates a document type's contract surface (adds a field / changes an enum) in-memory and asserts the equality check fails against the checked-in template until regeneration — proving the single-source property. Conversely, a hand-edit inside the generated region (fixture copy) fails with a message that names the generator command to run.

7. **Versioned contract evolution.** The generated region's marker carries the document type's contract version (from 39-2's envelope/type versioning); bumping a type's contract version without regenerating every cell that produces it fails the equality test — so a type change forcibly fans out to all its producing cells in the same commit.

8. **No behavioral prompt regression.** After regenerating all migrated cells, the resolved prompt for each cell still satisfies the previous contract's required tokens (the old binding map is run one final time as a migration assertion, then deleted) and full `dotnet test` passes — proving the flip changed the mechanism, not the effective contracts.

## Technical Notes

- **Why equality, not "contains".** Token-presence allows a template to drift in ways tokens don't catch (reordered examples, stale enum values, contradictory prose inside the contract block). Byte-equality of a delimited region is trivial to check, trivial to fix (run the generator), and leaves zero interpretive gap.
- **Overrides inherit the guarantee at the default layer.** Tenant/user prompt overrides (`prompt_overrides`, per CLAUDE.md) can still replace a template wholesale — this story governs the **system defaults** embedded in the binary. Consider (non-blocking, note in `.dev/findings/`) a future warn-on-save when an override's contract region diverges from the type.
- **Generator output style:** keep rendered blocks minimal and model-facing (field contract + rules + one example). Resist duplicating validator prose exhaustively — the block instructs the model; the validator judges. Both come from the same type members, which is the actual single source.
- **Check-in strategy:** generated regions are committed (prompts are embedded resources; no build-time generation into the binary), with the equality test as the freshness gate — same trade-off as EF migrations.
- **Landing order flexibility:** the story can land after 39-12 with only migrated cells flipped (others stay allowlisted), tightening as 39-13..39-15 land; AC5's ratchet encodes that path.

## Dependencies

- **Blocking:** 39-2 (`RenderContract` renderer + type versioning), 39-3/39-4 (per-type implementations), 39-12 at minimum (first `produces`-bound cell to generate against).
- **Maximized by:** 39-13/39-14/39-15 — each migration moves cells from allowlist to generated.
- **Existing substrate:** PR #475 file-backed prompt registry, `PromptFileLoader`/`SystemPrompts`, `ContractBindingTests` + `TaxonomyDriftBuildTests` enumeration.
- **Supersedes:** the hand-maintained token-group binding map (epic README "Supersedes / absorbs").

## Estimated Effort

3–4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |

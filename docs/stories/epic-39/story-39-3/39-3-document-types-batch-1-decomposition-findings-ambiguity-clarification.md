# Story 39-3: Document Types Batch 1 — Decomposition, Findings, AmbiguityAssessment, Clarification

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

As a **platform developer migrating the assessment/decomposition workflow family onto the typed-document lifecycle**,
I want **the first four document types — `Decomposition`, `Findings`, `AmbiguityAssessment`, `Clarification` — implemented as C# records with executable domain validators, prompt-contract renderers, and examples, registered in the `DocumentTypeRegistry`**,
So that the shapes currently scattered across four hand-rolled fail-closed parsers become first-class typed artifacts whose domain rules (no dangling/cyclic `dependsOn`, evidence-cited findings, bounded scores, open-ended questions) are enforced in one place — and the existing parsers' behavior is provably subsumed, not regressed.

## Priority

P0 — These four types cover the assessment family that runs earliest in every issue cycle (research → ambiguity → clarify → decompose) and back the pilot migration (39-12, `IssueDecomposition`) and the family migration (39-13). `Decomposition` in particular is consumed downstream by Stories 2-15/2-16 (dependency mapping/sequencing), which the README names as the typed `Decomposition`'s first consumers.

## Architectural Context (READ FIRST)

Types implement the 39-2 contract and land in `apps/tamma-elsa/src/Tamma.Core/Documents/Types/` (one file per type + its validator), registered in `DocumentTypeRegistry`.

**The compatibility baseline is the existing fail-closed parsers.** Read each end-to-end — their accepted spellings, defaults, and rejection rules are the floor the new validators must subsume:

- `apps/tamma-elsa/src/Tamma.Activities/Decomposition/DecompositionParsing.cs` — today's decomposition shape (tasks, ids, `dependsOn`, sizing) consumed by `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Research/ResearchParsing.cs` — findings shape consumed by `ResearchWorkflow.cs` (and context-scan callers)
- `apps/tamma-elsa/src/Tamma.Activities/Ambiguity/AmbiguityParsing.cs` — ambiguity score + typed ambiguities consumed by `AmbiguityScoringWorkflow.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Clarify/ClarifyParsing.cs` — questions/resolution shapes consumed by `ClarifyingQuestionsWorkflow.cs`

**Existing parser test fixtures are the round-trip corpus:**

- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Decomposition/DecompositionParsingTests.cs`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Research/ResearchParsingTests.cs`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Ambiguity/AmbiguityParsingTests.cs`

**The prompt contracts these types will render (39-16) currently live in** `apps/tamma-elsa/src/Tamma.Api/Prompts/{role}/{action}.md` for the producing cells (`decompose-issue`, `score-ambiguity`, `clarify`, `incorporate-answers`, research/context-scan — see `Tamma.Core/Agents/AgentAction.cs` for the wire names) and are pinned by `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`. Keep `RenderContract()` consistent with those templates' required tokens — do not contradict a binding the CI gate enforces.

Domain rules per type come from the Epic 39 README document-type table and are restated in the ACs below.

## Acceptance Criteria

1. **Four types registered.** `Decomposition`, `Findings`, `AmbiguityAssessment`, and `Clarification` each ship as: a C# payload record (+ nested records), an `IDocumentType` implementation with `Validate`/`RenderContract`/`Examples`, and a `DocumentTypeRegistry` registration. The 39-2 registry count pin is consciously bumped by exactly 4, and all 39-2 registry drift tests (contract non-empty, examples self-consistent) pass for the new types.

2. **Decomposition domain rules.** `Validate` enforces, with domain-phrased violations: unique task IDs; no dangling `dependsOn` (referencing a non-existent ID), no self-dependency, no dependency cycles (graph check, naming the cycle members); per-task sizing within 2–8h; and a computable prerequisite order (topological order exists — implied by acyclicity but asserted with its own violation code so downstream sequencing consumers get a stable signal).

3. **Findings domain rules.** `Validate` enforces: every finding cites evidence (non-empty source/reference); `relevance` and `confidence` ∈ [0,1] with out-of-range values rejected (not clamped); findings are ranked (explicit rank or ordered with no duplicate ranks). An empty findings list is handled exactly as the baseline `ResearchParsing` handles it today (document the inherited choice in the type's XML doc).

4. **AmbiguityAssessment domain rules.** `Validate` enforces: `score` ∈ [0,1]; each ambiguity is one of a closed typed set (mirror the categories `AmbiguityParsing` accepts today — enumerate them, don't invent); **a clear assessment (low score) with an empty ambiguity list is valid** — the validator must not require ambiguities to exist.

5. **Clarification domain rules (two-phase shape).** The type models Questions → Resolution: the questions phase requires ≥1 open-ended question (a question mark alone is not the test — mirror/tighten `ClarifyParsing`'s current rule and document it); the resolution phase requires each resolution to state the clarified requirement (non-empty, references the question it resolves). A resolution referencing an unknown question ID is a violation.

6. **Fail-closed subsumption of the baseline parsers.** For each type, a test suite feeds the inputs its baseline parser **rejects** (drawn from the existing `*ParsingTests.cs` negative cases) and asserts the new validator also rejects them — with a violation, never a silent default. Where a baseline parser is *lenient* (accepts alternative spellings, e.g. token alternatives recorded in `ContractBindingTests`' binding map), the new type either accepts the same spellings on read or the divergence is explicitly listed in the story-completion notes as a deliberate tightening with the affected prompt cells named.

7. **Round-trip against existing fixtures.** Every fixture the existing parser tests successfully parse is deserialized into the new typed payload, passes `Validate` (or the failure is a documented deliberate tightening per AC6), and re-serializes to JSON that the *old* parser still parses — proving instances can flow through un-migrated consumers during the 39-12/39-13 transition window.

8. **Contract renderers + examples.** Each type's `RenderContract()` output includes every required field the validator checks and every JSON token the type's prompt cells are bound to in `ContractBindingTests`' binding map; each type ships ≥2 examples (≥1 valid, ≥1 invalid with the expected violation codes asserted).

9. **No parser deletion, no workflow rewiring.** The existing `*Parsing.cs` files and their callers are untouched (migration is 39-12/39-13). Diff surface: `Tamma.Core/Documents/**`, `Tamma.Core.Tests/**` only.

## Technical Notes

- **Validators are supersets, not rewrites-from-taste.** The baseline parsers encode hard-won fail-closed rules (see the PR #475 history in `ContractBindingTests.cs`'s doc comment — commit `580d355` minted `propose-design`/`incorporate-answers` precisely because one cell was parsed two ways). Every accepted/rejected behavior change must be deliberate and listed.
- Cycle detection for `Decomposition` should report the actual cycle path in the violation message — this is the canonical example of a "domain-phrased violation" the epic README calls for, and it is what the 39-9 repair ring will feed back to the model.
- `Clarification` as one type with two phases (vs. two types) follows the README's table ("Questions → Resolution" as a single row); model the phase as a discriminated shape inside the payload, and let the lifecycle state (39-2 envelope) carry progress — do not invent a second lifecycle.
- Violation codes defined here (e.g. `DANGLING_DEPENDS_ON`, `CYCLIC_DEPENDS_ON`, `SCORE_OUT_OF_RANGE`, `MISSING_EVIDENCE`, `NO_OPEN_QUESTION`) become part of the platform vocabulary — SCREAMING_SNAKE_CASE, stable, documented on each validator.
- No implementation code in this story file beyond illustrative signatures; the record shapes follow from the baseline parsers plus the README table.

## Dependencies

- **Prerequisite:** Story 39-2 (`DocumentEnvelope`, `IDocumentType`, `DocumentTypeRegistry`, drift-test scaffolding).
- **Prerequisite:** Story 39-1 audit — confirms each baseline parser's callers and any additional informal consumers of these four shapes.
- **Feeds:** 39-12 (pilot: `IssueDecomposition` onto the lifecycle consumes the `Decomposition` type), 39-13 (assessment family migration), 39-16 (contract generation), Stories 2-15/2-16 (first consumers of typed `Decomposition`).

## Estimated Effort

4–5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |

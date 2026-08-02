# Story 43-16: Acceptance Unification (Form α) — the Shipped Acceptor Floor Becomes Derived

Status: drafted

Implements: Story 43-11 **Amendment 2, section G** (form α), constrained by the Amendment 3 zone numbers and the caller-kind re-audit's FLAGs on the three human-pinned document types.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **platform operator**,
I want one source of truth for "who accepts this document type at this dial" — the document-type's catalog level against the dial — with the stored per-type acceptor kept only as a named override,
So that the action catalog and the acceptance resolver can never disagree about the same decision.

## Priority

P0 blocking for 43-11's landing — Amendment 2-G states it plainly: after 43-11, the two systems become unsatisfiable together. The catalog puts `document-type:design` on a real level while `AcceptanceDefaults` says "a person at every level", and the lockstep test (`ActionCatalogDefaultsTests.cs:93-120`, `DesignDocumentType_MatchesAcceptanceDefaults`) fails by construction. Doing nothing is not an option.

## Architectural Context (READ FIRST)

- **The two surfaces today.** `AcceptanceDefaults.For` returns `AcceptorRequirement.Human` for exactly `design`, `sprint-plan`, `threat-model` (`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:122,146,170`, switch at `:206-223`); `AcceptanceFloors.ShippedFloorFor` (`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceFloors.cs:69`) turns that into a floor composed by `max()` (`:65-66`, applied at `:85`). That is `AlwaysHuman` in a second vocabulary.
- **Form α (adopted; form β — full field deletion — waits until the toggle surface has proven itself):**
  1. **The shipped acceptor floor is DERIVED**: `Human` while `dial < catalogLevel(document-type:<type>)`, `Any` at or above it — `ShippedFloorFor` gains `(type, dial)` inputs, reading `ActionCatalog.Get(new ActionKey(DocumentType, type.ToWire())).DefaultMinAutonomy` and the resolved dial. The `max()` lattice is unchanged; only its shipped input moves.
  2. **The stored per-type `AcceptorRequirement` survives ONLY as the named-type override.** An explicit per-type `any` still lowers (the pinned `Upsert_explicit_any_clears_the_human_floor` semantics); a base-row PUT still cannot erase the floor (CD-1 protection).
  3. **"The dial" in the derivation is the BASE row's `AutonomyLevel`** — resolved the way the gate resolves it (`apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:196`: `baseRules?.Rules.AutonomyLevel ?? AutonomyDial.Min`) — **never a per-type row's own level**. Otherwise a per-type autonomy edit silently moves that type's acceptor. This caveat is load-bearing (Amendment 2-G's words) and gets its own AC.
- **The zone numbers create a day-one loosening that 43-11 never resolves — this story forces the recorded decision.** Amendment 1's M5 promised "at the shipped default dial of 70 all [human-pinned types] remain gated, so no upgrade loosens anything on day one" (levels 80/85/90). Amendment 3's zones put **all binding-doc acceptances at 45** — including `design`, `sprint-plan`, `threat-model` — and the re-audit FLAGs each ("ships `AcceptorRequirement.Human` today; 45 automates it at dial ≥ 45"). With `DefaultAutonomyLevel = 70` (`AcceptanceDefaults.cs:33`), the derivation automates all three human-pinned acceptances **on day one at the shipped dial**. The zones and the no-day-one-loosening principle cannot both hold. This story does not pick silently: AC7 requires a recorded ACCEPT (the loosening is intended) or REBASE (the three types get levels above 70) in the 43-11 AC9 decision-table mechanism, product-owner-signed, before the derivation ships.
- **Acceptance is still always a workflow step** (43-11 M6): the level chooses *who* answers — orchestrator, single reviewer, or the 7-role panel (`AcceptanceDefaults.cs:206-223`, `ReviewerSelection`). No self-accept is created. The two runtime escape signals (ambiguity ≥ threshold, blocking-review violation) remain untouched and remain the only level-independent human pulls.

## Acceptance Criteria

1. **The three hardcoded `AcceptorRequirement.Human` rows are removed** (`AcceptanceDefaults.cs:122,146,170`); `ShippedFloorFor` derives from `(catalog level, base-row dial)`; the `max()` composition site (`AcceptanceFloors.cs:85`) is unchanged.
2. **The derivation is pinned as a biconditional over the whole space**: `ActionCatalogDefaultsTests.cs:93-120` is rewritten to assert, for **every** `DocumentTypeKey` at **every** `AutonomyDial.ValidLevels()` position: shipped floor is `Human` ⟺ `dial < ActionCatalog.Get(document-type:<type>).DefaultMinAutonomy`. (This is 43-11 AC7's test, landed here.)
3. **"The dial" is the base row, never a per-type row.** A test sets a per-type autonomy row above the base dial and asserts the type's derived acceptor is computed from the **base** value — the per-type edit does not move the acceptor. Removing the caveat fails this test.
4. **Explicit-any semantics preserved**: `AcceptanceRulesEndpointsTests.Upsert_explicit_any_clears_the_human_floor` is re-vectored onto the derived floor (per-type stored `any` lowers below the derived `Human`), not deleted. The CD-1 protection (base PUT cannot lower the floor below what the level implies) still fires, re-vectored.
5. **Test re-vectoring is enumerated and complete** — the story's implementation touches exactly: `ActionCatalogDefaultsTests.cs:93-120` (rewrite, AC2), `AcceptanceFloorsTests` (derived inputs), `AcceptanceRulesEndpointsTests.Upsert_explicit_any_clears_the_human_floor` (AC4), and the `AcceptanceDefaultsDriftTests` rows that named the three `Human` constants. Any other acceptance test edited by the diff is a story-scope violation to be justified in review.
6. **The panel path is untouched**: the 7-role majority panel types (`plan`, `acceptance-criteria`, `review`, `ux-spec`) keep their `ReviewerSelection`; the derivation changes *whether a person is forced*, not *which panel reviews*. Pinned by an unchanged panel-selection test.
7. **The day-one-loosening decision is recorded before ship.** The 43-11 AC9 decision table gains three rows (`document-type:design`, `sprint-plan`, `threat-model`) marked ACCEPT (automate at dial ≥ 45, loosening on upgrade — product owner signs) or REBASE (their catalog levels move above `AcceptanceDefaults.DefaultAutonomyLevel`); a test cross-checks the table against the catalog so an undecided or stale row fails the build. This AC is the story's gate: the derivation must not merge with the row undecided.
8. **`dotnet test` green; no schema change.**

## Dependencies

- **Story 43-11** — the document-type levels the derivation reads; the AC9 decision-table mechanism AC7 extends. Blocking; must land together or 43-16 immediately after (the lockstep test fails in the gap — coordinate one PR train).
- **Story 43-15** — form β (deleting the stored field) is explicitly deferred until 43-15's toggle surface has proven itself; no dependency for form α.
- **Verified in tree**: `AcceptanceDefaults.cs:33,122,146,170,206-223`; `AcceptanceFloors.cs:65-69,85`; `AutonomyGateEvaluator.cs:196`; `ActionCatalogDefaultsTests.cs:93-120`.

## Out of Scope

- **Form β** — deleting `AcceptorRequirement` from storage.
- Changing `AcceptanceDefaults.DefaultAutonomyLevel` (70) — AC7's REBASE arm moves catalog levels, never the constant.
- Panel composition changes (`ReviewerSelection`, roster) — 41-1a territory.
- The ambiguity/no-agreement runtime signals — Story 39-25.

## Estimated Effort

2–3 days — 1 for the derivation + caveat, 1 for the re-vectoring in AC5, 0.5–1 for the decision-table AC and the coordination with 43-11's landing train.

## Change Log

| Date       | Version | Changes                                                                    | Author |
| ---------- | ------- | -------------------------------------------------------------------------- | ------ |
| 2026-08-02 | 1.0.0   | Initial story — form α derived acceptor floor, base-row dial caveat, explicit-any preserved, day-one-loosening decision forced (43-11 Amendment 2-G) | Claude |

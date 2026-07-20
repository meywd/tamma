# Story 39-2: Document Core — Envelope, Type Registry, Lineage, Drift Tests

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

As a **platform developer building any Epic 39 document type or lifecycle consumer**,
I want a **`Tamma.Core/Documents` foundation — a `DocumentEnvelope` carrying identity, type, schema version, lineage, producer provenance, and lifecycle state; an `IDocumentType` contract with executable validation, a prompt-contract renderer, and examples; and a drift-tested `DocumentTypeRegistry`**,
So that every document type added in 39-3/39-4 plugs into one compile-time vocabulary (the same pattern as the `AgentRole`/`AgentAction`/`RolePhaseMap` taxonomy), unknown types fail loud instead of flowing silently, and every instance carries the `issueId` lineage the resumability and escalation stories depend on.

## Priority

P0 — Everything in the epic sits on this. 39-3/39-4 implement `IDocumentType`; 39-5's policy keys off document type; 39-6's lifecycle moves envelopes through states; 39-8 attaches envelope lineage to escalations; 39-11 persists envelopes. Getting the envelope/lineage shape wrong here is the epic's most expensive class of mistake.

## Architectural Context (READ FIRST)

This story creates a **new namespace in the existing `Tamma.Core` project** (`apps/tamma-elsa/src/Tamma.Core/`) — pure types, **no database, no EF, no endpoints** (persistence is Story 39-11; this story is deliberately storage-free).

**The pattern to mirror is the agent taxonomy** — read it end-to-end before designing:

- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentRole.cs`, `AgentAction.cs` — static enum vocabulary with `[Wire("...")]` string mapping via `EnumWire.cs`
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs` — the compile-time composition map over the vocabulary
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRoleTests.cs` and `RolePhaseMapTests.cs` — the drift-test style to copy: **count pins** (e.g. `RolePhaseMap.ValidActions.Should().HaveCount(79)` — adding/removing a member forces a conscious test edit), wire-name round-trips, and fail-loud behavior on unknown wire strings

**Adjacent contracts the envelope must align with (not duplicate):**

- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` — the DCB event row; documents flow *through* events as JSON and reuse its tag conventions (`issueId` is the existing DCB tag convention formalized, per the Epic 39 README "Issues are anchors, not documents")
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` (`DefinitionId = "llm-call"`, inputs `agentRole`/`action`/`variables`) — the producer provenance fields (`producedBy` role/action/workflow) must be expressible in exactly this dispatch vocabulary
- `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs` + `PromptFileLoader.cs` — the file-backed prompt registry whose fail-loud startup posture (a taxonomy cell without a file refuses to start) is the model for the registry's fail-loud posture
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the build-time contract gate that 39-16 will regenerate from `IDocumentType.RenderContract`; the renderer's output shape should anticipate that consumer
- The 39-1 audit document — the verified consumer edge list that seeds the `consumes`/`produces` interface declarations

New code lands in `apps/tamma-elsa/src/Tamma.Core/Documents/`; tests in `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/` (project exists).

## Acceptance Criteria

1. **`DocumentEnvelope` record exists** in `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentEnvelope.cs` carrying at minimum: `Id` (UUID v7), `Type` (registry key), `SchemaVersion`, lineage (`IssueId` required anchor, `CorrelationId`, optional `ParentDocumentId`/`SupersedesDocumentId` for revision chains), producer provenance (`ProducedBy`: agent role wire-name, action wire-name, workflow definition id — validated against `AgentRole`/`AgentAction` wire values, not free strings), lifecycle state, `CreatedAt`/`UpdatedAt` (ISO 8601, millisecond precision), and the typed payload as JSON. Envelopes are immutable — state transitions produce a new envelope value, never a mutation.

2. **Lifecycle state enum** `DocumentState` with exactly `Draft → Validated → Reviewed → Accepted | Rejected | Escalated`, plus a static legal-transition map (e.g. `DocumentStateMachine.CanTransition(from, to)`) that rejects illegal jumps (`Draft → Accepted` without validation is illegal; terminal states have no exits). The transition map is data the 39-6 lifecycle workflow consumes — the enforcement seam lives here, written once.

3. **`IDocumentType` interface** in `Tamma.Core/Documents/IDocumentType.cs` with at minimum:
   - `string Key`, `int SchemaVersion`, `Type PayloadClrType`
   - `DocumentValidationResult Validate(JsonElement payload)` returning **domain-phrased violations** (e.g. "task `t3` depends on `t9`, which does not exist" — never a bare schema path), each violation carrying a stable code + human message so 39-9's repair ring can feed them back to the model
   - `string RenderContract()` — the prompt-contract block describing the required output shape (the single source 39-16 flips `ContractBindingTests` onto)
   - `IReadOnlyList<DocumentExample> Examples` — ≥1 valid and ≥1 invalid example per type, used by contract rendering and tests
   Illustrative signatures only in this story — 39-3/39-4 provide the implementations.

4. **`DocumentTypeRegistry`** in `Tamma.Core/Documents/DocumentTypeRegistry.cs`: a static, immutable key→`IDocumentType` lookup (mirroring the `SystemPrompts`/`RolePhaseMap` facade shape). `Resolve(key)` on an unknown key **throws a typed error** (`TammaError`-style, fail-loud) — no null returns, no silent default. The registry also exposes the workflow interface declarations (`consumes: [keys] / produces: key` per producing workflow) as static data, seeded from the 39-1 audit's edge list, so a build-time test can walk the producer/consumer graph the way `ContractBindingTests` walks dispatch pairs.

5. **Drift tests mirror the taxonomy pattern.** `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/DocumentTypeRegistryTests.cs` pins: (a) the exact registered-type **count** (updated consciously as 39-3/39-4 land — same style as `RolePhaseMapTests`' `HaveCount(79)`); (b) key uniqueness and stable wire spelling (kebab or SCREAMING dot-path — decided here, one convention, round-trip tested); (c) every registered type has a non-empty `RenderContract()` and ≥2 examples with every valid example passing its own `Validate` and every invalid example failing it; (d) `Resolve("nope")` throws the typed unknown-type error.

6. **Envelope serialization round-trip.** Envelope + payload serialize to the JSON that flows through events/API and deserialize back losslessly (System.Text.Json), including unknown-extra-field tolerance on read (forward compatibility across `SchemaVersion` bumps) and strict rejection of a missing `IssueId`/`Type`. A test pins the serialized property names so the wire format is a deliberate contract, not an accident of C# naming.

7. **State machine tests.** Every legal transition in AC2 is asserted allowed; a representative set of illegal transitions (including any transition out of `Accepted`/`Rejected`) is asserted rejected with a domain-phrased error naming both states.

8. **No storage, no I/O.** The story ships zero EF entities, zero migrations, zero endpoints, zero Elsa activities. A reviewer can verify by inspecting the diff: only `Tamma.Core` + `Tamma.Core.Tests` (and, if needed, `Tamma.Core.csproj`) change. `dotnet test` passes for `Tamma.Core.Tests` with no Docker/Testcontainers requirement.

## Technical Notes

- **Vocabulary static, composition dynamic** (README design principle): the set of types, their rules, and the state machine are compile-time; which workflows run and who reviews stay runtime policy (39-5). Do not add policy knobs to the envelope.
- `ProducedBy` should reuse the existing wire-string discipline (`EnumWire`) rather than raw strings, so a renamed role/action breaks compilation or a drift test — never runtime.
- `DocumentValidationResult` violations are the *interface* between deterministic validation and the LLM repair turn (39-9) and Review notes (39-6). Phrase codes as stable identifiers (`DANGLING_DEPENDS_ON`) and messages in domain language; both are load-bearing downstream.
- Keep `RenderContract()` output deterministic (stable ordering) — 39-16 will diff it in CI.
- The registry's static workflow-interface declarations may start with only the workflows the 39-1 audit confirms; the graph-walk build test should fail on a declared producer whose type key is unregistered — the same fail-loud posture as `PromptFileLoader`.

## Dependencies

- **Prerequisite:** Story 39-1 (audit) — the consumer edge list and informal-shape inventory seed the registry declarations and validate the envelope fields against reality.
- **Prerequisite (in place):** the agent taxonomy (`AgentRole`/`AgentAction`/`RolePhaseMap` + drift tests) and the PR #475 prompt registry — the patterns this story copies.
- **Feeds:** 39-3/39-4 (type implementations), 39-5 (policy keyed by type), 39-6 (lifecycle over envelopes/states), 39-8 (lineage payload), 39-11 (persistence of envelopes), 39-16 (contract generation from `RenderContract`).

## Estimated Effort

4–5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |

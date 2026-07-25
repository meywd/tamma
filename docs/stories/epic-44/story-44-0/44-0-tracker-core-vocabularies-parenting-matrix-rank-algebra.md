# Story 44-0: Tracker Core — Vocabularies, `WorkItemRef`, Parenting Matrix, Rank Algebra, Fail-Loud Index

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

As a **platform engineer** building the native tracker,
I want the tracker's closed vocabularies, identity type, hierarchy rules and ordering algebra to exist as a pure, dependency-free core in `Tamma.Core` — validated at startup, count-pinned by tests, and reachable from every assembly,
So that storage, API, engine and UI all bind to one vocabulary instead of four, and an out-of-vocabulary kind, status or parenting relationship cannot be expressed at all.

## Priority

P0 — Wave 0. Every other story in Epic 44 depends on these types. `Tamma.Core` is the only assembly with zero `ProjectReference`s and is therefore the only place `Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer` and `Tamma.Api` can all reach — the same reason `AgentAction`, `DocumentTypeKey` and Epic 43's `ActionKey` live there.

## Architectural Context (READ FIRST)

- **The `[Wire]` mechanism is the closed-vocabulary guarantee, and it is self-enforcing.** `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs:20` defines `WireAttribute`; the `EnumWire<TEnum>` static constructor (`:39-59`) throws on first touch if a member lacks `[Wire]` (`:50`), two members share a wire string (`:52-54`), or the enum is `[Flags]` (`:46-48`). `TryParse` is **ordinal and case-sensitive** (`:65`), so non-canonical casing in persisted data is rejected rather than coerced. Caveat to know: there is **no** test asserting that every enum in the solution carries `[Wire]` — enforcement is lazy, triggered only when `EnumWire<T>` is first used for that `T`. This story therefore adds explicit round-trip tests per enum.
- **The status-vocabulary precedent to copy exactly:** `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:20-29` — 7 `[Wire]` members, count-pinned at 7 by `DocumentInstanceStatusTests`, and a DB CHECK constraint `ck_document_instances_status` mirroring the exact wire strings (documented at `:12-14`). `WorkItemStatus` is that shape.
- **The vocabularies to reuse, not re-invent:** `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs:14-20` (`TriagePriority`: urgent/high/normal/low) and `:23-31` (`TriageIssueType`: bug/feature/chore/question/security/docs). Both are `[Wire]`, both have an alias-aware parser (`TriageVocabulary`, `:52-109`, folding `critical`→`Urgent` and `medium`→`Normal`), both are count-pinned (`tests/Tamma.Core.Tests/Documents/Types/TriageDecisionTypeTests.cs:34-43`) — and both are referenced **nowhere outside their own file and tests**. This story gives them their first consumer.
- **Do NOT adopt `TriageComplexity`** (`TriageDecision.cs:34-41`). Its `[Wire("epic")]` member is a *size* estimate and would read as a hierarchy level beside `WorkItemKind.Epic`. Epic README §1 records this as an accepted, flagged collision.
- **The built-index idiom for the parenting matrix:** `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:43-163` declares 93 `(role, action)` eligibility cells and *builds* the by-role index at `:170-171` — never hand-maintains it. `TrackerHierarchy` is that shape for `(parentKind, childKind)`.
- **The fail-loud posture:** `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs:88` validates in a pure `Build(files)` core and throws naming the offender; `SystemPrompts.cs:96` calls it from a static initializer, so a bad tree is a `TypeInitializationException` and the process refuses to serve. `DocumentTypeRegistry.cs:95` and `AcceptanceDefaults.cs:25` both cite it explicitly.
- **UUIDv7 already exists:** `apps/tamma-elsa/src/Tamma.Core/Documents/UuidV7.cs` — use it, do not add a second time-sortable id helper.
- **`Tamma.Core.Tracking` is a free namespace** (verified: zero matches in `src/`). So are the type names `WorkItemKind`, `WorkItemStatus`, `Project`, `Iteration`, `Rank`.

## Acceptance Criteria

1. **`WorkItemKind`** — a `[Wire]` enum in `apps/tamma-elsa/src/Tamma.Core/Tracking/WorkItemKind.cs` with exactly six members: `epic`, `story`, `task`, `bug`, `chore`, `spike`. Wire round-trip and count (`Be(6)`) pinned by tests.

2. **`WorkItemStatus`** — a `[Wire]` enum with exactly seven members: `backlog`, `ready`, `in_progress`, `in_review`, `blocked`, `done`, `cancelled`. Count pinned at 7. A `WorkItemStatus.IsTerminal` predicate covers `done` and `cancelled` only. The multi-word wires use `_` exactly as `DocumentInstanceStatus`'s `in_review` does — no hyphens, no camelCase.

3. **`TrackerHierarchy`** — the `(parentKind, childKind)` matrix, declared as data and with a **built** by-parent index (`RolePhaseMap.cs:170-171` idiom). Permitted: `Epic → {Story, Bug, Spike}`; `Story|Bug|Spike → {Task, Chore}`; `Task|Chore → {}`. `MaxDepth = 3` is a named constant on the type, not a literal at a call site. The index build **throws** if any `WorkItemKind` member has no row (including an explicit empty row), so adding an enum member without a rule is a boot failure.

4. **`WorkItemRef`** — a `readonly record struct WorkItemRef(string ProjectKey, int Number)` with `ToWire() => $"{ProjectKey}-{Number}"` and a strict `TryParse`. `ProjectKey` is validated `^[A-Z][A-Z0-9]{1,9}$` (upper-case, 2–10 chars) and `Number >= 1`. **`ToWire()` is the string written into `DocumentInstance.IssueId` and DCB `tags.issueId`** — the epic's join key (README §2). A test asserts round-trip and asserts rejection of lower-case, empty, over-long and zero/negative inputs.

5. **`Rank`** — a fractional-index algebra: `Rank.Between(string? left, string? right)` returning a base-62 string that sorts strictly between its neighbours under **ordinal** comparison, `Rank.First()`, `Rank.Last()`. Property tests assert: (a) 10 000 sequential midpoint insertions between a fixed pair never collide and never exceed a stated length bound; (b) ordinal sort order of a shuffled generated sequence matches insertion intent; (c) `Between(null, null)`, `Between(x, null)` and `Between(null, x)` are all defined.

6. **Priority and type reuse.** `WorkItem`'s priority and type dimensions bind to the existing `TriagePriority` and `TriageIssueType` (`TriageDecision.cs:14-31`) — **no new enum is introduced for either**. A test pins that the tracker's accepted priority wire set is exactly `TriagePriority`'s, so a future member added there flows through rather than drifting.

7. **The core is pure.** `Tamma.Core.Tracking` has no `ProjectReference`, no EF, no `HttpClient`, no `ILogger`, no I/O of any kind. A test asserts every public type in the namespace is constructible without DI.

8. **Fail-loud index test.** A test adds a synthetic `WorkItemKind`-shaped vocabulary missing a parenting row and asserts the index build throws naming the offending member — the `PromptFileLoader.Build` assertion shape.

## Technical Notes

- `WorkItemKind` and `WorkItemStatus` deliberately ship as **separate** vocabularies from `DocumentTypeKey` and `DocumentInstanceStatus`. They look similar and are not: a document status describes a *revision's* review position; a work-item status describes a *thing to be done*. Merging them would put `superseded` on a backlog board.
- `MaxDepth = 3` is enforced by the service layer (44-3), not by the matrix — the matrix expresses *what may parent what*, and depth is a consequence. Stating both keeps the rule readable and gives 44-3 one constant to reference.
- The rank alphabet is base-62 (`0-9A-Za-z`) chosen so that ordinal `string` comparison in C# and `ORDER BY "Rank"` in Postgres with the `C` collation agree. **If the column is created with a non-`C` collation the ordering silently differs** — 44-1 D3 pins the collation and this story's tests document why.
- Do not add a `WorkItemType` enum. "Type" is `TriageIssueType`; "kind" is the hierarchy level. Two words, two axes, both already named.

## Dependencies

- **Existing, no change required:** `EnumWire`/`WireAttribute` (`Tamma.Core/Agents/EnumWire.cs`), `UuidV7` (`Tamma.Core/Documents/UuidV7.cs`), `TriagePriority`/`TriageIssueType` (`Tamma.Core/Documents/Types/TriageDecision.cs`).
- **Blocks:** 44-1 (storage binds these), 44-2, 44-3, 44-4, 44-5, 44-7. Nothing in Epic 44 starts before this lands.
- **Blocked by:** nothing. Ships standalone and is independently reviewable.

## Out of Scope

- Any table, migration, entity, repository or DbSet — 44-1.
- Any endpoint, DTO or service — 44-2.
- Any DCB event constant — 44-5.
- Custom or per-principal status sets — deferred (epic README, Decisions D11).
- `TriageComplexity` adoption or retirement — an open question for the product owner (epic README, Open questions 5).

## Estimated Effort

4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |

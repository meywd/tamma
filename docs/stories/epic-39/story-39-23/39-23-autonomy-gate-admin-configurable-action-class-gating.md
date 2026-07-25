# Story 39-23: Autonomy Gate — Admin-Configurable, Dial-Sensitive Action-Class Gating

Status: **SUPERSEDED by Epic 43** (2026-07-25) — do not implement.

> **Why superseded.** This story hung the gating policy off the per-document-type
> `acceptance_rules_overrides` row, by adding an optional `minAutonomyLevel` to
> `EscalationClass`. That shape only carries an action in the model when someone
> remembers to list it on some document type's rules row — the inverse of what the
> product requires, which is an exhaustive permissions-list-style catalog where every
> action is present regardless of the current floor, grouped, and assignable as a group
> or individually.
>
> Two further problems this story could not have solved in place:
> - It re-hardcoded the dial bound (`minAutonomyLevel` "validated [70,100]"), which
>   would have made `[70,100]` live in three places and blocked ever lowering the floor
>   cheaply. Epic 43 D3 makes it one named constant.
> - Its premise that the always-escalate gate is enforced was wrong.
>   `AcceptanceGuardrails.TryPreGate` has zero production call sites, so the list it
>   extended never executes. Epic 43 gives `TryPreGate` its first production caller and
>   absorbs the legacy list as a floor rather than extending it.
>
> **Superseded by:** `docs/stories/epic-43/README.md`.
> **Decision record:** `.dev/decisions/epic-43-action-catalog-design.md`.
> Its `AcceptorRequirement` observation remains valid and is carried as an Epic 43 risk:
> a second "pin this to a human" concept coexists with the catalog and is deliberately
> not folded in.

---

_Original content follows, retained for the reasoning it records._

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform operator / tenant admin**,
I want to configure **which classes of action require a human** and **at what autonomy level each stops requiring one** — through the existing admin surface, not by shipping a code change,
So that the 70–100 autonomy dial actually withholds ambiguity calls, deployments and design decisions at 70, and the list of what it withholds is my policy rather than a hardcoded constant.

## Priority

P0 — This is the decided behavior of the autonomy dial (product-owner decision,
2026-07-24, recorded in `docs/stories/epic-42/README.md`): the dial is **both**
prompt context **and** a hard gate on action classes. Nothing gates on it today.
Epic 42's Story 42-3 blocks on this for its largest piece.

## What already exists (READ THIS FIRST — do not rebuild it)

Story 39-5 shipped most of the mechanism. **This story is a reach + dial-sensitivity
change, not a new subsystem.**

| Piece | Where | State |
|---|---|---|
| Gate vocabulary | `AcceptanceRules.AlwaysEscalate: IReadOnlyList<EscalationClass>`, `EscalationClass(Kind, Key)` with `EscalationClassKind ∈ { document-type, agent-action }` | ✅ built |
| Key validation | `AcceptanceRules.Validate()` parses each key against `DocumentTypeKeyExtensions.Parse` / `AgentActionExtensions.Parse` — a typo'd class fails loud with `ACCEPTANCE_RULES.INVALID` | ✅ built |
| Enforcement | `AcceptanceGuardrails.TryPreGate` matches the class list and short-circuits to `Escalate(AlwaysEscalateClass)` **before any acceptor runs** | ✅ built |
| Admin surface | `PUT /api/acceptance-rules/{documentTypeKey}` (`AcceptanceRulesUpsertRequest.AlwaysEscalate`), plus GET/DELETE-to-reset | ✅ built |
| Both scoping models | `AcceptanceRulesService` mirrors `PromptStoreService`'s single-user / `ForTenant` split; a **base row** (`documentTypeKey = null`) resolves as `PrincipalDefault` | ✅ built |
| The dial itself | `AcceptanceRules.AutonomyLevel`, validated `[70, 100]`, default 70 | ✅ built, **⛔ nothing reads it** |

The action vocabulary already covers the named classes exactly:
`score-ambiguity` · `deploy` · `rollback` · `plan-deployment` · `configure-cicd` ·
`propose-design` · `write-adr` · `plan-system-design` · `design-api-contract` — 80
actions in total, so "what to gate" is expressible today without extending the enum.

39-5's own design note already states the principle: *"whether breaking changes always
escalate is acceptance-rules configuration, not a hardcoded rule."* This story finishes
delivering on it.

## The three real gaps

### Gap 1 — the gate is binary, the dial is a range

`AlwaysEscalate` means *always*, unconditionally. The decision is that a class is gated
**below a level and released above it** ("certain actions and llm calls can't be made if
it's like 70%"). There is no way to express "gate `deploy` until autonomy reaches 90".

### Gap 2 — the gate only fires at the document accept gate

`TryPreGate` runs inside the acceptance path. An agent about to run `deploy` or
`propose-design` **outside** a document lifecycle is not gated at all. The dial's promise
covers `llm-call`s and workflow actions, not only document acceptance.

### Gap 3 — action-class gates have no natural home row

`AlwaysEscalate` lives on a per-`documentTypeKey` rules row. An `agent-action` gate on the
`plan` row is nonsense — it only matches when *that document type's* accept gate happens to
run with that action. Non-document-scoped action gating needs to resolve from the **base
row**, which already exists but is not used this way.

## Acceptance Criteria

1. **Dial-sensitive gating, expressed as configuration.** `EscalationClass` gains an
   OPTIONAL `minAutonomyLevel: int?` (wire `minAutonomyLevel`). Semantics: the class is
   gated when `AutonomyLevel < minAutonomyLevel`; it is released at or above it.
   **`null` means always gated** — the exact current meaning of `AlwaysEscalate` — so every
   existing row and every construction that omits the field keeps today's behavior. Validated
   `[70, 100]` when present (same bounds as the dial), rejected fail-loud with
   `ACCEPTANCE_RULES.INVALID` otherwise, per the file's existing convention.

2. **Configurable through the existing admin surface — no new surface.** The field is
   settable on `PUT /api/acceptance-rules/{documentTypeKey}` and on the base row, readable on
   GET, and reset by DELETE. The property is **trailing and defaulted** on
   `AcceptanceRulesUpsertRequest` so bodies written before it still bind. `AcceptanceRulesManage`
   remains the authorizing policy; **no gating rule is hardcoded anywhere** — a test asserts the
   shipped defaults are the only in-code gate list and that it is fully replaceable over the API.

3. **Both scoping models, explicitly.** Single-user: the sole user's rows own the gate list.
   SaaS: the tenant's rows own it, `tenant_owner`/`tenant_admin` only (member ⇒ 403), following
   `AcceptanceRulesService`'s existing `ForTenant` split. A test covers each mode resolving a
   different gate list for the same class.

4. **Base-row resolution for action classes (Gap 3).** Action-class gates are resolved from
   the **base row** for any dispatch that is not scoped to a document type; per-type rows may
   still carry them and win for that type's accept gate. The precedence — per-type row → base
   row → shipped default — is tested at all three tiers, including the "per-type row is silent,
   base row gates" case.

5. **The gate reaches action dispatch, not just acceptance (Gap 2).** A pure decision
   component (e.g. `AutonomyGate` in `Tamma.Core/Documents/Policy/`, alongside
   `AcceptanceGuardrails` — no I/O, no Elsa, no config read) answers
   `(action, resolvedRules) → Allowed | RequiresHuman(reason)`, and is consulted **before** the
   provider call on the `llm-call` path and before a gated workflow action runs. A denial
   produces the existing escalation/approval surface (39-8), never a silent skip and never a
   partially-executed action.

6. **Fail-closed.** If the rules cannot be resolved (store unavailable, corrupt row that fails
   `Validate()`), a gated-class action is **denied**, not allowed. This matches `ManagedAgent`'s
   existing rule — "if budget or credential cannot be evaluated, deny (never call the provider)".
   A test drives an unavailable store and asserts denial + a diagnostic reason.

7. **Shipped defaults are safe at 70 and stated.** `AcceptanceDefaults` ships a base-row gate
   list covering at minimum the decided classes — deployment (`deploy`, `rollback`), ambiguity
   (`score-ambiguity`) and design decisions (`propose-design`, `write-adr`) — each with an
   explicit `minAutonomyLevel`, and the exact list + levels are pinned by
   `AcceptanceDefaultsDriftTests` so a default cannot drift unnoticed. **The list is a default,
   not a rule**: AC2's replaceability test proves an admin can empty it entirely.

8. **Audited.** Every gate denial emits a DCB event carrying `{ action, class, autonomyLevel,
   minAutonomyLevel, rulesSource, tenantId }` so "why was this withheld?" is answerable from the
   event stream alone, and so a deployment's gate history is auditable. Follows the existing
   `AGGREGATE.ACTION.STATUS` convention and the `RepairRingEventTypes` single-source pattern.

9. **The dial is still prompt context.** AC5's gate is additive to — not a replacement for —
   the dial appearing in `DecisionGuidance` / `RoutingGuidance`. Both halves of the decided
   behavior ship; a test asserts the resolved autonomy level still reaches the prompt.

## Design Decisions

- **D1 — extend `EscalationClass`, don't add a parallel list.** A second
  `GatedBelowAutonomy` collection alongside `AlwaysEscalate` would give two places to look and
  two precedence rules. One optional field on the existing record keeps a single gate list, and
  `null` preserves the current meaning exactly.
- **D2 — a new pure component, not a change to `AcceptanceGuardrails.Clamp`.** The clamp can
  only convert a decision to `Escalate(reason)`, and `AcceptanceEscalationReason` is deliberately
  closed and count-pinned at six (39-5 D10) — the same constraint that blocked wiring
  `AcceptorRequirement` there (`.dev/findings/assessment-family-policy-gaps.md` #2). The
  action-dispatch gate is a different question ("may this run?") from the acceptance clamp
  ("is this decision legal?"), so it gets its own pure component and its own vocabulary.
- **D3 — `AcceptorRequirement` stays 39-17's consumer.** It answers *who accepts a document*,
  which is orchestrator routing. Out of scope here; noted so the two floors are not conflated.

## Dependencies

- **39-5 (Acceptance Rules)** — ✅ landed. Supplies the vocabulary, validation, admin surface,
  base row, and both scoping models. Without it this story would be 3× the size.
- **39-8 (Escalation & Approval Surface)** — ✅ landed. AC5's denial routes into it.
- **39-17 (Orchestrator Agent)** — independent. 39-17 consumes `AcceptorRequirement` and the
  dial for *acceptance routing*; this story gates *action dispatch*. Either may land first.
- **Epic 42 Story 42-3** — blocked on this. 42-3 modeled the floor for tools only; ambiguity
  and design decisions are `llm-call`s, so the floor belongs in this policy layer and 42-3
  consumes it rather than reimplementing it.

## Out of Scope

- Extending the 80-action `AgentAction` vocabulary — the decided classes are all already
  expressible.
- Per-user gate overrides in SaaS — same reasoning as the Prompt Store's deliberate absence
  of a per-user override layer (audit/compliance simplicity).
- Tool-level authorization — Epic 42 owns the tool descriptor/family model; this story gates
  the action, and 42-3 reads the same resolved floor.

## Est. Effort

5–7 days.

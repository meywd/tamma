# Epic 43 — Action Catalog: design decisions

**Date**: 2026-07-25
**Status**: Accepted
**Deciders**: product owner (D1–D3), Claude (S1–S6, mechanism choices)

## Context

The autonomy dial (`AcceptanceRules.AutonomyLevel`, 70–100) is stored, validated, defaulted and
displayed, and nothing reads it to decide anything. `AcceptanceGuardrails.TryPreGate` — the component
that would consume it — has zero production call sites. The consuming layer was never built; Epic 43
is that layer.

The product requirement is a permissions-list-style catalog of every action the system can take,
grouped, where an admin sets per group or per action what the system may do by itself at a given
autonomy level and what a person decides — with actions already automated at the current floor still
listed (greyed) so a future lower floor needs no redesign.

## Decisions

### D1 — v1 enforces, with behaviour-preserving defaults

The gate is **live in v1**. Every action ships assigned exactly as it behaves today, so nothing
changes on day one; admins opt into gating and it takes effect immediately.

**Rejected: declarative v1** (catalog + admin + audit, enforcement later). It leaves the dial gating
nothing, which is the problem being solved. The design that came out of the judged panel proposed
this shape in disguise — enforcement present but switched off per-target, with the flip gated on a
30-day observation soak. Under it an admin who sets deploy to human-only gets nothing until the flip.
Overridden.

**Rejected: enforce with pre-gated safe defaults** (ship deploy/rollback/MCP already gated). Two
behaviour changes in one release — the first thing in the codebase able to block a running workflow,
*and* a set of newly-gated actions.

The observation soak survives as advice about tightening *defaults*, not as a precondition for the
gate existing.

**One permanent exception:** the llm-call seam is observe-only in every version. A requires-human
returned there reaches a dispatch whose calling workflow has no human route in 44 of 45 cases —
escalation into a void — and it would double-gate deploy against the Elsa-graph seam. Enforcement for
agent-actions lives only where a real human wait exists.

### D2 — Unclassified is allowed at runtime, unmergeable in CI

A capability that performs an action with no catalog entry **fails CI**. If one reaches runtime
anyway it is **allowed**, not blocked: strictness where it is cheap, tolerance where it is expensive
(a live workflow stalling on a catalog gap).

**Rejected: fail-closed at runtime.** Any gap becomes a production stall, and the existing tool path
is fail-open (a null allowlist permits every tool), so this would be a large silent behaviour change.

**Rejected: a Roslyn analyzer**, which is how "blocked at build" was originally specified. Rejected on
measurement, not preference:

- **79 of the 200 mutating `Map*` calls in `Program.cs` terminate `);` on the same line** with no
  fluent chain to inspect — a ~40% structural miss rate for any rule keyed on "the chain contains X".
- `app.MapControllers()`, `app.MapHub<…>` and Elsa's `UseWorkflowsApi()` are invisible to syntax
  analysis; the last runs in a different process.
- A "body must call the gate" rule cannot apply to interface *declarations*, and is satisfiable by any
  dead or unreached call.

An analyzer with a 40% blind spot is worse than none: a completeness guarantee that is not complete is
the precise failure this epic exists to prevent. CI blocks the merge either way; what is lost is
local-build feedback. The one good analyzer idea — flagging an activity that carries no `[Activity]`
attribute — is taken as a reflection test instead, which is cheaper and equally effective.

### D3 — No lower bound in the model; `[70,100]` as one named constant

The catalog and policy model carry no lower bound. The validated range stays `[70,100]`, expressed
once in `AutonomyDial`, so widening downward later is a single edit.

Consequences:
- No story may hardcode 70 or 100 a second time. Two unlanded specs currently would — Story 39-23's
  `minAutonomyLevel` and Story 42-1's `AutonomyFloor`. Both are corrected or superseded here.
- No DB CHECK on the threshold column: a CHECK lives in a migration snapshot and is a permanent second
  hardcoding.
- When the range is eventually widened to include 0, the corrupt-row test vector that uses `5` becomes
  legal and needs replacing.
- `AlwaysHuman = Max + 1` is derived from `Max`, so "one edit" holds **downward only**. Raising `Max`
  would silently reinterpret every stored sentinel as an ordinary threshold. Only the downward case
  was asked for; the claim is not unconditional.

## Settled without the product owner

- **S1 Union catalog** across agent-actions, tools, external effects, document types, platform tasks
  and background automation. Not agent-actions only — anything less leaves the highest-consequence
  surfaces outside the catalog.
- **S2 Single source of truth.** Epic 42's tool governance becomes a consumer. Twelve concrete
  duplications resolved; 42-2 and 42-3 deleted.
- **S3 Level-independent storage, level-parameterized display.** One integer per action, not a
  level×action matrix. A matrix is 4,743 cells per principal, admits non-monotone policy, and costs 70
  new cells per action to widen the floor where a threshold costs zero. The per-level editing
  experience is identical — the admin clicks a row, never types a number.
- **S5 Groups assignable as a whole; an individual action overrides its group** (`??`, not `max()` —
  which is what "override" means, and what the existing rules resolver already does). The consequence,
  that an admin can lower one action below its group, is a recorded risk with UI mitigation.
- **S6 Both operating modes.**

## Mechanism choices worth recording

- **Composite `ActionKey`, not a flat enum.** A flat ~153-member enum would copy all 80 agent-action
  wire strings into a second vocabulary — the exact drift the epic prevents.
- **Bidirectional drift checks.** The catalog is derived from the code, so both directions hold when
  written; if code is deleted the check says to delete the entry. The judgment call is at authoring
  time: do not catalogue a placeholder workflow as a real capability.
- **Control-plane residency, forced not preferred.** Tenant residency fails three ways: hosted
  services have no ambient tenant context, the engine plane may carry no tenant at all, and a new
  tenant migration never reaches already-provisioned tenants (the migrator runs only on the two
  tenant-creation paths; there is no startup sweep).
- **Deliberate exclusion from the destructive startup DROP list.** Every other table on it is
  operational data; these are the only thing between an agent and a production deploy, and the list
  runs on every restart. Tested.
- **`409` on denial, never `202`.** The mediation client branches solely on success status and `202`
  is already a success code on it — a 202 escalation is indistinguishable from success.
- **Named `AutonomyGate*`, never `ActionGate*`** — `Tamma.Activities.Security.ActionGate` is a shipped
  DI-registered type and the name collides inside `Tamma.Api`.
- **`AlwaysEscalate` absorbed, not deleted.** It is shipped, DTO-exposed, UI-editable and has a live
  production producer. It becomes a floor composed with `max()`, so a legacy entry cannot be lowered
  by the new surface.

## Consequences

- The reconciliation deletes two Epic 42 stories and rewrites four, plus supersedes Story 39-23.
- Reconciling the three disagreeing tool vocabularies is a **privilege expansion, not a cleanup** —
  tools advertised under Claude-Code names currently cannot execute at all.
- Two holes stay open and are documented rather than hidden: `file_write`/`shell_execute` are
  undifferentiated members that bypass finer gates, and production deploy is an LLM tool loop rather
  than a typed activity, so gating it gates the stage transition and not the deploy.

## Related

- `docs/stories/epic-43/README.md`
- `docs/stories/epic-39/story-39-23/` (superseded)
- `docs/stories/epic-42/README.md` (reconciliation)

# Story 43-13: Caller-Kind Predicate — the Dial Governs the LLM Only

Status: drafted

Implements: Story 43-11 **Amendment 4** ("Who the dial governs: the LLM, and nothing else") and the **Caller-kind re-audit** (120 LLM / 7 HUMAN / 28 DUAL / 42 MACHINERY).

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **human admin using the dashboard, and as the platform's own background machinery**,
I want the autonomy gate to apply only when an LLM is the actor — never to me clicking a button, never to a deterministic service doing its job,
So that the dial is a control on model autonomy, not a lock on the product.

## Priority

P0 — without this predicate, every level assigned by 43-11 gates the wrong callers: a person cancelling their own mentorship session would hit a 90-zone gate, and 42 background services would need dial positions that mean nothing for them. Amendment 4 is the rule; nothing enforces it yet.

## Architectural Context (READ FIRST)

- **The rule (43-11 Amendment 4, verbatim in substance):** three caller kinds — a human (never gated), deterministic automation (never gated; the approval was the human writing/merging/configuring it, or an upstream gated LLM decision), an LLM/agent (the only nondeterministic actor; the dial exists for it alone). Gate where the LLM **decides**; everything deterministic downstream of a passed gate inherits the approval.
- **What the gate knows today.** Seam C's middleware resolves a `GovernancePrincipal(TenantId, UserId)` via `IGovernancePrincipalResolver` (`apps/tamma-elsa/src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs:242-287`, resolver at `apps/tamma-elsa/src/Tamma.Api/Services/Actions/GovernancePrincipalResolver.cs:39`). That is a *scope*, not a *kind* — it cannot tell a human JWT from the engine's service token. The engine authenticates with the service-scope `Tamma:ApiToken` Bearer via `TammaEngineAuthHandler` (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaEngineAuthHandler.cs`); humans carry a user JWT; background services call the evaluator in-process (Seam D).
- **The design: one predicate, one enum, fail-closed to LLM.**
  - `CallerKind { Human, Machinery, Llm }`, resolved in exactly one place in the gate service and passed into `AutonomyGateEvaluator.Evaluate` (`apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:183`).
  - **Human**: the request principal is an authenticated *user* (user JWT / dashboard credential), not the engine token. Result: the dial is never consulted; the decision reason is a new `ReasonCallerHuman`, and the request proceeds subject to ordinary RBAC only.
  - **Machinery**: only the named in-process actors (the Seam D opt-ins — `RevealTokenSweeper.cs:64`, `ChannelOutboxSweeper.cs:77`, `OutboxSlackSender.cs:133`, `TaskQueueProcessor.cs:94`, `OutboxSmtpSender.cs:153` — and future registrations through the same helper) declare themselves machinery. Seam D keeps its Amendment-4 job: deny only where a background job would execute an LLM decision that was never gated upstream.
  - **Llm**: everything else — in particular **every engine-token call defaults to Llm**. This is deliberately fail-closed: deterministic workflow steps share `TammaApiClient` with LLM-driven steps, and until a call is provably human or declared machinery, the gate treats it as the model acting. A deterministic engine step that is wrongly gated is a visible nuisance; an LLM call that is wrongly waved through is the failure mode this epic exists to prevent.
- **The 42 machinery catalog rows never resolve through the dial.** Per the re-audit's machinery inventory (29 `automation:*`, 8 `platform-task:*`, 5 plumbing-only effects), those descriptors keep key/group/risk/site for audit and drift, but carry **no level semantics**: the evaluator short-circuits them (a new terminal reason, e.g. `ReasonMachineryNotDialGoverned`) without reading dial, ladder, or ceiling. The `automation:*` mid-range-threshold API rules (`ActionPolicyEndpoints.cs:569-625`) become moot for these targets — a threshold write on a machinery row is a 400 naming this story.
- **The 7 dormant HUMAN rows** (`effect:schedule.create|update|delete`, `effect:mentorship.session.start|pause|resume|cancel` — re-audit Level 20 table) keep their levels, which bind **only if an LLM path ever reaches those routes** (e.g. the shell-curl bypass, which the gate should then catch). A human on those routes always passes.
- **The 28 DUAL rows** (tracker effects, document-type acceptances, `mcp.tool.invoke`) are the reason the predicate lives at the gate and not in the catalog: the same route gates one caller and passes another.

## Acceptance Criteria

1. **One predicate, single-sourced.** `CallerKind` is resolved in exactly one function in the gate service; both Seam C (`GovernanceEnforcement`) and Seam B/D callers pass a `CallerKind` into `AutonomyGateEvaluator.Evaluate`. A grep proves no second site computes caller kind from auth state.
2. **A human caller on a governed route passes; an LLM caller on the same route is gated — pinned both directions.** For at least one DUAL route (a tracker write) and one HUMAN route (`PUT` schedule), a test drives the same request once with a user JWT (passes at dial `Min`, reason `ReasonCallerHuman`) and once with the engine token (gated when level > dial). Both assertions in one test class so they cannot drift apart.
3. **Engine-token calls default to Llm.** A test calls a governed route with the engine token and no explicit caller-kind declaration and asserts the dial was consulted. Removing the fail-closed default fails this test.
4. **The 42 machinery rows never consult the dial.** A test enumerates the machinery inventory (the re-audit's 5 + 29 + 8 list, carried as a fixture) and asserts, for each, that `Evaluate` returns the machinery terminal reason at dial 1 and dial 100 alike — identical decisions at both extremes is the proof the dial is not in the path. The fixture doubles as the drift pin: a descriptor moving between dial and machinery sections without editing the fixture fails.
5. **Threshold writes on machinery targets are rejected** with a 400 naming the machinery classification; the old two-state (`Min`/`AlwaysHuman`) validation for `automation:*` (`ActionPolicyEndpoints.cs:600-625`) is removed as moot, per the re-audit's consequence list.
6. **Seam D semantics unchanged for its job**: the five opted-in actors still deny (never escalate) when executing an un-gated upstream LLM decision; their existing tests pass unmodified except for the explicit machinery declaration.
7. **The 7 dormant HUMAN rows are pinned dormant**: a fixture lists exactly those seven keys; a test asserts a human caller passes each at dial `Min`. Adding an LLM caller path to one of those routes without updating the fixture is the intended failure.
8. **Audit rows carry the caller kind.** Every gate decision event gains a `callerKind` tag so the trail distinguishes "passed because human" from "passed because automated at level".
9. **`dotnet test` green; no count pins move** (no catalog membership changes in this story).

## Dependencies

- **Story 43-11** — the caller-kind classification table (120/7/28/42) is this story's fixture content. Blocking.
- **Story 43-9** — the seams and the evaluator signature this story extends. Landed.
- **Story 43-14** — grants are minted for LLM callers; the ledger consume path reads the same `CallerKind`. Coordinate the evaluator signature change.
- **Verified in tree**: `GovernanceEnforcement.cs:242-287`; `GovernancePrincipalResolver.cs:29-80`; `TammaEngineAuthHandler.cs`; `AutonomyGateEvaluator.cs:124-183`; `ActionPolicyEndpoints.cs:569-625`; the Seam D call sites listed in 43-11 §5.

## Out of Scope

- Reclassifying any row between LLM/HUMAN/DUAL/MACHINERY — the re-audit's table is input; changes to it are 43-11 amendments.
- Argument-level grading of shell/git calls (42-10 and the recorded `git_operations` holes).
- A per-user "gate me anyway" preference — no such product feature exists.

## Estimated Effort

3 days — 1 for the enum + resolver + evaluator signature, 1 for the machinery short-circuit and API rejection, 1 for the two-direction pins, fixtures, and audit tag.

## Change Log

| Date       | Version | Changes                                              | Author |
| ---------- | ------- | ---------------------------------------------------- | ------ |
| 2026-08-02 | 1.0.0   | Initial story — caller-kind predicate per Amendment 4 | Claude |

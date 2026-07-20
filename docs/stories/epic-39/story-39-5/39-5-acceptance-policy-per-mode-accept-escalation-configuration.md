# Story 39-5: Acceptance Policy — per-mode accept/escalation configuration

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

As a **platform operator (full-auto) or supervising user (supervised mode)**,
I want a **configurable acceptance policy that answers "who decides a document is accepted" per operating mode — full-auto auto-accepting on an approving `Review` verdict with explicit escalation rules, supervised requiring a human gate — with bounded revision rounds and per-document-type overrides**,
So that "who decides" stops being an implicit property of each workflow's parse code and becomes stated, once, as policy: the document's validator, then a `Review` about it, then this policy — with humans in full-auto sitting only at intent, policy, and exceptions (the README's three positions).

## Priority

P0 — The 39-6 lifecycle's ACCEPT GATE is a direct consumer: without a policy model it has no way to branch full-auto vs supervised, no round bound, and no escalation criteria. 39-7 reads reviewer-selection from policy; 39-8's escalation surface fires on the outcomes this policy defines. Ships before 39-6 or in lockstep with it.

## Architectural Context (READ FIRST)

Policy is **configuration over the static vocabulary** — the README's "vocabulary static, composition dynamic" principle. The model (types, defaults) lives in `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/`; resolution/storage plumbing follows the prompt-store precedent in `Tamma.Api`.

**The per-mode ownership pattern to mirror (do not invent a new one):**

- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `TammaMode` + `ITammaModeProvider`: the process-stable single-user vs SaaS detection every mode-aware feature uses
- The `prompt_overrides` storage pattern (CLAUDE.md "Prompt Store Architecture" + `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`): **single-user mode keys overrides by `user_id`; SaaS mode keys by `tenant_id`; exactly-one XOR CHECK; static system defaults shipped in code/files with a per-principal override layer in Postgres.** The CLAUDE.md universal rule applies verbatim: design both scoping models, never ship the single-user model and assume it works for SaaS.
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — the resolution-order facade shape (principal override → system default) this story's resolver mirrors
- RBAC precedent: in SaaS, policy writes are `tenant_owner`/`tenant_admin` only, members read-only — same matrix as the prompt store (CLAUDE.md RBAC table)

**Operating-mode axes are two, not one.** Single-user vs SaaS decides *who owns the config* (`ITammaModeProvider`). Full-auto vs supervised decides *how the accept gate behaves* and is itself a policy value (defaulting to supervised, per the epic README's "supervised (70%)" reality). Do not conflate them.

**Consumers to design against (interfaces only in this story):**

- 39-6 `DocumentLifecycleWorkflow` — reads `AcceptanceDecision` at the ACCEPT GATE; reads `MaxRevisionRounds`/`MaxRepairAttempts` bounds; receives escalation criteria (e.g. ambiguity threshold feeding `AmbiguityAboveThreshold`)
- 39-7 review producers — read reviewer-role selection and panel composition from policy
- 39-8 escalation surface — receives the policy-driven "always escalate this class" routing (e.g. breaking changes), which the README pins as **configuration, not a hardcoded rule**

## Acceptance Criteria

1. **Policy model in `Tamma.Core`.** An `AcceptancePolicy` record (+ nested records) in `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptancePolicy.cs` expressing at minimum: `GateMode` (`FullAuto | Supervised`); full-auto rules (auto-accept when the accepted `Review.decision` is approve; what to do on `RequestChanges` — revise; on `NeedsDiscussion` — escalate); `MaxRevisionRounds` and `MaxValidationRepairAttempts` (both bounded, defaults documented); escalation criteria including an ambiguity-score threshold and a configurable list of always-escalate document/action classes; and reviewer selection (single reviewer role vs panel composition + quorum reference) for 39-7. All enums closed; no stringly-typed knobs.

2. **Per-document-type overrides.** The effective policy for a `(documentTypeKey)` resolves as: per-type override → policy default — so e.g. `Decomposition` can run full-auto while `Design` stays human-gated in the same deployment. Unknown document-type keys in an override are rejected fail-loud against the 39-2 `DocumentTypeRegistry` (a typo cannot silently create dead config).

3. **Static defaults shipped in code.** A complete, sensible default policy ships as static data (supervised gate mode; conservative round bounds; `NeedsDiscussion` ⇒ escalate; blocking classes empty), so a deployment with zero configuration behaves safely. A drift test pins the default values the way `RolePhaseMapTests` pins counts — changing a default is a conscious, reviewed edit.

4. **Two scoping models, explicitly.** Policy configuration storage keys by `user_id` in single-user mode and `tenant_id` in SaaS mode (XOR, mirroring `prompt_overrides`' `principal_xor` CHECK), resolved via `ITammaModeProvider`. The story delivers the resolution seam (`IAcceptancePolicyResolver` returning the effective policy for the current principal + document type); whether the override rows land in an existing config table or a new `acceptance_policy_overrides` table is decided here with the schema documented — but the per-mode ownership answer for BOTH modes is written down before any code, per the CLAUDE.md universal rule.

5. **RBAC parity with the prompt store.** In SaaS mode, policy reads are available to any tenant member; policy writes are `tenant_owner`/`tenant_admin` only (403 for members). In single-user mode the sole user owns everything. Enforced at the (thin) API surface this story adds or explicitly deferred to the story that exposes the endpoints — either way, the matrix is stated and tested where implemented.

6. **Pure decision function.** The accept-gate decision is exposed as a pure, unit-testable function — illustratively `AcceptanceDecision Decide(AcceptancePolicy effective, DocumentEnvelope doc, Review review, int roundsUsed)` returning `Accept | Revise(notes) | Escalate(reason) | RequireHuman` — with no I/O, no Elsa dependency, so 39-6 calls it and tests cover the full matrix: approve/full-auto ⇒ Accept; approve/supervised ⇒ RequireHuman; RequestChanges under round budget ⇒ Revise; rounds exhausted ⇒ Escalate(RoundsExhausted); NeedsDiscussion ⇒ Escalate(ReviewUndecidable); always-escalate class ⇒ Escalate regardless of verdict.

7. **Round bounds are hard.** No policy configuration can express an unbounded loop: `MaxRevisionRounds`/`MaxValidationRepairAttempts` have enforced ceilings (validation rejects absurd values), and `Decide` provably terminates in `Accept`/`Escalate`/`RequireHuman` within the bound — property-style test over arbitrary verdict sequences.

8. **Blocking-review invariant respected.** `Decide` can never return `Accept` for a `Review` that 39-4's validator would reject (blocking issues + approve) — the policy layer re-asserts the invariant defensively, and a test pins that a forged approve-with-blockers review cannot be auto-accepted.

## Technical Notes

- Keep the policy *model* (`Tamma.Core`, no dependencies) separate from the policy *resolver/storage* (`Tamma.Api`/`Tamma.Data`) — 39-6 runs in the Elsa server process and should depend only on the model + a resolver interface.
- The always-escalate class list is how the README's "whether breaking changes always escalate is acceptance-policy configuration, not a hardcoded rule" lands — express it as document-type keys and/or `AgentAction` wire names, validated against the registries.
- Supervised-mode `RequireHuman` is the input to 39-8's bookmark suspend; full-auto `Escalate` is the input to 39-8's escalation events. This story defines the decisions; 39-6/39-8 wire the machinery.
- If override storage lands in Postgres in this story, mirror the migration discipline of the prompt/audit stories: additive migration, `dotnet ef migrations has-pending-model-changes` reports none, config in `TammaModelConfiguration.cs`.

## Dependencies

- **Prerequisite:** 39-2 (registry for type-key validation, envelope), 39-4 (`Review` decision enum the gate reads).
- **Prerequisite (in place):** `ITammaModeProvider` / `TammaMode.cs`; the `prompt_overrides` per-mode XOR pattern; CLAUDE.md two-scoping-models rule.
- **Feeds:** 39-6 (ACCEPT GATE calls `Decide`), 39-7 (reviewer selection/panel composition), 39-8 (escalation routing + human-gate suspend).

## Estimated Effort

3–4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |

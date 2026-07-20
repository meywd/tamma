# Story 39-5: Acceptance Rules — configurable policy, admin UI, orchestrator read path

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
I want **acceptance rules that are configurable, editable in the admin UI, and read by the acceptor at decision time — where the accept gate always submits the document to an acceptor (the orchestrator in full-auto, a human in supervised mode) and the orchestrator receives the resolved rules either rendered into its decision prompt or fetched via MCP**,
So that "who decides" is always an actor applying stated, inspectable rules — never an if-else in workflow code that skips the decision — and changing what "acceptable" means is a configuration edit in the admin UI, not a code change.

## Priority

P0 — The 39-6 lifecycle's ACCEPT stage is a direct consumer: without the acceptor contract and the rules model it has no decision step, no round bound, and no escalation criteria. 39-7 reads reviewer-selection from the rules; 39-8's escalation surface fires on the outcomes defined here. Ships before 39-6 or in lockstep with it.

## Architectural Context (READ FIRST)

**The settled design (do not regress to an if-else):** the lifecycle's accept stage ALWAYS submits the finished document (plus its `Review` and lineage) to an **acceptor**. The operating mode selects only WHO that acceptor is:

- **full-auto** → the orchestrator is the acceptor: an `llm-call` decision turn whose prompt carries the resolved acceptance rules (or which fetches them via an MCP tool), returning a typed `AcceptanceDecision`.
- **supervised (70%)** → a human is the acceptor, via the 39-8 bookmark suspend/resume gate.

Deterministic code enforces only the **hard guardrails around** the acceptor's decision — round bounds, the blocking-review invariant, always-escalate classes. It validates and clamps the decision; it never makes it.

Rules are **configuration over the static vocabulary** — the README's "vocabulary static, composition dynamic" principle. The model (types, defaults) lives in `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/`; resolution/storage/API plumbing follows the prompt-store precedent in `Tamma.Api`; the admin editing surface lands in the admin dashboard.

**The per-mode ownership pattern to mirror (do not invent a new one):**

- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `TammaMode` + `ITammaModeProvider`: the process-stable single-user vs SaaS detection every mode-aware feature uses
- The `prompt_overrides` storage pattern (CLAUDE.md "Prompt Store Architecture" + `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`): **single-user mode keys overrides by `user_id`; SaaS mode keys by `tenant_id`; exactly-one XOR CHECK; static system defaults shipped in code/files with a per-principal override layer in Postgres.** The CLAUDE.md universal rule applies verbatim: design both scoping models, never ship the single-user model and assume it works for SaaS.
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — the resolution-order facade shape (principal override → system default) this story's resolver mirrors
- RBAC precedent: in SaaS, rule writes are `tenant_owner`/`tenant_admin` only, members read-only — same matrix as the prompt store (CLAUDE.md RBAC table)
- The orchestrator read path precedent: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`'s conventions resolution (Story 27-13) — config resolved server-side and injected as a `{{conventions}}` prompt variable. The `{{acceptanceRules}}` injection follows the same seam.

**Operating-mode axes are two, not one.** Single-user vs SaaS decides *who owns the config* (`ITammaModeProvider`). Full-auto vs supervised decides *who the acceptor is* and is itself a configured value (defaulting to supervised, per the epic README's "supervised (70%)" reality). Do not conflate them.

**Consumers to design against:**

- 39-6 `DocumentLifecycleWorkflow` — its ACCEPT stage submits to the acceptor defined here; reads `MaxRevisionRounds`/`MaxRepairAttempts` bounds; receives escalation criteria (e.g. ambiguity threshold feeding `AmbiguityAboveThreshold`)
- 39-7 review producers — read reviewer-role selection and panel composition from the rules
- 39-8 escalation surface — hosts the human-acceptor suspend gate and receives the rules-driven "always escalate this class" routing (e.g. breaking changes), which the README pins as **configuration, not a hardcoded rule**

## Acceptance Criteria

1. **Rules model in `Tamma.Core`.** An `AcceptanceRules` record (+ nested records) in `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs` expressing at minimum: `GateMode` (`FullAuto | Supervised` — selecting the acceptor actor, never whether the gate runs); **decision guidance** for the orchestrator-acceptor (the rule content it reasons over: what warrants acceptance, revision, or escalation per document type — structured knobs plus operator-authored guidance text); `MaxRevisionRounds` and `MaxValidationRepairAttempts` (bounded, defaults documented); escalation criteria including an ambiguity-score threshold and a configurable list of always-escalate document/action classes; and reviewer selection (single reviewer role vs panel composition + quorum reference) for 39-7. All enums closed; no stringly-typed knobs outside the guidance text.

2. **The acceptor contract.** A closed `AcceptanceDecision` type (`Accept | RequestRevision(notes) | Escalate(reason)`) that BOTH acceptors return through the same seam (`IDocumentAcceptor` or equivalent): the **orchestrator acceptor** is an `llm-call` decision turn against a new dedicated taxonomy cell (a `decide-acceptance` action minted in `AgentAction`/`RolePhaseMap`; role assignment settled at implementation against the taxonomy; one cell = one contract — its prompt's output contract IS the `AcceptanceDecision` parser, bound in `ContractBindingTests`); the **human acceptor** is the 39-8 suspend/resume gate mapped onto the same decision type. The lifecycle code never branches into an auto-accept shortcut — a test asserts the accept stage submits to an acceptor in both modes.

3. **Orchestrator read path — prompt or MCP, same resolver.** The resolved effective rules reach the orchestrator-acceptor at decision time through one of two supported channels, both reading the same `IAcceptanceRulesResolver`: **(a) prompt injection (default)** — the rules rendered into the decision cell's `{{acceptanceRules}}` variable server-side in `LlmCallWorkflow`, exactly the `{{conventions}}` seam; **(b) MCP** — a `get_acceptance_rules` tool exposed to tool-enabled decision turns returning the same resolved payload. The story implements (a) fully and delivers (b) at least as the resolver-backed tool endpoint with the wiring documented, so a tool-enabled acceptor can switch channels without a model change. A test pins that both channels serialize the identical resolved rules.

4. **Per-document-type overrides.** The effective rules for a `(documentTypeKey)` resolve as: per-type override → default — so e.g. `Decomposition` can run full-auto while `Design` stays human-gated in the same deployment. Unknown document-type keys in an override are rejected fail-loud against the 39-2 `DocumentTypeRegistry` (a typo cannot silently create dead config).

5. **Static defaults shipped in code.** A complete, sensible default ships as static data (supervised gate mode; conservative round bounds; empty always-escalate classes; default decision guidance), so a deployment with zero configuration behaves safely. A drift test pins the default values the way `RolePhaseMapTests` pins counts — changing a default is a conscious, reviewed edit.

6. **Two scoping models, explicitly.** Rules storage keys by `user_id` in single-user mode and `tenant_id` in SaaS mode (XOR, mirroring `prompt_overrides`' `principal_xor` CHECK), resolved via `ITammaModeProvider`. The story delivers `IAcceptanceRulesResolver` returning the effective rules for the current principal + document type; the schema (`acceptance_rules_overrides` or equivalent) is documented, with the per-mode ownership answer for BOTH modes written down before any code, per the CLAUDE.md universal rule.

7. **Admin API + admin UI.** REST endpoints in the prompt-store shape (`GET /api/acceptance-rules` resolved list, `GET/PUT/DELETE /api/acceptance-rules/{documentTypeKey}`, `GET /api/acceptance-rules/defaults`) with RBAC parity: SaaS reads for any tenant member, writes `tenant_owner`/`tenant_admin` only (403 for members); single-user mode the sole user owns everything. An **admin dashboard screen** (React, `packages/dashboard`) lists the effective rules per document type, shows default-vs-override provenance, and edits gate mode, bounds, escalation criteria, always-escalate classes, and the decision guidance text — so the rules the orchestrator decides by are visible and editable by the operator, not buried in config files.

8. **Deterministic guardrails, not deterministic decisions.** A pure, unit-testable guardrail function (no I/O, no Elsa) wraps every acceptor decision: (a) pre-gate — always-escalate classes short-circuit to `Escalate` before any acceptor runs; rounds exhausted forces `Escalate(RoundsExhausted)`; (b) post-gate — an acceptor's `Accept` for a `Review` that 39-4's validator would reject (blocking issues + approve) is refused and converted to `Escalate`, pinned by a forged-approval test; `RequestRevision` beyond the round budget converts to `Escalate(RoundsExhausted)`. Property-style tests over arbitrary decision sequences prove termination in `Accept`/`Escalate` within the bounds; no configuration can express an unbounded loop (validation rejects absurd bound values).

## Technical Notes

- Keep the rules *model* (`Tamma.Core`, no dependencies) separate from the *resolver/storage/API* (`Tamma.Api`/`Tamma.Data`) — 39-6 runs in the Elsa server process and should depend only on the model + the resolver interface.
- The always-escalate class list is how the README's "whether breaking changes always escalate is acceptance-rules configuration, not a hardcoded rule" lands — express it as document-type keys and/or `AgentAction` wire names, validated against the registries.
- The `decide-acceptance` prompt cell follows every PR #475 rule: file-backed under `Prompts/{role}/decide-acceptance.md`, purpose-built body, output contract bound to the `AcceptanceDecision` parser in `ContractBindingTests`, count pins updated. Its `{{acceptanceRules}}` variable is resolved server-side — the caller never passes rules from the client.
- Supervised-mode submission feeds 39-8's bookmark suspend; `Escalate` decisions feed 39-8's escalation events. This story defines the acceptor contract + rules; 39-6/39-8 wire the machinery.
- Storage migration mirrors the prompt/audit discipline: additive migration, `dotnet ef migrations has-pending-model-changes` reports none, config in `TammaModelConfiguration.cs`.
- The MCP channel's transport (which MCP server hosts `get_acceptance_rules`, how tool-enabled cells reach it) may land as documented wiring rather than a shipped server if the platform's MCP surface is not yet in place — the requirement this story cannot skip is that the resolver seam serves both channels identically.

## Dependencies

- **Prerequisite:** 39-2 (registry for type-key validation, envelope), 39-4 (`Review` decision enum the guardrails read).
- **Prerequisite (in place):** `ITammaModeProvider` / `TammaMode.cs`; the `prompt_overrides` per-mode XOR pattern; the `{{conventions}}` injection seam in `LlmCallWorkflow`; CLAUDE.md two-scoping-models rule.
- **Feeds:** 39-6 (ACCEPT stage submits to the acceptor; guardrails around it), 39-7 (reviewer selection/panel composition), 39-8 (human-acceptor gate + escalation routing).

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
| 2026-07-20 | 2.0.0   | Redesign per review: accept gate always submits to an acceptor (mode selects the actor — orchestrator in full-auto, human in supervised — never an if-else); rules configurable + admin UI; orchestrator reads rules at decision time via prompt injection or MCP; pure `Decide` reframed as guardrails around the acceptor | Claude |

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

As a **platform operator tuning how autonomous Tamma is (and the users who receive the decisions it assigns)**,
I want **acceptance rules and an autonomy level (70–100) that are configurable, editable in the admin UI, and read when needed — where the accept gate always submits the document to the orchestrator over its real-time channel, and the orchestrator, reading the resolved rules + autonomy level through its tools, decides whether to decide itself or assign the decision as a task to an eligible user**,
So that "who decides" is always an actor applying stated, inspectable rules — never an if-else in workflow code that skips the decision — and changing what "acceptable" means is a configuration edit in the admin UI, not a code change.

## Priority

P0 — The 39-6 lifecycle's ACCEPT stage is a direct consumer: without the acceptor contract and the rules model it has no decision step, no round bound, and no escalation criteria. 39-7 reads reviewer-selection from the rules; 39-8's escalation surface fires on the outcomes defined here. Ships before 39-6 or in lockstep with it.

## Architectural Context (READ FIRST)

**The settled design (do not regress to an if-else, and not an llm-call):** the lifecycle's accept stage ALWAYS submits the finished document (plus its `Review` and lineage) as an `AcceptanceRequest` on the workflow↔orchestrator channel (39-18) and suspends on the 39-8 gate. There is **one routing path — through the orchestrator** (39-17): a long-running LLM process with the platform as its context and tools over git, the event store, logs, workflows, documents, and these acceptance rules. On each request the orchestrator reads the effective rules + **autonomy level** via its `get_acceptance_rules` tool and decides WHO decides:

- **decide itself** — answer a typed `AcceptanceDecision` directly (the higher the autonomy level, the more it decides itself);
- **assign to a user** — pick an assignee from the 39-20 eligible set (initiator or repo access, server-validated) and create a task that lands in that user's 39-19 Task View; the human's decision comes back through the same 39-8 resume surface.

Either way the flow is: persist request → publish to orchestrator → suspend → (orchestrator decides | orchestrator assigns → human decides) → guardrails → resume. The old binary full-auto/supervised split is subsumed: **autonomy level 70 ≈ the supervised baseline (nearly everything assigned to humans), 100 = full auto (the orchestrator decides everything the rules allow)** — a continuous dial, not a mode branch. If the orchestrator is unavailable, the request waits in the 39-18 outbox — suspended, never lost, never defaulted.

Deterministic code enforces only the **hard guardrails around** the acceptor's decision — round bounds, the blocking-review invariant, always-escalate classes. It validates and clamps the decision; it never makes it.

Rules are **configuration over the static vocabulary** — the README's "vocabulary static, composition dynamic" principle. The model (types, defaults) lives in `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/`; resolution/storage/API plumbing follows the prompt-store precedent in `Tamma.Api`; the admin editing surface lands in the admin dashboard.

**The per-mode ownership pattern to mirror (do not invent a new one):**

- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `TammaMode` + `ITammaModeProvider`: the process-stable single-user vs SaaS detection every mode-aware feature uses
- The `prompt_overrides` storage pattern (CLAUDE.md "Prompt Store Architecture" + `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`): **single-user mode keys overrides by `user_id`; SaaS mode keys by `tenant_id`; exactly-one XOR CHECK; static system defaults shipped in code/files with a per-principal override layer in Postgres.** The CLAUDE.md universal rule applies verbatim: design both scoping models, never ship the single-user model and assume it works for SaaS.
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — the resolution-order facade shape (principal override → system default) this story's resolver mirrors
- RBAC precedent: in SaaS, rule writes are `tenant_owner`/`tenant_admin` only, members read-only — same matrix as the prompt store (CLAUDE.md RBAC table)
- The server-side config-resolution precedent: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`'s conventions resolution (Story 27-13) — config resolved server-side, never trusted from the client. The rules payload embedded in each `AcceptanceRequest` follows the same discipline.

**Operating-mode axes are two, not one.** Single-user vs SaaS decides *who owns the config* (`ITammaModeProvider`). The **autonomy level (70–100)** decides *how much the orchestrator decides itself vs assigns to users* and is itself a configured value (defaulting to 70, the supervised baseline). Do not conflate them.

**Consumers to design against:**

- 39-6 `DocumentLifecycleWorkflow` — its ACCEPT stage submits to the acceptor defined here; reads `MaxRevisionRounds`/`MaxRepairAttempts` bounds; receives escalation criteria (e.g. ambiguity threshold feeding `AmbiguityAboveThreshold`)
- 39-7 review producers — read reviewer-role selection and panel composition from the rules
- 39-8 escalation surface — hosts the suspend gate every decision resumes through, and receives the rules-driven "always escalate this class" routing (e.g. breaking changes), which the README pins as **configuration, not a hardcoded rule**
- 39-17 orchestrator agent — the single routing point: decides itself or assigns per the autonomy level; carries `get_acceptance_rules` in its toolset
- 39-18 channels — transport for `AcceptanceRequest`/`AcceptanceDecision` and task-assignment notifications
- 39-19 Task View — where orchestrator-assigned decisions land for humans
- 39-20 access model — supplies the eligible-assignee set the orchestrator picks from (initiator or repo access; teams/roles/permissions respected)

## Acceptance Criteria

1. **Rules model in `Tamma.Core`.** An `AcceptanceRules` record (+ nested records) in `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs` expressing at minimum: **`AutonomyLevel` (int, validated 70–100** — 70 = supervised baseline, nearly everything assigned to humans; 100 = the orchestrator decides everything the rules allow; never a value that skips the gate); **decision + routing guidance** for the orchestrator (what warrants acceptance, revision, escalation — and what, at a given autonomy level, must be assigned to a human, per document type: structured knobs plus operator-authored guidance text); `MaxRevisionRounds` and `MaxValidationRepairAttempts` (bounded, defaults documented); escalation criteria including an ambiguity-score threshold and a configurable list of always-escalate document/action classes; and reviewer selection (single reviewer role vs panel composition + quorum reference) for 39-7. All enums closed; no stringly-typed knobs outside the guidance text.

2. **The acceptor contract — one routing path, through the orchestrator.** A closed `AcceptanceDecision` type (`Accept | RequestRevision(notes) | Escalate(reason)`) plus a routing decision type (`DecideSelf | AssignToUser(assignee, basis)`): the accept stage builds an `AcceptanceRequest` (document + `Review` + lineage + the resolved rules payload including autonomy level + the decision-session id), publishes it on the workflow↔orchestrator channel, and suspends on the 39-8 gate. The orchestrator either answers the `AcceptanceDecision` itself or assigns the decision to a 39-20-eligible user (creating the 39-19 task; assignment server-validated against the eligible set); whichever actor decides, the decision resumes the same gate. The lifecycle code never branches into an auto-accept shortcut, never embeds an accept-decision `llm-call`, and never routes around the orchestrator — a test asserts the accept stage publishes and suspends regardless of autonomy level.

3. **Orchestrator read path — a tool over the resolver.** The resolved effective rules reach the orchestrator-acceptor two mutually pinning ways, both from `IAcceptanceRulesResolver`: **(a)** a `get_acceptance_rules` tool in the 39-17 agent's toolset (MCP-style), which the agent calls at decision time; **(b)** the same resolved payload embedded in the `AcceptanceRequest` for context and audit (the decision event records the rules version decided under). A test pins that both serialize the identical resolved rules for the same principal + document type.

4. **Per-document-type overrides.** The effective rules for a `(documentTypeKey)` resolve as: per-type override → default — so e.g. `Decomposition` can run at autonomy 100 (orchestrator decides) while `Design` is pinned to human assignment in the same deployment. Unknown document-type keys in an override are rejected fail-loud against the 39-2 `DocumentTypeRegistry` (a typo cannot silently create dead config).

5. **Static defaults shipped in code.** A complete, sensible default ships as static data (autonomy level 70 — the supervised baseline; conservative round bounds; empty always-escalate classes; default decision + routing guidance), so a deployment with zero configuration behaves safely. A drift test pins the default values the way `RolePhaseMapTests` pins counts — changing a default is a conscious, reviewed edit.

6. **Two scoping models, explicitly.** Rules storage keys by `user_id` in single-user mode and `tenant_id` in SaaS mode (XOR, mirroring `prompt_overrides`' `principal_xor` CHECK), resolved via `ITammaModeProvider`. The story delivers `IAcceptanceRulesResolver` returning the effective rules for the current principal + document type; the schema (`acceptance_rules_overrides` or equivalent) is documented, with the per-mode ownership answer for BOTH modes written down before any code, per the CLAUDE.md universal rule.

7. **Admin API + admin UI.** REST endpoints in the prompt-store shape (`GET /api/acceptance-rules` resolved list, `GET/PUT/DELETE /api/acceptance-rules/{documentTypeKey}`, `GET /api/acceptance-rules/defaults`) with RBAC parity: SaaS reads for any tenant member, writes `tenant_owner`/`tenant_admin` only (403 for members); single-user mode the sole user owns everything. An **admin dashboard screen** (React, `packages/dashboard`) lists the effective rules per document type, shows default-vs-override provenance, and edits the **autonomy level (a 70–100 dial, with per-document-type overrides)**, bounds, escalation criteria, always-escalate classes, and the decision + routing guidance text — so the rules the orchestrator decides and routes by are visible and editable by the operator, not buried in config files.

8. **Deterministic guardrails, not deterministic decisions.** A pure, unit-testable guardrail function (no I/O, no Elsa) wraps every acceptor decision: (a) pre-gate — always-escalate classes short-circuit to `Escalate` before any acceptor runs; rounds exhausted forces `Escalate(RoundsExhausted)`; (b) post-gate — an acceptor's `Accept` for a `Review` that 39-4's validator would reject (blocking issues + approve) is refused and converted to `Escalate`, pinned by a forged-approval test; `RequestRevision` beyond the round budget converts to `Escalate(RoundsExhausted)`. Property-style tests over arbitrary decision sequences prove termination in `Accept`/`Escalate` within the bounds; no configuration can express an unbounded loop (validation rejects absurd bound values).

## Technical Notes

- Keep the rules *model* (`Tamma.Core`, no dependencies) separate from the *resolver/storage/API* (`Tamma.Api`/`Tamma.Data`) — 39-6 runs in the Elsa server process and should depend only on the model + the resolver interface.
- The always-escalate class list is how the README's "whether breaking changes always escalate is acceptance-rules configuration, not a hardcoded rule" lands — express it as document-type keys and/or `AgentAction` wire names, validated against the registries.
- There is NO accept-decision prompt cell in the taxonomy: the accept step never dispatches `llm-call`. The decide-vs-assign routing and any self-decision happen inside the 39-17 agent's own session; the rules payload in the `AcceptanceRequest` is resolved server-side — the caller never passes rules from the client.
- The autonomy level is read WHEN NEEDED, never cached into a running workflow: the resolver is consulted per request (and by the agent per decision), so turning the dial changes behavior for the next decision without redeploys or workflow restarts.
- Both acceptor submissions feed 39-8's bookmark suspend; `Escalate` decisions feed 39-8's escalation events. This story defines the acceptor contract + rules; 39-6 publishes, 39-18 transports, 39-8 resumes.
- Storage migration mirrors the prompt/audit discipline: additive migration, `dotnet ef migrations has-pending-model-changes` reports none, config in `TammaModelConfiguration.cs`.
- `get_acceptance_rules` registers in the 39-17 toolset like any other tool (MCP-style over `IAcceptanceRulesResolver`) — this story ships the resolver + tool contract; the agent host that mounts it is 39-17's scope.

## Dependencies

- **Prerequisite:** 39-2 (registry for type-key validation, envelope), 39-4 (`Review` decision enum the guardrails read).
- **Prerequisite (in place):** `ITammaModeProvider` / `TammaMode.cs`; the `prompt_overrides` per-mode XOR pattern; CLAUDE.md two-scoping-models rule.
- **Feeds:** 39-6 (ACCEPT stage publishes the request; guardrails around the decision), 39-7 (reviewer selection/panel composition), 39-8 (suspend gate + escalation routing), 39-17 (rules tool + decision/routing guidance + autonomy level), 39-18 (request/decision message payloads), 39-19 (assigned tasks + autonomy context shown to users), 39-20 (eligibility set consumed by routing).

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
| 2026-07-20 | 2.0.0   | Redesign per review: accept gate always submits to an acceptor (mode selects the actor — orchestrator in full-auto, human in supervised — never an if-else); rules configurable + admin UI; orchestrator reads rules at decision time via prompt injection or MCP; pure `Decide` reframed as guardrails around the acceptor | Claude |
| 2026-07-20 | 3.0.0   | Acceptor transport redesign: the orchestrator is the long-running agent (39-17), reached over the 39-18 real-time channel — the accept step publishes an `AcceptanceRequest` and suspends, never dispatches an `llm-call`; `decide-acceptance` taxonomy cell dropped; rules read via the agent's `get_acceptance_rules` tool | Claude |
| 2026-07-20 | 4.0.0   | Autonomy-level redesign: binary GateMode replaced by `AutonomyLevel` (70–100, admin dial, per-type overrides, read when needed); ONE routing path — every request goes to the orchestrator, which decides itself or assigns to a 39-20-eligible user (task lands in the 39-19 Task View) | Claude |

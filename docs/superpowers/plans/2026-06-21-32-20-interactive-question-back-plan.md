# Story 32-20 — Interactive Question-Back (`request_input` + `IQuestionRouter` + durable human gate)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation, and the security re-derivation test (T3) is written FIRST.

**Date:** 2026-06-21

**Goal:** Make a managed agent run able to **ask a question back mid-turn** and have it answered by the
cheapest competent source — routed by a server-side policy the model cannot fool. Five pieces, all
behind the landed `/api/v1/llm/call` boundary (32-5) so the engine step still never calls a provider:
(1) a first-class **`request_input` tool** executed server-side inside `InlineToolLoopRunner`;
(2) **`IQuestionRouter`** routing by `kind` + context (orchestrator-fact → 32-7 panel → human);
(3) **`QuestionRoutingPolicy`** keyed `(principal, role, action, kind)`, tenant→system→ERROR, whose
**server-owned reversibility re-derivation** is the load-bearing security control (model can RAISE but
never DOWNGRADE the human gate); (4) the **in-stream** fast path (TCS-signaled, `inStreamAnswerTimeout`,
SSE heartbeats) for fact/panel answers; (5) the **durable human gate** —
`WaitForAgentQuestionActivity` (modeled byte-for-byte on `EscalateToSeniorActivity`) +
`POST /api/v1/agents/questions/{correlationId}/answer` → `SendSignalAsync` resume → a **stateless
re-invoke** of `/llm/call` with the human answer re-primed as the `request_input` tool result.

**Story file:** `docs/stories/epic-32/story-32-20/32-20-interactive-question-back.md`
**Design spec:** `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§5 — source of record; §6 item 5; §7 open decisions 5/6/7)
**Companion design:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§2 the call-LLM endpoint)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (`Tamma.Api` + `Tamma.Activities` + `Tamma.ElsaServer`).
Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/` and `tests/Tamma.Activities.Tests/` (xUnit).
Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group is stale; plain
`dotnet build` needs no wrapper). **`packages/api` is DELETED — all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO change to the call-LLM endpoint mechanics, the tool loop, the resilience relocation, or the
  thin-client cutover** — that is **32-5**. This story adds a tool + a router + a wait-activity *on top
  of* the landed endpoint and extends `LlmCallResponse` with one new `failureCode` (`INPUT_REQUIRED`).
- **NO new panel primitives.** `judgment` answers go to **32-7**'s `RunAgentPanelActivity` +
  `AggregatePanelActivity`; `PanelAnswerResolver` *adapts* to them, it does not reimplement them.
- **NO new durable-suspend machinery.** `WaitForAgentQuestionActivity` is a byte-for-byte structural
  clone of `EscalateToSeniorActivity` (`CreateBookmark` + `OnResumeAsync` + `AutoBurn`). The resume is
  `ElsaWorkflowService.SendSignalAsync`. The in-stream wait reuses `WebhookSignalRegistry`'s TCS model.
- **NO SSE frames / live `IToolLoopEventSink` / run tap** — the **"Streaming run tap"** follow-on owns
  the `question`/`answer` SSE frames. This story reuses only the buffered path's heartbeat seam to hold
  the in-stream wait.
- **NO new control-plane table.** The routing policy follows the prompt-override pattern (tenant- or
  user-keyed in the appropriate store); the action trail is tenant-schema-resident. → no `Program.cs`
  DROP-list entry, no `ControlPlaneDbContextModelTests` edit. (Re-confirm if a future revision persists
  policy as a CP entity.)
- **NO markup / invoicing.** Panel-answer cost is the raw provider cost basis (32-9/34-5 consume it);
  budgeting attribution is an explicit open decision, not resolved here.
- **NO fuller blast-radius/ADL model.** Ship only the minimal server-owned reversibility derivation the
  security control needs; a richer model is an ADL concern.

---

## Current-state findings (verified 2026-06-21, worktree @ epic32-specs)

| Seam | Where it is today | How 32-20 uses it |
|---|---|---|
| **Tool loop** | `Tamma.Activities/LlmCall/InlineToolLoopRunner.cs` (extracted by 32-5; runs **inside `Tamma.Api`**). 6 built-in tools; no `ask_user`. | Register `RequestInputTool` in the same `IToolExecutorRegistry`; execute it server-side; append the answer as a tool-result message → model resumes (validate→execute→append). |
| **Managed run** | `Tamma.Api/Services/Agents/ManagedAgent.cs` (32-5) composes gate→resolve→cred→render→loop→meter. | Wire `IQuestionRouter` into the loop; on a human-gate resolution, end the turn with `INPUT_REQUIRED`. |
| **Fail-closed envelope** | `Tamma.Api/Services/Agents/LlmCallResponse.cs` (32-5): `success:false` + `failureCode` + preserved `httpStatusCode`. | Add a distinct `INPUT_REQUIRED` code + a key-free `question` payload (NO `httpStatusCode` — it must not be retried as a provider failure). |
| **Durable human gate** | `Tamma.Activities/Blocker/EscalateToSeniorActivity.cs` — `CreateBookmark(BookmarkName, Callback=OnResumeAsync, AutoBurn=true)`, notify, suspend, resume on signal. | `WaitForAgentQuestionActivity` is its structural clone with bookmark `agent-question-{correlationId}`. |
| **Fast in-process signal** | `WebhookSignalRegistry` (TCS wakeups for inbound webhook → bookmark). | The in-stream wait for fact/panel answers (TCS), bounded by `inStreamAnswerTimeout`. |
| **Panel answerer** | `Tamma.Activities/AgentDispatch/RunAgentPanelActivity.cs` + `AggregatePanelActivity.cs` (32-7), tenant-scoped, budget-clamped, SaaS-gated per member. | `PanelAnswerResolver` adapts a `judgment` question to a panel run. |
| **Resume mechanism** | `ElsaWorkflowService.SendSignalAsync(signalName, payload)`. | The answer endpoint resumes `agent-question-{correlationId}`. |
| **Tenant trail** | `Tamma.Data/Repositories/IEventRepository.cs` — `AppendAsync(DomainEvent)`, tenant-scoped (32-6). | Emit `AGENT.QUESTION.RAISED/ANSWERED/ASSUMED/ROUTE_DENIED` from `Tamma.Api`; rehydration substrate keyed by `correlationId`. |
| **Policy resolution discipline** | Epic 27 prompt store — tenant→system→ERROR, never empty/plain (`feedback_resolution_no_empty_fallback`). | `QuestionRoutingPolicyResolver` reuses the exact discipline, keyed `(principal, role, action, kind)`. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser \| SaaS). | Policy keyed by `UserId` (single-user) / `TenantId` (SaaS); answerer auth by mode. |

**Key insight:** the only genuinely new behaviour is (a) the `request_input` *tool*, (b) the *router +
policy + reversibility classifier* triad (the security core), and (c) the *durable wait + answer
endpoint + stateless re-invoke*. Everything else — the bookmark suspend, the TCS signal, the panel run,
the trail append, the tenant→system→error policy resolution — is wiring **existing** patterns.

---

## Architecture

```
InlineToolLoopRunner (inside Tamma.Api, 32-5)
   │  model emits tool call: request_input { question, kind, blocking, default_assumption, confidence }
   ▼
RequestInputTool.ExecuteAsync          -- server-side; NEVER calls a provider
   │
   ▼
IQuestionRouter.RouteAsync(q, ctx)
   0. policy   = QuestionRoutingPolicyResolver.Resolve(principal, role, action, kind)   tenant→system→ERROR (fail-loud)
   1. radius   = ReversibilityClassifier.Classify(ctx.PendingAction)   ── SERVER-OWNED (model never feeds it)
   2. gate     = radius.Irreversible || policy.RequiresHuman(kind, autonomy)   ── re-derive (MAX of hint, blast radius)
        gate ──► QuestionRouting.Human(q)                              [model can RAISE, never DOWNGRADE]  (AC4)
   3. !blocking && default_assumption && policy.AllowsAssumption ──► Assumed(default_assumption)  (AC5, AGENT.QUESTION.ASSUMED)
   4. by kind (escalating cost/latency):
        fact      ─► OrchestratorFactResolver   (workflow vars + Epic-27 conventions + issue/PR ctx; zero LLM/human)  in-stream
        decision  ─► policy.TryDecide ?? PanelAnswerResolver                                                          in-stream
        judgment  ─► PanelAnswerResolver  ─► RunAgentPanelActivity + AggregatePanelActivity (32-7)   in-stream / short signal
        approval  ─► Human (already caught by gate)
   5. in-stream answers bounded by inStreamAnswerTimeout (~90s, tenant-tunable, TCS-signaled, SSE heartbeats);
        timeout ─► escalate (default: human gate for judgment)   (AC6/AC11)
   ▼
 ┌─ InStreamAnswer / Assumed ─► ToolResult.Text(answer) ─► appended as tool-result ─► model resumes      (fast path)
 └─ HumanGate ─► turn ends ─► LlmCallResponse{ success:false, failureCode:"INPUT_REQUIRED", question, usage }   (slow path)
                                   │
                                   ▼  thin CallLlmInlineActivity: do NOT retry — route the WORKFLOW:
                              WaitForAgentQuestionActivity   (notify human + CreateBookmark "agent-question-{correlationId}" + suspend)
                                   │  …hours–days…
                                   ▼  POST /api/v1/agents/questions/{correlationId}/answer { answer, answeredBy }
                              authorize answerer ─► ElsaWorkflowService.SendSignalAsync("agent-question-{correlationId}", …)
                                   │
                                   ▼  workflow resumes ─► re-invoke /llm/call with prior messages + human answer
                                      re-primed as the request_input tool result   (endpoint STATELESS across the gap; AC9)
```

Per-mode ownership (CLAUDE.md two-scoping-model): single-user = sole user is principal, owns the
policy, answers human gates, runs the panel locally; SaaS = tenant is principal, `tenant_owner`/
`tenant_admin` own the policy + elevated answers, panel members SaaS-gated/budget-clamped, events in the
tenant `t_<hex>` store, never cross-tenant. Mode from `ITammaModeProvider`.

---

## Task breakdown

Order: **T1 (records + event types) → T2 (`request_input` tool + parse) → T3 (reversibility classifier
+ the security test FIRST) → T4 (policy resolver) → T5 (router) → T6 (in-stream fast path:
orchestrator-fact + panel) → T7 (human gate: response envelope + `WaitForAgentQuestionActivity` + answer
endpoint + stateless re-invoke) → T8 (audit events) → T9 (mode/RBAC + isolation + wiring)**. T1∥T2 are
parallel-safe; T3 must precede T5 (the router consumes the classifier and the security test is its
acceptance gate).

### T1 — Records + event-type constants

**Scope:** the data shapes; no behaviour.
**Files (new):** `Services/Agents/Questions/RaisedQuestion.cs`
(`{ Question, Kind(fact|decision|judgment|approval), Options?, Schema?, Blocking, DefaultAssumption?, Confidence }`
+ `Parse(args)` with schema validation), `QuestionRouting.cs`
(`{ Answerer(orchestrator|panel|human), Mechanism, Resolution: InStreamAnswer|Assumed|HumanGate }`),
`AgentQuestionEventTypes.cs` (`AGENT.QUESTION.RAISED/ANSWERED/ASSUMED/ROUTE_DENIED`).
**Tests (first):** `RaisedQuestionTests` — `Parse` rejects an unknown `kind`, enforces
`options`/`schema` shape, defaults `blocking` per the schema, `confidence ∈ [0,1]`.
**Acceptance:** records build clean; `Parse` is total (bad args → typed parse error, never a silent default).

### T2 — The `request_input` tool (server-side executor)

**Scope:** `RequestInputTool : IToolExecutor` registered in the 32-5 `IToolExecutorRegistry` catalog.
Parses args → calls `IQuestionRouter` (fake until T5) → returns `ToolResult.Text` (in-stream/assumed) or
signals a human gate (`ToolResult.Suspend`/a typed bubble-up the runner maps to `INPUT_REQUIRED`).
**Files:** new `Services/Agents/Questions/RequestInputTool.cs`; modify
`Tamma.Activities/LlmCall/InlineToolLoopRunner.cs` (execute `request_input`; append the tool-result so
the model resumes; on a human-gate result, end the turn cleanly so `ManagedAgent` emits `INPUT_REQUIRED`).
**Tests (first):** `RequestInputToolTests` — a `request_input` call returns a tool-result message and
the runner appends it (model resumes one extra turn); a human-gate result ends the turn without calling
the provider again; `grep` confirms one registration (no fork of the tool catalog).
**Acceptance:** the tool round-trips as a tool-result; the runner never calls a provider for the tool;
`enableToolLoop` runs gain it only when the agent's allowed-tool set (32-2) includes it.

### T3 — Reversibility classifier + the LOAD-BEARING security test (written FIRST)

**Scope:** `IReversibilityClassifier.Classify(pendingAction) -> BlastRadius { Irreversible, Class }`,
server-owned (the model never feeds it). Minimal mapping: `merge`/`deploy`/`spend`/`schema` → irreversible;
everything else → reversible. This is the single load-bearing control.
**Files:** new `Services/Agents/Questions/IReversibilityClassifier.cs`, `ReversibilityClassifier.cs`.
**Tests (FIRST — the security acceptance gate):** `ReversibilitySecurityTests` —
- `RaisedQuestion{ kind="fact", blocking=false }` + `PendingAction=Merge` ⇒ classifier reports
  irreversible (and, once T5 lands, the router routes to **human**, never in-stream/assume);
- repeat for `deploy`/`spend`/`schema`;
- a reversible action with `kind="approval"` may still be human-gated by policy (model can RAISE), but a
  reversible action with `kind="fact"` is **not** force-gated by the classifier.
**Acceptance:** the classifier is pure + deterministic; the security test compiles and asserts the
irreversible set (it goes green end-to-end after T5 wires it into the router).

### T4 — `QuestionRoutingPolicy` resolver (tenant→system→ERROR)

**Scope:** `IQuestionRoutingPolicyResolver.ResolveAsync(principal, role, action, kind) -> RoutingPolicy`
with `{ RequiresHuman(kind, autonomy), AllowsAssumption(radius, autonomy, budget), TryDecide(q) }`.
Resolution order **tenant→system→ERROR**, never empty/plain. Keyed `(principal, role, action, kind)`;
`UserId` in single-user, `TenantId` in SaaS (XOR/index discipline like `prompt_overrides`).
**Files:** new `Services/Agents/Questions/IQuestionRoutingPolicyResolver.cs`,
`QuestionRoutingPolicyResolver.cs`.
**Tests (first):** `QuestionRoutingPolicyResolverTests` — tenant override beats system default; system
default beats absence; **no policy ⇒ throws/route-denied (no empty fallback)**; single-user keyed by
`UserId`, SaaS by `TenantId`.
**Acceptance:** fail-loud-never-empty; correct key per mode.

### T5 — `IQuestionRouter` (composition + the kind-map + the gate re-derivation)

**Scope:** `QuestionRouter.RouteAsync` composes: resolve policy (T4) → classify reversibility (T3) →
**gate = MAX(model hint, blast radius)** (human always wins) → non-blocking auto-answer (AC5) → kind-map
(fact/decision/judgment/approval) → bound in-stream by `inStreamAnswerTimeout`. The T3 security test
goes **green** here.
**Files:** new `Services/Agents/Questions/IQuestionRouter.cs`, `QuestionRouter.cs`.
**Collaborators (injected, fakes in tests):** `IQuestionRoutingPolicyResolver` (T4),
`IReversibilityClassifier` (T3), `OrchestratorFactResolver` (T6), `PanelAnswerResolver` (T6),
`IEventRepository` (T8), `ITammaModeProvider`, `ILogger<QuestionRouter>`.
**Tests (first):** `QuestionRouterTests` — each `kind` → its answerer once; `decision` with closed
options+default → policy-then-panel; **adversarial `kind="fact"`-on-irreversible ⇒ human** (the T3 test,
now end-to-end); `blocking:false`+reversible ⇒ `Assumed`; `blocking:false`+irreversible ⇒ ignored +
human-gated; no-policy ⇒ `ROUTE_DENIED`.
**Acceptance:** the gate re-derivation holds (model can RAISE, never DOWNGRADE); every question resolves
to exactly one terminal outcome.

### T6 — In-stream fast path: orchestrator-fact + panel answerers

**Scope:** `OrchestratorFactResolver` (synchronous lookup over workflow vars + Epic-27 conventions +
issue/PR context — zero LLM/human) and `PanelAnswerResolver` (adapts a `judgment`/`decision` question to
a 32-7 `RunAgentPanelActivity` + `AggregatePanelActivity` run, tenant-scoped + budget-clamped). Both
resolve **inside** the same `/llm/call` invocation, bounded by `inStreamAnswerTimeout` (~90s,
tenant-tunable), TCS-signaled (reusing the `WebhookSignalRegistry` model), with SSE heartbeats holding
the connection. On timeout → escalate (default: human gate for `judgment`).
**Files:** new `Services/Agents/Questions/OrchestratorFactResolver.cs`, `PanelAnswerResolver.cs`.
**Tests (first):** fact answered from workflow vars/conventions with no LLM call; a panel answer
resolves within the timeout (fake panel) and is returned as a tool-result; a timeout escalates per the
configured default; the timeout value is read per-tenant.
**Acceptance:** fast answers never leave the stream; the timeout is tenant-tunable; panel cost is
attributed to the asking agent's budget (open decision tagged distinctly for 32-9/34-5).

### T7 — The durable human gate (envelope + wait-activity + answer endpoint + stateless re-invoke)

**Scope:**
1. **Response envelope (AC7):** extend `LlmCallResponse` (32-5) with `failureCode="INPUT_REQUIRED"` + a
   key-free `question` payload + accrued `usage`, **no `httpStatusCode`** (so it is NOT retried as a
   provider failure). `ManagedAgent` emits it on a human-gate resolution.
2. **`WaitForAgentQuestionActivity` (AC7):** a structural clone of `EscalateToSeniorActivity` —
   `NotifyHuman(...)` (same channel/integration) then
   `CreateBookmark(new CreateBookmarkArgs { BookmarkName = $"agent-question-{correlationId}",
   Callback = OnResumeAsync, AutoBurn = true })`; `OnResumeAsync` reads `Answer`/`AnsweredBy` from
   `WorkflowInput` and `CompleteActivityAsync()`.
3. **Thin-step routing (AC7):** `CallLlmInlineActivity` (32-5 shim) detects `INPUT_REQUIRED` and routes
   the **workflow** to `WaitForAgentQuestionActivity` (does NOT advance the provider chain / retry).
4. **Answer endpoint (AC8):** `POST /api/v1/agents/questions/{correlationId}/answer { answer, answeredBy }`
   → `IAgentQuestionAuthorizer` (tenant member; elevated where the action demands; **never cross-tenant /
   platform-admin for another tenant's run**) → `ElsaWorkflowService.SendSignalAsync("agent-question-{correlationId}", …)`.
5. **Stateless re-invoke (AC9):** on resume, the workflow re-invokes `/llm/call` with the prior messages
   + the human answer **re-primed as the `request_input` tool result**. The endpoint holds no per-run
   state across the gap; the conversation is rehydrated from the request or the action trail (32-6) by
   `correlationId` (leaning action-trail — open decision).
**Files:** modify `LlmCallResponse.cs`, `ManagedAgent.cs`, `CallLlmInlineActivity.cs`; new
`Tamma.Activities/AgentDispatch/WaitForAgentQuestionActivity.cs`,
`Tamma.Api/Endpoints/AgentQuestionEndpoints.cs` (+ `IAgentQuestionAuthorizer`).
**Tests (first):** human routing ⇒ `success:false`/`INPUT_REQUIRED` + question + accrued usage; the step
does NOT retry; `WaitForAgentQuestionActivity` creates `agent-question-{correlationId}` and suspends
(survives a host restart in an Elsa integration test); the answer endpoint authorizes (cross-tenant →
403/404), resumes, and the re-invoke carries the answer re-primed as the tool result; the endpoint holds
no state across the gap.
**Acceptance:** the durable wait lives in the **workflow** (bookmark), never an HTTP connection; resume +
re-prime work end-to-end; cross-tenant answer is rejected.

### T8 — Audit events (from the API, tenant-scoped)

**Scope:** emit, via the tenant `IEventRepository` (32-6, never CP): `AGENT.QUESTION.RAISED` (per ask,
records the model's untrusted `kind`/`blocking`/`confidence`); exactly one `AGENT.QUESTION.ANSWERED` per
resolved question, **tagged `answerer ∈ {orchestrator, panel, human}`** (+ `latencyMs`, `routingReason`,
server `blastRadius`); `AGENT.QUESTION.ASSUMED` for the AC5 path; `AGENT.QUESTION.ROUTE_DENIED` for no-policy.
All payloads **key-free**.
**Tests (first):** exactly one RAISED per ask; exactly one ANSWERED per resolved question with the right
`answerer`; ASSUMED on the non-blocking-reversible path; ROUTE_DENIED on no-policy; events tenant-scoped
(`TenantId` set, never CP); **no key in any payload**.
**Acceptance:** the RAISED↔(ANSWERED|ASSUMED|ROUTE_DENIED) invariant holds; tenant isolation holds.

### T9 — Mode separation, RBAC posture, isolation & DI wiring

**Scope:** prove single-user (sole user owns policy + answers) vs SaaS (tenant policy,
`tenant_owner`/`tenant_admin` elevated answers, members can't edit policy, panel SaaS-gated); prove
`AGENT.QUESTION.*` land in the resolving tenant's store and never cross-tenant; register all services +
the answer endpoint + `RequestInputTool` in the catalog at host startup.
**Files:** modify `Tamma.Api/Program.cs` (map endpoint; register router/policy/classifier/resolvers +
`RequestInputTool`); extend the router/policy tests with the mode matrix + a 2-tenant isolation test.
**Tests (first):** SaaS member cannot edit the policy (403); cross-tenant answer rejected; two tenants'
`AGENT.QUESTION.*` events carry their own `TenantId`; DI resolves the whole chain
(`WebApplicationFactory` smoke).
**Acceptance:** mode matrix passes; cross-tenant isolation holds; host boots with everything registered.

---

## Story order & dependencies

External prereqs (must land first): **32-5** (the call-LLM endpoint + `InlineToolLoopRunner` +
`ManagedAgent` + `LlmCallResponse` + the thin `CallLlmInlineActivity`) and **32-7** (the panel
primitives — the `judgment` answerer). Code to the **32-6** trail interface (`IEventRepository`).
Use fakes for 32-5/32-7/32-6 until landed. Internal: T1∥T2 → T3 (security test first) → T4 → T5 →
T6 → T7 → T8 → T9. Downstream consumers (32-9 cost, the streaming run tap, 32-8 outcome) depend on this;
they are NOT blockers.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Questions"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/ --filter FullyQualifiedName~AgentDispatch|FullyQualifiedName~LlmCall"
# the load-bearing security gate (must be GREEN before merge)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~ReversibilitySecurity"
# no-fork / no-key-leak checks
grep -rn "class RequestInputTool\|\"request_input\"" apps/tamma-elsa/src        # one tool registration
grep -rn "Anthropic:ApiKey\|ApiKey" apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions  # zero — key-free by contract
```

## Risks

- **Security downgrade (T3/T5, AC4) — Critical:** an adversarial model tags a merge-approval as `fact`
  to dodge the human gate. Mitigation: the server **re-derives** the gate from server-owned reversibility;
  routing is `MAX(model hint, blast radius)`; the security test is written FIRST and is a merge gate.
- **Lost/stalled question (T5/T8) — High:** a question raised but never resolved. Mitigation: every
  `request_input` resolves to exactly one of {in-stream, assumed, human-gate, route-denied}; the
  RAISED↔terminal invariant test.
- **`INPUT_REQUIRED` retried as a provider failure (T7, AC7) — High:** the workflow retries instead of
  pausing. Mitigation: `INPUT_REQUIRED` is a **distinct** code with **no `httpStatusCode`**; the thin step
  routes to `WaitForAgentQuestionActivity`, never the provider chain; explicit test.
- **State leak across the human gap (T7, AC9) — High:** a sticky/stale server session breaks isolation.
  Mitigation: endpoint **stateless across the gap**; rehydrate from the action trail (32-6) by
  `correlationId`; only durable state is the Elsa bookmark; restart-survival integration test.
- **In-stream wait blocks a worker (T6, AC6) — High:** Mitigation: `inStreamAnswerTimeout` (~90s,
  tenant-tunable) + heartbeats; only fast answers wait in-stream; timeout → human gate.
- **`blocking:false` waives a human gate (T5, AC5) — High:** Mitigation: a non-blocking hint cannot waive
  a gate — when the policy forbids assumption (irreversible/out-of-autonomy), `blocking:false` is ignored
  and the question is human-gated; explicit test.
- **Empty-policy silent routing (T4, AC3) — Medium:** Mitigation: tenant→system→**ERROR**
  (`feedback_resolution_no_empty_fallback`); `ROUTE_DENIED` on no-policy.
- **Panel-answer cost mis-attribution (T6, AC11.3) — Medium:** Mitigation: open decision documented;
  attribute to the asking agent's budget for now, tag the panel sub-run distinctly so 32-9/34-5 can split.
- **Dependency timing (32-5/32-7) — Medium:** Mitigation: interfaces + fakes; this story is the
  integrator, gated behind them.

## Open decisions (deep-dive §7 — surfaced, not pre-decided)

1. **In-stream timeout → escalation for `judgment` (§7 #5):** human bookmark vs proceed-on-assumption vs
   fail-the-turn. **Default chosen:** escalate to the human gate (tenant-configurable) — conservative vs
   the 70% autonomy target.
2. **Conversation-state rehydration across the human gap (§7 #6):** workflow variable vs action-trail
   (32-6) keyed by `correlationId`. **Leaning:** action-trail.
3. **`request_input` budgeting (§7 #7):** panel-answer tokens charged to the asking agent's budget vs a
   separate "clarification" line (affects 32-9/34-5). **Until decided:** asking-agent budget, panel
   sub-run tagged distinctly.

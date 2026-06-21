# Story 32-20: Interactive Question-Back (`request_input` tool + `IQuestionRouter` + durable human gate)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform engineer running agent-driven workflows through the managed `/api/v1/llm/call` endpoint**,
I want an agent to be able to **ask a question back mid-run** via a first-class `request_input` tool — and have the answer come from the cheapest competent source (the orchestrator's own facts, an agent panel, or a human) routed by a server-side policy that the model cannot fool —
So that an agent that hits an ambiguity, a trade-off, or an irreversible decision **stops guessing**: facts are answered in-stream for free, judgment calls go to a budget-clamped agent panel, and merge/deploy/spend/schema approvals durably suspend the workflow for a human — all key-free, fully audited, and time-travel-debuggable, without the thin engine step ever calling a provider.

## Priority

P1 — The **single most novel piece** of the Epic 32 pivot (deep-dive §5, §6 item 5). It turns the managed agent from a one-shot generator into an interactive collaborator and is the structural fix for the 70%-autonomous-completion goal: an agent that can ask the *right* source the *right* way (instead of fabricating or failing) is the difference between a run that lands and a run that derails. It depends on the call-LLM endpoint (32-5) being the single execution path and on the agent-panel primitives (32-7) being the `judgment` answerer.

## Context

### What exists today (the gap, confirmed — deep-dive §5)

The agentic tool loop (extracted into `IInlineToolLoopRunner`/`InlineToolLoopRunner` by 32-5) has **six** tools (`git_operations`, `shell_execute`, `file_read`, `file_write`, `run_tests`, `search_code`) — **none** of them lets the agent ask a question. When the model has nothing more to call it ends the turn on a non-`ToolUse` stop reason, so a question the model *wants to ask* becomes the run's final "answer" with **nothing answering it**. The agent's only options today are to guess or to stop — both corrosive to the autonomy target.

But the repo already has **every primitive** to close the gap:

- the **tool loop** (32-5) — a question can be a *tool call* that returns a *tool-result message*, so the model resumes its own reasoning in-context through the existing validate→execute→append cycle (no new conversation-shaping code);
- the **agent panel** (32-7) — `RunAgentPanelActivity` + `AggregatePanelActivity` (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`), tenant-scoped, budget-clamped, SaaS-gated per member — the natural answerer for `judgment` questions;
- the **fast-signal model** — `WebhookSignalRegistry`'s in-process `TaskCompletionSource` wakeups (the inbound-webhook→bookmark path) — the model for resolving a fast answer **inside** an open request;
- the **durable human gate** — `EscalateToSeniorActivity` (`apps/tamma-elsa/src/Tamma.Activities/Blocker/EscalateToSeniorActivity.cs`): notify a human, `context.CreateBookmark(...)` with an `OnResumeAsync` callback + `AutoBurn`, **suspend durably**, resume on a signal. This story's human path is modeled **byte-for-byte** on it.

### What this story does (deep-dive §5)

Adds **interactive question-back** to the managed run as five cooperating pieces, all behind the existing `/api/v1/llm/call` boundary (32-5) so **the step still never calls a provider**:

1. A first-class **`request_input` tool** — the agent's ONLY way to ask back (a tool call, not a stop reason), executed **server-side inside the runner**.
2. **`IQuestionRouter`** — routes a raised question by `kind` + context to the cheapest competent answerer (orchestrator-fact → agent-panel → human), escalating cost/latency.
3. **`QuestionRoutingPolicy`** — a server-side, fail-loud, never-empty policy keyed `(principal, role, action, kind)` (tenant→system→error) that **re-derives** the human-gate decision from the pending action's blast radius — the load-bearing security control.
4. The **in-stream resolution path** for fast answers (orchestrator-fact, agent-panel), bounded by `inStreamAnswerTimeout` with SSE heartbeats, reusing the `WebhookSignalRegistry` TCS model.
5. The **durable human gate**: a new **`WaitForAgentQuestionActivity`** (modeled on `EscalateToSeniorActivity`) + the human-answer endpoint + the stateless re-invoke of `/llm/call` with the human answer re-primed as the `request_input` tool result.

### Why a tool call, not a stop reason (deep-dive §5.1)

A `request_input` **tool call** means the answer comes back as a **tool-result message** appended to the conversation, so the model resumes its own reasoning with the answer in-context — it flows through the runner's existing validate→execute→append cycle with no new conversation-shaping code. A parsed `end_turn` "question" would require bespoke re-prompting and would lose the model's mid-turn state. The tool executes **inside `Tamma.Api`** (where the runner runs after 32-5), so the engine step still owns no provider logic and never calls out.

### Explicitly out of scope (referenced, not implemented here)

- **The call-LLM endpoint, `InlineToolLoopRunner`, the resilience relocation, the thin-client cutover** — that is **32-5** (the hard prerequisite). This story adds a tool + a router + a wait-activity *on top of* the landed endpoint.
- **The agent-panel primitives** (`RunAgentPanelActivity`/`AggregatePanelActivity`, the strategies) — that is **32-7** (the `judgment` answerer this story *calls*, not builds).
- **The SSE response mode + live `IToolLoopEventSink` + run tap** — the **"Streaming run tap"** follow-on (deep-dive §3, §6.4). This story's in-stream wait reuses the buffered path's heartbeat seam; the `question`/`answer` SSE frames are wired by the run-tap story.
- **The reversibility/blast-radius classifier as a standalone, richly-configured ADL feature** — this story ships the *minimal* server-owned reversibility derivation needed for the human-gate security control; a fuller blast-radius model is an ADL concern (referenced, not owned).
- **MCP/plugin tools, prompt/response cache, harness adapter, non-LLM mediation** — separate follow-ons / Epic 38.

## Acceptance Criteria

1. **`request_input` is a first-class tool in the managed run.** A new tool `request_input` is registered in the `IToolExecutorRegistry` catalog used by `InlineToolLoopRunner` (32-5), with the schema `{ question:string, kind:"fact"|"decision"|"judgment"|"approval", options?:string[]|null, schema?:object|null, blocking:bool, default_assumption?:string|null, confidence:number }`. It is **executed server-side inside `Tamma.Api`** (where the runner runs), and its result is appended to the conversation as a **tool-result message** so the model resumes its own reasoning in-context through the existing validate→execute→append cycle. The step never calls a provider; `enableToolLoop` runs gain the tool when the agent's allowed-tool set (32-2) includes it.

2. **`IQuestionRouter` routes by `kind` + context (deep-dive §5.2).** A new `IQuestionRouter.RouteAsync(RaisedQuestion, QuestionContext, ct)` (`Tamma.Api/Services/Agents/Questions/`) returns a `QuestionRouting { Answerer ∈ {orchestrator, panel, human}, Mechanism, Resolution }`. The decision uses the `QuestionRoutingPolicy` (AC4) and the escalating cost/latency map: `fact`→**orchestrator/workflow-state** (synchronous lookup over workflow vars + Epic-27 conventions + issue/PR context — zero LLM, zero human, in-stream); `judgment`→**agent panel (32-7)** (`RunAgentPanelActivity`+`AggregatePanelActivity`, tenant-scoped, budget-clamped, in-stream/short signal); `decision` with closed `options` + a confident `default_assumption`→**orchestrator policy first, panel fallback**; `approval`/irreversible (merge/deploy/spend/schema)→**human-in-the-loop** (durable bookmark + signal).

3. **`QuestionRoutingPolicy` is server-side, keyed, and fail-loud-never-empty (deep-dive §5.2).** A policy keyed `(principal, role, action, kind)` resolves **tenant→system→ERROR** (never empty/plain — `feedback_resolution_no_empty_fallback`), with inputs: `kind`, the **reversibility/blast-radius of the pending action** (orchestrator-owned), the run's **autonomy level** (ADL limits config), and **budget**. If no policy resolves, the router **fails loud** (`AGENT.QUESTION.ROUTE_DENIED`), never silently auto-answers. The policy lives in `Tamma.Api` where the tenant store lives; in single-user mode it is keyed by `UserId`, in SaaS by `TenantId` — same XOR/index discipline as `prompt_overrides`/`AgentRoleSelection`.

4. **Security (LOAD-BEARING) — the model cannot downgrade routing (deep-dive §5.2).** The model-supplied `kind` and `blocking` are treated as **hints**. The server **re-derives** the human-gate decision from the pending action's reversibility, which the orchestrator owns — **a question whose pending action is irreversible (merge/deploy/spend/schema) routes to the human gate regardless of the model's `kind`**. A misclassifying or adversarial model that tags a `merge`-approval as `fact` (to dodge human gating) is **upgraded** to the human gate by the server; the model can **raise** a question and can route it *more* conservatively, but **cannot route below what blast radius mandates**. Tested with an adversarial fixture: `kind:"fact"` + an irreversible pending action ⇒ human gate, never an in-stream auto-answer.

5. **`blocking:false` ⇒ audited assumption, never a pause (deep-dive §5.2).** When the model sets `blocking:false` and supplies a `default_assumption`, and the policy permits (reversible action, within autonomy + budget), the orchestrator **auto-answers with the `default_assumption`**, records it as an **audited assumption** (`AGENT.QUESTION.ASSUMED`), and the run **never pauses**. The assumed answer is returned as the `request_input` tool result so the model proceeds. If the policy does NOT permit (irreversible / out of autonomy), `blocking:false` is **ignored** and the question is gated per AC4 (a non-blocking hint cannot waive a human gate).

6. **In-stream resolution for fast answers (deep-dive §5.3).** `fact` and `panel` answers resolve **inside the same `/llm/call` invocation**, on the server, bounded by an `inStreamAnswerTimeout` (default ~90s, **tenant-tunable**) with SSE heartbeats holding the connection (reusing the buffered path's flush seam). The turn never leaves the stream; the resolution reuses the **`WebhookSignalRegistry` `TaskCompletionSource`** fast-signal model. On in-stream timeout the router applies the configured escalation (AC11 open decision) — default for `judgment` is to fail the turn into the human gate (AC7) rather than silently assume.

7. **Slow (human) answers cross engine→bookmark→signal→re-call (deep-dive §5.3).** When the policy routes to a human, the turn **ends** returning the 32-5 fail-closed envelope: **HTTP 200 `success:false`, `failureCode = "INPUT_REQUIRED"`**, the **question** (key-free), and the **accrued `usage`** (key never leaked). The thin step **does NOT retry**; it routes the **workflow** into a new **`WaitForAgentQuestionActivity`** (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WaitForAgentQuestionActivity.cs`), modeled **byte-for-byte on `EscalateToSeniorActivity`**: notify the human (channel/integration as `EscalateToSeniorActivity` does), `context.CreateBookmark(new CreateBookmarkArgs { BookmarkName = $"agent-question-{correlationId}", Callback = OnResumeAsync, AutoBurn = true })`, **suspend durably**. The durable wait lives in the **workflow** (Elsa bookmark), **never** in an HTTP connection.

8. **The human-answer endpoint resumes the workflow (deep-dive §5.3).** A new **`POST /api/v1/agents/questions/{correlationId}/answer`** (`Tamma.Api/Endpoints/AgentQuestionEndpoints.cs`) accepts `{ answer, answeredBy }`, authorizes the answerer (tenant member for a tenant run; `tenant_owner`/`tenant_admin` where the pending action requires elevated approval — never a cross-tenant or platform-admin answer for another tenant's run), and calls `ElsaWorkflowService.SendSignalAsync` (resume the `agent-question-{correlationId}` bookmark). On resume, the workflow **re-invokes `/llm/call`** (via the thin `CallLlmInlineActivity`, 32-5) with the **prior messages + the human answer re-primed as the `request_input` tool result**, so the model resumes where it paused. The endpoint stays **stateless across the human gap** (AC9).

9. **Endpoint statelessness + conversation rehydration (deep-dive §5.3, §7 open #6).** `/api/v1/llm/call` holds **no per-run conversation state across the human gap**. On the re-invoke, the prior conversation is **rehydrated** either from the request (the workflow carries the messages) **or** from the **action trail (32-6) keyed by `correlationId`** (the leaning design — open decision AC11.2). The human answer is re-primed as the `request_input` tool result before the runner continues. No sticky server session; the only durable state is the Elsa bookmark + the action trail.

10. **Audit — from the API where the tenant store lives (deep-dive §5.3).** The managed run emits, via the tenant `IEventRepository` (32-6 trail, tenant-scoped — never the control plane): `AGENT.QUESTION.RAISED` (the model asked: `kind`, `blocking`, `confidence`, `correlationId`, `questionId`), exactly one `AGENT.QUESTION.ANSWERED` per resolved question **tagged `answerer ∈ {orchestrator, panel, human}`** (+ `latencyMs`, `routingReason`), and `AGENT.QUESTION.ASSUMED` for the AC5 auto-answer path. A routed-but-denied question emits `AGENT.QUESTION.ROUTE_DENIED`. The question text and answer are **key-free**; no provider key, `BaseUrl` auth, or raw header ever appears in any event, log, or response.

11. **Open decisions are documented as Risks/Open-Questions (deep-dive §7), not silently resolved:** (1) **in-stream timeout → escalation for `judgment`** — promote to human bookmark vs proceed-on-assumption vs fail-the-turn (default contentious vs the 70% autonomy target); the chosen default is the human gate, configurable. (2) **conversation-state rehydration** — workflow variable vs action-trail (32-6) keyed by `correlationId` (leaning action-trail). (3) **`request_input` budgeting** — panel-answer tokens charged to the asking agent's budget vs a separate "clarification" line (affects 32-9/34-5). Each is called out, not pre-decided in code.

12. **Tests cover the tool, the router, the security control, the in-stream path, and the durable human path.** `request_input` appears as a tool-result message and the model resumes (runner test); router maps each `kind` to its answerer; the policy resolves tenant→system→ERROR (no empty fallback); the **adversarial `kind:"fact"`-on-irreversible-action upgrade to human** holds; `blocking:false`+reversible auto-answers (`ASSUMED`) without pausing; `blocking:false`+irreversible is **ignored** and gated; the in-stream fast path resolves within timeout and times out into the human gate; the human path returns `success:false`/`INPUT_REQUIRED`, creates the `agent-question-{correlationId}` bookmark, and the answer endpoint resumes + re-invokes with the answer re-primed; exactly one `AGENT.QUESTION.ANSWERED` per question tagged with the right `answerer`; and **no key leaks** in any event/log/response.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/
  RequestInputTool.cs              # NEW — the server-side request_input tool executor (IToolExecutor)
  RaisedQuestion.cs                # NEW — parsed tool args { Question, Kind, Options?, Schema?, Blocking, DefaultAssumption?, Confidence }
  IQuestionRouter.cs               # NEW — RouteAsync(RaisedQuestion, QuestionContext, ct) -> QuestionRouting
  QuestionRouter.cs                # NEW — policy + escalating-cost map; re-derives human gate from blast radius
  QuestionRouting.cs               # NEW — { Answerer, Mechanism, Resolution(InStreamAnswer? | HumanGate | Assumed) }
  IQuestionRoutingPolicyResolver.cs# NEW — (principal, role, action, kind) -> RoutingPolicy; tenant->system->ERROR
  QuestionRoutingPolicyResolver.cs # NEW — fail-loud-never-empty resolution (Epic 27-style)
  IReversibilityClassifier.cs      # NEW — server-owned: pending-action -> { Reversible, BlastRadius }  (the security control)
  ReversibilityClassifier.cs       # NEW — minimal merge/deploy/spend/schema -> irreversible derivation
  AgentQuestionEventTypes.cs       # NEW — AGENT.QUESTION.RAISED/ANSWERED/ASSUMED/ROUTE_DENIED constants
  OrchestratorFactResolver.cs      # NEW — synchronous fact lookup over workflow vars + Epic-27 conventions + issue/PR ctx
  PanelAnswerResolver.cs           # NEW — adapts a judgment question to RunAgentPanelActivity (32-7), budget-clamped

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  AgentQuestionEndpoints.cs        # NEW — POST /api/v1/agents/questions/{correlationId}/answer -> SendSignalAsync

apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/
  WaitForAgentQuestionActivity.cs  # NEW — modeled byte-for-byte on EscalateToSeniorActivity: notify + CreateBookmark + suspend + OnResumeAsync

apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  ManagedAgent.cs                  # MODIFY (32-5) — wire IQuestionRouter into the run; emit INPUT_REQUIRED on human-gate
  LlmCallResponse.cs               # MODIFY (32-5) — add INPUT_REQUIRED failureCode + Question payload (key-free)
  InlineToolLoopRunner.cs (Tamma.Activities/LlmCall) # MODIFY — execute request_input via IQuestionRouter; append tool-result; or end turn with INPUT_REQUIRED

apps/tamma-elsa/src/Tamma.Activities/LlmCall/
  CallLlmInlineActivity.cs         # MODIFY (32-5 thin shim) — on INPUT_REQUIRED do NOT retry; surface the question to the workflow
```

### The `request_input` tool (AC1)

```jsonc
// tool: request_input — the agent's ONLY way to ask back (a tool call, not a stop reason)
{ "question": "Should the migration drop the legacy column, or keep it nullable?",
  "kind": "decision",                 // fact | decision | judgment | approval  (HINT — server re-derives gating)
  "options": ["drop", "keep-nullable"] | null,
  "schema": { } | null,               // optional JSON schema the answer must satisfy
  "blocking": true,                   // false => orchestrator may auto-answer with default_assumption (AC5)
  "default_assumption": "keep-nullable" | null,
  "confidence": 0.4 }
```

```csharp
// Server-side executor — runs INSIDE Tamma.Api (where InlineToolLoopRunner runs after 32-5).
public sealed class RequestInputTool : IToolExecutor   // registered in IToolExecutorRegistry (32-5 catalog)
{
    public string Name => "request_input";
    public async Task<ToolResult> ExecuteAsync(ToolInvocation inv, ToolExecutionContext ctx, CancellationToken ct)
    {
        var q = RaisedQuestion.Parse(inv.Arguments);                 // validated against the schema above
        var routing = await _router.RouteAsync(q, QuestionContext.From(ctx), ct);   // AC2/AC3/AC4
        return routing.Resolution switch
        {
            InStreamAnswer a => ToolResult.Text(a.Answer),           // fact/panel/assumed -> tool-result message (model resumes)
            Assumed a        => ToolResult.Text(a.Assumption),       // AC5 — recorded AGENT.QUESTION.ASSUMED
            HumanGate _      => ToolResult.Suspend(q),               // AC7 — bubble up: end turn, INPUT_REQUIRED
            _                => throw new TammaError("AGENT.QUESTION.ROUTE_DENIED", ...)   // AC3 fail-loud
        };
    }
}
```

### `IQuestionRouter` + the escalating-cost map (AC2)

```csharp
public interface IQuestionRouter
{
    Task<QuestionRouting> RouteAsync(RaisedQuestion q, QuestionContext ctx, CancellationToken ct);
}

// Composition order inside QuestionRouter.RouteAsync:
// 0. policy   = await _policy.ResolveAsync(ctx.Principal, ctx.Role, ctx.Action, q.Kind);   // tenant->system->ERROR (AC3)
// 1. radius   = _reversibility.Classify(ctx.PendingAction);                                // SERVER-OWNED (AC4)
// 2. gate     = radius.Irreversible || policy.RequiresHuman(q.Kind, ctx.Autonomy);         // re-derive — model can't downgrade
//    if (gate) return QuestionRouting.Human(q);                                            // AC4/AC7 — upgrade wins
// 3. non-blocking auto-answer (AC5):
//    if (!q.Blocking && q.DefaultAssumption is not null && policy.AllowsAssumption(radius, ctx.Autonomy, ctx.Budget))
//        return QuestionRouting.Assumed(q.DefaultAssumption);                              // AGENT.QUESTION.ASSUMED, never pauses
// 4. by kind (escalating cost/latency):
//    fact      -> _orchestratorFacts.TryAnswer(q, ctx)   // zero LLM/human, in-stream
//    decision  -> policy.TryDecide(q) ?? _panel.Answer(q, ctx)   // policy first, panel fallback
//    judgment  -> _panel.Answer(q, ctx)                  // 32-7 panel, budget-clamped, in-stream/short signal
//    approval  -> QuestionRouting.Human(q)               // always human (already caught by gate)
// 5. in-stream answers bounded by ctx.InStreamAnswerTimeout (~90s, tenant-tunable); on timeout -> escalate (AC6/AC11)
```

| `kind` | Answerer | Mechanism | Latency class |
|---|---|---|---|
| `fact` | **Orchestrator / workflow state** | sync lookup vs workflow vars + Epic-27 conventions + issue/PR context — zero LLM, zero human | in-stream (sub-second) |
| `judgment` | **Agent panel (32-7)** | `RunAgentPanelActivity`+`AggregatePanelActivity`, tenant-scoped, budget-clamped | in-stream / short in-process signal |
| `decision` w/ closed options + confident default | **Orchestrator policy**, fallback panel | policy first, panel second | in-stream |
| `approval` / irreversible (merge/deploy/spend/schema) | **Human-in-the-loop** | durable Elsa bookmark + signal | **workflow-suspend** (hours–days) |

### The security control — server re-derives the gate (AC4, LOAD-BEARING)

```csharp
// IReversibilityClassifier is SERVER-OWNED — the model never feeds it.
// The pending action (the orchestrator's current step intent: merge / deploy / spend / schema-change)
// is supplied by the workflow context, NOT by the model's request_input args.
public sealed record BlastRadius(bool Irreversible, string Class);  // "merge" | "deploy" | "spend" | "schema" | "reversible"

// In QuestionRouter: the human gate is the MAX of (model hint, server derivation).
// model says kind:"fact" + pending action == merge  ==>  Irreversible ==>  HUMAN GATE.
// The model can RAISE the question and can ask for MORE caution; it can NEVER route below blast radius.
```

A test fixture asserts: `RaisedQuestion { kind="fact", blocking=false }` with `ctx.PendingAction = Merge` ⇒ `QuestionRouting.Answerer == human`, the in-stream/assume paths are **never taken**, and `AGENT.QUESTION.RAISED` records the model's (untrusted) `kind` alongside the server's (authoritative) routing.

### The durable human gate — `WaitForAgentQuestionActivity` (AC7), modeled on `EscalateToSeniorActivity`

```csharp
[Activity("Tamma.AgentDispatch", "Wait For Agent Question",
          "Notify a human and suspend until the agent's question is answered", Kind = ActivityKind.Task)]
public class WaitForAgentQuestionActivity : Activity
{
    [Input] public Input<string> CorrelationId { get; set; } = default!;
    [Input] public Input<string> Question { get; set; } = default!;
    [Input] public Input<string> Kind { get; set; } = default!;
    [Output] public Output<string?> Answer { get; set; } = default!;
    [Output] public Output<string?> AnsweredBy { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var correlationId = CorrelationId.Get(context);
        await NotifyHuman(correlationId, Question.Get(context), Kind.Get(context));   // same channel/integration as EscalateToSenior
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = $"agent-question-{correlationId}",   // <-- the signal name the answer endpoint resumes
            Callback = OnResumeAsync,
            AutoBurn = true
        });
    }

    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;
        context.Set(Answer, input.TryGetValue("Answer", out var a) ? a?.ToString() : null);
        context.Set(AnsweredBy, input.TryGetValue("AnsweredBy", out var b) ? b?.ToString() : null);
        await context.CompleteActivityAsync();   // workflow re-invokes /llm/call with the answer re-primed
    }
}
```

### The human-answer endpoint (AC8) → resume + re-invoke

```csharp
// POST /api/v1/agents/questions/{correlationId}/answer   body: { answer, answeredBy }
app.MapPost("/api/v1/agents/questions/{correlationId}/answer", async (
        string correlationId, AgentQuestionAnswer body, HttpContext http,
        IElsaWorkflowService elsa, IAgentQuestionAuthorizer authz, CancellationToken ct) =>
{
    await authz.EnsureCanAnswerAsync(http, correlationId, body, ct);   // tenant member; elevated where the action demands; never cross-tenant
    await elsa.SendSignalAsync($"agent-question-{correlationId}",       // resume the bookmark
        new { Answer = body.Answer, AnsweredBy = body.AnsweredBy }, ct);
    return Results.Accepted();
})
.RequireAuthorization(/* tenant-member policy */);
```

On resume, `WaitForAgentQuestionActivity` completes; the workflow re-invokes `/llm/call` (thin `CallLlmInlineActivity`, 32-5) with the **prior messages + the human answer re-primed as the `request_input` tool result**. The endpoint stays stateless across the gap; the conversation is rehydrated from the request or the action trail (32-6) keyed by `correlationId` (AC9).

### The fail-closed envelope for the human path (AC7) — rides 32-5's §2.4 contract

```jsonc
// 32-5 LlmCallResponse, extended with INPUT_REQUIRED — HTTP 200, key never leaked:
{ "success": false,
  "failureCode": "INPUT_REQUIRED",
  "question": { "questionId": "…", "text": "…key-free…", "kind": "approval",
                "options": [...]|null, "blocking": true },
  "usage": { …accrued-before-pause… },        // tokens accrued so far are preserved (AC7)
  "credentialSource": "platform", "correlationId": "…" }
```

The thin step does **not** treat `INPUT_REQUIRED` as a retryable provider failure (distinct from `PROVIDER_ERROR`/`httpStatusCode`); it routes the workflow to `WaitForAgentQuestionActivity` instead of advancing the provider chain.

## Dependencies

**Internal (hard prerequisites):**

- **32-5** (Call-LLM endpoint + managed execution) — the single execution path: `IInlineToolLoopRunner` (where `request_input` is executed), `ManagedAgent.RunAsync` (where the router is wired), the `LlmCallResponse` fail-closed envelope (extended with `INPUT_REQUIRED`), and the thin `CallLlmInlineActivity` (which routes to the wait-activity). **Sequence F.**
- **32-7** (Multi-agent design-review panels) — `RunAgentPanelActivity` + `AggregatePanelActivity` (`Tamma.Activities/AgentDispatch/`), the `judgment` answerer (`PanelAnswerResolver` adapts to it, budget-clamped per 32-7 AC9).
- **32-6** (Agent action trail) — the tenant `IEventRepository` for `AGENT.QUESTION.*` events; the leaning rehydration substrate keyed by `correlationId` (AC9).
- **Epic 27** (prompt/convention store) — the orchestrator-fact resolver reads conventions; the policy resolver reuses the tenant→system→error discipline.
- **`EscalateToSeniorActivity`** (`Tamma.Activities/Blocker/`) — the byte-for-byte model for `WaitForAgentQuestionActivity` (bookmark + notify + `OnResumeAsync` + `AutoBurn`).
- **`WebhookSignalRegistry`** — the in-process `TaskCompletionSource` fast-signal model reused for the in-stream wait.
- **`ElsaWorkflowService.SendSignalAsync`** — the resume mechanism the answer endpoint calls.

**Consumers (downstream, not blockers):**

- **32-9** (usage & cost metering) / **34-5** (markup) — consume the panel-answer token attribution (AC11.3 open decision: asking-agent budget vs separate "clarification" line).
- **Streaming run tap** follow-on — adds the `question`/`answer` SSE frames over this story's in-stream seam.
- **32-8** (outcome capture) — a run that paused for and resumed from a human answer is a distinguishable outcome.

**External:** none new (reuses the existing notification/integration stack used by `EscalateToSeniorActivity`).

## Testing Strategy

1. **`request_input` round-trips as a tool-result (AC1).** A runner test: the model emits a `request_input` tool call → the executor returns an answer → the answer is appended as a tool-result message → the model resumes its own reasoning (one extra turn). `grep` confirms `request_input` is registered in the single `IToolExecutorRegistry` catalog (no fork).
2. **Router `kind` map (AC2).** Fakes for orchestrator-facts/panel/human: `fact`→orchestrator, `judgment`→panel, `decision`(closed options+default)→policy-then-panel, `approval`→human; each routed exactly once.
3. **Policy fail-loud-never-empty (AC3).** No policy resolves → `AGENT.QUESTION.ROUTE_DENIED`, never a silent auto-answer; tenant override beats system default; system default beats absence; single-user keyed by `UserId`, SaaS by `TenantId`.
4. **Security upgrade — the load-bearing test (AC4).** `kind:"fact"`+`blocking:false` with `PendingAction=Merge` ⇒ `Answerer==human`; in-stream/assume paths never taken; the model's `kind` recorded but not honored for gating. Repeat for deploy/spend/schema.
5. **`blocking:false` assumption vs ignore (AC5).** reversible action + `default_assumption` ⇒ `AGENT.QUESTION.ASSUMED`, no pause, assumption returned as tool result; irreversible action ⇒ `blocking:false` ignored, human-gated.
6. **In-stream fast path + timeout (AC6).** panel answers within `inStreamAnswerTimeout` resolve in-stream (TCS-signaled); exceeding the timeout escalates per the configured default (human gate for `judgment`); the timeout is tenant-tunable.
7. **Human gate envelope + bookmark (AC7).** human routing ⇒ `success:false`/`INPUT_REQUIRED` + the question + accrued usage; the workflow creates `agent-question-{correlationId}`; the step does NOT retry (distinct from `PROVIDER_ERROR`).
8. **Answer endpoint resume + re-invoke (AC8/AC9).** `POST .../answer` authorizes the answerer (member; elevated where required; cross-tenant → 403/404), calls `SendSignalAsync`, resumes the bookmark, and the workflow re-invokes `/llm/call` with the answer re-primed as the `request_input` tool result; endpoint holds no per-run state across the gap; rehydration from action trail (32-6) by `correlationId`.
9. **Audit (AC10).** exactly one `AGENT.QUESTION.RAISED` per ask; exactly one `AGENT.QUESTION.ANSWERED` per resolved question tagged with the correct `answerer ∈ {orchestrator, panel, human}` (+ `latencyMs`); `AGENT.QUESTION.ASSUMED` on the AC5 path; all tenant-scoped (never CP); question/answer key-free.
10. **Credential safety (AC10/AC12).** the resolved API key never appears in any `AGENT.QUESTION.*` event, log line, `request_input` tool result, or the `INPUT_REQUIRED` response body.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

7-9 days (the `request_input` tool + the router/policy/reversibility-classifier triad + the in-stream TCS wait + the durable `WaitForAgentQuestionActivity` + the answer endpoint + the stateless re-invoke/rehydration + the audit events — gated behind 32-5 and 32-7).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/RequestInputTool.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/RaisedQuestion.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/IQuestionRouter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/QuestionRouter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/QuestionRouting.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/IQuestionRoutingPolicyResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/QuestionRoutingPolicyResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/IReversibilityClassifier.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/ReversibilityClassifier.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/OrchestratorFactResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/PanelAnswerResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Questions/AgentQuestionEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentQuestionEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WaitForAgentQuestionActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` | Modify (wire `IQuestionRouter`; emit `INPUT_REQUIRED`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/LlmCallResponse.cs` | Modify (add `INPUT_REQUIRED` + key-free `question` payload) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/InlineToolLoopRunner.cs` | Modify (execute `request_input`; end turn with `INPUT_REQUIRED` on human gate) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Modify (on `INPUT_REQUIRED` route to wait-activity, do not retry) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map answer endpoint; register router/policy/classifier/resolvers + `RequestInputTool` in the catalog) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Questions/QuestionRouterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Questions/ReversibilitySecurityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/AgentQuestionEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/WaitForAgentQuestionActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/RequestInputToolTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`).
3. Read the deep-dive §5 (interactive question-back) IN FULL + §6 item 5 + §7 open decisions, and the design of record §2 (the call-LLM endpoint).
4. Reviewed `EscalateToSeniorActivity.cs` (the byte-for-byte model for the wait-activity), `WebhookSignalRegistry` (the TCS fast-signal model), `RunAgentPanelActivity`/`AggregatePanelActivity` (32-7, the panel answerer), `InlineToolLoopRunner`/`ManagedAgent` (32-5, where the tool + router are wired), and `IEventRepository` (32-6, the tenant-scoped trail).
5. Confirmed 32-5 (endpoint) and 32-7 (panels) are landed before wiring them; code to the 32-6 trail interface.
6. Planned the TDD approach; the **security re-derivation test (AC4) is written FIRST** — it is the load-bearing control.

### Key Design Decisions

- **A tool call, not a stop reason (deep-dive §5.1).** `request_input` is a tool so the answer returns as a tool-result message and the model resumes in-context — no bespoke conversation-shaping. Executed server-side inside `Tamma.Api`, so the step still never calls a provider.
- **The server re-derives the gate; the model only RAISES (AC4, deep-dive §5.2).** `kind`/`blocking` are hints. The human-gate decision is the MAX of (model hint, server-owned reversibility). A model cannot tag a merge-approval as `fact` to dodge human gating. This is the single load-bearing security control of the story.
- **Pause/resume boundary (deep-dive §5.3).** Fast answers (orchestrator-fact, panel) resolve **in-stream** within `inStreamAnswerTimeout` (TCS-signaled, SSE heartbeats). Slow (human) answers **end the turn** with `INPUT_REQUIRED` and cross engine→bookmark→signal→re-call. **The durable wait lives in the workflow (Elsa bookmark), never in an HTTP connection.** Cost: one extra LLM call to re-prime after the human gap — the only correct shape given a streaming request can't survive an hours-long wait.
- **Stateless endpoint across the human gap (AC9, deep-dive §7 open #6).** No sticky server session; the conversation is rehydrated from the request or the action trail (32-6) keyed by `correlationId` (leaning action-trail). The only durable state is the Elsa bookmark.
- **Audit from the API (AC10, deep-dive §5.3).** `AGENT.QUESTION.*` emitted via the tenant `IEventRepository` where the tenant store lives — never the control plane. Performance/question data is ALWAYS tenant-scoped (design ownership rule).
- **No new control-plane table.** This story adds **no CP table** (the routing policy follows the prompt-override pattern — tenant- or user-keyed in the appropriate store; the action trail is tenant-schema-resident). So: no entry in `Program.cs`'s startup-reset DROP list and no `ControlPlaneDbContextModelTests` edit. If a future revision persists policy as a CP entity, both MUST be updated.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal of a question-back? | The sole user (keyed by `UserId`; `TenantId` may be null). | The tenant (keyed by `TenantId` from `X-Tenant-Id`). No per-user layer. |
| Whose `QuestionRoutingPolicy` applies? | The sole user's policy → system default → ERROR (keyed by `UserId`). | The tenant's policy → system default → ERROR (keyed by `TenantId`; members can't edit it — `tenant_owner`/`tenant_admin` only). |
| Who can answer a human-gated question? | The sole user. | A tenant member; elevated (`tenant_owner`/`tenant_admin`) where the pending action demands approval. **Never** a cross-tenant or platform-admin answer for another tenant's run. |
| Who provides the `judgment` panel? | The user's enabled agents (32-16) run the panel locally. | The tenant's enabled agents (32-16), budget-clamped, SaaS-gated per member (32-7). |
| Where do `AGENT.QUESTION.*` events land? | The user's (sole) tenant event store (`IEventRepository`). | The tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`; `TenantId` set. Never cross-tenant. |
| Who owns the question/answer data? | The user. | The tenant — platform admin sees none of it (design ownership rule). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Adversarial/misclassifying model downgrades a merge-approval to `fact` to dodge the human gate (AC4) | **Critical** | The server **re-derives** the gate from server-owned reversibility; routing is the MAX of (model hint, blast radius); the model can only RAISE/escalate. Load-bearing adversarial test written FIRST. |
| The endpoint holds conversation state across the human gap → a leaked/stale session breaks isolation (AC9) | High | Endpoint is **stateless across the gap**; rehydrate from the action trail (32-6) by `correlationId`; the only durable state is the Elsa bookmark. |
| In-stream wait holds the request too long / blocks a worker (AC6) | High | `inStreamAnswerTimeout` (~90s, tenant-tunable) with SSE heartbeats; only fast (fact/panel) answers wait in-stream; on timeout escalate to the human gate (default for `judgment`). |
| A question is lost (raised, never answered, never paused) → silent stall | High | Every `request_input` resolves to exactly one of {in-stream answer, assumed, human-gate}; `AGENT.QUESTION.RAISED` always pairs with an `ANSWERED`/`ASSUMED`/`ROUTE_DENIED`; tested invariant. |
| `INPUT_REQUIRED` mistaken for a retryable provider failure → workflow retries instead of pausing (AC7) | High | `INPUT_REQUIRED` is a **distinct** `failureCode` (no `httpStatusCode`); the thin step routes to `WaitForAgentQuestionActivity`, never the provider-chain retry; explicit test. |
| `blocking:false` used to waive a human gate (AC5) | High | A non-blocking hint **cannot** waive a gate: when the policy forbids assumption (irreversible / out-of-autonomy), `blocking:false` is ignored and the question is human-gated. |
| Policy resolves to empty/plain → silent wrong routing (AC3) | Medium | tenant→system→**ERROR** (`feedback_resolution_no_empty_fallback`); no empty fallback; `AGENT.QUESTION.ROUTE_DENIED` on no-policy. |
| Panel-answer cost double-counted or mis-attributed (AC11.3) | Medium | Open decision documented (asking-agent budget vs separate clarification line); until decided, attribute to the asking agent's budget and tag the panel sub-run distinctly so 32-9/34-5 can split later. |
| Depends on 32-5 + 32-7 not yet landed | Medium | Code to their interfaces (`IInlineToolLoopRunner`, `RunAgentPanelActivity`); gate behind them; use fakes in tests until they land. |

### Open Questions (from deep-dive §7 — documented, not pre-decided)

1. **In-stream timeout → escalation for `judgment` (open #5):** promote to human bookmark vs proceed-on-assumption vs fail-the-turn. **Chosen default:** escalate to the human gate (configurable per tenant) — conservative vs the 70% autonomy target; revisit if it pauses too aggressively.
2. **Conversation-state rehydration across the human gap (open #6):** workflow variable vs action-trail (32-6) keyed by `correlationId`. **Leaning:** action-trail (durable, already tenant-scoped, time-travel-debuggable).
3. **`request_input` budgeting (open #7):** panel-answer tokens charged to the asking agent's budget vs a separate "clarification" line (affects 32-9 usage + 34-5 markup). **Until decided:** asking-agent budget, panel sub-run tagged distinctly.

### Success Metrics

- [ ] An agent that hits an ambiguity asks via `request_input` instead of guessing/stopping — measurable as `AGENT.QUESTION.RAISED` events on real runs.
- [ ] **Zero** cases where a model's `kind`/`blocking` downgrades a merge/deploy/spend/schema below the human gate (adversarial test + invariant hold).
- [ ] Every `request_input` resolves to exactly one terminal outcome (answered / assumed / human-gated / route-denied) — no silent stalls.
- [ ] Human-gated runs durably suspend (Elsa bookmark) and resume on answer with the conversation re-primed — survives a process restart.
- [ ] `grep` finds no API key in any `AGENT.QUESTION.*` event payload, log, or `INPUT_REQUIRED` response.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§2 the call-LLM endpoint; §5.3 what cannot be mediated)
- Managed-LLM deep dive: `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§5 interactive question-back — the source of record; §6 item 5; §7 open decisions 5/6/7)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-20-interactive-question-back-plan.md`
- Sibling stories: `story-32-5/` (call-LLM endpoint + `InlineToolLoopRunner`/`ManagedAgent`), `story-32-7/` (agent panels — the `judgment` answerer), `story-32-6/` (action trail — events + rehydration substrate), `story-32-9/` (usage metering — panel-answer cost)
- Reused code: `apps/tamma-elsa/src/Tamma.Activities/Blocker/EscalateToSeniorActivity.cs` (the byte-for-byte model for `WaitForAgentQuestionActivity`), `WebhookSignalRegistry` (TCS fast-signal), `ElsaWorkflowService.SendSignalAsync` (resume)

## Logging Requirements

- **INFO**: question raised (`correlationId`, `questionId`, model-supplied `kind`/`blocking`/`confidence` — **never the prompt body verbatim if it may contain secrets**); routing decision (`answerer`, `routingReason`, server-derived `blastRadius`); question answered (`answerer`, `latencyMs`); human gate opened (`correlationId`, bookmark name); answer received (`answeredBy`, `correlationId`).
- **DEBUG**: policy resolution chain (tenant→system), in-stream wait start/signal/timeout, panel sub-run start/aggregate, conversation rehydration source (request vs action-trail).
- **WARN**: in-stream timeout → escalation; `blocking:false` ignored because the action is irreversible; route-denied (no policy).
- **ERROR**: `AGENT.QUESTION.ROUTE_DENIED` (fail-loud, no policy); a raised question that reaches a terminal turn with no resolution (invariant violation); DCB append failure (the run still surfaces its result; the append failure is logged, not swallowed).
- **Structured context**: `{ correlationId, questionId, kind(model), answerer, blastRadius, tenantId, role, action }` where applicable.
- **Credential safety (LOAD-BEARING)**: NEVER log, return, or persist the resolved API key, `BaseUrl` auth, or raw provider headers. The `request_input` tool result, the `INPUT_REQUIRED` response body, every `AGENT.QUESTION.*` event payload, and the action trail are **key-free by contract**. The question/answer text is tenant data — log identifiers, not the verbatim body, where it may carry secrets.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — interactive question-back for the managed agent run: the first-class `request_input` tool (server-side, tool-result-resumes), `IQuestionRouter` + `QuestionRoutingPolicy` (fail-loud tenant→system→error) + the load-bearing server-side reversibility re-derivation (model can RAISE but not DOWNGRADE the human gate), the in-stream fast path (orchestrator-fact / 32-7 panel, TCS-signaled, `inStreamAnswerTimeout`), and the durable human gate (`WaitForAgentQuestionActivity` modeled on `EscalateToSeniorActivity` + `POST /api/v1/agents/questions/{correlationId}/answer` → `SendSignalAsync` resume → stateless re-invoke with the answer re-primed). `AGENT.QUESTION.RAISED/ANSWERED/ASSUMED/ROUTE_DENIED` audit from the API. Depends on 32-5 + 32-7. Open decisions (in-stream timeout escalation, rehydration substrate, request_input budgeting) documented, not pre-decided. | Claude |

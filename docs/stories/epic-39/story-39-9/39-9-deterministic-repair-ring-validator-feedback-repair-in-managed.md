# Story 39-9: Deterministic Repair Ring — Validator-Feedback Repair in the Managed Layer

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

As the **DocumentLifecycleWorkflow** (and any producing workflow behind it),
I want the managed LLM execution layer to run a **bounded, harness-generated repair turn** when a produced document fails its deterministic validator — feeding the domain-phrased violations back to the model as a repair message inside the SAME conversation,
So that transient malformed output (a missing field, a dangling `dependsOn`, an out-of-range score) is fixed in one cheap turn instead of surfacing as a review round or an escalation — while genuinely un-repairable content fails fast as a **typed, non-transient content failure** that never poisons the provider circuit breaker.

## Priority

P1 — The innermost ring of the Epic 39 lifecycle (`VALIDATE → bounded repair turn`, see the lifecycle diagram in the epic README). The lifecycle works without it (validation failure goes straight to `ValidationExhausted` escalation), so it lands after 39-6, and it is **explicitly gated**: repair is enabled per document type only after real-provider failure-rate data justifies the extra turn (AC9).

## Architectural Context (READ FIRST)

The repair ring lives in the **managed execution layer**, NOT in Elsa workflow graphs — because that is the only place the conversation state exists.

- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` — the managed agent execution path (Epic 32). It owns the request/response cycle for a `(role, action)` cell.
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs` (+ `IInlineToolLoopRunner.cs`) — the single home of the agentic loop, extracted verbatim from `CallLlmInlineActivity.AgenticToolLoop`. **It already holds multi-turn conversation state and already has the precedent this story generalizes**: when a tool call fails, the tool error is appended as a `tool_result` block in a follow-up user-role message and the model retries within the same conversation (see the `tool_result` batching around line ~711). The repair turn is the same move with a validator instead of a tool: append a harness-generated user message carrying the violations, re-invoke, re-validate.
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — the engine-side activity that produces `ProviderAttemptDiagnostic` records per provider attempt.
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` — defines `ProviderAttemptDiagnostic` (`ProviderName`, `Succeeded`, `HttpStatusCode`, `ErrorMessage`, `CircuitBreakerSkipped`, `BudgetExhausted`, …). **This story adds an additive `FailureCode` field here.**
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsInlineActivity.cs` — deserializes the last `ProviderAttemptDiagnostic` and, on `!Succeeded`, calls `CheckCircuitBreakerActivity.RecordFailure(...)` (line ~87). **This is exactly where a content failure would today be misrecorded as a provider failure** — the breaker-exclusion branch goes here.
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs` / `CircuitBreakerState.cs` / `CircuitBreakerOptions.cs` — the API-side provider breaker; same exclusion rule applies to any path that records attempts into it.
- **Validators come from Epic 39 document types** (Stories 39-3/39-4): each document type exposes a deterministic validator producing domain-phrased violations (e.g. `Decomposition: task 'T3' depends on undeclared 'T9'`). The repair ring consumes those violations verbatim; it never invents its own error language.
- **Distinction that must not blur:** provider/transport failures (HTTP 5xx, timeout, rate limit) are the breaker's business and are retried by the existing provider chain. Validation failure is a **content** failure — the provider worked fine; the output is wrong. Think 422 vs 503. Conflating them opens the breaker on healthy providers.

## Acceptance Criteria

1. **Repair turn in the managed layer.** When a lifecycle produce step's output fails its document validator and repair is enabled for that document type, the managed execution layer (`ManagedAgent` / `InlineToolLoopRunner`) appends a **harness-generated repair message** to the SAME conversation — containing the domain-phrased violations from the validator, verbatim, plus a fixed instruction to re-emit the full corrected document — and re-invokes the model. The conversation is not restarted; system prompt, prior turns, and tool state are preserved.

2. **Bounded globally.** `maxRepairTurns` defaults to **1** with a hard cap of **2**, configured globally (options class, e.g. `RepairRingOptions`), NOT per call site. No workflow, document type, or prompt can raise the cap above 2. The repaired output is re-validated with the same validator; a passing repair exits the ring immediately.

3. **Typed exhaustion, 422-style.** When repair turns are exhausted and the output still fails validation, the result surfaces to the caller as a **non-transient content failure** (a typed result the lifecycle maps to its `ValidationExhausted` unhandleable outcome — never a bare exception, never a provider error). The final validator violations and the per-turn history (turn count, violation counts) ride on the result for the escalation lineage payload.

4. **Additive `FailureCode` on diagnostics.** `ProviderAttemptDiagnostic` (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs`) gains a nullable `FailureCode` string field (e.g. `"content_validation"`, `"transport"`, `"rate_limit"`, `"budget"`). Additive only: existing serialized diagnostics (older JSON without the field) deserialize cleanly; existing consumers are unaffected. `CallLlmInlineActivity` populates it on the paths it owns.

5. **Content failures never trip the provider circuit breaker.** `RecordDiagnosticsInlineActivity` (and any API-side path recording into `CircuitBreakerService`) treats a diagnostic with `FailureCode == "content_validation"` as **excluded** from `RecordFailure` — it neither increments the failure count nor opens the breaker (and does not count as a success either). A unit test proves: N consecutive validation-exhaustion results leave the breaker Closed, while the same N transport failures open it.

6. **Event trail.** The ring emits DCB events: `LLM.VALIDATION.FAILED` (per failed validation, tags: `issueId`, `documentType`, `role`, `action`, `repairTurn`, data: violation summaries), `LLM.REPAIR.SUCCEEDED` (repair turn produced a valid document, data: turn number), `LLM.REPAIR.EXHAUSTED` (cap hit, still invalid). Event constants live beside the existing agent event-type classes in `apps/tamma-elsa/src/Tamma.Api/Services/Agents/` (`AgentRunEventTypes.cs` pattern).

7. **Repair rate is measurable per cell.** From the events alone (no extra store) one can compute, per `(role, action)` × `documentType` cell: validation-failure rate, first-repair success rate, and exhaustion rate. A test seeds a mixed event stream and computes the three rates via the existing event query path (Story 4-7 API), proving the tags carry enough dimensions.

8. **Repair message is deterministic and safe.** The harness-generated repair message is produced by a pure function (violations in → message out): same violations, same message. It contains ONLY validator output plus fixed instruction text — never raw provider error bodies, never credentials (run it through the existing redaction seam if violations can embed model output). Golden-file or snapshot test pins the template.

9. **Gated per document type, default OFF.** Repair enablement is per-document-type configuration (e.g. `RepairRingOptions.EnabledDocumentTypes`), defaulting to **empty**. With the gate off, a validation failure skips the ring entirely (zero extra turns, straight to the typed content failure) and emits only `LLM.VALIDATION.FAILED`. The story ships the mechanism dark; enabling a type requires observed real-provider failure-rate evidence (documented in `.dev/findings/` when flipped).

## Technical Notes

- **Where the loop hook goes.** `InlineToolLoopRunner.RunAsync` returns the final assistant message today. The cleanest seam is a post-completion callback/validator delegate on the run request (`ManagedAgentRequest`) so the runner can validate-then-repair inside the conversation before returning — mirroring how tool errors already loop. Do NOT bolt repair onto the Elsa graph (a second `llm-call` dispatch would start a fresh conversation and lose the produce context, which defeats the point).
- **Repair turns vs tool-loop turns.** Repair turns do not consume `ToolLoopConfig.maxSteps`; they are counted separately (`RepairTurns` on the result, alongside `ToolLoopTurns`). Token usage from repair turns is accumulated into the existing usage totals so budget accounting stays truthful.
- **Interaction with the provider chain.** A transport failure DURING a repair turn is still a transport failure — normal provider retry/breaker semantics apply to it. Only the validation verdict is content-coded. Keep the two axes orthogonal in the diagnostics: one attempt can be `Succeeded=true` transport-wise while the ring later records a content failure.
- **Why cap 2.** Data from the tool-loop precedent shows the second identical correction rarely converges; each repair turn is a full-context spend. Above the cap the marginal fix rate does not pay for the tokens — the lifecycle's review/revise ring (which brings a reviewer, not just a validator) is the correct next escalation, not more repair.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/` beside `InlineToolLoopRunnerTests.cs` / `ManagedAgentTests.cs`; NUnit + FluentAssertions, no live provider (fake HTTP handler, as those tests already do).

## Dependencies

- **Story 39-3 / 39-4 (document types)** — supply the deterministic validators and domain-phrased violations the ring feeds back. Blocking.
- **Story 39-6 (DocumentLifecycleWorkflow)** — the caller: maps ring exhaustion to `ValidationExhausted`. The ring's result contract must land in 39-6's outcome union.
- **Epic 32 managed layer** — `ManagedAgent` / `InlineToolLoopRunner` as the execution home (existing).
- **Story 4-7 (event query API)** — used by the repair-rate measurability AC (existing).
- **Consumed by 39-12..39-15** — migrated workflows get repair for free once their document type is gated on.

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |

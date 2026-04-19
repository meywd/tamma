# Finding 017: SaaS LLM proxy response shape changed from OpenAI-compatible to flat

**Scope**: engine (SaaS)
**Severity**: P2 (correctness — SDK clients must update, no silent data loss)
**Status**: Behavioral drift (ported + enhanced, but response shape diverged from TS)
**Estimated port effort**: 2h

## 1. What's in TS

- File: `packages/api/src/routes/saas/llm-proxy.ts` (9e9a57c~1)
- Contract: `POST /api/v1/llm/chat` — OpenAI-compatible request/response. The TS implementation was a **stub** (returned a hardcoded `[Stub]` assistant reply), but its response shape was the contract that any future real provider would have to satisfy:

```typescript
// packages/api/src/routes/saas/llm-proxy.ts:41-70 (9e9a57c~1)
return reply.send({
  id: `chat_${Date.now()}`,
  model: model ?? 'stub',
  choices: [
    {
      index: 0,
      message: {
        role: 'assistant' as const,
        content: '[Stub] LLM proxy is not yet connected to a real provider.',
      },
      finishReason: 'stop',
    },
  ],
  usage: {
    promptTokens: messages.reduce((sum, m) => sum + m.content.length, 0),
    completionTokens: 0,
    totalTokens: messages.reduce((sum, m) => sum + m.content.length, 0),
  },
  meta: {
    maxTokens: maxTokens ?? null,
    temperature: temperature ?? null,
    stub: true,
  },
});
```

Response is **OpenAI Chat Completions-shaped**: `{id, model, choices: [{message: {role, content}, finishReason}], usage: {...}}`. This is the shape any SDK written against OpenAI/compatibility layers (LangChain, OpenRouter, many internal tools) expects.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:35-82`

```csharp
// SaaSEndpoints.cs:70-81 (current)
return Results.Ok(new
{
    model = response.Model,
    text = response.Text,
    usage = new
    {
        promptTokens = response.PromptTokens,
        completionTokens = response.CompletionTokens,
        totalTokens = response.TotalTokens
    },
    costUsd = response.CostUsd
});
```

- File: `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/LlmProxyService.cs` — the response shape is **flat**: `{model, text, usage, costUsd}`. No `choices[]`, no `id`, no `finishReason`, no message role.

This is a substantial upgrade in terms of **behavior** (real Anthropic call, per-tenant budget enforcement, cost accounting via `IDiagnosticsService`) but a **contract break** for any SDK client that parsed `choices[0].message.content`.

- Tests: C# tests assert `response.text` is populated. No test asserts OpenAI-shape compatibility.

## 3. The gap

- TS did: OpenAI-compatible response (even as a stub, the contract was preserved).
- C# does: flat `{text, costUsd}` plus usage. Real Anthropic integration, per-tenant budget enforcement, plus a `costUsd` field not present in TS (positive addition).

For an SDK client parsing `response.choices[0].message.content`:

- TS: `[Stub] LLM proxy is not yet connected to a real provider.`
- C#: `TypeError: Cannot read properties of undefined (reading '0')`.

For a client parsing `response.text`:

- TS: `undefined`.
- C#: the real LLM output.

Note that **no deployed Elsa activity currently calls this endpoint** — all LLM calls go direct to Anthropic via `CallLlm` in each activity (see finding 001 — activities use `/api/engine/execute-task` instead). So the immediate impact is limited to third-party SDK clients. But the SaaS pricing tier is sold on this endpoint; external partners will hit this.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md` notes the SaaS endpoints were to be ported "as-is" but acknowledges the TS version was a stub.
- No story explicitly locks in the OpenAI-compatible shape, but the TS stub was clearly designed for OpenAI SDK compatibility (the field names and structure are one-to-one with OpenAI).
- Story alignment:
  - [x] Matches TS behavior (C# diverges — both improved and broke)
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (no story locks the OpenAI shape)
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift — real improvement in functionality, silent break in response contract.
- **What's needed to finish**:
  1. Decide: maintain OpenAI-compatible shape (recommended) or document the new flat shape as the contract.
  2. If OpenAI-compatible: wrap `response.Text` into `{choices: [{index: 0, message: {role: "assistant", content: response.Text}, finishReason: "stop"}]}` plus `id` and `model`. Keep `costUsd` as an extension field under `meta` or a top-level non-OpenAI addition.
  3. Add an OpenAPI example response locking the shape in.
- **Is it "just a stub" or is scope missing?** The C# port exceeded TS scope (good!) but broke the response contract (bad). Quick to fix.
- **Blockers**: none. Product decision about which shape to ship.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:70-81` — wrap the response.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/SaaS/ChatCompletionResponseDto.cs` — typed OpenAI-compatible shape.
- Tests to add:
  - `LlmChat_ResponseShape_MatchesOpenAiCompatibility` — asserts `choices[0].message.content` present.
  - `LlmChat_Usage_FieldsCamelCase_PromptTokens_CompletionTokens_TotalTokens`
  - `LlmChat_Exposes_CostUsd_AsExtension`
- Estimated effort: 2h
  - Wrap response: 30m
  - Typed DTO: 30m
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/saas/llm-proxy.ts` (stub but OpenAI-shaped)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:35-82`, `Services/SaaS/LlmProxyService.cs`
- Story: `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md`
- Related findings: `001-execute-task-stub.md` (alternative LLM surface used by Elsa activities)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: c9dd51e
- **Notes**: `SaaSEndpoints.LlmChat` now wraps the Anthropic response in
  the OpenAI Chat Completions shape `{id, model, choices: [{index,
  message: {role, content}, finishReason}], usage}` so SDK clients
  written against the TS contract / OpenAI compatibility layers don't
  break on `choices[0].message.content` access. Tamma-specific
  extension fields (`text`, `costUsd`, `meta`) are retained as a
  superset. Tenant resolution rewritten to honour the same priority
  the rest of the platform uses: ambient `ITenantContext` →
  `AuthPrincipal` tagged-union → JWT `tenantId|tid` claim. Closes the
  drift orgs flagged at line 202.

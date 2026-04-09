# Story 12-7c: Context Budget Manager

Status: ready-for-dev

## Story

As an **LLM agent in the tool loop**,
I want the system to track how many tokens my context tool results are consuming and stop me from pulling more context when the budget is nearly full,
so that I don't waste the context window on excessive context retrieval and leave room for my actual task output.

## Summary

Implement a context budget manager that tracks cumulative token usage across all context tool call results within a single tool loop session. The budget is derived from the provider's context window size (from agent config) minus reserved space for system prompt, user prompt, and expected output. When the budget is nearly full, the manager signals the LLM via a warning in tool results. Priority-based dropping ensures CRITICAL results are kept while LOW results are truncated first. This replaces the static `ContextCompactor` approach for context tool results (the ContextCompactor still handles overall conversation compaction).

## Acceptance Criteria

### AC1: Token Budget Tracking
- [ ] `ContextToolBudgetManager` tracks cumulative token usage across all context tool results in the current session
- [ ] Budget = `contextWindowTokens - systemPromptTokens - userPromptTokens - reservedOutputTokens`
- [ ] `reservedOutputTokens` defaults to `maxTokens` from the LLM call config (space for the LLM's response)
- [ ] Each context tool result's token count is recorded after the tool returns
- [ ] `getRemainingBudget()` returns tokens remaining for additional context

### AC2: Budget Enforcement
- [ ] When remaining budget < 500 tokens, context tools append a warning to their output: "Context budget nearly full. {N} tokens remaining. Consider using the context you already have."
- [ ] When remaining budget <= 0, context tools return: "Context budget exhausted. You have enough context to proceed. No more context queries allowed this session."
- [ ] The tool still executes the search (results may be useful even if truncated), but the output is truncated to fit the remaining budget

### AC3: Priority-Based Result Dropping
- [ ] Context tool results are tagged with priority levels:
  - `CRITICAL`: Direct matches to the current task (score > 0.9), prohibited actions, blocking conventions
  - `HIGH`: Strong matches (score > 0.7), relevant learnings, important conventions
  - `NORMAL`: Moderate matches (score > 0.5), general context
  - `LOW`: Weak matches (score < 0.5), tangential context
- [ ] When budget is tight (< 30% remaining), LOW results are dropped first
- [ ] When budget is very tight (< 15% remaining), NORMAL results are also dropped
- [ ] CRITICAL and HIGH results are never dropped, only truncated if absolutely necessary

### AC4: Provider-Specific Limits
- [ ] Context window size is read from the agent configuration (via `ToolLoopConfig.ContextWindowTokens`)
- [ ] Different providers have different limits: Anthropic Claude (200K), OpenAI GPT-4 (128K), etc.
- [ ] The budget manager receives the context window size at initialization, not hardcoded

### AC5: Integration with Tool Loop
- [ ] Budget manager is instantiated per tool loop session (scoped lifetime)
- [ ] After each context tool execution, the tool loop records the result's token count with the budget manager
- [ ] Budget state is available to all context tools via dependency injection
- [ ] Non-context tools (file_read, shell_execute, etc.) do NOT count against the context budget -- they use the general conversation compaction mechanism

### AC6: Reporting
- [ ] Budget utilization logged at INFO level after each context tool call
- [ ] Budget exhaustion logged at WARN level
- [ ] Final budget summary logged at session end: total context tokens used, budget utilization %, results dropped count

## Technical Context

### Existing Budget Manager (Node.js)

The `packages/intelligence/src/context/budget-manager.ts` already implements budget allocation for the static context aggregation pipeline. It allocates tokens across sources (vector_db, rag, mcp, web_search) based on task-type priorities.

The new `ContextToolBudgetManager` is different:
- It runs in C# within the Elsa tool loop (not Node.js)
- It tracks cumulative usage across tool calls over time (not a single-shot allocation)
- It integrates with the tool executor pattern, not the context aggregation pipeline
- It focuses on dynamic, LLM-driven context retrieval, not pre-computed context

### Relationship to ContextCompactor

The existing `ContextCompactor` (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextCompactor.cs`) handles overall conversation compaction when the context window is getting full. It summarizes older messages.

The `ContextToolBudgetManager` is complementary:
- **ContextCompactor**: Manages the overall conversation history size (system prompt + messages + tool results)
- **ContextToolBudgetManager**: Manages specifically how much context the LLM can pull via context tools before it should stop searching and start working

They do not replace each other. The budget manager prevents the LLM from filling the context window with search results, leaving room for the actual task conversation.

### Key Files

| File | Role |
|------|------|
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextCompactor.cs` | Existing conversation compactor (kept as-is) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/TokenEstimator.cs` | Token counting utility |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | `ToolLoopConfig.ContextWindowTokens` |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Tool loop where budget is tracked |
| `packages/intelligence/src/context/budget-manager.ts` | Existing Node.js budget manager (reference, not modified) |

## Dependencies

- **Story 12-7a**: Vector DB search tools (budget tracks their results)
- **Story 12-7b**: Convention & history tools (budget tracks their results)
- **Story 12-2**: Tool loop in CallLlmInlineActivity
- **Epic 9**: Agent configuration (provides context window size per provider)

## Estimated Effort

| Task | Hours |
|------|-------|
| `ContextToolBudgetManager` C# class | 6 |
| Priority tagging for tool results | 4 |
| Integration with tool loop | 4 |
| Unit tests (10+ tests) | 4 |
| Integration tests (2 tests) | 2 |
| **Total** | **20 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |

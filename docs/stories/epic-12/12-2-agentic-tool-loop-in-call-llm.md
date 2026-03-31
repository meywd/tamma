# Story 12.2: Agentic Tool Loop in CallLlm

Status: ready-for-dev

## Story

As a **platform engineer**,
I want `CallLlmInlineActivity` to support a multi-turn agentic tool loop that iteratively calls the LLM, executes returned tool calls, and feeds results back until the LLM produces a text-only response,
so that workflows can delegate complex multi-step tasks (code generation, debugging, refactoring) to a single LLM activity that autonomously uses tools to complete the work.

## Acceptance Criteria

1. When `EnableToolLoop` is `false` (default), `CallLlmInlineActivity` behaves identically to its current single-turn implementation (zero behavior change for existing callers)
2. When `EnableToolLoop` is `true`, the activity enters a while-loop: call LLM with conversation messages, parse response for tool calls, execute tools via `IToolExecutorRegistry`, append results as conversation messages, repeat
3. The loop terminates when: LLM produces a text-only response (no tool calls), OR `stop_reason` is `end_turn`, OR `maxSteps` is reached
4. `maxSteps` defaults to 20 and is configurable via `ToolLoopConfig`
5. Tool calls are validated against the `allowedTools` list before execution; rejected tools return an error result message to the LLM (the loop continues)
6. Conversation history is maintained as a `List<ConversationMessage>` — system prompt, user message, then alternating assistant (with tool calls) and tool result messages
7. Provider-specific message formatting: Anthropic uses `tool_result` as user-role content blocks; OpenAI uses `tool` role messages with `tool_call_id`
8. `StopReason` is parsed from the LLM response (`end_turn`, `tool_use`, `stop`, `max_tokens`) and used for loop control
9. `LlmCallWorkflow` propagates new output fields: `ToolLoopTokens` (cumulative token usage) and `ToolLoopTurns` (number of loop iterations)
10. Token usage is accumulated across all turns and reported in the final output
11. When `maxSteps` is reached without a text-only response, the last LLM response is returned with a warning annotation
12. 20+ tests covering the loop, termination conditions, tool execution, error handling, and backward compatibility

## Technical Context

### The While-Loop

```
if (!EnableToolLoop):
    // Existing single-turn behavior — zero changes
    response = await CallLlm(systemPrompt, userPrompt)
    return response

// Agentic tool loop
messages = [
    ConversationMessage("system", systemPrompt),
    ConversationMessage("user", userPrompt)
]

for (int step = 0; step < config.MaxSteps; step++):
    response = await CallLlm(messages)

    if (response.StopReason != "tool_use" || response.ToolCalls.Length == 0):
        break  // LLM is done

    // Validate and execute tool calls
    foreach (var toolCall in response.ToolCalls):
        if (!registry.IsAllowed(toolCall.Name, config.AllowedTools)):
            results.Add(ErrorResult(toolCall, "Tool not allowed"))
            continue
        var executor = registry.GetExecutor(toolCall.Name)
        if (executor == null):
            results.Add(ErrorResult(toolCall, "Unknown tool"))
            continue
        var result = await executor.ExecuteAsync(toolCall.Id, toolCall.ArgumentsJson, ct)
        results.Add(result)

    // Append assistant message + tool results to conversation
    messages.Add(ConversationMessage("assistant", response))
    foreach (var result in results):
        messages.Add(ConversationMessage("tool", result))

    // Accumulate tokens
    totalTokens += response.InputTokens + response.OutputTokens

return response with ToolLoopTokens=totalTokens, ToolLoopTurns=step
```

### Provider Message Format Differences

**Anthropic:**
```json
{
  "role": "user",
  "content": [
    { "type": "tool_result", "tool_use_id": "call_123", "content": "file contents..." }
  ]
}
```

**OpenAI:**
```json
{
  "role": "tool",
  "tool_call_id": "call_123",
  "content": "file contents..."
}
```

The `ConversationMessage` model is provider-agnostic. Serialization to provider format happens in the LLM call layer (existing provider-specific code in `CallLlmInlineActivity`).

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — add tool loop logic, inject `IToolExecutorRegistry`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` — add `EnableToolLoop` and `ToolLoopConfig` to `LlmCallWorkflowInput`; add `StopReason`, `ToolCalls` to `NormalizedLlmResponse`; add `ToolLoopTokens`, `ToolLoopTurns` to output
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` — propagate `EnableToolLoop`, `ToolLoopConfig` from input to activity; propagate `ToolLoopTokens`, `ToolLoopTurns` to output

### Backward Compatibility

This is the highest-risk area. The guard is simple:

```csharp
if (!input.EnableToolLoop)
{
    // Exact existing code path — no changes whatsoever
    return await SingleTurnCall(systemPrompt, userPrompt, tools, ct);
}
// New: agentic loop
return await AgenticToolLoop(systemPrompt, userPrompt, tools, config, ct);
```

`EnableToolLoop` defaults to `false`. Existing workflows that do not set it get zero behavior change.

## Implementation Notes

1. Extract the current single-turn LLM call logic into a private `SingleTurnCall()` method before adding the loop. This ensures the existing code path is untouched.
2. The agentic loop should be a separate private method `AgenticToolLoop()` for clarity and testability.
3. Parse `StopReason` from provider responses: Anthropic returns `"end_turn"` or `"tool_use"` in `stop_reason`; OpenAI returns `"stop"` or `"tool_calls"` in `finish_reason`. Normalize both to the `StopReason` enum.
4. When `maxSteps` is reached, log WARN: `"Tool loop reached maxSteps ({maxSteps}), returning last response"`. Annotate the response with `ToolLoopExhausted = true`.
5. Each tool execution should be wrapped in a try-catch. On exception, return a `ToolExecutionResult` with `Success = false` and the exception message as `Output` — the LLM sees the error and can adapt.
6. Token accumulation uses `NormalizedLlmResponse.InputTokens` and `OutputTokens` fields. If a provider does not report tokens, estimate using the 4-chars-per-token approximation.

## Testing Strategy

- **Backward compatibility tests** (3): `EnableToolLoop=false` produces identical output to current behavior; existing workflow tests still pass; no regression in single-turn call path
- **Loop termination tests** (4): Text-only response terminates loop; `end_turn` stop reason terminates; `maxSteps` reached terminates with warning; empty tool calls terminates
- **Tool execution tests** (5): Allowed tool executes and result fed back; disallowed tool returns error to LLM; unknown tool returns error to LLM; tool exception returns error to LLM; multiple tools in one turn all execute
- **Conversation history tests** (3): Messages accumulate correctly; system prompt always first; tool results formatted correctly per provider
- **Token tracking tests** (3): Tokens accumulated across turns; reported in final output; estimation used when provider does not report
- **Integration test** (2): Multi-turn session: LLM calls tool, sees result, calls another tool, produces final answer; LLM calls disallowed tool, gets error, adapts and uses allowed tool
- **Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/AgenticToolLoopTests.cs`

## Dependencies

- **Story 12.1** (Tool Executor Interface & Registry) — provides `IToolExecutor`, `IToolExecutorRegistry`, `ToolExecutionResult`, `ConversationMessage`, built-in tools

## Estimated Effort

3-4 days

## Logging Requirements

### Existing Coverage

Line 121 mentions: "log WARN: 'Tool loop reached maxSteps ({maxSteps}), returning last response'". This is a single log statement. The tool loop is the most complex runtime path in the system and needs comprehensive observability.

### Required Additions

`CallLlmInlineActivity` already has `ILogger<CallLlmInlineActivity>`. Use it throughout the loop.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Tool loop entered | INFO | `{WorkflowInstanceId}`, `{Provider}`, `{Model}`, `{MaxSteps}`, `{AllowedToolCount}` | One-time log at loop entry |
| Tool loop turn started | INFO | `{WorkflowInstanceId}`, `{TurnNumber}`, `{MessageCount}`, `{EstimatedTokens}` | Per-turn entry point |
| LLM response received in loop | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{StopReason}`, `{ToolCallCount}`, `{InputTokens}`, `{OutputTokens}`, `{DurationMs}` | Per-turn LLM response metadata |
| Tool call dispatched | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ToolCallId}`, `{ToolName}` | Before each tool execution |
| Tool call result received | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ToolCallId}`, `{ToolName}`, `{Success}`, `{DurationMs}`, `{OutputSizeBytes}` | After each tool execution |
| Tool call rejected (not allowed) | WARN | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ToolCallId}`, `{ToolName}` | Tool not in allowedTools list |
| Tool call rejected (unknown tool) | WARN | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ToolCallId}`, `{ToolName}` | Tool not in registry |
| Tool call exception | ERROR | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ToolCallId}`, `{ToolName}`, `{ExceptionType}`, `{ExceptionMessage}` | Tool execution threw |
| Tool loop turn completed | INFO | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ToolsExecuted}`, `{ToolsSucceeded}`, `{ToolsFailed}`, `{TurnDurationMs}`, `{CumulativeTokens}` | Per-turn summary |
| Tool loop completed (text response) | INFO | `{WorkflowInstanceId}`, `{TotalTurns}`, `{TotalToolCalls}`, `{TotalTokens}`, `{TotalDurationMs}` | Normal termination |
| Tool loop completed (end_turn) | INFO | `{WorkflowInstanceId}`, `{TotalTurns}`, `{TotalToolCalls}`, `{TotalTokens}`, `{TotalDurationMs}` | Normal termination via stop reason |
| Tool loop exhausted (maxSteps) | WARN | `{WorkflowInstanceId}`, `{MaxSteps}`, `{TotalToolCalls}`, `{TotalTokens}`, `{TotalDurationMs}` | Abnormal termination — LLM did not finish within budget |
| Token usage per turn | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{InputTokens}`, `{OutputTokens}`, `{CumulativeInputTokens}`, `{CumulativeOutputTokens}` | Running token accounting |

### Sensitive Data Redaction

- **Never** log prompt content, tool call arguments, or tool execution output.
- **Never** log the LLM response text — only metadata (stop reason, token counts, tool call count).
- Log tool names (from a known vocabulary) and tool call IDs (LLM-assigned, opaque).

### Correlation IDs

- All loop logs must include `{WorkflowInstanceId}` for tracing across the entire workflow execution.
- `{TurnNumber}` is the loop iteration counter (0-based) — critical for debugging multi-turn sessions.
- `{ToolCallId}` links to specific tool execution logs from Story 12.1.

### Execution Store Operations

- When `ToolLoopTokens` and `ToolLoopTurns` are written to workflow output variables, log at DEBUG: `{WorkflowInstanceId}`, `{ToolLoopTokens}`, `{ToolLoopTurns}`.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-agentic-tool-loop.md` Phases 1.5-1.7 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |

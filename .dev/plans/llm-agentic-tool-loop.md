# Plan: Agentic Tool Execution Loop

## Summary
Add in-process tool execution loop to CallLlmInlineActivity. Transforms single-turn LLM calls into multi-turn agentic sessions. Backward compatible via EnableToolLoop flag.

## Phase 1 (P0): Tool Loop + Multi-Turn (core, ship together)

### 1.1 New Models (LlmCallModels.cs)
- ConversationMessage (role, content, toolCalls, toolCallId)
- ToolExecutionResult (toolCallId, toolName, success, output, durationMs)
- ToolLoopTokenTracker (cumulative per-turn token tracking)
- ToolLoopConfig (maxSteps=20, allowedTools, contextWindowTokens, compactionThreshold)
- Add StopReason to NormalizedLlmResponse
- Add EnableToolLoop + ToolLoopConfig to LlmCallWorkflowInput

### 1.2 IToolExecutor Interface
- `string ToolName { get; }`
- `Task<ToolExecutionResult> ExecuteAsync(toolCallId, argumentsJson, ct)`

### 1.3 IToolExecutorRegistry
- `GetExecutor(toolName)` → IToolExecutor?
- `IsAllowed(toolName, allowlist)` → bool
- Populated via DI (IEnumerable<IToolExecutor>)

### 1.4 Built-in Tool Implementations
- FileReadTool, FileWriteTool, SearchCodeTool
- ShellExecuteTool (with blockedCommandPatterns validation)
- GitOperationsTool, RunTestsTool

### 1.5 The While-Loop (CallLlmInlineActivity.cs)
```
if (!EnableToolLoop): single call (exact current behavior)
else:
  messages = [system, user]
  for step in 0..maxSteps:
    response = CallLlm(messages)
    if no tool_calls or stop_reason == "end_turn": break
    validate tool calls against allowlist
    execute tools → results
    append assistant msg + tool result msgs to history
    accumulate tokens
  return final response
```

### 1.6 Provider Message Format
- Anthropic: tool_result as user-role content blocks
- OpenAI: tool role messages with tool_call_id
- Both: parse stop_reason/finish_reason for loop control

### 1.7 Workflow Integration
- LlmCallWorkflow propagates new output fields (ToolLoopTokens, ToolLoopTurns)
- No structural workflow changes needed

## Phase 2 (P1): Context Compaction
- TokenEstimator.cs — ~4 chars/token approximation
- ContextCompactor.cs — trigger at 80% of context window
- Summarize oldest messages (excluding system + recent 4) via compaction LLM call
- Replace summarized messages with single summary message

## Phase 3 (P2): Streaming + Parallel Tools
- SSE streaming: stream=true for Anthropic/OpenAI
- Progress events: TOOL_LOOP.TURN_STARTED, TOOL_EXECUTING, TOOL_COMPLETED
- Parallel tool execution: Task.WhenAll when LLM returns multiple tool calls
- Thread safety: semaphore for filesystem tools

## Phase 4: Security Integration
- Tool allowlist checked INSIDE loop before each execution
- Rejected tools return error message to LLM (not crash)
- Tool output truncated to 50KB, redacted for API keys
- Shell commands validated against blockedCommandPatterns
- File paths validated against workspace root (no traversal)

## New Files: 11 source + 5 test files
## Modified Files: 5 (CallLlmInlineActivity, LlmCallModels, LlmCallWorkflow, Program.cs, AgentSeeder)

## Risks
- Backward compat: EnableToolLoop=false by default, zero behavior change for existing callers
- Infinite loops: MaxSteps=20 guard + budget tracking per turn
- Context overflow: 50KB tool output cap + 80% compaction trigger
- Provider divergence: ConversationMessage is provider-agnostic, serialized per-provider in call methods

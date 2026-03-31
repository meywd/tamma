---
title: "Story 12.4: Streaming & Parallel Tools"
sidebar:
  order: 120
---

Status: ready-for-dev

## Story

As a **platform engineer**,
I want the agentic tool loop to stream LLM responses in real-time via SSE and execute independent tool calls in parallel,
so that users see incremental progress during long-running tool sessions, and multi-tool turns complete faster when tools are independent.

## Acceptance Criteria

1. When streaming is enabled (`stream=true`), LLM responses are streamed token-by-token via SSE (Server-Sent Events)
2. SSE events emitted during the tool loop: `TOOL_LOOP.TURN_STARTED`, `TOOL_LOOP.TOOL_EXECUTING`, `TOOL_LOOP.TOOL_COMPLETED`, `TOOL_LOOP.TURN_COMPLETED`
3. Each SSE event includes: `turnNumber`, `toolName` (where applicable), `durationMs` (where applicable), and `tokenCount` (where applicable)
4. When the LLM returns multiple tool calls in a single response, independent tools execute in parallel via `Task.WhenAll`
5. Filesystem tools (FileRead, FileWrite) are serialized via a semaphore to prevent race conditions (concurrent reads to the same file, write-after-read, etc.)
6. Parallel execution timeout: if any tool exceeds its individual timeout, it is cancelled without affecting other tools
7. Streaming is opt-in via a configuration flag (default: `false`) to maintain backward compatibility
8. 15+ tests covering streaming event emission, parallel execution, semaphore serialization, timeout handling, and backward compatibility

## Technical Context

### SSE Streaming

Streaming allows the UI to show real-time progress during long tool loops. The SSE events provide structured progress information:

```
event: TOOL_LOOP.TURN_STARTED
data: {"turnNumber": 3, "messageCount": 8, "estimatedTokens": 45000}

event: TOOL_LOOP.TOOL_EXECUTING
data: {"turnNumber": 3, "toolName": "file_read", "toolCallId": "call_abc"}

event: TOOL_LOOP.TOOL_COMPLETED
data: {"turnNumber": 3, "toolName": "file_read", "toolCallId": "call_abc", "success": true, "durationMs": 45}

event: TOOL_LOOP.TOOL_EXECUTING
data: {"turnNumber": 3, "toolName": "search_code", "toolCallId": "call_def"}

event: TOOL_LOOP.TOOL_COMPLETED
data: {"turnNumber": 3, "toolName": "search_code", "toolCallId": "call_def", "success": true, "durationMs": 120}

event: TOOL_LOOP.TURN_COMPLETED
data: {"turnNumber": 3, "totalTools": 2, "totalDurationMs": 165, "cumulativeTokens": 47500}
```

### Parallel Tool Execution

When the LLM returns 3 tool calls in one turn:
- `file_read("src/foo.cs")` + `file_read("src/bar.cs")` + `search_code("pattern")` — all 3 can run in parallel
- `file_read("src/foo.cs")` + `file_write("src/foo.cs")` — must be serialized (same file)
- `shell_execute("dotnet test")` + `file_write("test.cs")` — can run in parallel (different resources)

Filesystem tools use a `SemaphoreSlim(1, 1)` keyed by normalized file path. This serializes operations on the same file while allowing operations on different files to proceed in parallel.

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolLoopEventEmitter.cs` — SSE event emission for tool loop progress
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ParallelToolExecutor.cs` — parallel execution with semaphore serialization

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — integrate streaming and parallel execution into the tool loop
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` — add `EnableStreaming` flag to `ToolLoopConfig`

### Parallel Execution Strategy

```csharp
async Task<ToolExecutionResult[]> ExecuteToolsInParallel(
    ToolCallInfo[] toolCalls,
    IToolExecutorRegistry registry,
    CancellationToken ct)
{
    var tasks = toolCalls.Select(async tc =>
    {
        var executor = registry.GetExecutor(tc.Name);
        if (executor is IFileSystemTool fsTool)
        {
            var key = NormalizePath(fsTool.GetTargetPath(tc.ArgumentsJson));
            await _fileLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1)).WaitAsync(ct);
            try { return await executor.ExecuteAsync(tc.Id, tc.ArgumentsJson, ct); }
            finally { _fileLocks[key].Release(); }
        }
        return await executor.ExecuteAsync(tc.Id, tc.ArgumentsJson, ct);
    });

    return await Task.WhenAll(tasks);
}
```

### Streaming Integration

The tool loop calls the LLM with `stream: true` when streaming is enabled. This requires:
- Anthropic: `stream: true` in the Messages API — response arrives as SSE `message_start`, `content_block_delta`, `message_stop` events
- OpenAI: `stream: true` in the Chat Completions API — response arrives as SSE `delta` chunks

Tool calls in streaming mode are accumulated from the stream (they arrive as partial deltas) before execution begins.

## Implementation Notes

1. `ToolLoopEventEmitter` takes an `IHttpContextAccessor` (or equivalent SSE writer) and emits events as JSON lines. If no SSE connection is active (e.g., CLI mode, non-streaming caller), events are silently dropped.
2. `ParallelToolExecutor` manages the `ConcurrentDictionary<string, SemaphoreSlim>` for file path locking. The dictionary should be scoped to the activity execution (not static) to avoid cross-session interference.
3. Individual tool timeout is enforced via `CancellationTokenSource.CreateLinkedTokenSource(ct)` with a per-tool timeout. When a tool times out, its result is `Success = false, Output = "Tool execution timed out after {timeout}ms"`.
4. Streaming adds complexity to tool call parsing: tool calls arrive as partial JSON deltas that must be accumulated into complete `ToolCallInfo` objects. This accumulation logic should be in a separate `StreamingToolCallAccumulator` helper.
5. Backward compatibility: when `EnableStreaming` is `false`, no SSE events are emitted and tool calls are parsed from the complete (non-streaming) response as before.
6. Consider rate-limiting SSE events (max 10 per second) to avoid overwhelming the client with high-frequency updates during rapid tool execution.

## Testing Strategy

- **SSE event tests** (4): Turn started event emitted, tool executing event emitted, tool completed event emitted, turn completed event emitted with correct aggregates
- **Parallel execution tests** (4): Independent tools execute in parallel (verify via timing), same-file tools serialized (verify order), timeout cancels individual tool without affecting others, all results collected
- **Semaphore tests** (3): File lock acquired and released, concurrent reads to different files proceed, write-after-read on same file serialized
- **Streaming tests** (2): Streaming enabled produces SSE events, streaming disabled produces no SSE events
- **Backward compatibility tests** (2): Non-streaming callers unaffected, single tool call still works (no parallel overhead)
- **Test files**:
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ParallelToolExecutorTests.cs`
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ToolLoopEventEmitterTests.cs`

## Dependencies

- **Story 12.2** (Agentic Tool Loop in CallLlm) — the sequential tool loop must exist before streaming and parallel execution are added on top

## Estimated Effort

2-3 days

## Logging Requirements

### Existing Coverage

The story defines SSE events (`TOOL_LOOP.TURN_STARTED`, `TOOL_LOOP.TOOL_EXECUTING`, etc.) which serve as external observability events, but **no structured logging** (ILogger) is specified. SSE events are client-facing; server-side logs are needed independently for backend debugging.

### Required Additions

`ToolLoopEventEmitter` and `ParallelToolExecutor` **must** inject `ILogger<T>` via constructor.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| SSE event emitted | DEBUG | `{EventType}`, `{TurnNumber}`, `{ToolName}` (if applicable), `{WorkflowInstanceId}` | Trace each SSE event emission for debugging client connectivity issues |
| SSE connection not active (event dropped) | DEBUG | `{EventType}`, `{WorkflowInstanceId}` | Silent drop — only log for debugging, not a problem |
| Parallel tool execution started | INFO | `{WorkflowInstanceId}`, `{TurnNumber}`, `{TotalToolCalls}`, `{ParallelBatchCount}` | How many tools run in this parallel batch |
| Parallel tool execution completed | INFO | `{WorkflowInstanceId}`, `{TurnNumber}`, `{TotalToolCalls}`, `{TotalDurationMs}`, `{SuccessCount}`, `{FailureCount}` | Batch summary |
| File semaphore acquired | DEBUG | `{ToolCallId}`, `{ToolName}`, `{NormalizedPath}`, `{WaitDurationMs}`, `{WorkflowInstanceId}` | Semaphore contention tracking — do NOT log the actual file path if it may be sensitive |
| File semaphore released | DEBUG | `{ToolCallId}`, `{ToolName}`, `{WorkflowInstanceId}` | Semaphore release confirmation |
| Individual tool timeout in parallel batch | WARN | `{ToolCallId}`, `{ToolName}`, `{TimeoutMs}`, `{WorkflowInstanceId}` | One tool timed out; others continued |
| Streaming response accumulation started | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{Provider}` | Streaming mode entry |
| Streaming response accumulation completed | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{AccumulatedToolCallCount}`, `{ResponseChunkCount}`, `{DurationMs}` | Streaming mode exit — how many chunks were accumulated |
| Streaming tool call parsing error | WARN | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ErrorMessage}` | Partial JSON delta could not be accumulated |

### Sensitive Data Redaction

- **Never** log file paths from the semaphore key (use a hash or normalized form only if needed for debugging).
- **Never** log streaming response content — only chunk counts and accumulated tool call counts.
- Normalized path in semaphore logs should be relative to workspace root, not absolute.

### Correlation IDs

- All parallel execution logs must include `{WorkflowInstanceId}`, `{TurnNumber}`, and `{ToolCallId}` (where applicable) for correlation with the sequential tool loop logs (Story 12.2).

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-agentic-tool-loop.md` Phase 3 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |

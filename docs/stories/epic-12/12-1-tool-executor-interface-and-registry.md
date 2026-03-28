# Story 12.1: Tool Executor Interface & Registry

Status: ready-for-dev

## Story

As a **platform engineer**,
I want a typed `IToolExecutor` interface and `IToolExecutorRegistry` with built-in tool implementations for file I/O, code search, shell execution, git operations, and test running,
so that the agentic tool loop (Story 12.2) has a clean abstraction for discovering and executing tools within a single LLM activity.

## Acceptance Criteria

1. `IToolExecutor` interface exists with `ToolName` property and `ExecuteAsync(toolCallId, argumentsJson, cancellationToken)` method returning `ToolExecutionResult`
2. `ToolExecutionResult` record exists with `ToolCallId`, `ToolName`, `Success`, `Output`, `DurationMs` fields
3. `IToolExecutorRegistry` interface exists with `GetExecutor(toolName)` and `IsAllowed(toolName, allowlist)` methods
4. `ToolExecutorRegistry` implementation is populated via DI (`IEnumerable<IToolExecutor>`) and supports allowlist filtering
5. `FileReadTool` implementation reads file contents with path validation (no directory traversal beyond workspace root)
6. `FileWriteTool` implementation writes file contents with path validation and workspace root enforcement
7. `SearchCodeTool` implementation searches code via regex or literal patterns within workspace
8. `ShellExecuteTool` implementation executes shell commands with `ActionGate` validation (from Story 11.3) and configurable timeout
9. `GitOperationsTool` implementation supports `status`, `diff`, `add`, `commit`, `push` subcommands
10. `RunTestsTool` implementation runs test commands and captures stdout/stderr with configurable timeout
11. Tool output is truncated to 50KB maximum per execution
12. 25+ unit tests covering all tool implementations, registry lookup, allowlist filtering, path validation, and output truncation

## Technical Context

### Design Rationale

The agentic tool loop (Story 12.2) needs a uniform interface for executing tools that LLMs request. Each tool is a self-contained unit with a name matching what the LLM sees, and an execution method that takes JSON arguments and returns a structured result.

### Models (to add to LlmCallModels.cs)

```csharp
public record ToolExecutionResult(
    string ToolCallId,
    string ToolName,
    bool Success,
    string Output,
    long DurationMs
);

public record ToolLoopConfig(
    int MaxSteps = 20,
    string[]? AllowedTools = null,
    int ContextWindowTokens = 200000,
    double CompactionThreshold = 0.8
);

public record ConversationMessage(
    string Role,           // "system", "user", "assistant", "tool"
    string? Content,
    ToolCallInfo[]? ToolCalls,
    string? ToolCallId     // for role="tool" messages
);

public record ToolCallInfo(
    string Id,
    string Name,
    string ArgumentsJson
);
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolExecutor.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolExecutorRegistry.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolExecutorRegistry.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileReadTool.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileWriteTool.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/SearchCodeTool.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/ShellExecuteTool.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/GitOperationsTool.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/RunTestsTool.cs`

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` — add `ToolExecutionResult`, `ToolLoopConfig`, `ConversationMessage`, `ToolCallInfo` records; add `StopReason` to `NormalizedLlmResponse`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` — register all tool executors and registry in DI

### Security Boundaries

- **Path validation**: All file tools resolve paths against a configured workspace root. Any path that resolves outside the workspace (via `../` or symlinks) is rejected.
- **Shell validation**: `ShellExecuteTool` checks commands against `ActionGate` (from Story 11.3) before execution. If `ActionGate` is not yet available, embed a minimal blocked-command list.
- **Output truncation**: All tool outputs are truncated to 50KB with a `[truncated: {totalBytes} bytes total]` suffix.
- **Timeout**: Shell and test tools have a configurable timeout (default 60 seconds) enforced via `CancellationTokenSource`.

## Implementation Notes

1. Each tool class is registered as `IToolExecutor` in DI: `services.AddTransient<IToolExecutor, FileReadTool>()`. The registry collects all via `IEnumerable<IToolExecutor>`.
2. `ToolExecutorRegistry.GetExecutor(name)` returns `null` for unknown tools (caller decides how to handle — in the loop, unknown tools return an error message to the LLM).
3. `IsAllowed(name, allowlist)` returns `true` if allowlist is null/empty (all tools allowed) or if the name is in the allowlist. This allows workflow-level tool restriction.
4. `ShellExecuteTool` uses `System.Diagnostics.Process` with `RedirectStandardOutput` and `RedirectStandardError`. Capture both streams, concatenate with a separator.
5. `GitOperationsTool` wraps `git` CLI commands (not a Git library). Parse arguments JSON for `subcommand` and `args` fields.
6. `RunTestsTool` runs a configurable test command (e.g., `dotnet test`, `pnpm test`) and captures output. The test command template should be configurable per workspace.
7. Tool execution timing uses `Stopwatch` for accurate `DurationMs` measurement.

## Testing Strategy

- **Interface tests** (3): Verify `IToolExecutor` contract, `ToolExecutionResult` serialization, `ConversationMessage` serialization
- **Registry tests** (5): Lookup by name, unknown name returns null, allowlist filtering, null allowlist allows all, case sensitivity
- **FileReadTool tests** (4): Read existing file, path traversal rejected, file not found returns error, output truncated at 50KB
- **FileWriteTool tests** (3): Write new file, overwrite existing file, path traversal rejected
- **SearchCodeTool tests** (3): Regex search finds matches, no matches returns empty, workspace root enforced
- **ShellExecuteTool tests** (4): Successful command, blocked command rejected, timeout enforced, stderr captured
- **GitOperationsTool tests** (2): Status returns output, unknown subcommand returns error
- **RunTestsTool tests** (2): Test command runs and captures output, timeout enforced
- **Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ToolExecutorRegistryTests.cs`
- **Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/Tools/FileReadToolTests.cs` (and one per tool)

## Dependencies

- **None** (foundational story for Epic 12)
- **Optional**: Story 11.3 (Tool Call Validation) — `ShellExecuteTool` can use `ActionGate` if available, otherwise embeds a minimal blocked-command list

## Estimated Effort

3 days

## Logging Requirements

### Existing Coverage

The story has **no logging requirements** specified. This is a significant gap — tool execution is one of the most critical paths to observe for debugging workflows and diagnosing LLM behavior.

### Required Additions

Every `IToolExecutor` implementation and the `ToolExecutorRegistry` **must** inject `ILogger<T>` via constructor.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Tool execution started | INFO | `{ToolName}`, `{ToolCallId}`, `{ArgumentsSizeBytes}`, `{WorkflowInstanceId}` | Entry point for every tool execution. Do NOT log argument contents. |
| Tool execution completed | INFO | `{ToolName}`, `{ToolCallId}`, `{Success}`, `{DurationMs}`, `{OutputSizeBytes}`, `{WorkflowInstanceId}` | Exit point with performance data |
| Tool execution failed (exception) | ERROR | `{ToolName}`, `{ToolCallId}`, `{ExceptionType}`, `{ExceptionMessage}`, `{DurationMs}`, `{WorkflowInstanceId}` | Caught exception during tool execution |
| Tool output truncated | WARN | `{ToolName}`, `{ToolCallId}`, `{OriginalSizeBytes}`, `{TruncatedSizeBytes}`, `{WorkflowInstanceId}` | When 50KB limit is applied |
| Tool executor not found in registry | WARN | `{ToolName}`, `{RegisteredToolCount}` | Unknown tool name requested |
| Tool rejected by allowlist | WARN | `{ToolName}`, `{ToolCallId}`, `{WorkflowInstanceId}` | Tool exists but not in workflow's allowed list |
| Shell command blocked by ActionGate | WARN | `{ToolName}`, `{ToolCallId}`, `{BlockedPatternName}`, `{WorkflowInstanceId}` | Never log the command itself |
| Shell command execution started | DEBUG | `{ToolName}`, `{ToolCallId}`, `{TimeoutMs}`, `{WorkflowInstanceId}` | Never log the command itself |
| Shell command timed out | WARN | `{ToolName}`, `{ToolCallId}`, `{TimeoutMs}`, `{WorkflowInstanceId}` | Process exceeded timeout |
| File path validation failed (traversal) | WARN | `{ToolName}`, `{ToolCallId}`, `{WorkflowInstanceId}` | Never log the rejected path (may contain sensitive info) |
| Git operation executed | DEBUG | `{ToolName}`, `{ToolCallId}`, `{Subcommand}`, `{DurationMs}` | Log the subcommand (status, diff, etc.) but not full args |
| Registry initialized | INFO | `{RegisteredToolCount}`, `{ToolNames}` | Emitted once at startup during DI |

### Sensitive Data Redaction

- **Never** log tool call arguments content — may contain file paths, shell commands, or code.
- **Never** log tool output content — may contain file contents, credentials, or secrets.
- Log only sizes (bytes), durations, tool names (from a known vocabulary), and success/failure status.
- Shell commands must not be logged — only the tool name and blocked pattern name (if blocked).

### Correlation IDs

- All tool execution logs must include `{WorkflowInstanceId}` and `{ToolCallId}` for tracing across the tool loop (Story 12.2).
- `ToolCallId` is the LLM-assigned identifier that links the tool execution back to the specific LLM request.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-agentic-tool-loop.md` Phases 1.1-1.4 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |

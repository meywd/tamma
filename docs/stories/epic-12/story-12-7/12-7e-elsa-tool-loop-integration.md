# Story 12-7e: Elsa Tool Loop Integration

Status: ready-for-dev

## Story

As a **platform engineer**,
I want the new context tools wired into the existing Elsa agentic tool loop end-to-end,
so that when LlmCallWorkflow executes, the LLM has access to context tools based on its role, results are budget-managed, and everything works with parallel tool calls.

## Summary

This is the integration story that connects all pieces from 12-7a through 12-7d into a working system. Wire the context tool executors into the Elsa DI container and tool loop, update `LlmCallWorkflow` to pass available tools based on role config, ensure tool results are injected into the conversation context correctly, and verify parallel tool call support for context tools.

## Acceptance Criteria

### AC1: Context Tools Registered in DI
- [ ] All 5 context tool executors registered as `IToolExecutor` in the Elsa DI container
- [ ] `ToolExecutorRegistry` discovers them alongside existing tools (file_read, shell_execute, etc.)
- [ ] `IToolExecutorRegistry.GetAll()` returns context tools in addition to existing tools
- [ ] Context tools appear in the tool definitions sent to the LLM when allowed by role config

### AC2: LlmCallWorkflow Passes Available Tools
- [ ] When `EnableToolLoop = true`, `LlmCallWorkflow` resolves context tools for the current role
- [ ] Context tool allowlist is merged with the existing `ToolLoopConfig.AllowedTools`
- [ ] If `ToolLoopConfig.AllowedTools` is already set (explicit allowlist), context tools are added to it (not replacing it)
- [ ] If `ToolLoopConfig.AllowedTools` is null (all tools allowed), context tools are also allowed

### AC3: Tool Results in Conversation Context
- [ ] Context tool results are added to the conversation history as tool result messages (same format as existing tools)
- [ ] The LLM can reference context tool results in subsequent messages
- [ ] Context tool results are included in the context compaction when the conversation history grows too long
- [ ] Context tool results are tagged with `[CONTEXT]` prefix in the conversation to help the compactor prioritize them lower than action tool results

### AC4: Parallel Tool Call Support
- [ ] When the LLM returns multiple tool calls in a single turn (e.g., `search_code_semantic` + `search_conventions`), both execute in parallel
- [ ] Parallel context tool calls share the same budget manager instance (thread-safe)
- [ ] Budget enforcement works correctly under parallel execution (no double-spending)
- [ ] Mixed parallel calls (context tool + regular tool) execute correctly

### AC5: Account Context Propagation
- [ ] `accountId` is propagated from the workflow context to all context tool executors
- [ ] `issueId`, `workflowInstanceId`, and `repositoryId` are propagated for history and findings tools
- [ ] Context values are set at the start of the tool loop and available to all tool executions

### AC6: Configuration
- [ ] Context tools can be globally disabled via `ContextTools:Enabled = false` in appsettings
- [ ] Individual context tools can be disabled via `ContextTools:DisabledTools = ["search_findings"]`
- [ ] Context tool API base URL configurable via `ContextTools:ApiBaseUrl`
- [ ] Per-tool timeout configurable via `ContextTools:SearchTimeoutMs` (default: 3000ms)

### AC7: Logging and Diagnostics
- [ ] Context tool invocations logged with structured fields: `{ toolName, accountId, query, resultCount, tokenCount, durationMs }`
- [ ] Context budget utilization included in the `RecordDiagnosticsActivity` output
- [ ] `ToolLoopTokenTracker` records context tool token usage separately from regular tool usage
- [ ] Final diagnostics include: `contextToolCalls`, `contextToolTokens`, `contextBudgetUtilization`

### AC8: Error Resilience
- [ ] If the Node.js API is unreachable, context tools return an error message to the LLM (do not crash the tool loop)
- [ ] If one context tool fails, other tools in the same parallel batch still execute
- [ ] If the context budget manager encounters an error, it fails open (allows tool execution without budget tracking)
- [ ] Circuit breaker on the context tools API: 3 failures in 30s = open for 60s, tools return "Context tools temporarily unavailable"

## Technical Context

### Integration Points

```
LlmCallWorkflow
  |
  +-- ResolveAgentConfigActivity (Epic 9)
  |     -> agentConfig.providerChain, agentConfig.model, etc.
  |
  +-- ResolvePromptFromRegistryActivity (Epic 27)
  |     -> resolvedPrompt.contextTools (NEW)
  |
  +-- ResolveToolsActivity
  |     -> resolvedTools (existing + context tools based on role)
  |
  +-- CallLlmInlineActivity
        |
        +-- Tool Loop
              |
              +-- ToolExecutorRegistry.GetAllowed(allowedTools)
              |     -> Returns existing tools + allowed context tools
              |
              +-- Execute tool call
              |     -> If IsContextTool: record with ContextToolBudgetManager
              |     -> Append budget warning if needed
              |
              +-- (parallel) Execute multiple tool calls
              |
              +-- ContextCompactor.CompactIfNeeded()
              |     -> Compacts conversation including context tool results
              |
              +-- RecordDiagnosticsActivity
                    -> Includes context budget summary
```

### Key Files to Modify

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` | Register all 5 context tools + budget manager + prioritizer |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Wire budget manager into tool loop, handle context tool results |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` | Merge context tools into resolved tool list |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` | Include context budget diagnostics |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | Add context diagnostics to `LlmCallWorkflowOutput` |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Pass resolved context tools to tool loop |

### What Already Works

- `IToolExecutor` pattern and `ToolExecutorRegistry` DI discovery
- `ToolLoopConfig.AllowedTools` filtering via `IToolExecutorRegistry.GetAllowed()`
- Parallel tool execution via `ParallelToolExecutor` (Story 12-4)
- Context compaction via `ContextCompactor` (Story 12-3)
- Token tracking via `ToolLoopTokenTracker`
- SSE streaming for tool loop progress

### What This Story Adds

- Wiring the 5 context tools from 12-7a and 12-7b into the existing infrastructure
- Budget manager integration from 12-7c
- Role-based tool filtering from 12-7d
- Account context propagation
- Diagnostics and error resilience

## Dependencies

- **Story 12-7a**: Vector DB search tools (must be implemented)
- **Story 12-7b**: Convention & history tools (must be implemented)
- **Story 12-7c**: Context budget manager (must be implemented)
- **Story 12-7d**: Tool access configuration per role (must be implemented)
- **Story 12-2**: Agentic tool loop (already implemented)
- **Story 12-4**: Parallel tool execution (already implemented)

## Estimated Effort

| Task | Hours |
|------|-------|
| DI registration and configuration | 2 |
| CallLlmInlineActivity integration (budget, context tagging) | 6 |
| ResolveToolsActivity changes (context tool merging) | 2 |
| Account context propagation | 2 |
| Diagnostics and RecordDiagnosticsActivity changes | 2 |
| Error resilience and circuit breaker | 2 |
| Unit tests (10+ tests) | 2 |
| Integration tests (4 tests) | 2 |
| **Total** | **20 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |

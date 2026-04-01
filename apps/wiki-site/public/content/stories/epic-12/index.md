---
title: "Epic 12: Agentic Tool Loop"
---

## Overview

**Goal**: Add in-process tool execution loop to `CallLlmInlineActivity`, transforming single-turn LLM calls into multi-turn agentic sessions. Backward compatible via `EnableToolLoop` flag.

**Value Delivered**:
- LLM can call tools iteratively (file read/write, search, shell, git, tests) without returning to the workflow
- Multi-turn conversation history maintained within a single activity execution
- Token counting with automatic context compaction at 80% window utilization
- SSE streaming for real-time progress visibility
- Parallel tool execution when LLM returns multiple tool calls in one turn

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 12.1 | Tool Executor Interface & Registry | P0 | None | Planned |
| 12.2 | Agentic Tool Loop in CallLlm | P0 | Story 12.1 | Planned |
| 12.3 | Context Compaction | P1 | Story 12.2 | Planned |
| 12.4 | Streaming & Parallel Tools | P2 | Story 12.2 | Planned |
| 12.5 | Prompt Engineering Framework | P0 | Stories 12.1-12.4, 9-6 | Planned |

## Architecture

The tool loop lives inside `CallLlmInlineActivity`. When `EnableToolLoop=false` (default), behavior is identical to today. When enabled, the activity enters a while-loop: call LLM, execute tool calls, feed results back, repeat until the LLM produces a text-only response or `maxSteps` is reached.

```
if (!EnableToolLoop): single call (exact current behavior)
else:
  messages = [system, user]
  for step in 0..maxSteps:
    response = CallLlm(messages)
    if no tool_calls or stop_reason == "end_turn": break
    validate tool calls against allowlist
    execute tools -> results
    append assistant msg + tool result msgs to history
    accumulate tokens
  return final response
```

## Source Plan

`.dev/plans/llm-agentic-tool-loop.md`

---

**Last Updated**: 2026-03-31
**Epic Owner**: Architecture Team

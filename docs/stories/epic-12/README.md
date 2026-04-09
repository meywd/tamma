# Epic 12: Agentic Tool Loop

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
| 12.5 | Prompt Engineering Framework | P0 | Stories 12.1-12.4, 9-6 | Partially Complete |
| 12.6 | Tool Executor Enhancements | P1 | Stories 12.1-12.4 | Planned |
| 12.7 | LLM Context Tool Access | P0 | Stories 12.1-12.2, Epic 6 | Planned |

### Story 12-7 Sub-Stories (LLM Context Tool Access)

| Sub-Story | Title | Priority | Effort | Dependencies |
|-----------|-------|----------|--------|-------------|
| 12-7a | Vector DB Search Tools | P0 | 24h | Epic 6 (6-2, 6-3) |
| 12-7b | Convention & History Tools | P0 | 16h | Epic 27, Event Store |
| 12-7c | Context Budget Manager | P1 | 20h | 12-7a, 12-7b, Epic 9 |
| 12-7d | Tool Access Configuration Per Role | P1 | 12h | 12-7a, 12-7b, Epic 27 |
| 12-7e | Elsa Tool Loop Integration | P0 | 20h | 12-7a through 12-7d |

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

## Story 12-7: LLM Context Tool Access

Instead of dumping static context blobs into LLM prompts, the LLM gets **tool access** to query context on demand:

- `search_code_semantic` -- semantic code search via vector DB
- `search_findings` -- prior scan findings (security, quality, performance)
- `search_stories` -- stories, specs, architecture docs
- `search_conventions` -- project coding conventions
- `search_history` -- previous LLM call results for this issue/workflow

The LLM decides what context it needs and when. A context budget manager tracks token usage across tool results and enforces per-provider limits. Per-role configuration controls which tools each agent role can access.

See `docs/stories/epic-12/story-12-7/` for full stories and implementation plans.

## Source Plan

`.dev/plans/llm-agentic-tool-loop.md`

---

**Last Updated**: 2026-04-08
**Epic Owner**: Architecture Team

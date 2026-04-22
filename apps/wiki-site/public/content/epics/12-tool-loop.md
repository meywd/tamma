---
title: "Epic 12: Agentic Tool Loop"
sidebar:
  order: 12
---

**Status:** Mostly Complete (12-1..12-4 done; 12-5 partially complete; 12-6 + 12-7 planned)
**Stories:** 7 (12-1 through 12-7, plus 5 sub-stories under 12-7)

## Overview

Epic 12 implements the agentic tool loop in the Elsa workflow engine — the mechanism by which the LLM can call tools iteratively to accomplish tasks. Originally scoped as 4 stories (12-1..12-4 — all shipped), it expanded with 12-5 (prompt engineering framework), 12-6 (tool executor enhancements), and 12-7 (LLM context tool access — 5 sub-stories) to support context-on-demand for the autonomous loop.

## Goals

1. Define tool executor interface and build a tool registry **(done — 12-1)**
2. Implement the agentic tool loop in the `CallLlmInlineActivity` **(done — 12-2)**
3. Add context compaction for long-running conversations **(done — 12-3)**
4. Enable streaming responses and parallel tool execution **(done — 12-4)**
5. Build the prompt engineering framework on top of Epic 27 **(in progress — 12-5)**
6. Add tool executor enhancements (registration, schema validation extensions) **(planned — 12-6)**
7. Give the LLM tool access to query context on demand **(planned — 12-7 + sub-stories)**

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 12-1 | Tool Executor Interface & Registry | P0 | M | Done |
| 12-2 | Agentic Tool Loop in `CallLlmInlineActivity` | P0 | L | Done |
| 12-3 | Context Compaction (token budget + summarization at 80%) | P1 | M | Done |
| 12-4 | Streaming & Parallel Tool Execution | P2 | M | Done |
| 12-5 | Prompt Engineering Framework | P0 | L | Partially Complete |
| 12-6 | Tool Executor Enhancements | P1 | M | Planned |
| 12-7 | LLM Context Tool Access | P0 | XL | Planned |

### Story 12-7 sub-stories

| Sub-Story | Title | Priority | Effort | Dependencies |
|-----------|-------|----------|--------|--------------|
| 12-7a | Vector DB Search Tools | P0 | 24h | Epic 6 (6-2, 6-3) |
| 12-7b | Convention & History Tools | P0 | 16h | Epic 27, Event Store |
| 12-7c | Context Budget Manager | P1 | 20h | 12-7a, 12-7b, Epic 9 |
| 12-7d | Tool Access Configuration Per Role | P1 | 12h | 12-7a, 12-7b, Epic 27 |
| 12-7e | Elsa Tool Loop Integration | P0 | 20h | 12-7a..12-7d |

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
    execute tools → results
    append assistant msg + tool result msgs to history
    accumulate tokens
  return final response
```

## Story 12-7: LLM Context Tool Access

Instead of dumping static context blobs into LLM prompts, the LLM gets **tool access** to query context on demand:

- `search_code_semantic` — semantic code search via vector DB
- `search_findings` — prior scan findings (security, quality, performance)
- `search_stories` — stories, specs, architecture docs
- `search_conventions` — project coding conventions
- `search_history` — previous LLM call results for this issue/workflow

The LLM decides what context it needs and when. A context budget manager tracks token usage across tool results and enforces per-provider limits. Per-role configuration controls which tools each agent role can access.

## Built-in tools (12-1, 12-6)

| Tool | Purpose |
|------|---------|
| FileRead | Read file contents |
| FileWrite | Write file contents (with path validation) |
| SearchCode | Search codebase (text-based) |
| ShellExecute | Execute shell commands (with command validation) |
| RunTests | Run test suites |
| GitOperations | Git status, diff, commit, branch |

## Key technical details

- **Tool Registry**: Central registry where tools are registered with schemas; Elsa activities look up tools by name
- **Agentic Loop**: `CallLlmInlineActivity` sends messages to LLM, receives tool call requests, executes tools, returns results, repeats until LLM produces a final response
- **Context Compaction**: When conversation history exceeds context window limits, older messages are summarized to free space while preserving essential context
- **Streaming**: LLM responses streamed token-by-token; tool calls executed in parallel when independent

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Security Hardening | Epic 11 | Tool calls validated by security layer |
| Elsa Workflows | Epic 7 | Tool loop runs inside Elsa activities |
| AI Providers | Epic 1 | LLM providers power the agentic loop |
| Vector DB / RAG | Epic 6 | 12-7a needs 6-2 / 6-3 |
| Prompt Store | Epic 27 | 12-5 + 12-7b + 12-7d depend on 27-2 / 27-3 |
| Event Store | Epic 4, 10 | 12-7b queries the event log for prior LLM call results |

## Related Epics

This epic is part of the Elsa workflow engine group (Epics 11-14). See also:

- [Epic 11: Security Hardening](Epic-11-Security.md)
- [Epic 13: Workflow Decomposition](Epic-13-Workflow-Decomposition.md)
- [Epic 14: Custom Elsa Studio](Epic-14-ELSA-Studio.md)
- [Combined page: Epics 11-14](Epic-11-14-ELSA.md)

## Story files

[Epic 12 stories on GitHub](/stories/epic-12/)

---

_Last updated: 2026-04-21_

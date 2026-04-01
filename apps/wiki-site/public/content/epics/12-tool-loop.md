---
title: "Epic 12: Agentic Tool Loop"
sidebar:
  order: 12
---

**Status:** Done
**Stories:** 4 (12-1 through 12-4)

## Overview

Epic 12 implements the agentic tool loop in the ELSA workflow engine -- the mechanism by which the LLM can call tools iteratively to accomplish tasks. This includes a tool executor interface and registry, the core loop in the `CallLlm` activity, context compaction for long conversations, and streaming with parallel tool execution.

## Goals

1. Define tool executor interface and build a tool registry
2. Implement the agentic tool loop in the CallLlm ELSA activity
3. Add context compaction for long-running conversations
4. Enable streaming responses and parallel tool execution

## Stories

| Story | Title | Status |
|-------|-------|--------|
| 12-1 | Tool Executor Interface & Registry | Done |
| 12-2 | Agentic Tool Loop in CallLlm | Done |
| 12-3 | Context Compaction | Done |
| 12-4 | Streaming & Parallel Tools | Done |

## Key Technical Details

- **Tool Registry**: Central registry where tools are registered with schemas; ELSA activities look up tools by name
- **Agentic Loop**: `CallLlm` activity sends messages to LLM, receives tool call requests, executes tools, returns results, repeats until LLM produces a final response
- **Context Compaction**: When conversation history exceeds context window limits, older messages are summarized to free space while preserving essential context
- **Streaming**: LLM responses streamed token-by-token; tool calls executed in parallel when independent

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Security Hardening | Epic 11 | Tool calls validated by security layer |
| ELSA Workflows | Epic 7 | Tool loop runs inside ELSA activities |
| AI Providers | Epic 1 | LLM providers power the agentic loop |

## Related Epics

This epic is part of the ELSA workflow engine group (Epics 11-14). See also:
- [Epic 11: Security Hardening](/epics/11-security/)
- [Epic 13: Workflow Decomposition](/epics/13-workflow-decomposition/)
- [Epic 14: Custom ELSA Studio](/epics/14-elsa-studio/)
- [Combined page: Epics 11-14](/epics/11-14-elsa/)

## Story Files

[Story documents on GitHub](/stories/epic-12/)

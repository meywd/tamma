---
title: "Epic 12: Agentic Tool Loop + Context Tools"
sidebar:
  order: 12
---

**Status:** Mostly complete. 12-1..12-4 done; 12-5 partially complete (prompt framework landed, sub-tasks 12-5a..12-5e mixed); 12-6 planned; 12-7 (5 sub-stories) drafted.
**Stories:** 7 primary (12-1..12-7) + 5 sub-stories under 12-7.
**Primary code:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`, `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/`.

## Overview

Epic 12 turns ELSA's LLM activity from a single-turn request/response call into an in-process agentic tool loop. Before, `CallLlmActivity` sent a prompt, got a response, returned. After Story 12-2, `CallLlmInlineActivity` enters a while loop: call LLM → execute any tool calls it returned → feed results back → repeat → exit when the LLM returns a text-only response or `maxSteps` is reached. The entire multi-turn session completes inside one activity invocation, so the workflow graph stays simple (one box) while the LLM does real tool-using work.

The loop is backward compatible: `EnableToolLoop=false` (default) is a bit-for-bit single-turn call. Turn it on per-activity or per-workflow and the LLM gets a registered tool catalog (`FileRead`, `FileWrite`, `SearchCode`, `ShellExecute`, `RunTests`, `GitOperations`) + safety plumbing from Epic 11 (`ToolCallValidator`, `ActionGate`, `PathValidator`). Story 12-3 adds context compaction — token counter + summarization at 80% of the context window — so long sessions don't blow past model limits. Story 12-4 adds SSE streaming of assistant text + tool calls and parallel execution when the LLM returns independent tool calls in one turn.

Story 12-5 is the prompt engineering framework — role + action prompt keys, 80 defaults shipped, few-shot injection, A/B testing hooks, context truncation, and three follow-up fixes (skill-level adaptation for mentorship, CI retry counter fix). Story 12-6 rounds out tool executor ergonomics (richer registration, richer schema validation). Story 12-7 is the big one: LLM context tool access — instead of dumping static context into prompts, the LLM gets *tools* to query context on demand (vector DB semantic search, prior scan findings, stories/specs, project conventions, previous LLM call history). A context budget manager tracks how much of the window context tools have consumed, and per-role configuration gates which agents can call which tools.

## Architecture

```
+---------------------------------------------------------------+
|                CallLlmInlineActivity (12-2)                    |
|                                                                |
|   input: systemPrompt, userPrompt, ToolLoopConfig              |
|           (maxSteps, allowedTools, contextWindow, thresh)      |
|                                                                |
|   messages = [system, user]                                    |
|   for step in 0..maxSteps:                                     |
|      response = CallLlm(messages, streaming=cfg.stream)        |
|      if no tool_calls or stop_reason=="end_turn": break        |
|      for tc in response.tool_calls (parallel when safe, 12-4): |
|          ToolCallValidator.Validate(tc.name, tc.argsJson)      |
|          result = registry.GetExecutor(tc.name).ExecuteAsync() |
|          append tool-result message                            |
|      append assistant message                                  |
|      if tokens >= 80% of window: ContextCompactor.Compact(...) |
|   return final response + usage                                |
+---------------------------------------------------------------+
                       |                                |
                       v                                v
              ToolExecutorRegistry (12-1)      ContextCompactor (12-3)
                       |                                |
     +-----------------+-----------------+              |
     v                 v                 v              v
Built-in tools      LLM Context      Custom tools     Token estimate +
(file/search/       Tools (12-7):    registered by    structured summary
 shell/git/test)    search_code_     workflows
                    semantic, etc.

+---------------------------------------------------------------+
|              Prompt Engineering Framework (12-5)              |
|                                                                |
|  (role, action) pair -> prompt template                        |
|   - 80 built-in defaults (9 roles × 10 actions)                |
|   - {{variables}} + few-shot examples                          |
|   - A/B testing hooks                                          |
|   - context truncation with priority                           |
|   - role-based skill-level adaptation                          |
+---------------------------------------------------------------+
```

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `IToolExecutor` + `ToolExecutionResult` | Uniform tool execution contract | `Tamma.Activities/LlmCall/Tools/IToolExecutor.cs` | 12-1 / Done |
| `IToolExecutorRegistry` + impl | DI-populated registry with allowlist filtering | `IToolExecutorRegistry.cs`, `ToolExecutorRegistry.cs` | 12-1 / Done |
| `FileReadTool` / `FileWriteTool` | Workspace-rooted file IO with path validation | `FileReadTool.cs`, `FileWriteTool.cs`, `PathValidator.cs` | 12-1 / Done |
| `SearchCodeTool` | Regex / literal code search inside workspace | `SearchCodeTool.cs` | 12-1 / Done |
| `ShellExecuteTool` | Shell with `ActionGate` validation + timeout | `ShellExecuteTool.cs`, `CommandValidator.cs` | 12-1 / Done |
| `GitOperationsTool` | `status`/`diff`/`add`/`commit`/`push` | `GitOperationsTool.cs` | 12-1 / Done |
| `RunTestsTool` | Test runner with stdout/stderr capture + timeout | `RunTestsTool.cs` | 12-1 / Done |
| Tool loop inside activity | Multi-turn loop in `CallLlmInlineActivity` | `CallLlmInlineActivity.cs` | 12-2 / Done |
| `ContextCompactor` + `TokenEstimator` | 80% threshold summarization, preserves system + latest | `ContextCompactor.cs`, `TokenEstimator.cs` | 12-3 / Done |
| Streaming + parallel tools | SSE events + `Task.WhenAll` for independent tool calls | `CallLlmInlineActivity.cs` (12-4 commit) | 12-4 / Done |
| Prompt registry API | 80 role+action defaults, overrides, variables, few-shot | `apps/tamma-elsa/src/Tamma.Api/Controllers/Prompts*`, DB migrations | 12-5 / Partial |
| Tool executor enhancements | Richer registration + schema extensions | (planned, design in 12-6 doc) | 12-6 / Planned |
| LLM Context Tools | `search_code_semantic`, `search_findings`, `search_stories`, `search_conventions`, `search_history` | (planned; 12-7a..e sub-stories) | 12-7 / Planned |
| Context Budget Manager | Tracks token spend across tool results; enforces per-provider caps | (planned, 12-7c) | 12-7c / Planned |
| Tool access config per role | Per-role allowlist of context tools | (planned, 12-7d) | 12-7d / Planned |

## Class / type structure

```
apps/tamma-elsa/src/Tamma.Activities/LlmCall/
  CallLlmInlineActivity : TammaAsyncActivity
    Inputs:
      string SystemPrompt
      string UserPrompt
      ToolLoopConfig Config          // MaxSteps, AllowedTools, ContextWindowTokens, CompactionThreshold
      bool EnableToolLoop            // default false
    Outputs:
      CallLlmResult Result
    Run:
      if (!EnableToolLoop) => single CallLlm
      else => loop (see architecture)

  Models/LlmCallModels.cs
    record ToolExecutionResult(ToolCallId, ToolName, Success, Output, DurationMs)
    record ToolLoopConfig(MaxSteps=20, AllowedTools=null, ContextWindowTokens=200000, CompactionThreshold=0.8)
    record ConversationMessage(Role, Content?, ToolCalls?, ToolCallId?)
    record ToolCallInfo(Id, Name, ArgumentsJson)
    record CallLlmResult(FinalText, Steps, Usage, ToolExecutions[])

Tamma.Activities.LlmCall.Tools
  interface IToolExecutor
    string ToolName { get; }
    Task<ToolExecutionResult> ExecuteAsync(string toolCallId, string argumentsJson, CancellationToken ct)
  interface IToolExecutorRegistry
    IToolExecutor? GetExecutor(string toolName)
    bool IsAllowed(string toolName, IReadOnlyCollection<string>? allowlist)
  class ToolExecutorRegistry : IToolExecutorRegistry
    ctor(IEnumerable<IToolExecutor> executors)

  // Built-in tools
  class FileReadTool       : IToolExecutor
  class FileWriteTool      : IToolExecutor
  class SearchCodeTool     : IToolExecutor
  class ShellExecuteTool   : IToolExecutor
  class GitOperationsTool  : IToolExecutor
  class RunTestsTool       : IToolExecutor

  static class PathValidator                       // workspace-root enforcement
  static class CommandValidator                    // shell-arg validation, ActionGate bridge
  static class TokenEstimator                      // heuristic token count per message
  class ContextCompactor                           // summarize + retain system + latest N
  static class ToolOutputHelper                    // 50KB truncation with notice

// LLM Context Tools (12-7, planned)
class SemanticCodeSearchTool : IToolExecutor       // 12-7a
class SearchFindingsTool     : IToolExecutor       // 12-7a/b
class SearchStoriesTool      : IToolExecutor       // 12-7b
class SearchConventionsTool  : IToolExecutor       // 12-7b
class SearchHistoryTool      : IToolExecutor       // 12-7b
class ContextBudgetManager                         // 12-7c
```

## Sequence — tool loop with compaction

```
Workflow    CallLlmInlineActivity    LLM provider    Registry    FileReadTool    ContextCompactor    EventStore
  |                |                       |             |             |                  |               |
  | dispatch ----->|                       |             |             |                  |               |
  |                | build messages[sys,user]                                               |               |
  |                | step 0: CallLlm ----->|             |             |                  |               |
  |                | <-- tool_calls=[read('/src/foo.ts'), read('/src/bar.ts')]              |               |
  |                | Validator.Validate('file_read', args) x2                                              |
  |                | parallel exec (12-4):                                                                  |
  |                |   +--> registry.Get('file_read') ----> FileReadTool.ExecuteAsync                       |
  |                |   +--> registry.Get('file_read') ----> FileReadTool.ExecuteAsync                       |
  |                | <-- results[] (truncate 50KB each)                                                     |
  |                | append assistant + 2 tool-result msgs                                                  |
  |                | record Tool Executed events x2 -----------------------------------------------------> |
  |                | step 1: CallLlm ----->|             |             |                  |               |
  |                | <-- tool_call=[run_tests()]                                                            |
  |                | exec RunTests (timeout 600s)                                                           |
  |                | <-- failure: 3 tests red                                                               |
  |                | step 2: CallLlm ----->|             |             |                  |               |
  |                | <-- tool_call=[file_write(fix)]                                                        |
  |                | tokens so far ~85% of 200k budget                                                      |
  |                | ContextCompactor.Compact(messages) -------->|                                         |
  |                | <-- compressed [sys, user, summary-of-history, recent k turns]                         |
  |                | continue...                                                                             |
  |                | step N: CallLlm ----->|             |             |                  |               |
  |                | <-- text only "done; tests passing now"                                                |
  |                | return CallLlmResult(FinalText, Steps=N, Usage, ToolExecutions=[...])                  |
  | <-- complete --|                                                                                        |
```

## Use cases

- **LLM-driven fix** — reviewer flags a failing test. Workflow dispatches `CallLlmInlineActivity` with `EnableToolLoop=true`. LLM reads the source + failing test, edits the source with `file_write`, reruns tests with `run_tests`, verifies, responds with summary — all inside one activity box in the workflow graph.
- **Long debugging session with compaction** — debugging mentorship session runs 30+ tool-call turns accumulating file reads and test outputs. At 80% of the context window, `ContextCompactor` summarizes older turns into a single assistant message ("earlier you read src/a.ts (exports X), src/b.ts (imports Y), ran tests (2 red)"), keeps the last 3 turns verbatim, and the loop continues without OOM-ing the model.
- **Parallel independent reads** — LLM returns `[read(a.ts), read(b.ts), read(c.ts)]` in one response. Story 12-4 runs all three concurrently instead of serializing, cutting wall-clock ~3x on large context gathering.
- **Streaming assistant text to ELSA Studio** — Studio subscribes to the workflow's SSE feed; partial assistant tokens and in-flight tool calls appear live for operator observability.
- **Context-on-demand (12-7)** — instead of a 20 KB static context blob, the LLM receives `search_code_semantic`, `search_findings`, `search_stories`, etc., and calls them as it needs. Budget manager caps total token spend across tool results. Per-role config: `implementer` can use `search_code_semantic`; `reviewer` gets `search_findings` + `search_stories`; `scrum-master` only gets `search_history`.
- **Prompt A/B testing (12-5)** — ship two prompt variants for `(implementer, CODE_GENERATION)`; route 50/50; compare success rate + cost/token from diagnostics. Switch to the winner without a deploy.

## Dependencies

**Upstream**
- Epic 11 — `ToolCallValidator`, `ActionGate`, `ContentSanitizer` are prerequisites for safe tool execution.
- Epic 7 — `LlmCallWorkflow` / mentorship workflows are the primary tool-loop consumers.
- Epic 1 — provider interfaces; the activity calls through the provider chain.

**Downstream**
- Epic 10 — engine brain is the same pattern applied at the top of the stack.
- Epic 13 — decomposed sub-workflows (TDD retry, CI retry) wrap `CallLlmInlineActivity` calls.
- Epic 6 — 12-7a depends on vector DB indexer (Stories 6-2, 6-3).
- Epic 27 — 12-5 + 12-7b + 12-7d depend on prompt store (27-2, 27-3).
- Epic 4 / 10 — 12-7b queries the event log for prior LLM call results.

## Current state

Landed:
- `d594e0f feat(agentic): add IToolExecutor framework + 6 built-in tools [12-1]`
- `7c722b0 feat(agentic): add tool execution loop to CallLlmInlineActivity [12-2]`
- `a78c746 feat(agentic): add context compaction for tool loop [12-3]`
- `876f29f feat(agentic): parallel tool execution + progress events [12-4]`
- `456c2ec fix(security): address 4 critical tool execution vulnerabilities [12-1]` — follow-up hardening.
- `3906a66 feat: Story 12-5 — Prompt Registry API with 80 default templates`
- `1a0bb05 docs: update Story 12-5 with role+action prompt key system`
- `0dfd038 fix: Story 12-5c — mentorship workflow skill-level adaptation`
- `5531efd fix: Story 12-5e — CI retry counter investigation + fix`
- `39db61c docs(stories): Epic 12 Layer-4 impl plans (12-5a, 12-5b, 12-5d)`
- `505a0d7 docs: Story 12-7 — LLM Context Tool Access (5 sub-stories, 92h)`

Outstanding:
- 12-5a (context truncation), 12-5b (few-shot injection), 12-5d (A/B testing hooks) — impl plans merged, implementation pending.
- 12-6 (tool executor enhancements) — design drafted, implementation pending.
- 12-7a (vector DB search tools) — blocked on Epic 6 indexer finalization.
- 12-7b (convention + history tools) — blocked on Epic 27 prompt-store completion.
- 12-7c (context budget manager), 12-7d (per-role tool access), 12-7e (ELSA integration) — sequenced behind 12-7a/b.

Stubs / deferrals:
- 50 KB tool output truncation is a hard cap; streaming larger results is a 12-6 item.
- `ContextCompactor` uses heuristic token counts; exact tokenizer per provider is a follow-up.
- Per-role tool access is enforced via allowlist in `ToolLoopConfig`; store-side enforcement (API deny at registry lookup) ships with 12-7d.

## See also

- [Epic 7: Mentorship](Epic-7-Mentorship.md) — primary consumer of the tool loop.
- [Epic 11: Security](Epic-11-Security.md) — validator + action gate backing the loop.
- [Epic 10: Engine Core](Epic-10-Engine-Core.md) — same pattern at the orchestrator layer.
- [Epic 6: Context & Knowledge](Epic-6-Context-Knowledge.md) — indexer for 12-7 semantic search.
- [Epic 27: Prompt Store](Epic-27-Prompt-Store.md) — prompt templates + overrides consumed by 12-5.
- [Epic 13: Workflow Decomposition](Epic-13-Workflow-Decomposition.md) — reuses the loop inside extracted sub-workflows.
- Source plan: `.dev/plans/llm-agentic-tool-loop.md`.
- Impl plans: [`docs/stories/epic-12/`](/stories/epic-12/).
- Source: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`, `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/`.

---

_Last refreshed 2026-04-22._

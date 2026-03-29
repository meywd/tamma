# Epics 11-14: ELSA Hardening & Enhancement

These four epics collectively harden, extend, and refine the ELSA workflow engine layer.

---

## Epic 11: Security Hardening

**Status:** Completed | **Stories:** 5 (11-1 through 11-5)

**Goal:** Port the TypeScript security pipeline to C# and wire it into the ELSA workflow layer to defend against LLM injection attacks.

### Deliverables

| Story | Title | Key Implementation |
|-------|-------|-------------------|
| 11-1 | ContentSanitizer C# Port | C# sanitizer with null byte removal, HTML stripping, zero-width char removal, NFKD normalization, 40+ injection pattern detection |
| 11-2 | LLM Input Sanitization | Sanitization wired into `ResolveLlmPromptActivity` -- all prompts sanitized before LLM call |
| 11-3 | Tool Call Validation | Tool name allowlist, argument schema validation, size cap enforcement in `ToolExecutorRegistry` |
| 11-4 | Output Sanitization & Prompt Hardening | LLM output sanitized before storage/display; system prompts hardened against extraction attacks |
| 11-5 | Fail-Closed Guards & Provider Allowlist | Circuit breaker and budget check errors deny (not allow); provider names validated against known list; error bodies redacted |

### Security Properties

- All LLM inputs sanitized (null bytes, HTML, zero-width chars, 40+ injection patterns)
- Tool call names validated against an allowlist; arguments schema-checked and size-capped
- LLM outputs sanitized before storage or display
- System prompts hardened against extraction attacks
- Fail-closed guards on circuit breaker and budget checks
- Provider names validated against a known allowlist
- Error bodies redacted to prevent internal URL and API key leakage
- 8 end-to-end attack simulation tests

---

## Epic 12: Agentic Tool Loop

**Status:** Completed | **Stories:** 4 (12-1 through 12-4)

**Goal:** Add in-process tool execution loop to `CallLlmInlineActivity`, transforming single-turn LLM calls into multi-turn agentic sessions.

### Deliverables

| Story | Title | Key Implementation |
|-------|-------|-------------------|
| 12-1 | Tool Executor Interface & Registry | `IToolExecutor` interface, `ToolExecutorRegistry` with built-in tools: FileRead, FileWrite, SearchCode, ShellExecute, RunTests, GitOperations |
| 12-2 | Agentic Tool Loop in CallLlm | Multi-turn loop in `CallLlmInlineActivity` -- LLM calls tools iteratively until task complete or limit reached |
| 12-3 | Context Compaction | `TokenEstimator` for token counting, `ContextCompactor` for automatic compaction at 80% context window utilization |
| 12-4 | Streaming & Parallel Tools | SSE streaming for real-time progress visibility; parallel tool execution when LLM returns multiple tool calls |

### Architecture

```
CallLlmInlineActivity (EnableToolLoop = true)
  |
  +-- Send prompt to LLM
  |     |
  |     +-- LLM returns tool_use blocks
  |           |
  |           +-- For each tool_use (parallel if multiple):
  |           |     ToolExecutorRegistry.Execute(toolName, args)
  |           |       |
  |           |       +-- FileReadTool / FileWriteTool / SearchCodeTool
  |           |       +-- ShellExecuteTool (with CommandValidator)
  |           |       +-- RunTestsTool / GitOperationsTool
  |           |
  |           +-- Append tool results to conversation history
  |           |
  |           +-- Check token count via TokenEstimator
  |           |     If > 80% capacity: run ContextCompactor
  |           |
  |           +-- Loop: send updated conversation back to LLM
  |
  +-- LLM returns final text response (no tool_use)
  |
  +-- Return result
```

Backward compatible via `EnableToolLoop` flag -- existing single-turn calls unaffected.

---

## Epic 13: Workflow Decomposition

**Status:** Completed | **Stories:** 3 (13-1 through 13-3)

**Goal:** Split the 783-line / 39-activity `SingleIssueCycleWorkflow` into smaller, composable sub-workflows.

### Deliverables

| Story | Title | Key Implementation |
|-------|-------|-------------------|
| 13-1 | TDD Debug Retry Sub-Workflow | `TddWithDebugRetryWorkflow` extracted from main workflow -- handles TDD cycle with automatic debug retry |
| 13-2 | CI Debug Retry Sub-Workflow | `CiWithDebugRetryWorkflow` extracted -- handles CI pipeline execution with automatic debug retry |
| 13-3 | Consolidate Finish Sequences | 7 duplicated finish sequences consolidated into 1 shared reusable sequence |

### Results

- Main workflow reduced from ~783 lines / 39 activities to ~500 lines / ~29 activities
- Sub-workflows independently testable and versionable
- Easier visual debugging in ELSA Studio (smaller graph per workflow)
- Both sub-workflows reusable across different parent workflows

---

## Epic 14: Custom ELSA Studio

**Status:** Completed | **Stories:** 3 (14-1 through 14-3)

**Goal:** Replace the upstream `elsa-studio-v3-5` Docker image with a custom Blazor WASM project.

### Deliverables

| Story | Title | Key Implementation |
|-------|-------|-------------------|
| 14-1 | Studio Blazor WASM Scaffold | Custom Blazor WASM project (`Tamma.Studio`) referencing ELSA Studio NuGet packages, Tamma branding, purple MudBlazor theme |
| 14-2 | Studio Docker & CI | Custom Dockerfile producing ~30MB nginx-served static site; CI/CD for automated builds and GHCR pushes |
| 14-3 | Studio Custom UI Hints | JSON editor UI hint for workflow JSON inputs; provider selector UI hint for multi-select provider configuration |

### Architecture

The custom studio is a Blazor WASM single-page application that loads the standard ELSA Studio UI components as NuGet packages, then adds:
- Tamma logo, favicon, and app title
- Purple-themed MudBlazor palette
- Custom UI hint handlers for domain-specific input types
- Served via nginx in a Docker container at `elsa.tamma.dev`

---

## Dependencies Between Epics 11-14

```
Epic 11 (Security)  -- Standalone, no deps on 12-14
Epic 12 (Tool Loop) -- Depends on Epic 11 (tool validation from 11-3)
Epic 13 (Decomposition) -- Depends on Epic 10 (workflow engine) and Epic 12 (tool loop in sub-workflows)
Epic 14 (Studio)    -- Standalone, no deps on 11-13
```

---

_For story details, see the respective epic directories:_
- _[Epic 11](https://github.com/meywd/tamma/tree/main/docs/stories/epic-11)_
- _[Epic 12](https://github.com/meywd/tamma/tree/main/docs/stories/epic-12)_
- _[Epic 13](https://github.com/meywd/tamma/tree/main/docs/stories/epic-13)_
- _[Epic 14](https://github.com/meywd/tamma/tree/main/docs/stories/epic-14)_

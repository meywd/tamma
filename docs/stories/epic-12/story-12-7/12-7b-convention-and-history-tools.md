# Story 12-7b: Convention & History Tools

Status: ready-for-dev

## Story

As an **LLM agent in the tool loop**,
I want `search_conventions` and `search_history` tools,
so that I can look up project-specific coding conventions and previous LLM call results relevant to my current task.

## Summary

Implement two additional context tools: `search_conventions` retrieves project-specific coding conventions from the account's `.tamma/config.json` conventions field and the convention template library. `search_history` queries previous LLM call outputs for the current issue or workflow from the event store or vector DB. Both tools are account-scoped.

## Acceptance Criteria

### AC1: search_conventions Tool
- [ ] Tool registered as `search_conventions` in the `IToolExecutorRegistry`
- [ ] Input: `query` (string, optional -- returns all conventions if omitted), `category` (string, optional: "code_style", "testing", "error_handling", "logging", "imports", "all")
- [ ] Reads conventions from three sources in priority order:
  1. Account-level convention overrides (stored in prompt store or account config)
  2. Repository-level `.tamma/config.json` `conventions` field
  3. Convention template library (matched by detected language/framework)
- [ ] Returns conventions formatted as a structured list with category labels
- [ ] When `query` is provided, filters conventions to those relevant to the query (keyword matching)
- [ ] Token-efficient: returns only relevant sections, not the entire convention document

### AC2: search_history Tool
- [ ] Tool registered as `search_history` in the `IToolExecutorRegistry`
- [ ] Input: `query` (string, required), `scope` (string, optional: "issue", "workflow", "repo", default "issue"), `max_results` (int, optional, default 5), `role_filter` (string, optional -- filter by the role that produced the result)
- [ ] Queries previous LLM call outputs:
  - `scope=issue`: results from other LLM calls for the same issue (e.g., a planner's output available to the implementer)
  - `scope=workflow`: results from the current workflow instance only
  - `scope=repo`: results from any workflow on this repository
- [ ] Returns: operation name, role, timestamp, relevant excerpt, and token count
- [ ] Useful for: implementation referencing the planner's design, reviewer seeing what the implementer intended, debugger checking previous fix attempts

### AC3: Convention Sources
- [ ] Account convention overrides stored in the prompt store (`conventions` column or separate table)
- [ ] Repository-level conventions read from `.tamma/config.json` (already exists in repo workspace)
- [ ] Convention templates from `packages/api/src/services/convention-templates.ts` matched by repository language
- [ ] Priority: account override > repo config > template defaults

### AC4: History Querying
- [ ] LLM call outputs are queried from the event store (events with type `LLM.CALL.COMPLETED`)
- [ ] Filtered by `tags.issueId`, `tags.workflowInstanceId`, or `tags.repositoryId` depending on scope
- [ ] Semantic search on the `data.responseText` field when the query is provided
- [ ] Results ordered by relevance (if query provided) or recency (if no query)

### AC5: Account Scoping
- [ ] Convention lookups scoped to the current account's repositories and overrides
- [ ] History queries filtered by `accountId` at the event store level
- [ ] No cross-tenant data access possible

### AC6: Error Handling
- [ ] Missing `.tamma/config.json`: return template conventions only, no error
- [ ] No convention overrides configured: return template conventions only
- [ ] No history found: return "No previous LLM call results found for this [issue/workflow/repo]"
- [ ] Event store unreachable: return error message, do not throw

## Technical Context

### Convention System (existing)

The convention system is already implemented:

- **Convention templates**: `packages/api/src/services/convention-templates.ts` -- 20 preset templates (TypeScript, Python, Go, etc.) with language-specific coding rules
- **`{{conventions}}` injection**: LlmCallWorkflow already injects conventions into prompts via the `{{conventions}}` template variable in `ResolvePromptFromRegistryActivity`
- **`.tamma/config.json`**: Repositories can define conventions in their config file

The difference: currently conventions are injected statically into every prompt. With `search_conventions`, the LLM can query for specific conventions when it needs them (e.g., "what are the error handling conventions for this project?").

### Event Store for History

LLM call results are recorded as events by `RecordDiagnosticsActivity` at `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs`. These events contain the response text, provider used, token counts, and correlation IDs.

### Key Files

| File | Role |
|------|------|
| `packages/api/src/services/convention-templates.ts` | Convention template library (20 templates) |
| `packages/api/src/services/prompt-store.ts` | Prompt store with convention injection |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` | Convention injection point |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` | Records LLM call results to event store |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs` | Interface to implement |

## Dependencies

- **Epic 27**: Prompt store (for account-level convention overrides)
- **Story 12-1**: `IToolExecutor` interface and registry
- **Story 12-2**: Agentic tool loop
- Event store infrastructure (for history queries)

## Estimated Effort

| Task | Hours |
|------|-------|
| C# `SearchConventionsTool` implementation | 4 |
| C# `SearchHistoryTool` implementation | 4 |
| Node.js API endpoints (2 routes) | 3 |
| Convention source resolution logic | 2 |
| Unit tests (8+ tests) | 2 |
| Integration tests (2 tests) | 1 |
| **Total** | **16 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |
| 2026-04-09 | 1.1 | Added note: convention templates may move to DB in a future story (see Epic 27 README) | Cross-epic review |

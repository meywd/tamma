# Story 12-7d: Tool Access Configuration Per Role

Status: ready-for-dev

## Story

As a **platform administrator**,
I want to configure which context tools are available to each agent role,
so that agents only have access to the context tools relevant to their task (a security reviewer doesn't need `search_stories`, a planner doesn't need `search_findings`).

## Summary

Extend the existing `ToolLoopConfig.AllowedTools` mechanism to support per-role context tool whitelists. Add role-based tool access configuration in prompt templates (building on the existing `enableTools` flag). Add account-level overrides so platform admins can customize tool access for their tenants.

## Acceptance Criteria

### AC1: Per-Role Default Tool Access
- [ ] Each role has a default set of allowed context tools:

| Role | search_code_semantic | search_findings | search_stories | search_conventions | search_history |
|------|---------------------|----------------|----------------|-------------------|----------------|
| developer | Yes | No | Yes | Yes | Yes |
| tester | Yes | No | No | Yes | Yes |
| security_reviewer | Yes | Yes | No | Yes | Yes |
| planner | No | No | Yes | Yes | Yes |
| architect | Yes | No | Yes | Yes | Yes |
| code_reviewer | Yes | Yes | No | Yes | Yes |
| mentor | No | No | Yes | Yes | Yes |
| devops | Yes | No | No | Yes | Yes |

- [ ] Defaults defined in a configuration constant, not hardcoded per tool

### AC2: Prompt Template Integration
- [ ] Prompt templates (from `default-prompts.ts` / prompt store) gain a `contextTools` field:
  ```typescript
  interface PromptTemplate {
    // ... existing fields
    enableTools: boolean;
    contextTools?: string[];  // NEW: which context tools this role+action has access to
  }
  ```
- [ ] When `contextTools` is null/undefined, use the per-role defaults from AC1
- [ ] When `contextTools` is an empty array, no context tools are available
- [ ] When `contextTools` has values, only those tools are available

### AC3: Account-Level Overrides
- [ ] Account admins can override the default context tool access per role
- [ ] Override stored in the prompt store (extends Epic 27 schema) or account config
- [ ] Account override takes precedence over system defaults
- [ ] Account can disable context tools entirely for a role, or add tools not in the default set

### AC4: Tool Resolution in LlmCallWorkflow
- [ ] `ResolveToolsActivity` resolves context tools based on:
  1. Prompt template `contextTools` field (if set)
  2. Account-level override (if exists)
  3. Per-role defaults (fallback)
- [ ] Resolved context tool names are merged into `ToolLoopConfig.AllowedTools`
- [ ] Existing non-context tools (file_read, shell_execute, etc.) are unaffected by this configuration

### AC5: Configuration Validation
- [ ] Unknown tool names in `contextTools` are logged as warnings and skipped
- [ ] Configuration changes take effect on the next LLM call (no restart required)
- [ ] API endpoint to view resolved tool access for a given role + account combination

### AC6: API Endpoints
- [ ] `GET /api/v1/context-tools/access/:role` -- returns the resolved tool access for a role (considering account overrides)
- [ ] `PUT /api/v1/context-tools/access/:role` -- set account-level override for a role's context tool access
- [ ] `DELETE /api/v1/context-tools/access/:role` -- remove account-level override, revert to defaults

## Technical Context

### Existing Tool Access Mechanism

`ToolLoopConfig.AllowedTools` (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs`) already supports an allowlist of tool names. When null or empty, all tools are allowed. When populated, only listed tools can be invoked.

The `IToolExecutorRegistry.GetAllowed(string[]? allowlist)` method already filters tools by allowlist.

### Prompt Template enableTools Flag

The existing `enableTools` boolean in prompt templates controls whether tools are available at all. The new `contextTools` field is orthogonal -- it controls which context tools specifically, not whether tool calling is enabled.

### Key Files

| File | Role |
|------|------|
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | `ToolLoopConfig.AllowedTools` |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs` | `GetAllowed()` method |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` | Tool resolution logic |
| `packages/api/src/services/default-prompts.ts` | Default prompt templates (80 role+action) |
| `packages/api/src/services/prompt-store.ts` | Prompt store with template retrieval |

## Dependencies

- **Story 12-7a**: Vector DB search tools (tools being configured)
- **Story 12-7b**: Convention & history tools (tools being configured)
- **Epic 27**: Prompt store (for persisting account overrides)
- **Story 12-1**: `IToolExecutorRegistry` and allowlist mechanism

## Estimated Effort

| Task | Hours |
|------|-------|
| Per-role default configuration constant | 1 |
| `contextTools` field on prompt templates | 2 |
| Account-level override storage and retrieval | 3 |
| Tool resolution logic in ResolveToolsActivity | 2 |
| API endpoints (3 routes) | 2 |
| Unit tests (8+ tests) | 2 |
| **Total** | **12 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |

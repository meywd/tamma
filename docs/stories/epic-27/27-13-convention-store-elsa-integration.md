# Story 27-13: Convention Store Elsa Workflow Integration

Status: ready-for-dev

## Story

As a **workflow engine developer**,
I want Elsa workflows to resolve conventions per-tenant from the convention store at LLM-call time,
so that the `{{conventions}}` template variable is populated with keyword-matched conventions instead of the static repo config value.

## Acceptance Criteria

1. `ReadRepoConventionsActivity` is updated (or replaced) to call the convention store resolver instead of reading `.tamma/config.json`
2. The activity builds an `LlmCallContext` from the current workflow state: action (from `LlmCallWorkflow` input), tools (from agent config), searchable text (from issue body / task prompt), repo languages (from repo metadata or context scan)
3. The activity calls `POST /api/conventions/resolve` (or directly invokes `IConventionStore.ResolveAsync` if in-process) with the context
4. The resolved body replaces the `{{conventions}}` placeholder in the prompt template (same substitution point as today)
5. When the convention store returns an empty body, the activity falls back to the repo config `.tamma/config.json` `conventions` field (backward compatibility)
6. `TenantId` is passed through from `LlmCallWorkflow` (wired in Story 27-6) for tenant-scoped resolution
7. A `CONVENTIONS.RESOLVED.SUCCESS` event is emitted for each resolution containing: triggered keys, trigger reasons, source layer (system/tenant), skipped keys, total chars, estimated tokens
8. All existing workflow tests pass without modification (backward compatible when convention store is unavailable or empty)
9. Integration test: trigger a workflow with a tenant that has convention overrides → verify the override conventions are injected into the prompt

## Technical Context

### Current Convention Flow

```
LlmCallWorkflow
  → ResolvePromptFromRegistryActivity (resolves prompt template)
  → ReadRepoConventionsActivity
      → GET /api/engine/repo-config?repo=owner/repo
      → reads .tamma/config.json from the repo
      → extracts the `conventions` field (single string)
  → template.Replace("{{conventions}}", conventions)
  → LLM call
```

### New Convention Flow

```
LlmCallWorkflow
  → ResolvePromptFromRegistryActivity (resolves prompt template — unchanged)
  → ResolveConventionsActivity (NEW or updated ReadRepoConventionsActivity)
      → Build LlmCallContext from workflow variables:
          action   = agentRole + "/" + taskAction (or just the action name)
          tools    = agent's configured tool list
          text     = issue body + task prompt
          langs    = repo language metadata
      → Call IConventionStore.ResolveAsync(tenantId, context)
      → If result.Body is non-empty → use it
      → If result.Body is empty → fallback to repo-config conventions
      → Emit CONVENTIONS.RESOLVED.SUCCESS event
  → template.Replace("{{conventions}}", conventions)
  → LLM call
```

### Building LlmCallContext from Workflow State

The workflow variables available at convention-resolution time:

| Context field | Source variable | Example |
|--------------|----------------|---------|
| `Action` | `agentAction` or `taskAction` from LlmCallWorkflow input | `"writeCode"`, `"reviewCode"` |
| `Tools` | Agent config from `GetAgentsConfig` / agent role lookup | `["edit", "bash", "write"]` |
| `SearchableText` | `issueBody` + `taskPrompt` from workflow input | Issue title + body + task description |
| `RepoLanguages` | Repo metadata from context scan or installation config | `["typescript", "react"]` |

If `RepoLanguages` is not available (no context scan yet), fall back to empty array — keyword matching still works via action and searchable text.

### Event Format

```json
{
  "type": "CONVENTIONS.RESOLVED.SUCCESS",
  "timestamp": "2026-05-04T10:23:11.234Z",
  "tags": {
    "tenantId": "acme-uuid",
    "issueId": "issue-uuid",
    "action": "writeCode"
  },
  "metadata": {
    "workflowVersion": "1.0.0",
    "eventSource": "system"
  },
  "data": {
    "tools": ["edit", "write", "bash"],
    "repoLanguages": ["typescript", "react"],
    "triggered": [
      { "key": "typescript-react", "reason": "keyword:typescript", "source": "system" },
      { "key": "house-style", "reason": "always_apply", "source": "tenant" }
    ],
    "skipped": ["python", "go", "security-review"],
    "totalChars": 3200,
    "estimatedTokens": 800,
    "fallbackUsed": false
  }
}
```

The `fallbackUsed` field indicates whether the repo-config fallback was used (convention store returned empty).

### Files to Create

| File | Purpose |
|------|---------|
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveConventionsActivity.cs` | New activity (or rename existing ReadRepoConventionsActivity) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/ResolveConventionsActivityTests.cs` | Unit tests |

### Files to Modify

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Replace ReadRepoConventionsActivity with ResolveConventionsActivity |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ReadRepoConventionsActivity.cs` | Deprecate or repurpose as fallback-only |

## Implementation Plan

### Step 1: Create ResolveConventionsActivity

New Elsa activity that:
1. Reads workflow variables: `tenantId`, `agentAction`, `toolList`, `issueBody`, `taskPrompt`, `repoLanguages`
2. Builds `LlmCallContext` from these variables
3. Calls `IConventionStore.ResolveAsync(tenantId, context)` (injected via DI — in-process call, not HTTP)
4. If resolution returns non-empty body → set output
5. If resolution returns empty → fall back to `ReadRepoConventionsActivity` (reads `.tamma/config.json`)
6. Emits `CONVENTIONS.RESOLVED.SUCCESS` event via `IEventStore`

```csharp
[Activity(
    Type = "Tamma.ResolveConventions",
    DisplayName = "Resolve Conventions",
    Description = "Resolves coding conventions from the convention store based on keyword matching")]
public class ResolveConventionsActivity : CodeActivity<string>
{
    [Input(Description = "Tenant ID for tenant-scoped resolution")]
    public Input<string?> TenantId { get; set; } = new(default(string));

    [Input(Description = "Current action (e.g. writeCode, reviewCode)")]
    public Input<string> Action { get; set; } = new("");

    [Input(Description = "Available tools for this call")]
    public Input<string[]> Tools { get; set; } = new(Array.Empty<string>());

    [Input(Description = "Issue body + task prompt for keyword matching")]
    public Input<string> SearchableText { get; set; } = new("");

    [Input(Description = "Repository languages")]
    public Input<string[]> RepoLanguages { get; set; } = new(Array.Empty<string>());

    [Input(Description = "Fallback: repo conventions from .tamma/config.json")]
    public Input<string> FallbackConventions { get; set; } = new("");
}
```

### Step 2: Update LlmCallWorkflow

Replace `ReadRepoConventionsActivity` with `ResolveConventionsActivity` in the workflow sequence. Wire the input variables from existing workflow state.

The existing `ReadRepoConventionsActivity` is retained but demoted to a fallback — it runs only if `ResolveConventionsActivity` returns empty. Alternatively, the fallback is built into `ResolveConventionsActivity` itself (preferred — keeps the workflow simpler).

### Step 3: Wire DI

`ResolveConventionsActivity` needs `IConventionStore` and `IEventStore` injected. Register these in the Elsa activity DI container.

### Step 4: Emit Event

After resolution, emit the `CONVENTIONS.RESOLVED.SUCCESS` event with the full trigger detail. This is the audit trail for "which conventions fired and why for this LLM call."

## Implementation Notes

1. **In-process vs HTTP**: The activity calls `IConventionStore.ResolveAsync` directly (in-process DI), not via HTTP to the API. This avoids a round-trip and keeps the convention resolution within the same transaction boundary as the workflow. The API `/resolve` endpoint exists separately for the dashboard test panel.
2. **Fallback strategy**: When the convention store returns empty body (no conventions match), the activity falls back to `ReadRepoConventionsActivity`'s repo-config value. This preserves backward compatibility for repos that haven't set up conventions in the store but have a `.tamma/config.json` conventions field.
3. **SearchableText size**: The issue body + task prompt can be large. Truncate to 10,000 characters for keyword matching — we only need enough text for keyword presence detection, not the full content.
4. **RepoLanguages detection**: If not provided by a prior activity, this can be inferred from the repo's GitHub language breakdown (already available via the GitHub engine callback) or from file extensions in the context scan output.
5. **Event emission is best-effort**: Same as Story 27-7 — if the event store is unavailable, convention resolution still succeeds.
6. The `CONVENTIONS.RESOLVED.SUCCESS` event is separate from any prompt event. An LLM call now emits both a prompt resolution event (if Story 27-7 is implemented) and a convention resolution event. They are correlated by `issueId` and timestamp.
7. **Resolution performance**: The in-process `ResolveAsync` call uses the normalized `convention_keywords` table with a B-tree index on `keyword` for the hot path (`WHERE keyword IN (@terms)` — single index scan). This is 2-3 queries regardless of how many conventions exist (see Story 27-9 resolution algorithm). The repo-config fallback is a single HTTP call that only fires when the store returns empty.

## Testing Strategy

### Unit Tests

1. `ResolveConventionsActivity` calls `IConventionStore.ResolveAsync` with correct `LlmCallContext`
2. Activity returns resolved body when conventions match
3. Activity returns fallback conventions when store returns empty body
4. Activity returns empty string when both store and fallback are empty
5. Activity passes `tenantId` through to resolver
6. `CONVENTIONS.RESOLVED.SUCCESS` event is emitted with correct triggered/skipped data
7. Event is emitted even when fallback is used (with `fallbackUsed: true`)
8. Activity does not throw when event store is unavailable

### Integration Tests

9. Trigger `LlmCallWorkflow` with a tenant that has convention overrides → verify conventions appear in the resolved prompt
10. Trigger `LlmCallWorkflow` without tenant conventions → verify fallback to repo-config works
11. Trigger `LlmCallWorkflow` with keywords matching multiple conventions → verify correct concatenation order

### Backward Compatibility

12. All existing `LlmCallWorkflow` tests pass (activity gracefully handles missing convention store)
13. Elsa Studio can still inspect and run workflows (new activity has default input values)

## Dependencies

- **Story 27-9** (Convention Store Service) — `IConventionStore.ResolveAsync` must exist
- **Story 27-6** (Elsa Workflow Integration — Prompts) — `tenantId` propagation must be wired
- Internal: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ReadRepoConventionsActivity.cs` (fallback)
- Internal: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`

## Estimated Effort

| Task | Hours |
|------|-------|
| ResolveConventionsActivity implementation | 3 |
| LlmCallContext construction from workflow state | 2 |
| LlmCallWorkflow wiring (replace/add activity) | 1.5 |
| Event emission | 1 |
| Fallback to repo-config | 1 |
| DI registration | 0.5 |
| Unit tests (8 tests) | 2 |
| Integration tests (3 tests) | 2 |
| Backward compat verification | 1 |
| **Total** | **14 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-05-04 | 1.0 | Initial story creation | Architecture Team |

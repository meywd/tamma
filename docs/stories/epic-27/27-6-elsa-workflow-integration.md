# Story 27-6: Elsa Workflow Integration

Status: ready-for-dev

## Story

As a **workflow engine developer**,
I want Elsa workflows to resolve prompts per-account from the PostgreSQL prompt store,
so that different organizations using Tamma get their own customized prompt templates when the AI runs.

## Acceptance Criteria

1. `ResolvePromptFromRegistryActivity` accepts an `AccountId` input parameter (string, optional)
2. `ResolvePromptFromRegistryActivity` passes `accountId` as a query parameter or header when calling `POST /api/prompts/:role/:action/render`
3. When `AccountId` is empty or null, the activity falls back to system defaults (current behavior preserved)
4. `LlmCallWorkflow` accepts an `accountId` input variable and propagates it to `ResolvePromptFromRegistryActivity`
5. `SingleIssueCycleWorkflow` accepts an `accountId` input variable and propagates it to all sub-workflow dispatches (LlmCallWorkflow, PlanGenerationWorkflow, etc.)
6. The `accountId` is extracted from the GitHub App installation context: `installation_id` maps to `tenant_id` (from Epic 17), which is the `accountId` for prompt resolution
7. All existing workflow tests pass without modification (backward compatible when accountId is not provided)
8. Integration test: trigger a workflow with an accountId, verify the resolved prompt comes from the account's override (not system default)

## Technical Context

### Current Elsa Activity

The `ResolvePromptFromRegistryActivity` in `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs`:
- Inputs: `Role`, `Action`, `VariablesJson`, `FallbackPrompt`
- Calls: `POST /api/prompts/{role}/{action}/render` with `{ variables }` body
- No concept of account or tenant

### How accountId Flows

```
GitHub App Installation webhook
  │
  ▼
installation_id → tenants table (Epic 17) → tenant_id
  │
  ▼
SingleIssueCycleWorkflow.Input["accountId"] = tenant_id
  │
  ▼
DispatchWorkflow(LlmCallWorkflow, Input: { accountId, agentRole, ... })
  │
  ▼
LlmCallWorkflow → ResolvePromptFromRegistryActivity.AccountId = accountId
  │
  ▼
POST /api/prompts/{role}/{action}/render?accountId={accountId}
```

### API Call Change

Current call:
```
POST http://localhost:3100/api/prompts/developer/implement/render
Body: { "variables": { "role": "developer", ... } }
```

New call (with account context):
```
POST http://localhost:3100/api/prompts/developer/implement/render
Headers: X-Account-Id: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
Body: { "variables": { "role": "developer", ... } }
```

Alternatively, the accountId can be passed as a query parameter:
```
POST http://localhost:3100/api/prompts/developer/implement/render?accountId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
```

The header approach is preferred because it matches the auth middleware pattern from Epic 16/17 and keeps the URL clean. The render endpoint extracts accountId from either the header or the authenticated session.

### Files to Modify

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` | Add `AccountId` input; pass to API call |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Accept `accountId` input; pass to ResolvePromptFromRegistryActivity |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Accept `accountId` input; propagate to sub-workflow dispatches |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs` | Accept and propagate `accountId` |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowStructureTests.cs` | Update tests for new input parameter |

### Files Potentially Modified

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/ADL/DispatchCycleActivity.cs` | Pass accountId when dispatching SingleIssueCycleWorkflow |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/CheckLimitsActivity.cs` | Pass accountId through context |

## Implementation Plan

### Step 1: Update ResolvePromptFromRegistryActivity

Add the `AccountId` input:

```csharp
[Input(Description = "Account ID for tenant-scoped prompt resolution (empty = system defaults)")]
public Input<string> AccountId { get; set; } = new("");
```

Update the HTTP call to include the header:

```csharp
var accountId = AccountId.Get(context);
if (!string.IsNullOrEmpty(accountId))
{
    httpClient.DefaultRequestHeaders.Add("X-Account-Id", accountId);
}
```

### Step 2: Update LlmCallWorkflow

Add `accountId` as an input variable:

```csharp
var accountIdVar = new Variable<string>("accountId", "");
// In Build():
workflow.WithInput("accountId", accountIdVar);
```

Wire it to the ResolvePromptFromRegistryActivity:

```csharp
resolvePrompt.AccountId = new Input<string>(context => accountIdVar.Get(context));
```

### Step 3: Update SingleIssueCycleWorkflow

Accept `accountId` as a workflow input and pass it through to all `DispatchWorkflow` activities:

```csharp
var accountIdVar = new Variable<string>("accountId", "");
// When dispatching LlmCallWorkflow:
new DispatchWorkflow
{
    Input = new Input<IDictionary<string, object>>(ctx => new Dictionary<string, object>
    {
        ["accountId"] = accountIdVar.Get(ctx) ?? "",
        ["agentRole"] = role,
        ["taskPrompt"] = prompt,
        // ...
    })
}
```

### Step 4: Update PlanGenerationWorkflow

Same pattern as SingleIssueCycleWorkflow: accept `accountId` input, propagate to LlmCallWorkflow dispatches.

### Step 5: Wire accountId from Installation Context

In `DispatchCycleActivity` (the ADL entry point that dispatches `SingleIssueCycleWorkflow`), extract the tenant/account ID from the installation context:

```csharp
// The installation context carries the tenant_id from Epic 17
var accountId = installationContext?.TenantId ?? "";
```

This requires that the `InstallationContext` (or equivalent) carries the `tenantId` from Epic 17. If this field does not yet exist, it should be added as part of this story or deferred to when Epic 17 wiring is complete.

### Step 6: API Endpoint Update

The render endpoint (`POST /api/prompts/:role/:action/render`) must accept accountId from:
1. The `X-Account-Id` header (for Elsa workflow calls)
2. The authenticated session (for dashboard calls)
3. A query parameter `?accountId=...` (fallback)

Priority: authenticated session > header > query parameter.

This may already be handled by Story 27-3's auth middleware integration. If the render endpoint is called by Elsa (server-to-server), the header approach is used. The API endpoint code in Story 27-3 should be designed to handle both patterns.

## Implementation Notes

1. The Elsa activity makes HTTP calls to the Tamma API. It does not directly access PostgreSQL. This keeps the C# and TypeScript codebases decoupled.
2. The `X-Account-Id` header is for internal service-to-service calls. It should be validated (UUID format) and only accepted from trusted sources (e.g., the Elsa server's IP or a shared secret).
3. When `accountId` is empty/null, the behavior is identical to the current implementation (system defaults). This ensures backward compatibility.
4. The `FallbackPrompt` mechanism is retained: if the API is unreachable, the activity uses the fallback prompt regardless of accountId.
5. All sub-workflows that use LlmCallWorkflow must propagate accountId. This includes: SingleIssueCycleWorkflow, PlanGenerationWorkflow, and any future workflows that call LlmCallWorkflow.
6. The `WorkflowStructureTests` verify that all expected inputs exist on workflows. These tests must be updated to include `accountId`.

## Testing Strategy

### Unit Tests

1. `ResolvePromptFromRegistryActivity` with empty AccountId resolves system default (existing behavior)
2. `ResolvePromptFromRegistryActivity` with AccountId sends `X-Account-Id` header
3. `LlmCallWorkflow` has `accountId` input variable
4. `SingleIssueCycleWorkflow` has `accountId` input variable
5. `PlanGenerationWorkflow` has `accountId` input variable
6. `WorkflowStructureTests` pass with updated input expectations

### Integration Tests

7. Trigger `LlmCallWorkflow` with accountId; verify the render API receives the header
8. Trigger `LlmCallWorkflow` without accountId; verify system default is resolved
9. Create an account override, trigger workflow with that accountId, verify the override prompt is used

### Backward Compatibility

10. All existing workflow tests pass without providing accountId
11. Elsa Studio can still inspect and run workflows (new input has default value)

## Dependencies

- **Story 27-2** (Prompt Store Service) -- Postgres-backed store must exist for account resolution
- **Story 27-3** (API Endpoints) -- render endpoint must accept accountId
- **Epic 17** (Story 17-1: Tenant Model) -- tenant_id must be available in installation context
- Internal: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs`
- Internal: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`
- Internal: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

## Estimated Effort

| Task | Hours |
|------|-------|
| Update ResolvePromptFromRegistryActivity (add AccountId input, header) | 1.5 |
| Update LlmCallWorkflow (accept and propagate accountId) | 1.5 |
| Update SingleIssueCycleWorkflow (accept and propagate accountId) | 1.5 |
| Update PlanGenerationWorkflow + other sub-workflows | 1.5 |
| Wire accountId from installation context | 1 |
| Unit tests (6 tests) | 1.5 |
| Integration tests (3 tests) | 1.5 |
| **Total** | **10 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |

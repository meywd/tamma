# Story 27-6: Elsa Workflow Integration

Status: ready-for-dev

## Story

As a **workflow engine developer**,
I want Elsa workflows to resolve prompts per-tenant from the PostgreSQL prompt store,
so that different organizations using Tamma get their own customized prompt templates when the AI runs.

## Acceptance Criteria

1. `ResolvePromptFromRegistryActivity` accepts an `TenantId` input parameter (string, optional)
2. `ResolvePromptFromRegistryActivity` passes `tenantId` as a query parameter or header when calling `POST /api/prompts/:role/:action/render`
3. When `TenantId` is empty or null, the activity falls back to system defaults (current behavior preserved)
4. `LlmCallWorkflow` accepts an `tenantId` input variable and propagates it to `ResolvePromptFromRegistryActivity`
5. `SingleIssueCycleWorkflow` accepts an `tenantId` input variable and propagates it to all sub-workflow dispatches (LlmCallWorkflow, PlanGenerationWorkflow, etc.)
6. The `tenantId` is extracted from the GitHub App installation context: `installation_id` maps to `tenant_id` (from Epic 17), which is the `tenantId` for prompt resolution
7. All existing workflow tests pass without modification (backward compatible when tenantId is not provided)
8. Integration test: trigger a workflow with an tenantId, verify the resolved prompt comes from the account's override (not system default)

## Technical Context

### Current Elsa Activity

The `ResolvePromptFromRegistryActivity` in `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs`:
- Inputs: `Role`, `Action`, `VariablesJson`, `FallbackPrompt`
- Calls: `POST /api/prompts/{role}/{action}/render` with `{ variables }` body
- No concept of account or tenant

### How tenantId Flows

```
GitHub App Installation webhook
  │
  ▼
installation_id → tenants table (Epic 17) → tenant_id
  │
  ▼
SingleIssueCycleWorkflow.Input["tenantId"] = tenant_id
  │
  ▼
DispatchWorkflow(LlmCallWorkflow, Input: { tenantId, agentRole, ... })
  │
  ▼
LlmCallWorkflow → ResolvePromptFromRegistryActivity.TenantId = tenantId
  │
  ▼
POST /api/prompts/{role}/{action}/render?tenantId={tenantId}
```

### API Call Change

Current call:
```
POST http://localhost:3100/api/prompts/developer/implement/render
Body: { "variables": { "role": "developer", ... } }
```

New call (with tenant context):
```
POST http://localhost:3100/api/prompts/developer/implement/render
Headers: X-Tenant-Id: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
Body: { "variables": { "role": "developer", ... } }
```

Alternatively, the tenantId can be passed as a query parameter:
```
POST http://localhost:3100/api/prompts/developer/implement/render?tenantId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
```

The header approach is preferred because it matches the auth middleware pattern from Epic 16/17 and keeps the URL clean. The render endpoint extracts tenantId from either the header or the authenticated session.

### Files to Modify

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` | Add `TenantId` input; pass to API call |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Accept `tenantId` input; pass to ResolvePromptFromRegistryActivity |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Accept `tenantId` input; propagate to sub-workflow dispatches |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs` | Accept and propagate `tenantId` |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowStructureTests.cs` | Update tests for new input parameter |

### Files Potentially Modified

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/ADL/DispatchCycleActivity.cs` | Pass tenantId when dispatching SingleIssueCycleWorkflow |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/CheckLimitsActivity.cs` | Pass tenantId through context |

## Implementation Plan

### Step 1: Update ResolvePromptFromRegistryActivity

Add the `TenantId` input:

```csharp
[Input(Description = "Account ID for tenant-scoped prompt resolution (empty = system defaults)")]
public Input<string> TenantId { get; set; } = new("");
```

Update the HTTP call to include the header:

```csharp
var tenantId = TenantId.Get(context);
if (!string.IsNullOrEmpty(tenantId))
{
    httpClient.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
}
```

### Step 2: Update LlmCallWorkflow

Add `tenantId` as an input variable:

```csharp
var tenantIdVar = new Variable<string>("tenantId", "");
// In Build():
workflow.WithInput("tenantId", tenantIdVar);
```

Wire it to the ResolvePromptFromRegistryActivity:

```csharp
resolvePrompt.TenantId = new Input<string>(context => tenantIdVar.Get(context));
```

### Step 3: Update SingleIssueCycleWorkflow

Accept `tenantId` as a workflow input and pass it through to all `DispatchWorkflow` activities:

```csharp
var tenantIdVar = new Variable<string>("tenantId", "");
// When dispatching LlmCallWorkflow:
new DispatchWorkflow
{
    Input = new Input<IDictionary<string, object>>(ctx => new Dictionary<string, object>
    {
        ["tenantId"] = tenantIdVar.Get(ctx) ?? "",
        ["agentRole"] = role,
        ["taskPrompt"] = prompt,
        // ...
    })
}
```

### Step 4: Update PlanGenerationWorkflow

Same pattern as SingleIssueCycleWorkflow: accept `tenantId` input, propagate to LlmCallWorkflow dispatches.

### Step 5: Wire tenantId from Installation Context

In `DispatchCycleActivity` (the ADL entry point that dispatches `SingleIssueCycleWorkflow`), extract the tenant/tenant ID from the installation context:

```csharp
// The installation context carries the tenant_id from Epic 17
var tenantId = installationContext?.TenantId ?? "";
```

This requires that the `InstallationContext` (or equivalent) carries the `tenantId` from Epic 17. If this field does not yet exist, it should be added as part of this story or deferred to when Epic 17 wiring is complete.

### Step 6: API Endpoint Update

The render endpoint (`POST /api/prompts/:role/:action/render`) must accept tenantId from:
1. The `X-Tenant-Id` header (for Elsa workflow calls)
2. The authenticated session (for dashboard calls)
3. A query parameter `?tenantId=...` (fallback)

Priority: authenticated session > header > query parameter.

This may already be handled by Story 27-3's auth middleware integration. If the render endpoint is called by Elsa (server-to-server), the header approach is used. The API endpoint code in Story 27-3 should be designed to handle both patterns.

## Implementation Notes

1. The Elsa activity makes HTTP calls to the Tamma API. It does not directly access PostgreSQL. This keeps the C# and TypeScript codebases decoupled.
2. The `X-Tenant-Id` header is for internal service-to-service calls. It should be validated (UUID format) and only accepted from trusted sources (e.g., the Elsa server's IP or a shared secret).
3. When `tenantId` is empty/null, the behavior is identical to the current implementation (system defaults). This ensures backward compatibility.
4. The `FallbackPrompt` mechanism is retained: if the API is unreachable, the activity uses the fallback prompt regardless of tenantId.
5. All sub-workflows that use LlmCallWorkflow must propagate tenantId. This includes: SingleIssueCycleWorkflow, PlanGenerationWorkflow, and any future workflows that call LlmCallWorkflow.
6. The `WorkflowStructureTests` verify that all expected inputs exist on workflows. These tests must be updated to include `tenantId`.

## Testing Strategy

### Unit Tests

1. `ResolvePromptFromRegistryActivity` with empty TenantId resolves system default (existing behavior)
2. `ResolvePromptFromRegistryActivity` with TenantId sends `X-Tenant-Id` header
3. `LlmCallWorkflow` has `tenantId` input variable
4. `SingleIssueCycleWorkflow` has `tenantId` input variable
5. `PlanGenerationWorkflow` has `tenantId` input variable
6. `WorkflowStructureTests` pass with updated input expectations

### Integration Tests

7. Trigger `LlmCallWorkflow` with tenantId; verify the render API receives the header
8. Trigger `LlmCallWorkflow` without tenantId; verify system default is resolved
9. Create an tenant override, trigger workflow with that tenantId, verify the override prompt is used

### Backward Compatibility

10. All existing workflow tests pass without providing tenantId
11. Elsa Studio can still inspect and run workflows (new input has default value)

## Dependencies

- **Story 27-2** (Prompt Store Service) -- Postgres-backed store must exist for tenant resolution
- **Story 27-3** (API Endpoints) -- render endpoint must accept tenantId
- **Epic 17** (Story 17-1: Tenant Model) -- tenant_id must be available in installation context
- Internal: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs`
- Internal: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`
- Internal: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

## Estimated Effort

| Task | Hours |
|------|-------|
| Update ResolvePromptFromRegistryActivity (add TenantId input, header) | 1.5 |
| Update LlmCallWorkflow (accept and propagate tenantId) | 1.5 |
| Update SingleIssueCycleWorkflow (accept and propagate tenantId) | 1.5 |
| Update PlanGenerationWorkflow + other sub-workflows | 1.5 |
| Wire tenantId from installation context | 1 |
| Unit tests (6 tests) | 1.5 |
| Integration tests (3 tests) | 1.5 |
| **Total** | **10 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |

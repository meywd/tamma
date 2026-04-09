# Story 27-6: Elsa Workflow Integration — Implementation Plan

## Overview

Update the C# Elsa workflows to propagate `accountId` from the GitHub App installation context through to the prompt registry API. The `ResolvePromptFromRegistryActivity` gains an `AccountId` input and sends it as an `X-Account-Id` header. All parent workflows (`LlmCallWorkflow`, `SingleIssueCycleWorkflow`, `PlanGenerationWorkflow`) accept and propagate `accountId` as a workflow input.

---

## Step-by-Step Implementation Tasks

### Task 1: Update ResolvePromptFromRegistryActivity (1.5 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs`

#### 1a. Add AccountId Input Property

After line 43 (`FallbackPrompt` input), add:

```csharp
[Input(Description = "Account ID for tenant-scoped prompt resolution (empty = system defaults)")]
public Input<string> AccountId { get; set; } = new("");
```

#### 1b. Read AccountId in RunAsync

After line 75 (`var fallback = FallbackPrompt.Get(context);`), add:

```csharp
var accountId = AccountId.Get(context);
```

#### 1c. Send X-Account-Id Header in HTTP Call

Replace the HTTP call block (lines 100-112) to include the header:

```csharp
var httpClient = _httpClientFactory?.CreateClient() ?? new HttpClient();

// Add account context header for tenant-scoped resolution
if (!string.IsNullOrEmpty(accountId))
{
    httpClient.DefaultRequestHeaders.Remove("X-Account-Id");
    httpClient.DefaultRequestHeaders.Add("X-Account-Id", accountId);
}

// Parse variables
Dictionary<string, object>? variables = null;
if (!string.IsNullOrEmpty(variablesJson) && variablesJson != "{}")
{
    variables = JsonSerializer.Deserialize<Dictionary<string, object>>(variablesJson);
}

// Call render endpoint
var response = await httpClient.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/prompts/{Uri.EscapeDataString(role)}/{Uri.EscapeDataString(action)}/render",
    new { variables = variables ?? new Dictionary<string, object>() });
```

**Note**: Use `httpClient.DefaultRequestHeaders.Remove()` before `Add()` to avoid duplicate headers if the client is reused.

#### 1d. Update BuildStartData to Include AccountId

```csharp
public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
{
    ["role"] = Role.Get(context),
    ["action"] = Action.Get(context),
    ["accountId"] = AccountId.Get(context),
};
```

---

### Task 2: Update LlmCallWorkflow (1.5 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`

#### 2a. Add accountId Variable

After line 68 (`var sessionIdVar = ...`), add:

```csharp
var accountIdVar = builder.WithVariable<string>("AccountId", "");
```

#### 2b. Capture accountId from Workflow Input

In the `InitInputs` activity (around line 100), add inside the input capture block:

```csharp
var acctId = ctx.GetInput<string>("accountId");
if (!string.IsNullOrEmpty(acctId)) accountIdVar.Set(ctx, acctId);
```

#### 2c. Wire accountId to ResolvePromptFromRegistryActivity

Find where `ResolvePromptFromRegistryActivity` is instantiated (the `resolvePrompt` activity). Add:

```csharp
AccountId = new Input<string>(ctx => accountIdVar.Get(ctx) ?? ""),
```

alongside the existing `Role`, `Action`, `VariablesJson`, and `FallbackPrompt` inputs.

---

### Task 3: Update SingleIssueCycleWorkflow (1.5 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

#### 3a. Add accountId Variable

In the Variables section, add:

```csharp
var accountIdVar = builder.WithVariable<string>("AccountId", "");
```

#### 3b. Capture accountId from Workflow Input

In the `InitInputs` activity, add:

```csharp
var acctId = ctx.GetInput<string>("accountId");
if (!string.IsNullOrEmpty(acctId)) accountIdVar.Set(ctx, acctId);
```

#### 3c. Propagate to Sub-Workflow Dispatches

Find all `DispatchWorkflow` activities that dispatch to `llm-call`, `tdd-with-debug-retry`, `tdd-cycle`, `plan-generation`, or `debugging`. In each `Input` dictionary, add:

```csharp
["accountId"] = accountIdVar.Get(ctx) ?? "",
```

Example for LlmCallWorkflow dispatch:

```csharp
Input = new(ctx => new Dictionary<string, object>
{
    ["accountId"] = accountIdVar.Get(ctx) ?? "",
    ["agentRole"] = role,
    ["taskPrompt"] = prompt,
    ["context"] = contextJson,
    // ... existing inputs
}),
```

---

### Task 4: Update PlanGenerationWorkflow (1.5 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs`

Same pattern as SingleIssueCycleWorkflow:

1. Add `accountIdVar` variable
2. Capture from workflow input
3. Propagate to all `DispatchWorkflow(llm-call)` dispatches

---

### Task 5: Update TddWithDebugRetryWorkflow (if it exists) (0.5 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs`

If this sub-workflow (from Story 13.1) dispatches `llm-call` or other sub-workflows, it must also propagate `accountId`.

---

### Task 6: Wire accountId from Installation Context (1 hour)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/ADL/DispatchCycleActivity.cs`

The ADL entry point dispatches `SingleIssueCycleWorkflow` when a GitHub issue is assigned. It must extract the `accountId` (tenant ID) from the installation context.

```csharp
// The installation context carries the GitHub installation ID.
// Map installation_id → tenant_id via the tenants table (Epic 17).
// For now, pass the installation_id as-is if tenantId is not yet available.
var accountId = context.GetVariable<string>("TenantId")
    ?? context.GetVariable<string>("InstallationId")
    ?? "";

// Pass to SingleIssueCycleWorkflow dispatch:
Input = new(ctx => new Dictionary<string, object>
{
    ["accountId"] = accountId,
    // ... existing inputs
}),
```

**Note**: The mapping from `installation_id` to `tenant_id` depends on Epic 17. If that mapping is not yet available, the raw `installation_id` can be passed. The prompt store will fall back to system defaults if the accountId doesn't match any overrides.

---

### Task 7: Update API Render Endpoint for X-Account-Id Header (0.5 hours)

**File to verify**: `packages/api/src/routes/prompts/prompt-routes.ts`

The render endpoint from Story 27-3 should already accept `X-Account-Id` header. Verify:

```typescript
// In POST /api/prompts/:role/:action/render handler:
const accountId = request.accountId
  ?? (request.headers['x-account-id'] as string | undefined)
  ?? (request.query as Record<string, string>)['accountId']
  ?? null;
```

If not present, add this logic. The `X-Account-Id` header should be validated as a UUID format.

---

### Task 8: Update WorkflowStructureTests (1.5 hours)

**File to modify**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowStructureTests.cs`

Add `accountId` to the expected input variables for:
- `LlmCallWorkflow`
- `SingleIssueCycleWorkflow`
- `PlanGenerationWorkflow`

```csharp
[Test]
public void LlmCallWorkflow_ShouldHaveAccountIdInput()
{
    var workflow = new LlmCallWorkflow();
    var builder = new WorkflowBuilder();
    workflow.Build(builder);
    var variables = builder.Variables;
    Assert.That(variables.Any(v => v.Name == "AccountId"), Is.True,
        "LlmCallWorkflow must accept AccountId input");
}
```

Also update any existing tests that enumerate expected variables to include `AccountId`.

---

### Task 9: Unit and Integration Tests (3 hours)

#### Unit Tests

**File to create/modify**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/ResolvePromptFromRegistryActivityTests.cs`

| # | Test | Assertion |
|---|------|-----------|
| 1 | Empty AccountId resolves system default | No X-Account-Id header sent |
| 2 | Non-empty AccountId sends header | HTTP request includes `X-Account-Id: <uuid>` |
| 3 | AccountId included in BuildStartData | Event data contains accountId |
| 4 | Fallback works with AccountId set | API failure returns fallback prompt |

#### Integration Tests

| # | Test | Assertion |
|---|------|-----------|
| 5 | LlmCallWorkflow accepts accountId input | Variable exists after build |
| 6 | SingleIssueCycleWorkflow propagates accountId | Dispatch input includes accountId |
| 7 | All existing workflow tests pass | Backward compatible (accountId defaults to "") |

#### Backward Compatibility Tests

| # | Test | Assertion |
|---|------|-----------|
| 8 | LlmCallWorkflow without accountId | Defaults to empty string, works normally |
| 9 | ResolvePromptFromRegistryActivity without AccountId | Falls back to system defaults |

---

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` | Add `AccountId` input, send header |
| 2 | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Add `accountIdVar`, capture input, wire to activity |
| 3 | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Add `accountIdVar`, propagate to dispatches |
| 4 | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs` | Add `accountIdVar`, propagate to dispatches |
| 5 | `apps/tamma-elsa/src/Tamma.Activities/ADL/DispatchCycleActivity.cs` | Pass accountId from installation context |
| 6 | `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowStructureTests.cs` | Update expected inputs |

## Files to Verify (no change expected)

| # | File Path | Verification |
|---|-----------|-------------|
| 1 | `packages/api/src/routes/prompts/prompt-routes.ts` | Render endpoint accepts X-Account-Id header (from Story 27-3) |

---

## Dependencies

- **Story 27-2** (Prompt Store Service) — account-scoped resolution must work
- **Story 27-3** (API Endpoints) — render endpoint must accept accountId
- **Epic 17** (Tenant Model) — `tenant_id` mapping from `installation_id` (can work without it by passing installation_id directly)

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Epic 17 not deployed yet — no `tenant_id` available | Pass `installation_id` as accountId; prompt store returns system defaults if no overrides exist for that ID |
| `DefaultRequestHeaders.Add` can throw if header already exists | Use `Remove()` before `Add()` to handle reused HttpClient instances |
| Multiple sub-workflows need updating | Track all DispatchWorkflow sites via grep for `DispatchWorkflow` + `llm-call` |
| Elsa Studio may not display new inputs correctly | New inputs have default values (empty string); Elsa Studio supports this |
| WorkflowStructureTests are brittle | Update tests to use flexible assertions (contains, not exact count) |

---

## Verification Steps

1. `cd apps/tamma-elsa && dotnet build` — 0 errors
2. `cd apps/tamma-elsa && dotnet test` — all existing tests pass
3. Start Elsa server and verify `LlmCallWorkflow` has `AccountId` variable in Studio
4. Trigger a workflow with accountId via the API; verify the render endpoint receives `X-Account-Id` header (check API logs)
5. Trigger without accountId; verify system defaults are used (backward compatible)

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Update ResolvePromptFromRegistryActivity | 1.5 |
| Update LlmCallWorkflow | 1.5 |
| Update SingleIssueCycleWorkflow | 1.5 |
| Update PlanGenerationWorkflow + others | 1.5 |
| Wire from installation context | 1 |
| API endpoint verification | 0.5 |
| WorkflowStructureTests update | 1.5 |
| Unit + integration tests (9 tests) | 3 |
| **Total** | **12 hours** |

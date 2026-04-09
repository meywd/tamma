# Story 12-7d: Tool Access Configuration Per Role -- Implementation Plan

## Overview

Add per-role context tool access configuration. Define default tool access per role, extend prompt templates with a `contextTools` field, add account-level overrides via API, and wire resolution into `ResolveToolsActivity` so that the tool loop receives the correct allowlist.

---

## Step-by-Step Implementation Tasks

### Task 1: Per-Role Default Configuration (1 hour)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolDefaults.cs`

```csharp
namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Default context tool access per agent role.
/// Used when no prompt template or account override specifies contextTools.
/// </summary>
public static class ContextToolDefaults
{
    /// <summary>
    /// All available context tool names.
    /// </summary>
    public static readonly string[] AllContextTools =
    {
        "search_code_semantic",
        "search_findings",
        "search_stories",
        "search_conventions",
        "search_history"
    };

    /// <summary>
    /// Default context tools per role.
    /// Key: role name (lowercase). Value: allowed context tool names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultsByRole =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["developer"] = new[]
            {
                "search_code_semantic", "search_stories",
                "search_conventions", "search_history"
            },
            ["tester"] = new[]
            {
                "search_code_semantic",
                "search_conventions", "search_history"
            },
            ["security_reviewer"] = new[]
            {
                "search_code_semantic", "search_findings",
                "search_conventions", "search_history"
            },
            ["planner"] = new[]
            {
                "search_stories",
                "search_conventions", "search_history"
            },
            ["architect"] = new[]
            {
                "search_code_semantic", "search_stories",
                "search_conventions", "search_history"
            },
            ["code_reviewer"] = new[]
            {
                "search_code_semantic", "search_findings",
                "search_conventions", "search_history"
            },
            ["mentor"] = new[]
            {
                "search_stories",
                "search_conventions", "search_history"
            },
            ["devops"] = new[]
            {
                "search_code_semantic",
                "search_conventions", "search_history"
            },
        };

    /// <summary>
    /// Get the default context tools for a role.
    /// Returns all context tools if the role is not in the defaults.
    /// </summary>
    public static string[] GetDefaults(string role)
    {
        return DefaultsByRole.TryGetValue(role, out var tools)
            ? tools
            : AllContextTools;
    }
}
```

---

### Task 2: Extend Prompt Templates with contextTools Field (2 hours)

**File to modify**: `packages/api/src/services/default-prompts.ts`

Add `contextTools` to the `PromptTemplate` interface and to the 80 default templates.

```typescript
interface PromptTemplate {
  role: string;
  action: string;
  version: number;
  template: string;
  variables: string[];
  systemPrompt: string;
  enableTools: boolean;
  maxTokens: number;
  contextTools?: string[];  // NEW
  createdAt: string;
  updatedAt: string;
}
```

For each role, set the default `contextTools` matching the C# defaults above.

**File to modify**: `packages/api/src/services/prompt-store.ts`

Update the prompt store to read and return the `contextTools` field.

---

### Task 3: Account-Level Override Storage (3 hours)

**File to create**: `packages/api/src/services/context-tool-access-service.ts`

```typescript
/**
 * Manages per-role context tool access configuration with account overrides.
 *
 * Resolution order:
 * 1. Account-level override (if exists)
 * 2. Prompt template contextTools field (if set)
 * 3. Per-role defaults from ContextToolDefaults
 */
export interface IContextToolAccessService {
  /**
   * Resolve the effective context tool list for a role + account.
   */
  resolveToolAccess(role: string, accountId?: string): Promise<string[]>;

  /**
   * Get the account-level override for a role (null if no override).
   */
  getAccountOverride(role: string, accountId: string): Promise<string[] | null>;

  /**
   * Set an account-level override for a role's context tools.
   */
  setAccountOverride(
    role: string,
    accountId: string,
    contextTools: string[]
  ): Promise<void>;

  /**
   * Remove an account-level override, reverting to defaults.
   */
  removeAccountOverride(role: string, accountId: string): Promise<void>;
}
```

Default implementation stores overrides in the database (extends the prompt store schema) or in a separate `context_tool_access` table:

```sql
CREATE TABLE IF NOT EXISTS context_tool_access (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id  UUID NOT NULL,
  role        TEXT NOT NULL,
  tools       JSONB NOT NULL DEFAULT '[]'::jsonb,  -- array of tool names
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(account_id, role)
);
```

---

### Task 4: Tool Resolution in ResolveToolsActivity (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs`

Add logic to resolve context tools and merge them into the overall tool list:

```csharp
// After resolving standard tools:
var contextTools = ResolveContextTools(role, accountId);
foreach (var ctxTool in contextTools)
{
    var tool = GetBuiltInContextTool(ctxTool);
    if (tool != null)
    {
        resolved.Add(tool);
    }
}
```

Add a new method `GetBuiltInContextTool()` for context tool definitions (similar to the existing `GetBuiltInTool()`).

Also modify to read `contextTools` from the resolved prompt template when available.

---

### Task 5: API Endpoints (2 hours)

**File to create**: `packages/api/src/routes/context-tool-access.ts`

```typescript
import type { FastifyInstance } from 'fastify';

export async function contextToolAccessRoutes(app: FastifyInstance): Promise<void> {
  // GET /api/v1/context-tools/access/:role
  // Returns resolved tool access for the authenticated account + role
  app.get<{ Params: { role: string } }>(
    '/api/v1/context-tools/access/:role',
    async (request, reply) => {
      const { role } = request.params;
      const accountId = request.accountId; // From auth middleware
      const tools = await contextToolAccessService.resolveToolAccess(role, accountId);
      return { role, accountId, contextTools: tools };
    }
  );

  // PUT /api/v1/context-tools/access/:role
  // Set account-level override
  app.put<{ Params: { role: string }; Body: { contextTools: string[] } }>(
    '/api/v1/context-tools/access/:role',
    async (request, reply) => {
      const { role } = request.params;
      const { contextTools } = request.body;
      const accountId = request.accountId;
      // Validate tool names
      await contextToolAccessService.setAccountOverride(role, accountId, contextTools);
      return { role, accountId, contextTools };
    }
  );

  // DELETE /api/v1/context-tools/access/:role
  // Remove account-level override
  app.delete<{ Params: { role: string } }>(
    '/api/v1/context-tools/access/:role',
    async (request, reply) => {
      const { role } = request.params;
      const accountId = request.accountId;
      await contextToolAccessService.removeAccountOverride(role, accountId);
      return { role, accountId, contextTools: null, message: 'Override removed, using defaults' };
    }
  );
}
```

Register in `packages/api/src/routes/index.ts`.

---

### Task 6: Unit Tests (2 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ContextToolDefaultsTests.cs`

```csharp
// 3 tests:
// 1. GetDefaults_Developer_ReturnsDeveloperTools
// 2. GetDefaults_SecurityReviewer_IncludesSearchFindings
// 3. GetDefaults_UnknownRole_ReturnsAllTools
```

**File to create**: `packages/api/src/services/__tests__/context-tool-access-service.test.ts`

```typescript
// 5 tests:
// 1. resolveToolAccess returns defaults when no override exists
// 2. resolveToolAccess returns account override when set
// 3. setAccountOverride persists and is returned by resolveToolAccess
// 4. removeAccountOverride reverts to defaults
// 5. resolveToolAccess validates tool names (rejects unknown tools)
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolDefaults.cs` | Default tool access per role |
| 2 | `packages/api/src/services/context-tool-access-service.ts` | Account override management |
| 3 | `packages/api/src/routes/context-tool-access.ts` | API endpoints |
| 4 | `apps/tamma-elsa/tests/.../ContextToolDefaultsTests.cs` | C# unit tests |
| 5 | `packages/api/src/services/__tests__/context-tool-access-service.test.ts` | TS unit tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/services/default-prompts.ts` | Add `contextTools` field to templates |
| 2 | `packages/api/src/services/prompt-store.ts` | Read/return `contextTools` field |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` | Resolve context tools per role |
| 4 | `packages/api/src/routes/index.ts` | Register context-tool-access routes |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Adding contextTools to all 80 templates is tedious | Generate from the defaults map; only set explicitly where different from role default |
| Account override schema not yet in DB | Use in-memory map initially; migrate to DB table when Epic 27 schema is deployed |
| Role names may change or be added | Defaults map returns all tools for unknown roles; log warning for unknown roles |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Task 1: Per-role defaults constant | 1 |
| Task 2: Prompt template contextTools field | 2 |
| Task 3: Account override service | 3 |
| Task 4: ResolveToolsActivity changes | 2 |
| Task 5: API endpoints (3 routes) | 2 |
| Task 6: Unit tests (8 tests) | 2 |
| **Total** | **12 hours** |

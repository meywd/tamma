# Finding 001: `POST /api/engine/execute-task` one-line stub with wrong DTO shape

**Scope**: engine
**Severity**: P0 (cutover-blocking)
**Status**: Not-yet-implemented (stub) — the DTO signature is also structurally wrong, so even a caller that happened to send the right field names would fail model binding.
**Estimated port effort**: 8–12h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/engine/engine-task-routes.ts`.

- File: `packages/api/src/routes/engine/engine-task-routes.ts:131-220`
- Contract: `POST /api/engine/execute-task` accepts a prompt + role + optional repo/model/budget, resolves an agent through the role-based resolver (with full provider chain fallback), executes the task, and returns the LLM output with cost accounting.
- Request body (`ExecuteTaskBodySchema`):

```typescript
// packages/api/src/routes/engine/engine-task-routes.ts:47-55 (9e9a57c~1)
const ExecuteTaskBodySchema = z.object({
  prompt: z.string().min(1),
  role: z.string().min(1).optional(),
  repository: z.string().optional(),
  enableTools: z.boolean().optional(),
  model: z.string().optional(),
  maxBudgetUsd: z.number().positive().optional(),
  cwd: z.string().optional(),
});
```

- Response (`ExecuteTaskResponse`):

```typescript
// packages/api/src/routes/engine/engine-task-routes.ts:63-71 (9e9a57c~1)
interface ExecuteTaskResponse {
  success: boolean;
  output: string;
  tokensUsed: number;
  costUsd: number;
  durationMs: number;
  toolCalls: number;
  error?: string;
}
```

- Behavior: resolves the agent via `IRoleBasedAgentResolver.getAgentForRole(role, ctx)`, builds an `AgentTaskConfig`, calls `agent.executeTask()`, logs `Agent task executed`, and returns the full response. When the resolver is not wired the endpoint returns 503 with an explanation.
- Dependencies: `@tamma/providers` (IRoleBasedAgentResolver), fastify logger, Zod validation.
- Tests: `packages/api/src/routes/engine/__tests__/engine-task-routes.test.ts` covered validation, 503 when resolver absent, agent task success path, and error path.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:93-94`
- Contract: one-line expression-bodied stub that ignores everything except `req.TaskType`, which does not exist in the request shape the Elsa activities send.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:93-94 (current)
public static Task<IResult> ExecuteTask(ExecuteTaskRequest req) =>
    Task.FromResult(Results.Ok(new { message = "Task execution started (stub)", taskType = req.TaskType }));
```

- DTO in `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:10`:

```csharp
public record ExecuteTaskRequest(string TaskType, object? Context);
```

- Dependencies: none. No agent provider, no resolver, no diagnostics, no cost accounting. The endpoint is not even aware of the LLM proxy surface next to it in `SaaSEndpoints`.

### Deployed Elsa activities that POST this endpoint

Every LLM-driven activity on the Elsa server constructs a request body with
`{prompt, role}` (sometimes + `analysisType`) and expects `{output, ...}` back.
These activities are not dead code — they are wired into live workflows under
`apps/tamma-elsa/src/Tamma.Workflows/`. Representative call sites:

```csharp
// apps/tamma-elsa/src/Tamma.Activities/TDD/WriteImplementationActivity.cs:165-172
var requestBody = new { prompt, role = "implementer" };
var response = await httpClient.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task", requestBody);
response.EnsureSuccessStatusCode();
var result = await response.Content.ReadFromJsonAsync<JsonElement>();
return result.GetProperty("output").GetString() ?? "{}";
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/TDD/WriteTestsActivity.cs:191-197
var requestBody = new { prompt, role = "tester" };
var response = await httpClient.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task", requestBody);
response.EnsureSuccessStatusCode();
var result = await response.Content.ReadFromJsonAsync<JsonElement>();
return result.GetProperty("output").GetString() ?? "{}";
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/Debug/RefineHypothesisActivity.cs:160-164
var response = await client.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task",
    new { prompt, analysisType = "debugging_refinement", role = "debugger" });
response.EnsureSuccessStatusCode();
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/Debug/WriteRegressionTestActivity.cs:165-168
var response = await client.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task",
    new { prompt, analysisType = "regression_test", role = "tester" });
```

`apps/tamma-elsa/src/Tamma.Activities/TDD/AnalyzeCodeActivity.cs`,
`ApplyRefactoringActivity.cs`, `RevertRefactoringActivity.cs`,
`CommitChangesActivity.cs`, `Debug/AIDiagnosisActivity.cs`,
`AI/ClaudeAnalysisActivity.cs`, and `ADL/ApplyReviewFixesActivity.cs` all follow
the same pattern (grep confirmed 11 activity files POST this endpoint).

- Tests: no C# tests under `Tamma.Api.Tests` target `ExecuteTask`. There are contract-free smoke tests that do not validate the DTO shape.

## 3. The gap

Concrete behavioral difference:

- TS did: accept `{prompt, role, analysisType?, ...}`, resolve a real agent by role, run an LLM, return `{success, output, costUsd, durationMs, tokensUsed, toolCalls, error?}`.
- C# does: accept `{taskType, context?}` (a shape no Elsa activity sends), do nothing, and return `{message: "Task execution started (stub)", taskType: null}`.

For a caller sending `{prompt: "...", role: "implementer"}`:

- TS: 200 with `{success: true, output: "...", costUsd: 0.023, ...}` and an executed LLM call.
- C#: ASP.NET Core reads `prompt` and `role` into properties that don't exist on `ExecuteTaskRequest`. `req.TaskType` is `null`, `req.Context` is `null`, and the response is `{message: "Task execution started (stub)", taskType: null}`. No LLM is called.

Downstream, each activity does `result.GetProperty("output").GetString()`. The C# stub does not return an `output` field at all, so every caller throws `KeyNotFoundException` / `InvalidOperationException` on property access. The Elsa workflow step fails, the workflow enters the error branch, and the whole job aborts before any real work happens.

Error paths:

- TS error path: 500 with `{success: false, output: "", costUsd: 0, durationMs, toolCalls: 0, error}` — structured error that the activity can surface.
- C# error path: no error path. Success-shaped response lacking the `output` field kills the activity with a deserialization / property-access error upstream.

Impact: this is the single highest-impact regression in the port. It breaks every LLM-driven workflow (TDD red/green/refactor, Debug hypothesis refinement, ADL review fixes, mentorship guidance, Claude analysis).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`. The story explicitly lists this endpoint in its "Solution — New API Routes" section:

> ```
> POST /api/engine/execute-task
>   Body: { prompt, role, repository, enableTools }
>   → Resolves agent via RoleBasedAgentResolver
>   → Executes with tool loop if enableTools=true
>   Returns: { output, tokensUsed, costUsd, toolCalls }
> ```

- Also referenced in `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md` and the Phase 1 impl plan, which enumerate the TS routes to port. The stub was knowingly left in place during Phase 1 cutover.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub).
- **What's needed to finish**:
  1. Replace the DTO with the real request shape: `ExecuteTaskRequest(string Prompt, string? Role, string? AnalysisType, string? Repository, bool? EnableTools, string? Model, double? MaxBudgetUsd, string? Cwd)`.
  2. Inject an agent provider / resolver. Two options:
     - Short-term: wire the `LlmProxyService` path (already implemented for SaaS `/api/v1/llm/chat`) and use it as the execute-task implementation. Returns no role routing but unblocks the workflows.
     - Long-term: port `IRoleBasedAgentResolver` from `@tamma/providers` and resolve per role with provider chain fallback.
  3. Return the documented shape `{success, output, costUsd, durationMs, tokensUsed, toolCalls, error?}` so the 11 deployed activities can do `result.GetProperty("output")` without crashing.
  4. Add cycle-time logging + cost accounting via `IDiagnosticsService` (symmetric with `LlmProxyService`).
- **Is it "just a stub" or is scope missing?** Both. The expression-bodied handler is a literal stub, but the underlying scope — agent resolution by role with provider chain fallback — was never ported. A correct fix must close both.
- **Blockers**: depends on finding 017 (LLM proxy shape) if we decide to reuse the LLM proxy path; depends on a broader port of `@tamma/providers` for full role-based resolution.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs` — rewrite `ExecuteTaskRequest` record.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:93` — replace stub.
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — register an `IExecuteTaskService` (new) or inject `ILlmProxyService`.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/IExecuteTaskService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/ExecuteTaskService.cs` (delegates to `ILlmProxyService` in the short-term).
- Tests to add:
  - `ExecuteTaskEndpoint_ReturnsOutput_WhenProviderSucceeds` — asserts `output` property is present and non-null.
  - `ExecuteTaskEndpoint_ReturnsStructuredError_OnProviderFailure` — asserts `{success:false, error, costUsd:0}` shape.
  - `ExecuteTaskEndpoint_AcceptsAllElsaActivityPayloads` — parameterised against `{prompt, role}`, `{prompt, role, analysisType}`, etc.
  - Contract test: fake HTTP server that replays every Elsa activity's `requestBody` literal (pulled from the grep list) to catch future drift.
- Estimated effort: 8–12h
  - DTO + endpoint rewrite: 2h
  - `ExecuteTaskService` wrapping `LlmProxyService`: 3h
  - Role routing (short-term role→model mapping): 2h
  - Tests: 3h

## References

- TS source: `packages/api/src/routes/engine/engine-task-routes.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:93`, `Dtos/Engine/EngineDtos.cs:10`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`; `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md`
- Related findings:
  - `002-agent-available-verb-mismatch.md` (sister endpoint)
  - `017-saas-llm-proxy-shape-drift.md` (alternate implementation vector)
- CLAUDE.md section: "Story 6-11: Context API Wiring" per the original TS file headers.

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: ff581af
- **Notes**: `ExecuteTaskRequest` DTO rebuilt to match the deployed Elsa
  payloads (`{prompt, role?, analysisType?, repository?, enableTools?, model?,
  maxBudgetUsd?, cwd?}`). New `IExecuteTaskService` /
  `ExecuteTaskService` delegates to the existing `ILlmProxyService`
  (per the finding's short-term recommendation) and returns the documented
  `{success, output, tokensUsed, costUsd, durationMs, toolCalls, error?}`
  shape on every path so the 11 deployed activities can read `output`
  without crashing. Role→system-prompt mapping is a placeholder until the
  full `IRoleBasedAgentResolver` ports from `@tamma/providers` (TODO marked
  in source: requires running Elsa engine for E2E, plus epic-1/story-1-10
  for the real role-based provider chain).

# Finding 011: `POST /api/engine/trigger-ci` stub

**Scope**: engine
**Severity**: P0 (cutover-blocking — intelligent test pipeline dead)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 2h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-github-routes.ts:445-505` (9e9a57c~1)
- Contract: `POST /api/engine/trigger-ci` body `{repository, branchName, workflowFile, inputs?}` → `client.rest.actions.createWorkflowDispatch(...)` → `{dispatched: true, workflowFile, branch}`.

```typescript
// packages/api/src/routes/engine/engine-github-routes.ts:478-494 (9e9a57c~1)
await client.rest.actions.createWorkflowDispatch({
  owner: parsed.owner,
  repo: parsed.repo,
  workflow_id: workflowFile,
  ref: branchName,
  ...(inputs !== undefined && Object.keys(inputs).length > 0 ? { inputs } : {}),
});
fastify.log.info({ repository, branchName, workflowFile }, 'CI workflow dispatched');
return reply.send({ dispatched: true, workflowFile, branch: branchName });
```

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:90-91`
- DTO: `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:9`

```csharp
// Dtos/Engine/EngineDtos.cs:9
public record TriggerCiRequest(string Repo, string Ref, string Workflow);

// EngineEndpoints.cs:90-91
public static Task<IResult> TriggerCi(TriggerCiRequest req) =>
    Task.FromResult(Results.Ok(new { message = "CI triggered (stub)", workflow = req.Workflow }));
```

DTO field names are wrong: deployed callers send `repository`/`branchName`/`workflowFile`; C# has `Repo`/`Ref`/`Workflow`. No `inputs` field.

### Deployed caller

```csharp
// apps/tamma-elsa/src/Tamma.Activities/Testing/TriggerCIActivity.cs:127-131
var response = await httpClient.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/trigger-ci", requestBody);
response.EnsureSuccessStatusCode();
```

`requestBody` is built earlier in the file with shape `{repository, branchName, workflowFile, inputs}`. The System.Text.Json binder will not map those field names to `Repo`/`Ref`/`Workflow`. All three DTO properties stay null.

## 3. The gap

- TS did: dispatched a GitHub Actions workflow on the specified branch with typed inputs.
- C# does: logs nothing, does nothing, returns `{message: "CI triggered (stub)", workflow: null}`.

For `TriggerCIActivity` requesting `ci.yml` on `feature-branch`:

- TS: GitHub Actions run kicks off. Build / test results eventually flow back.
- C#: no run. The entire "intelligent test execution pipeline" (Story 3-13) is inert — the workflow step that waits for CI results will time out because no CI was triggered.

Response shape also drifts: TS `{dispatched, workflowFile, branch}`; C# `{message, workflow}`. No deployed caller parses either shape (they just check `EnsureSuccessStatusCode()`), but any future polling-for-status logic will not know what to key on.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`. Also `docs/stories/epic-3/story-3-13/3-13-intelligent-test-execution-pipeline.md` — CI triggering is the entry point of that pipeline.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub) + DTO field-name drift.
- **What's needed to finish**:
  1. Rewrite `TriggerCiRequest` as `(string Repository, string BranchName, string WorkflowFile, Dictionary<string, string>? Inputs)`.
  2. Parse `owner/repo` from `Repository`.
  3. Call Octokit `Actions.Workflows.CreateDispatch(...)` or equivalent.
  4. Return `{dispatched: true, workflowFile, branch}`.
  5. Validate all three required fields non-empty — 400 otherwise.
- **Is it "just a stub" or is scope missing?** Both — DTO drift + missing Octokit wiring.
- **Blockers**: shared GitHub client (findings 005-011).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:9`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:90-91`
- Tests to add:
  - `TriggerCi_BindsRepositoryBranchWorkflow_FromCamelCase`
  - `TriggerCi_DispatchesWorkflow_CallsOctokit`
  - `TriggerCi_IncludesInputs_WhenProvided`
  - `TriggerCi_ReturnsDispatchedShape`
  - `TriggerCi_ValidatesRequiredFields`
- Estimated effort: 2h — DTO + handler 30m, Octokit dispatch 30m, tests 1h.

## References

- TS source: `packages/api/src/routes/engine/engine-github-routes.ts:445-505`
- Deployed caller: `apps/tamma-elsa/src/Tamma.Activities/Testing/TriggerCIActivity.cs:127-131`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:90-91`, `Dtos/Engine/EngineDtos.cs:9`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`, `docs/stories/epic-3/story-3-13/3-13-intelligent-test-execution-pipeline.md`
- Related findings: 005-011

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `2c2cdfa` (engine wiring); depends on `4e1e0e4` (Octokit client)
- **Notes**: `OctokitGitHubEngineCallbackService.TriggerCiAsync` builds a
  `CreateWorkflowDispatch(branchName)` with any `Inputs` appended and calls
  `Octokit.Actions.Workflows.CreateDispatch(owner, repo, workflowFile, dispatch)`.
  Returns the `{dispatched: true, workflowFile, branch}` shape. CI actually
  fires now — the intelligent test pipeline (Story 3-13) can resume end-
  to-end.

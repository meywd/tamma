# Finding 018: SaaS `POST /workflows/:id/status` drops `step`, `progress`, `message`

**Scope**: engine (SaaS)
**Severity**: P1 (feature broken — dashboard "current step" indicator stuck)
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/saas/workflow-status.ts` (9e9a57c~1)

```typescript
// packages/api/src/routes/saas/workflow-status.ts:14-20 (9e9a57c~1)
const WorkflowStatusBodySchema = z.object({
  status: z.string().min(1),
  step: z.string().min(1),
  progress: z.number().min(0).max(100).optional(),
  message: z.string().optional(),
});
```

```typescript
// packages/api/src/routes/saas/workflow-status.ts:55-72 (9e9a57c~1)
const variables: Record<string, unknown> = {
  ...existing.variables,
  lastStep: step,
  lastStatus: status,
};
if (progress !== undefined) variables['progress'] = progress;
if (message !== undefined) variables['message'] = message;

const updated = await options.workflowStore.updateInstance(id, {
  status,
  currentActivity: step,
  variables,
});

return reply.send({ ok: true, workflowId: id, status: updated?.status ?? status, step });
```

Contract: engine posts granular progress updates. `step` is required. `progress` (0–100%) and `message` are optional. All four fields are stored so the dashboard can render "Running: CodeGeneration (42% — writing tests)".

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:87-111`

```csharp
// SaaSEndpoints.cs:87-91 (current)
public sealed class WorkflowStatusRequestDto
{
    public string Status { get; set; } = string.Empty;
    public JsonElement? Variables { get; set; }
}

// SaaSEndpoints.cs:101-110 (current)
var result = await lifecycle.UpdateStatusAsync(id, body.Status, body.Variables);
// ...
return Results.Ok(new { ok = true, workflowId = id, status = body.Status });
```

The DTO has only `Status` and `Variables`. No `Step`, no `Progress`, no `Message`.

## 3. The gap

- TS did: stored `step`, `status`, optional `progress`, optional `message` on the instance, updated `currentActivity`.
- C# does: accepts only `status` + `variables` blob. If the caller wants to supply step/progress/message, they have to bury them inside the opaque `variables` JSON.

For an engine posting `{status: "running", step: "CodeGeneration", progress: 42, message: "writing tests"}`:

- TS: instance updated with `currentActivity: "CodeGeneration"`, `variables.progress: 42`, `variables.message: "writing tests"`. Dashboard displays it all.
- C#: `step`, `progress`, `message` are silently discarded (the DTO doesn't bind them). `currentActivity` stays on whatever it was before. Dashboard freezes on the last step name.

Follow-on: the dashboard relies on `currentActivity` to render "what is this workflow doing right now?". With this endpoint stripped of `step`, the `currentActivity` column on `workflow_instances` only updates when some *other* code path sets it — which doesn't happen in the SaaS flow.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md` (dashboard consumers). Also cross-ref `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift — DTO reduction.
- **What's needed to finish**:
  1. Add `Step`, `Progress?` (0–100 range validation), `Message?` to `WorkflowStatusRequestDto`.
  2. Pass them to `IWorkflowLifecycleService.UpdateStatusAsync`.
  3. Lifecycle service must set `WorkflowInstance.CurrentActivity = step` and merge `progress`/`message` into `Variables` JSON.
  4. Return `{ok, workflowId, status, step}` to match TS.
- **Is it "just a stub" or is scope missing?** Scope was reduced during port. Mechanical to restore.
- **Blockers**: none.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:87-111`
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/IWorkflowLifecycleService.cs` + impl.
- Tests to add:
  - `UpdateWorkflowStatus_RequiresStep` — 400 when missing.
  - `UpdateWorkflowStatus_UpdatesCurrentActivity`
  - `UpdateWorkflowStatus_MergesProgressAndMessage_IntoVariables`
  - `UpdateWorkflowStatus_ProgressRangeValidation` — <0 or >100 → 400.
- Estimated effort: 3h — DTO + service changes 1h, tests 2h.

## References

- TS source: `packages/api/src/routes/saas/workflow-status.ts`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:87-111`, `Services/SaaS/WorkflowLifecycleService.cs`
- Story: `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`, `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md`
- Related findings: `019-saas-workflow-result-tri-to-binary.md`

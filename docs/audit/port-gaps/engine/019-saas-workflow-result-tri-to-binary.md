# Finding 019: SaaS `POST /workflows/:id/result` collapses `completed|failed|cancelled` → binary success/failed

**Scope**: engine (SaaS)
**Severity**: P1 (feature broken — failure metrics inflated, cancelled runs misclassified)
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/saas/workflow-result.ts` (9e9a57c~1)

```typescript
// packages/api/src/routes/saas/workflow-result.ts:13-18 (9e9a57c~1)
const WorkflowResultBodySchema = z.object({
  status: z.enum(['completed', 'failed', 'cancelled']),
  prNumber: z.number().int().positive().optional(),
  error: z.string().optional(),
  duration: z.number().nonnegative(),
});
```

Three terminal states, each with semantic meaning:

- `completed` — workflow finished successfully, possibly with a PR number.
- `failed` — workflow died due to an error. `error` field populated.
- `cancelled` — user cancelled, or system cancelled due to budget / timeout / supersession.

Structured fields (`prNumber`, `error`, `duration`) are stored in `instance.variables` for downstream dashboards.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:115-151`

```csharp
// SaaSEndpoints.cs:115-151 (current, excerpted)
public sealed class WorkflowResultRequestDto
{
    public string Status { get; set; } = string.Empty;
    public JsonElement? Result { get; set; }
}

// ...
var success = string.Equals(body.Status, "completed", StringComparison.OrdinalIgnoreCase);
var payload = body.Result ?? JsonDocument.Parse("{}").RootElement;
var outcome = await lifecycle.RecordResultAsync(id, payload, success);
// ...
return Results.Ok(new
{
    ok = true,
    workflowId = id,
    status = success ? "completed" : "failed"
});
```

Three observations:
1. The DTO is `{Status, Result?}` with `Status` as a free-form string (no enum validation). `Result` is an opaque JSON blob.
2. The service API is `RecordResultAsync(id, payload, bool success)` — the three-way status is crushed to a bool at the endpoint boundary.
3. The response echoes `"completed"` or `"failed"` — never `"cancelled"`. If the caller posted `{status: "cancelled"}`, the response says `"failed"`. Metrics that count failures will include cancellations.

## 3. The gap

- TS did: three-way terminal state with typed structured fields.
- C# does: binary success/failed, opaque payload, free-form status input.

For a caller posting `{status: "cancelled", duration: 12000}`:

- TS: instance status `cancelled`, `finalStatus` variable `cancelled`, dashboard category "Cancelled".
- C#: status `failed` in the response, `payload.success = false`, dashboard category "Failed". Cancelled workflows inflate the failure rate SLA metric.

For typed fields:

- TS: `prNumber`, `error`, `duration` stored as first-class keys on `variables`.
- C#: caller must smuggle them inside `result`, and there is no schema guarantee that any specific field exists.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md` (dashboard needs to distinguish cancelled from failed). Also cross-ref `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression — metrics, audit, dashboard all affected)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift — loss of type information at the endpoint boundary.
- **What's needed to finish**:
  1. Rewrite `WorkflowResultRequestDto` as `(string Status, int? PrNumber, string? Error, long Duration)` with `Status` validated as `completed|failed|cancelled`.
  2. Service API should accept the terminal state enum directly, not a bool.
  3. Persist the status to `WorkflowInstance.Status` verbatim.
  4. Store structured fields in `Variables`.
  5. Response echoes the caller's status (not a coerced bool).
- **Is it "just a stub" or is scope missing?** Semantic reduction. Mechanical to fix.
- **Blockers**: none.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:115-151`
  - `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/IWorkflowLifecycleService.cs` + impl — accept enum or string terminal state.
- Tests to add:
  - `PostWorkflowResult_AcceptsCancelled_StoresAsCancelled`
  - `PostWorkflowResult_RejectsUnknownStatus` — 400.
  - `PostWorkflowResult_ExtractsPrNumberIntoVariables`
  - `PostWorkflowResult_ExtractsErrorIntoVariables`
  - `PostWorkflowResult_Metrics_CancelledDoesNotIncrementFailureCount` (if metrics wired).
- Estimated effort: 3h — DTO + service 1h, tests 2h.

## References

- TS source: `packages/api/src/routes/saas/workflow-result.ts`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SaaSEndpoints.cs:115-151`
- Story: `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`, `docs/stories/epic-19/19-1-api-consolidation-to-csharp.md`
- Related findings: `018-saas-workflow-status-drops-fields.md` (sister endpoint)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: c9dd51e
- **Notes**: `WorkflowResultRequestDto` now exposes
  `(Status, PrNumber?, Error?, Duration?, Result?)` with `Status`
  validated against `{completed, failed, cancelled}` (400 on anything
  else). `IWorkflowLifecycleService.RecordResultAsync(string
  terminalStatus)` replaces the bool overload; service emits
  `WORKFLOW.{COMPLETED|FAILED|CANCELLED}` event types and persists the
  tri-state status verbatim on `WorkflowInstance.Status`. Cancelled
  runs no longer inflate the failure-rate SLA metric.

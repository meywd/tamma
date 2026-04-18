# Finding 010: No batch diagnostics ingest — TS accepted arrays up to 100

**Scope**: providers
**Severity**: P2 (correctness / throughput)
**Status**: Incomplete (single-row path ported, array path dropped)
**Estimated port effort**: 1–2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/settings/diagnostics-ingest-routes.ts`.

- File: `packages/api/src/routes/settings/diagnostics-ingest-routes.ts:20-39`
- Contract/behavior: `POST /diagnostics` accepted **either a single record or an array**. The store validated + upsized protection at `MAX_BATCH_SIZE = 100` (see `diagnostics-store.ts:36` and `pg-diagnostics-store.ts:36`).

```typescript
// packages/api/src/routes/settings/diagnostics-ingest-routes.ts (9e9a57c~1) — lines 20-39
app.post('/diagnostics', async (request, reply) => {
  try {
    const body = request.body;
    if (!body || typeof body !== 'object') {
      return reply.status(400).send({ error: 'Request body must be a JSON object or array' });
    }
    const inputs: DiagnosticsRecordInput[] = Array.isArray(body) ? body : [body as DiagnosticsRecordInput];
    if (inputs.length === 0) {
      return reply.status(400).send({ error: 'At least one diagnostics record is required' });
    }
    const recorded = await store.insert(inputs);
    return reply.status(201).send({ recorded });
  } catch (error) {
    const message = error instanceof Error ? error.message : 'Failed to record diagnostics';
    return reply.status(400).send({ error: message });
  }
});
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:272-290`
- Contract/behavior: A single `IngestDiagnosticRequest` per call. No array handling. The DTO binds one record; there is no overload or array body.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs — lines 272-290
public static async Task<IResult> IngestDiagnostic(
    IngestDiagnosticRequest req,
    [FromServices] IDiagnosticsService service,
    [FromServices] ITenantContext tc)
{
    var diag = new ProviderDiagnostic
    {
        ProviderKey = req.ProviderKey,
        RequestDurationMs = req.DurationMs,
        TokensUsed = req.TokensUsed,
        Cost = req.Cost,
        Model = req.Model,
        Success = req.Success,
        ErrorMessage = req.Error,
        TenantId = tc.TenantId
    };
    var id = await service.RecordEventAsync(diag);
    return Results.Created($"/api/providers/diagnostics/{id}", new { id });
}
```

- `DiagnosticsRepository.InsertAsync` takes a single entity; no `InsertRangeAsync` variant.

## 3. The gap

- TS: a workflow that accumulates a burst of 50 tool-invocations can send one HTTP POST.
- C#: same workflow has to make 50 POSTs — 50x HTTP overhead, 50x latency, 50x authz checks.
- For a caller sending `POST /api/providers/diagnostics` with body `[{...}, {...}]`:
  - TS: `201 {recorded: 2}`.
  - C#: `400 Bad Request` or `415 Unsupported Media Type` depending on model-binder behaviour with an array sent to a single-record DTO.

Error paths:
- TS: `400` if array empty, `400` if > 100 records (`Batch size ${inputs.length} exceeds max ${MAX_BATCH_SIZE}`).
- C#: model-bind failure; no explicit array handling.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`.
- Story 9-2 AC 2: "`POST /api/v1/diagnostics` — record a diagnostics event (used by Elsa and any external caller)." Singular "event". Does not require batch ingest.
- Story alignment:
  - [ ] Matches TS behavior (TS was ahead of spec).
  - [x] Matches C# behavior (story spec matches).
  - [ ] Describes a third behavior.
  - [ ] No story — there is a story and the C# behavior is conformant.

This is not technically a spec violation, but it is a throughput/latency regression that any caller used to the TS shape will hit. Elsa's `RecordDiagnosticsActivity` in the new world must make N calls where TS made 1.

## 5. Status

- **Classification**: Incomplete / behavioral drift.
- **What's needed to finish**:
  1. Add a parallel endpoint `POST /api/providers/diagnostics/batch` (cleanest) or accept either shape at `POST /api/providers/diagnostics` (TS-compatible).
  2. Add `DiagnosticsRepository.InsertRangeAsync(IEnumerable<ProviderDiagnostic>)` that uses `DbContext.AddRange` + single `SaveChangesAsync`.
  3. Cap batch size at 100 to match TS. Return `400` if exceeded.
- **Is it "just a stub" or is scope missing?** Scope intentionally narrowed — the story asked for a singular endpoint and the C# impl delivered exactly that. Restore batch capability for parity with TS callers.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:272-290`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/DiagnosticsRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/IDiagnosticsService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (add batch route)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Providers/IngestDiagnosticBatchRequest.cs`
- Tests to add:
  - `IngestDiagnosticBatch_AcceptsArrayOfUpTo100_InsertsAll`
  - `IngestDiagnosticBatch_Over100_Returns400`
  - `IngestDiagnosticBatch_EmptyArray_Returns400`
- Estimated effort: 2h.

## References

- TS source: `packages/api/src/routes/settings/diagnostics-ingest-routes.ts:20-39`, `packages/api/src/services/pg-diagnostics-store.ts:57-99`, constant `MAX_BATCH_SIZE = 100` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:272-290`
- Story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md` AC 2
- Related findings: `008-diagnostics-taxonomy-collapsed.md`

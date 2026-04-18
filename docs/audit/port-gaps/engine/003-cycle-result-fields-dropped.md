# Finding 003: `POST /api/engine/cycle-result` drops `exitReason`, `error`, `durationMs`

**Scope**: engine
**Severity**: P1 (feature broken — cycle audit truncated)
**Status**: Behavioral drift (ported but DTO collapses structured fields into opaque `Result`).
**Estimated port effort**: 2h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-task-routes.ts:220-295` (9e9a57c~1)
- Contract: `POST /api/engine/cycle-result` accepts a structured cycle completion record and stores it for audit / dashboard retrieval.

```typescript
// packages/api/src/routes/engine/engine-task-routes.ts:56-66 (9e9a57c~1)
const CycleResultBodySchema = z.object({
  exitReason: z.string().min(1),
  issueNumber: z.number().int().optional(),
  repository: z.string().optional(),
  error: z.string().optional(),
  durationMs: z.number().optional(),
  metadata: z.record(z.string(), z.unknown()).optional(),
});
```

Each field is stored verbatim on a `CycleResultEntry` and can be retrieved later via `GET /api/engine/cycle-results`. The exit reason is the primary classification (`"success"`, `"pr_merged"`, `"abandoned"`, `"timeout"`, etc.) and the `error` carries the failure message when the cycle died. `durationMs` lets the dashboard compute wall-clock cycle duration.

- Tests: `packages/api/src/routes/engine/__tests__/engine-task-routes.test.ts` covered valid/invalid bodies and round-tripping of `exitReason`/`error`.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:96-106`
- DTO: `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:11`

```csharp
// apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:11
public record CycleResultRequest(int IssueNumber, object Result);
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:96-106
public static async Task<IResult> PostCycleResult(
    CycleResultRequest req, IEventRepository eventRepo, ITenantContext tc)
{
    await eventRepo.AppendAsync(new DomainEvent
    {
        Type = "CYCLE.RESULT",
        TenantId = tc.TenantId,
        IssueNumber = req.IssueNumber,
        Data = System.Text.Json.JsonSerializer.Serialize(req.Result)
    });
    return Results.Ok(new { message = "Cycle result stored" });
}
```

The C# DTO collapses all the structured fields (`exitReason`, `error`, `repository`, `durationMs`, `metadata`) into a single opaque `object Result`. That's not how the deployed Elsa activity sends them.

### Deployed Elsa activity POST body

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/ReportCycleResultActivity.cs:68-77
var httpClient = _httpClientFactory.CreateClient();
var payload = new
{
    exitReason = reason,
    issueNumber,
    error,
    timestamp = DateTime.UtcNow,
};
await httpClient.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/cycle-result", payload);
```

`ReportCycleResultActivity` sends `{exitReason, issueNumber, error, timestamp}` as top-level fields. ASP.NET Core's default System.Text.Json binding will not populate `CycleResultRequest.Result` from these top-level fields — `Result` is an entirely separate object property. `IssueNumber` binds correctly; everything else is silently discarded.

- Tests: no C# test asserts `exitReason` is persisted.

## 3. The gap

- TS did: bound `{exitReason, issueNumber, error, durationMs, repository, metadata}` into a typed entry, returned `{id, storedAt}`, supported later retrieval via `GET /cycle-results`.
- C# does: stores a single JSON blob under `DomainEvent.Data` with only the fields the activity happens to pass inside a nested object called `Result` — which is never populated because the activity does not send one.

For the deployed `ReportCycleResultActivity` sending `{exitReason: "abandoned", issueNumber: 42, error: "CI failed"}`:

- TS: 201 `{id: "cr-...", storedAt: "..."}` + audit event with full structured data.
- C#: 200 `{message: "Cycle result stored"}` + `DomainEvent` whose `Data` field is `"null"` (the serialization of `req.Result` when no `Result` object is sent). The exit reason and error are lost entirely.

Downstream impact: dashboards querying cycle history see only `{issueNumber}`. Failure classification ("why did this cycle die?") is gone. SLA dashboards that compute mean-time-to-success from `durationMs` have no data. Abandonment vs. completion cannot be distinguished.

Error paths:

- TS: 400 with Zod error if `exitReason` missing.
- C#: 200 always, because the DTO's only required field is `IssueNumber` (int) which defaults to 0.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md` (lists `/cycle-result` under context API wiring). The original TS file also references Story 6-11 in its module docblock.
- Also indirectly `docs/stories/epic-10/story-10-2/10-2-comprehensive-event-catalog-and-typed-schema.md` — the cycle result is one of the canonical events whose schema is defined.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift — the endpoint exists and persists *something*, but the contract with deployed clients was silently broken.
- **What's needed to finish**:
  1. Rewrite `CycleResultRequest` as `(string ExitReason, int? IssueNumber, string? Repository, string? Error, long? DurationMs, Dictionary<string, JsonElement>? Metadata)`.
  2. Validate `ExitReason` is non-empty; return 400 otherwise.
  3. Serialize all fields (not just the outer `Result` property) into `DomainEvent.Data` under a typed shape.
  4. Add `GET /api/engine/cycle-results` (currently present) but ensure it maps the new fields back out.
- **Is it "just a stub" or is scope missing?** Scope was ported but DTO was collapsed. Fix is mechanical.
- **Blockers**: none.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:11`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:96-112`
- Tests to add:
  - `PostCycleResult_PersistsExitReason` — fixture sends the literal `ReportCycleResultActivity` payload; asserts `DomainEvent.Data` round-trips `exitReason` and `error`.
  - `PostCycleResult_Rejects_WhenExitReasonMissing` — 400.
  - `GetCycleResults_ReturnsStructuredFields` — asserts response shape includes `exitReason`, `error`, `durationMs`.
- Estimated effort: 2h
  - DTO + endpoint: 30m
  - Update `GetCycleResults` mapping: 30m
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/engine/engine-task-routes.ts:220-295`
- Deployed caller: `apps/tamma-elsa/src/Tamma.Activities/ADL/ReportCycleResultActivity.cs:68-83`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:96-112`, `Dtos/Engine/EngineDtos.cs:11`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- Related findings: `001-execute-task-stub.md`

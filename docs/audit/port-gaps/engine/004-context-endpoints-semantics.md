# Finding 004: Context endpoints (`store-context` / `GET context/:issueNumber` / `query-context`) return empty results

**Scope**: engine
**Severity**: P1 (feature broken — RAG / context retrieval is dead)
**Status**: Incomplete (partial port — storage exists but retrieval + query never ported).
**Estimated port effort**: 6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/engine/engine-context-routes.ts`.

- Three endpoints:

```
POST /api/engine/store-context      — store findings JSON for an issue
GET  /api/engine/context/:issueNumber — retrieve stored context by issue number
POST /api/engine/query-context      — simplified RAG query over stored context
```

Key behaviors:

- `store-context`: Zod-validated body `{repository, issueNumber, findings: Record<string, unknown>}`, stored in an in-memory `Map` keyed by `${repository}:${issueNumber}`, returns `{contextIds: [...], storedAt}`.
- `GET context/:issueNumber`: exact lookup by `(repository, issueNumber)` when the query includes `repository`, otherwise scans for the first matching issue number. Returns `{findings, contextIds, storedAt}` or 404.
- `query-context`: takes `{repository, issueNumber, query, role?, maxTokens?}`, filters findings by role, scores by simple term-match ratio, applies a 4-char-per-token budget, and returns `{chunks: [{content, role, score}], totalTokens}`.

```typescript
// packages/api/src/routes/engine/engine-context-routes.ts:214-237 (9e9a57c~1) — query-context scoring
const contentLower = content.toLowerCase();
const queryTerms = queryLower.split(/\s+/);
const matchCount = queryTerms.filter((term) => contentLower.includes(term)).length;
const score = queryTerms.length > 0 ? matchCount / queryTerms.length : 0;
chunks.push({ content, role: findingRole, score });
```

Story 6-11 calls this a "simplified RAG" — the full pipeline (embeddings + vector store) is under `@tamma/intelligence`, not yet exposed here. This endpoint is the MVP that unblocks workflows until the real RAG pipeline is wired.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:46-67`

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:46-56
public static async Task<IResult> StoreContext(
    StoreContextRequest req, IEventRepository eventRepo, ITenantContext tc)
{
    await eventRepo.AppendAsync(new DomainEvent
    {
        Type = "CONTEXT.STORED",
        TenantId = tc.TenantId,
        IssueNumber = req.IssueNumber,
        Data = System.Text.Json.JsonSerializer.Serialize(req.Context)
    });
    return Results.Ok(new { message = "Context stored" });
}

// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:58-64
public static async Task<IResult> GetContext(
    int issueNumber, IEventRepository eventRepo, ITenantContext tc)
{
    var events = await eventRepo.QueryAsync(tc.TenantId, "CONTEXT.STORED", issueNumber, 1);
    return events.Count > 0
        ? Results.Ok(new { issueNumber, context = events[0].Data })
        : Results.NotFound(new { error = "No context found" });
}

// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:66-67
public static Task<IResult> QueryContext(QueryContextRequest req) =>
    Task.FromResult(Results.Ok(new { query = req.Query, results = Array.Empty<object>() }));
```

- `StoreContextRequest(int IssueNumber, object Context)` — no `repository` field.
- `QueryContextRequest(string Query)` — no `role`, no `maxTokens`, no `repository`, no `issueNumber`.

### Deployed Elsa activities that call these endpoints

```csharp
// apps/tamma-elsa/src/Tamma.Activities/Context/StoreFindingsActivity.cs:99-105
var response = await httpClient.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/store-context",
    new
    {
        repository,
        issueNumber,
        findings,
    });
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/Context/StoreRoleFindingActivity.cs:79-88
var response = await httpClient.PostAsJsonAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/store-context",
    new
    {
        repository,
        issueNumber,
        role,
        finding,
    });
```

Both call `store-context` with `{repository, issueNumber, findings}` (or `{role, finding}`). The C# DTO has no `Repository` / `Role` / `Findings` properties — `req.Context` stays null, the persisted event's `Data` is `"null"`.

## 3. The gap

- **store-context**: TS persisted a keyed map entry with full findings. C# persists an event with `Data = "null"` because the DTO's `Context` property is never populated.
- **GET context/:issueNumber**: TS returned `{findings, contextIds, storedAt}`. C# returns `{issueNumber, context: "null"}` (because the persisted blob is null).
- **query-context**: TS implemented a RAG-lite scoring loop. C# returns `{query, results: []}` unconditionally.

For the `StoreFindingsActivity` sending `{repository: "owner/repo", issueNumber: 42, findings: {dev: "...", security: "..."}}`:

- TS: 200 `{contextIds: ["ctx-...", "ctx-..."], storedAt: "..."}`, keyed by `"owner/repo:42"`.
- C#: 200 `{message: "Context stored"}`, but `DomainEvent.Data = "null"`. Subsequent `GET /context/42` returns `{issueNumber: 42, context: "null"}`.

For a `query-context` call requesting role=`"security"` with query=`"auth vuln"`:

- TS: scored chunks where the security finding mentioned those terms.
- C#: empty array. Every scrum-master / role-based workflow step that relies on RAG filtering sees no context.

Error paths:

- TS 400 when `findings` is empty or `repository` is missing.
- C# never 400s — the DTO accepts anything and silently persists nothing.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md` explicitly lists all three endpoints with their exact contract shapes. The story also notes the full RAG pipeline lives in `@tamma/intelligence` and should eventually replace the in-memory simplified version.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete (partial port). `store-context` and `GET context` are half-ported (endpoints + event append exist, but DTOs are wrong). `query-context` is a one-line stub.
- **What's needed to finish**:
  1. Rewrite `StoreContextRequest` as `(string Repository, int IssueNumber, JsonElement Findings)`.
  2. Persist the full findings (not just `req.Context`) and compute a `contextIds` array derived from finding keys.
  3. Rewrite `QueryContextRequest` as `(string Repository, int IssueNumber, string Query, string? Role, int? MaxTokens)`.
  4. Implement the scoring loop from `engine-context-routes.ts:214-237`.
  5. `GET context` should accept optional `?repository=` query like TS — otherwise scan by issue number.
- **Is it "just a stub" or is scope missing?** Scope was understood and partially ported. The RAG-lite implementation is mechanical to port. The long-term path (wiring to the real `@tamma/intelligence` pipeline) is scope-level and out of this finding.
- **Blockers**: none for the MVP. Real RAG depends on porting `@tamma/intelligence` (out of scope).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:4-5` — rewrite `StoreContextRequest`, `QueryContextRequest`.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:46-67` — all three handlers.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/IContextStore.cs` (abstraction over in-memory + future DB-backed impl).
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/InMemoryContextStore.cs`.
- Tests to add:
  - `StoreContext_RoundTripsFindings` — fixture sends literal `StoreFindingsActivity` payload.
  - `GetContext_ExactMatchByRepoAndIssue`
  - `GetContext_FallbackScanByIssueNumber`
  - `QueryContext_RoleFilter_ReturnsOnlyMatchingRole`
  - `QueryContext_TokenBudget_TruncatesChunks`
- Estimated effort: 6h
  - DTOs + handlers: 2h
  - `InMemoryContextStore` port: 1h
  - Scoring loop port: 1h
  - Tests: 2h

## References

- TS source: `packages/api/src/routes/engine/engine-context-routes.ts`
- Deployed callers: `apps/tamma-elsa/src/Tamma.Activities/Context/StoreFindingsActivity.cs`, `StoreRoleFindingActivity.cs`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:46-67`, `Dtos/Engine/EngineDtos.cs:4-5`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- Related findings: `005-repo-config-stub.md` (sister endpoint from the same routes file), `001-execute-task-stub.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: ff581af
- **Notes**: `IContextStore` + thread-safe `InMemoryContextStore` port the
  TS keyed-map store. `StoreContext` accepts both the
  `StoreFindingsActivity` payload `{repository, issueNumber, findings}` and
  the `StoreRoleFindingActivity` shape `{repository, issueNumber, role,
  finding}`, normalising both to `{role: content}`. `GetContext` honours
  the optional `?repository=` query and falls back to issue-number scan
  otherwise. `QueryContext` implements the term-match scoring loop with
  optional role filter and 4-char-per-token budget. Real RAG pipeline
  (`@tamma/intelligence` port) is the long-term replacement.

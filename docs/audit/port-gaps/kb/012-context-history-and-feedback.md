# Finding 012: `ContextTestingService` — empty history, in-process feedback Map, hard-coded config

**Scope**: kb
**Severity**: P2 (observability, feedback signal loss)
**Status**: Not-yet-implemented (context aggregator never wired; feedback persistence absent)
**Estimated port effort**: 4-5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/ContextTestingService.ts`.

The deleted TS `ContextTestingService` followed the same null-fallback pattern. It provided:
- `getHistory(limit)` — recent context queries (from real aggregator or empty list).
- `submitFeedback(feedback)` — thumbs-up/down / notes on a past query.
- `getConfig()` — current context-aggregation config (max tokens, dedup, strategy).
- `testContext(request)` — on-demand context aggregation test.

Feedback was stored in an in-process `Map<requestId, ContextFeedback[]>`. This was flagged as a known TODO in the TS source comments ("should persist to feedback store when available").

- Dependencies: `IContextAggregator` from `@tamma/intelligence/context/`.
- Tests: `packages/api/src/__tests__/services/knowledge-base/ContextTestingService.test.ts`.

## 2. What's in C#

### C# side
Three endpoints forwarded:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:165-180 (current)
public static async Task<IResult> GetContextHistory(
    [FromServices] IIntelligenceHttpClient client,
    [FromQuery(Name = "limit")] int? limit,
    CancellationToken ct)
    => Results.Ok(await client.GetContextHistoryAsync(limit, ct));

public static async Task<IResult> PostContextFeedback(
    [FromServices] IIntelligenceHttpClient client,
    [FromBody] ContextFeedbackRequest body,
    CancellationToken ct)
    => Results.Ok(await client.PostContextFeedbackAsync(body, ct));

public static async Task<IResult> GetContextConfig(
    [FromServices] IIntelligenceHttpClient client,
    CancellationToken ct)
    => Results.Ok(await client.GetContextConfigAsync(ct));
```

### Sidecar side

```typescript
// packages/intelligence-server/src/services/ContextTestingService.ts:34-59 (current)
export class ContextTestingService {
  private readonly aggregator: IContextAggregatorAdapter | null;
  private history: ContextHistoryEntry[] = [];
  private feedback: Map<string, ContextFeedbackRequest> = new Map();
  private config: ContextConfigResponse = {
    maxTokens: 100000,
    strategy: 'sliding_window',
    deduplication: true,
  };

  constructor(aggregator?: IContextAggregatorAdapter) {
    this.aggregator = aggregator ?? null;
  }

  async getHistory(limit = 50): Promise<ContextHistoryResponse> {
    return { history: this.history.slice(0, limit) };
  }

  async submitFeedback(req: ContextFeedbackRequest): Promise<{ message: string }> {
    this.feedback.set(req.requestId, req);
    return { message: 'Feedback recorded' };
  }

  async getConfig(): Promise<ContextConfigResponse> {
    return { ...this.config };
  }
  // ...
}
```

Three distinct issues in one class:

1. **`history` is only populated via an in-process `recordTest` helper** (line 65) that tests call directly. No production path appends to `history`. The live `/kb/context/history` endpoint always returns `[]`.
2. **`feedback` is an in-process Map** that is lost on container restart. Unlike the TS version's TODO comment, the sidecar silently swallows this.
3. **`config` is hard-coded** (`maxTokens: 100000`, `strategy: 'sliding_window'`). The endpoint returns this literal every time; `updateContextConfig` does not exist (there's no PUT route — only GET `/kb/context/config`).

- Dependencies: `IContextAggregatorAdapter` (narrow). Never constructed in production (#001).
- Tests: unit tests call `recordTest()` directly to populate history, which is why they "pass" despite the production no-op.

## 3. The gap

- TS did: same three issues. Feedback was in-process. Config was hard-coded. History required a real aggregator. These were pre-existing shortcuts.
- C# + sidecar does: same three issues, preserved through the port.

For a dashboard user:
- `GET /api/kb/context/history` — empty list. UI shows "No query history yet" forever.
- `POST /api/kb/context/feedback` with `{ "requestId": "abc", "helpful": true }` — 200 OK, message "Feedback recorded". Feedback disappears on next sidecar restart.
- `GET /api/kb/context/config` — same hard-coded `{maxTokens: 100000, strategy: 'sliding_window', ...}` regardless of runtime state.

For a user trying to tune context aggregation:
- No PUT endpoint — can't change config at runtime.
- No feedback persistence — can't run an A/B comparison across sessions.

Error paths:
- Neither TS nor sidecar raise errors. All three endpoints are 200-OK with no-op semantics.

Secondary schema drift: the TS version exposed `testContext(request)` as a dedicated endpoint (`POST /api/knowledge-base/context/test`). The sidecar has the method (`runQuery` on line 75) but **no route** exposes it. The 3 C# routes (history/feedback/config) cover a subset of what TS offered.

## 4. Gap from stories

`docs/stories/epic-6/story-6-5/6-5-context-aggregator.md` is the covering story. Combined with `docs/stories/epic-12/12-3-context-compaction.md` (context compaction), the spec calls for:
- Runtime-configurable aggregation parameters.
- Per-query feedback capture for quality signals.
- Dashboard visibility into historical queries.

Story alignment:
- [x] Matches TS behavior (both fall short of the story; port preserved shortcuts)
- [x] Matches C# behavior (same)
- [ ] Describes a third behavior
- [x] Partial — story exists but implementation is incomplete in both TS and sidecar.

## 5. Status

- **Classification**: Not-yet-implemented (and data-model regression on config mutability).
- **What's needed to finish**:
  1. Wire real `IContextAggregator` in composition root.
  2. Add `PUT /kb/context/config` route + C# endpoint + DTO (expands contract).
  3. Persist feedback to Postgres: `CREATE TABLE context_feedback (request_id UUID, helpful BOOL, notes TEXT, user_id TEXT, created_at TIMESTAMPTZ)`.
  4. Populate `history` from a Postgres table written during live context aggregation calls (requires the aggregator to emit a history event per call — likely an event-sourcing hook per CLAUDE.md DCB pattern).
  5. Re-expose `POST /kb/context/test` (the `runQuery` method currently has no route).
- **Is it "just a stub" or is scope missing?** Both — most scope is specified by story 6-5 but implementation was left incomplete (TS version had the same TODO). The missing config PUT and test endpoint indicate the sidecar contract drifted vs TS as well.
- **Blockers**:
  - #001 (composition root) for aggregator wiring.
  - Postgres schema decision (new tables for history + feedback).

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/services/ContextTestingService.ts` — replace in-process storage with async Postgres-backed helpers.
  - `packages/intelligence-server/src/server.ts` — add `POST /kb/context/test`, `PUT /kb/context/config` routes.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs` — add matching C# endpoints.
  - `apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IntelligenceHttpClient.cs` — add `PostContextTestAsync`, `PutContextConfigAsync`.
- Files to create:
  - `database/migrations/NNN-context-history-feedback.sql` (or Postgres-migrate equivalent).
  - `packages/intelligence-server/src/stores/context-feedback-store.ts`.
- Tests to add:
  - `POST /kb/context/feedback` → Postgres row exists → survives restart.
  - `GET /kb/context/history?limit=10` returns 10 recent entries written by a test-aggregator during the setup phase.
  - `PUT /kb/context/config` with `{maxTokens: 50000}` then `GET` returns the new value.
- Estimated effort: 4-5h
  - Postgres schema + store: 1h
  - Aggregator wiring + history emission: 1.5h
  - New routes + C# endpoints: 1h
  - Tests: 1-1.5h

## References

- TS source: `packages/api/src/services/knowledge-base/ContextTestingService.ts` (commit `9e9a57c~1`)
- Sidecar source: `packages/intelligence-server/src/services/ContextTestingService.ts`
- Real aggregator: `packages/intelligence/src/context/` (exists, not wired)
- Stories: `docs/stories/epic-6/story-6-5/6-5-context-aggregator.md`, `docs/stories/epic-12/12-3-context-compaction.md`
- Related findings: #001, #014

## Remediation status

**Status (2026-04-18):** Deferred — out of scope for the C# port pass.

`ContextTestingService` is in `packages/intelligence-server/src/services/`.
The three sub-issues (empty history, in-process feedback Map,
hard-coded config) all need to be fixed inside the sidecar, and the new
`POST /kb/context/test` and `PUT /kb/context/config` routes need to be
introduced sidecar-first before C# forwarding endpoints can be added. The
proposed Postgres tables (`context_feedback`, history) belong to the
sidecar's data plane (the sidecar already has its own Postgres
connectivity for RAG caching); writing them from the C# host would split
ownership of KB persistence across two services and create the same
"contract drift" problem the C# layer was specifically designed to avoid.

**To unblock:** sidecar work — Postgres schema + store + aggregator
wiring + new routes. 4-5h. The two new C# forwarding endpoints would be
a 30-minute follow-up once the sidecar contract is finalised.

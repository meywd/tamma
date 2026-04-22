# Finding 006: `IndexManagementService.triggerIndex` returns literal "(stub — no indexer configured)"

**Scope**: kb
**Severity**: P1 (user-visible regression vs TS error)
**Status**: Behavioral drift (TS threw, sidecar returns debug string as success)
**Estimated port effort**: 0.5h (subset of #002 fix)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/IndexManagementService.ts`.

```typescript
// packages/api/src/services/knowledge-base/IndexManagementService.ts (9e9a57c~1)
async triggerIndex(_request?: TriggerIndexRequest): Promise<void> {
  if (this.currentStatus.status === 'indexing') {
    throw new Error('Indexing is already in progress');
  }

  let effectivePath = _request?.repositoryPath ?? this.projectPath;

  if (!this.indexer || !effectivePath) {
    throw new Error('No indexer or project path configured');
  }

  // ...path traversal guard...
  this.currentStatus = { status: 'indexing', /* ... */ };
  // ...subscribe to progress events, run indexing async...
}
```

- The deleted TS service threw an `Error` with a clear operator-facing message when `indexer` or `projectPath` was missing.
- It also had a directory-traversal guard for user-supplied `repositoryPath` (see the `if (!isUrl && this.projectPath)` block) — that guard is **missing from the sidecar port**, a separate small drift.
- The route layer wrapped the throw into an HTTP 400/500:

```typescript
// packages/api/src/routes/knowledge-base/index-routes.ts (9e9a57c~1) — paraphrased
// try { await service.triggerIndex(body); return { message: 'Indexing triggered' }; }
// catch (err) { return reply.status(400).send({ error: err.message }); }
```

- Dependencies: `ICodebaseIndexer` from `@tamma/intelligence` (see `packages/intelligence/src/indexer/codebase-indexer.ts`).
- Tests: `packages/api/src/__tests__/services/knowledge-base/IndexManagementService.test.ts` asserted the thrown errors for both "already indexing" and "no indexer" paths.

## 2. What's in C#

### C# side
The endpoint blindly forwards the sidecar's response:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:29-33 (current)
public static async Task<IResult> TriggerIndex(
    [FromServices] IIntelligenceHttpClient client,
    [FromBody] TriggerIndexRequest? body,
    CancellationToken ct)
    => Results.Ok(await client.TriggerIndexAsync(body, ct));
```

No status-code translation, no body inspection.

### Sidecar side — the stub string

```typescript
// packages/intelligence-server/src/services/IndexManagementService.ts:121-166 (current)
async triggerIndex(
  body?: { fullReindex?: boolean; repositoryPath?: string; changedFiles?: string[] },
): Promise<{ message: string }> {
  if (!this.indexer) {
    return { message: 'Indexing triggered (stub — no indexer configured)' };
  }
  if (this.currentStatus.status === 'indexing') {
    throw new Error('Indexing already in progress');
  }
  const effectivePath = body?.repositoryPath ?? this.projectPath;
  if (!effectivePath) {
    throw new Error('No project path configured');
  }
  this.currentStatus = { status: 'indexing', indexed: 0, pending: 0 };
  const indexer = this.indexer;
  const isFull = body?.fullReindex === true;
  const promise = isFull || !indexer.updateIndex
    ? indexer.indexProject(effectivePath, { fullReindex: isFull })
    : indexer.updateIndex(effectivePath, body?.changedFiles);

  promise
    .then(async () => { /* ... */ })
    .catch((err: unknown) => { /* ... */ });

  return { message: 'Indexing triggered' };
}
```

- Dependencies: `IIndexer` (narrow interface from `packages/intelligence-server/src/types.ts`).
- Tests: `packages/intelligence-server/src/__tests__/services/IndexManagementService.test.ts` asserts on the stub string — so the test suite **guards the bug**.

Missing from the sidecar port:
- Directory-traversal guard on `body?.repositoryPath`.
- Structured event emission (`logger.info({ projectPath }, 'indexing triggered')`).
- Rich `IndexHistoryEntry` recording.

## 3. The gap

- TS did: throw `Error('No indexer or project path configured')` → HTTP 400/500 with `{ "error": "No indexer or project path configured" }`.
- C# + sidecar does: respond HTTP 200 with `{ "message": "Indexing triggered (stub — no indexer configured)" }`. Dashboard toast reads the developer-debug string.

For a dashboard user clicking "Re-index codebase":
- TS: red toast "Error: No indexer or project path configured". User files a ticket.
- C# + sidecar: green toast "Indexing triggered (stub — no indexer configured)". User thinks it worked. After 10 minutes they notice nothing changed. They file a ticket that looks like "re-index silently doesn't complete" — a different, harder-to-diagnose bug class.

Error paths:
- TS: HTTP 400/500 with meaningful error string.
- C# + sidecar: HTTP 200 with misleading success.

Additionally, the `repositoryPath` path-traversal guard is missing in the sidecar — even when an indexer IS wired, an attacker could pass `repositoryPath: '../../etc'` and the indexer would try to scan it (depending on `indexProject` internals). This is a minor security regression vs TS. (See future finding TBD if wiring lands.)

## 4. Gap from stories

`docs/stories/epic-6/story-6-1/6-1-codebase-indexer.md` AC6:

> **AC6: Index Triggers**
> - [ ] Git hook integration (post-commit, post-merge)
> - [ ] File system watcher (development mode)
> - [ ] Scheduled re-index (configurable interval)
> - [ ] Manual trigger via CLI/API

The "Manual trigger via API" AC implies the API should either actually index or explicitly refuse. A success-with-debug-string falls into neither bucket.

Story alignment:
- [x] Matches TS behavior (C# is the regression vs TS error). TS error was compatible with the AC (manual trigger refused when unconfigured).
- [ ] Matches C# behavior
- [ ] Describes a third behavior
- [x] No story on the "(stub — …)" string — Epic 6 doesn't bless it.

## 5. Status

- **Classification**: Behavioral drift. The sidecar "ported" the TS service but replaced the throw with a success-string.
- **What's needed to finish**:
  1. Replace `return { message: 'Indexing triggered (stub — no indexer configured)' }` with `throw new DependencyNotConfiguredError('indexer')`.
  2. In `server.ts` route handler for `/kb/index/trigger`, translate the error to HTTP 503.
  3. Re-introduce the directory-traversal guard on `body?.repositoryPath`.
  4. Update sidecar unit test to expect the thrown error instead of the success string.
- **Is it "just a stub" or is scope missing?** Scope is fully spec'd; this is drift introduced by an over-eager "don't throw" style. Easy fix.
- **Blockers**: none — can be fixed independently of #001. Becomes dead code once the composition root is wired.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/services/IndexManagementService.ts:121-126` — throw instead of return stub.
  - `packages/intelligence-server/src/services/IndexManagementService.ts:130-133` — add traversal guard (port from TS).
  - `packages/intelligence-server/src/server.ts:62-73` — already wraps `triggerIndex` with 409 error handler; extend to 503 for `DependencyNotConfiguredError`.
  - `packages/intelligence-server/src/__tests__/services/IndexManagementService.test.ts` — replace stub-string assertion with error assertion.
- Files to create: helper in `packages/intelligence-server/src/services/assert-dep.ts` (see #002).
- Tests to add:
  - `POST /kb/index/trigger` with null indexer → HTTP 503, body `{"error":"KB feature unavailable: no indexer configured"}`.
  - `POST /kb/index/trigger` with `repositoryPath: '../../etc'` → HTTP 400 with traversal-guard error.
- Estimated effort: 0.5h
  - Service + server.ts: 15m
  - Tests: 15m

## References

- TS source: `packages/api/src/services/knowledge-base/IndexManagementService.ts:96-108` (commit `9e9a57c~1`)
- Sidecar source: `packages/intelligence-server/src/services/IndexManagementService.ts:121-126`
- Story: `docs/stories/epic-6/story-6-1/6-1-codebase-indexer.md` AC6
- Related findings: #001, #002 (general stub-string pattern), #015

## Remediation status

**Status (2026-04-18):** Deferred — out of scope for the C# port pass.

The literal stub string `Indexing triggered (stub — no indexer configured)`
is emitted from `packages/intelligence-server/src/services/IndexManagementService.ts:121-126`,
a TypeScript module the C# port has no access to. The C#
`KbEndpoints.TriggerIndex` correctly forwards body and returns whatever the
sidecar replies with — translating the message in C# would violate the
documented passthrough contract and silently mask the underlying sidecar
bug, not fix it. The directory-traversal guard (a separate sub-issue called
out in the finding) is also a sidecar-side concern: the path string is
opaque to C# and is consumed by `IndexProject` inside the sidecar.

**To unblock:** part of the finding-002 sweep (replace stub strings with
503s) — 15 min sidecar change.

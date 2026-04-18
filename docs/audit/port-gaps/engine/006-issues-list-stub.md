# Finding 006: `GET /api/engine/issues` returns `[]`

**Scope**: engine
**Severity**: P0 (cutover-blocking — ADL work-item selection is dead)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-github-routes.ts:98-155` (9e9a57c~1)
- Contract: `GET /api/engine/issues?repo=owner/repo&labels=a,b&state=open&per_page=30&page=1` — proxies to Octokit's `issues.listForRepo` with pull-request filtering.

```typescript
// packages/api/src/routes/engine/engine-github-routes.ts:128-149 (9e9a57c~1)
const { data } = await client.rest.issues.listForRepo({
  owner: parsed.owner,
  repo: parsed.repo,
  state,
  labels,
  per_page: perPage,
  page,
});
const issues = data.filter(
  (item) => !('pull_request' in item && item.pull_request !== undefined),
);
fastify.log.info({ repository: repoParam, count: issues.length, state }, 'Listed issues');
return reply.send({ issues, total: issues.length });
```

Returns `{issues: [...], total}` filtered to exclude PRs (which GitHub mixes into the issues endpoint).

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:72-73`

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:72-73
public static Task<IResult> GetIssues() =>
    Task.FromResult(Results.Ok(Array.Empty<object>()));
```

No query-parameter binding, no Octokit call. Returns an empty array regardless of any `?repo=`/`?state=`/`?labels=` query.

Also the response shape drifts: TS returned `{issues, total}`, C# returns `[]` (bare array).

### Deployed Elsa callers

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/SelectWorkItemActivity.cs:180-184
var response = await httpClient.GetAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/issues?repo={Uri.EscapeDataString(repo)}&labels={string.Join(",", autoLabels)}");
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/FetchUntriagedItemsActivity.cs:87-88
var response = await httpClient.GetAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/issues?repo={Uri.EscapeDataString(repo)}&state=open");
```

Both activities expect `{issues: [...], total}`. On the C# stub they get a bare `[]`, so `result.GetProperty("issues")` throws and the activity silently catches and returns zero items.

## 3. The gap

- TS did: real Octokit call returning live issues with pagination.
- C# does: empty array always.

For the core ADL loop `SelectWorkItemActivity` asking "what should I work on next?":

- TS: list of open issues filtered by `auto-*` labels → Scrum Master picks one and dispatches a workflow.
- C#: no issues, ever. The ADL loop exits with "no work to do" every iteration. Autonomous development stops.

Error paths:

- TS: 400 when `repo` missing/invalid. 502 on Octokit error.
- C#: always 200 `[]`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md` (explicit endpoint spec). The ADL epic (Epic 7, autonomous dev loop) relies on this. Also `docs/stories/epic-19/19-1-phase-2-3-impl-plan.md` notes the GitHub engine routes were deferred from Phase 1.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub).
- **What's needed to finish**:
  1. Bind `[FromQuery] string? repo, labels, state, perPage, page` on the handler.
  2. Wire an installation-scoped Octokit.NET client (or GraphQL equivalent) via DI.
  3. Port the TS pagination + PR filter.
  4. Return `{issues, total}` (not bare array).
  5. Return 400 on missing `repo`, 502 on GitHub errors — match TS error paths.
- **Is it "just a stub" or is scope missing?** The endpoint scope was understood; the GitHub client integration was deferred.
- **Blockers**: depends on a GitHub App / Octokit.NET client being wired (shared blocker with findings 005, 007-011).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:72-73`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubIssueService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/OctokitIssueService.cs`
- Tests to add:
  - `GetIssues_RejectsMissingRepoQuery` — 400.
  - `GetIssues_RejectsBadRepoFormat` — 400.
  - `GetIssues_FiltersPullRequests` — mock returns mixed issues/PRs, only issues pass.
  - `GetIssues_Paginates` — `?per_page=10&page=2` hits Octokit with those params.
  - `GetIssues_ReturnsIssuesTotalShape` — not a bare array.
- Estimated effort: 3h
  - Endpoint + binding: 1h
  - Octokit service + DI: 1h
  - Tests (with WireMock or Octokit test doubles): 1h

## References

- TS source: `packages/api/src/routes/engine/engine-github-routes.ts:98-155`
- Deployed callers: `apps/tamma-elsa/src/Tamma.Activities/ADL/SelectWorkItemActivity.cs:180-215`, `FetchUntriagedItemsActivity.cs:87-88`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:72-73`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- Related findings: `007-security-alerts-stub.md`, `008-issue-comment-stub.md`, `009-issue-labels-stub.md`, `010-create-issue-stub.md`, `011-trigger-ci-stub.md` (all share the same GitHub-client blocker)

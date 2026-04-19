# Finding 007: `GET /api/engine/security-alerts` returns `[]`

**Scope**: engine
**Severity**: P0 (cutover-blocking — autonomous security triage dead)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-github-routes.ts:158-223` (9e9a57c~1)
- Contract: `GET /api/engine/security-alerts?repo=owner/repo&type=dependabot|codeql|all` — fetches Dependabot and/or code-scanning alerts. Logs a warning and continues when a given alert type is not enabled on the repo (so a single disabled scanner doesn't fail the whole request).

```typescript
// packages/api/src/routes/engine/engine-github-routes.ts:179-197 (9e9a57c~1)
const alerts: { dependabot: unknown[]; codeScanning: unknown[] } = {
  dependabot: [],
  codeScanning: [],
};
if (alertType === 'dependabot' || alertType === 'all') {
  try {
    const { data } = await client.request(
      'GET /repos/{owner}/{repo}/dependabot/alerts',
      { owner: parsed.owner, repo: parsed.repo, state: 'open', per_page: 100 },
    );
    alerts.dependabot = data as unknown[];
  } catch (err) {
    fastify.log.warn({ err, repository: repoParam }, 'Failed to fetch dependabot alerts');
  }
}
```

Response: `{dependabot: [...], codeScanning: [...]}`.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:75-76`

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:75-76
public static Task<IResult> GetSecurityAlerts() =>
    Task.FromResult(Results.Ok(Array.Empty<object>()));
```

No query binding, no GitHub client, returns `[]` (bare array, not `{dependabot, codeScanning}`).

### Deployed callers

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/FetchUntriagedItemsActivity.cs:123-124
var response = await httpClient.GetAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/security-alerts?repo={Uri.EscapeDataString(repo)}&type=dependabot");
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/FetchUntriagedItemsActivity.cs:161-162
var response = await httpClient.GetAsync(
    $"{callbackUrl.TrimEnd('/')}/api/engine/security-alerts?repo={Uri.EscapeDataString(repo)}&type=codeql");
```

Two separate calls, one per type. The activity expects `{dependabot: [...]}` or `{codeScanning: [...]}` and parses those keys. With the C# stub both come back as bare `[]` — property access throws, caught by surrounding try/catch, net effect is zero untriaged items.

## 3. The gap

- TS did: two parallel GitHub REST calls (`/dependabot/alerts`, `/code-scanning/alerts`), returned a keyed object.
- C# does: empty array.

For the `FetchUntriagedItemsActivity` loop (the autonomous security triage path):

- TS: current open Dependabot + CodeQL alerts → triage workflow.
- C#: no alerts, ever. Security triage never runs. CVE-labelled vulnerabilities sit un-assessed.

Error paths:

- TS: 400 on missing `repo`, 502 on Octokit error, per-type try/catch that lets one scanner fail independently.
- C#: 200 `[]` always.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`. Also `docs/stories/epic-3/story-3-9/3-9-security-scanning-gate-implementation.md` (security triage depends on these alerts as an input).
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub).
- **What's needed to finish**:
  1. Add `[FromQuery] string? repo, type` binding.
  2. Port the per-type try/catch wrapper — if one scanner is disabled on the repo, the other still returns.
  3. Use an installation-scoped GitHub client for `/dependabot/alerts` and `/code-scanning/alerts`.
  4. Return `{dependabot, codeScanning}` keyed object (not bare array).
- **Is it "just a stub" or is scope missing?** Scope was spec'd; GitHub client integration was deferred.
- **Blockers**: shared GitHub-client blocker with findings 005/006/008-011.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:75-76`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubSecurityAlertsService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/OctokitSecurityAlertsService.cs`
- Tests to add:
  - `GetSecurityAlerts_RejectsMissingRepo` — 400.
  - `GetSecurityAlerts_PartialFailure_StillReturnsOtherType` — Dependabot 404 + CodeQL 200 → `{dependabot: [], codeScanning: [alert...]}`.
  - `GetSecurityAlerts_ReturnsKeyedObjectShape` — not bare array.
- Estimated effort: 3h
  - Endpoint + binding: 1h
  - Octokit service (two calls + per-type try/catch): 1h
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/engine/engine-github-routes.ts:158-223`
- Deployed caller: `apps/tamma-elsa/src/Tamma.Activities/ADL/FetchUntriagedItemsActivity.cs:123-162`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:75-76`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`, `docs/stories/epic-3/story-3-9/3-9-security-scanning-gate-implementation.md`
- Related findings: `006-issues-list-stub.md` and the rest of the GitHub stubs (005, 008-011)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `2c2cdfa` (engine wiring); depends on `4e1e0e4` (Octokit client)
- **Notes**: `OctokitGitHubEngineCallbackService.ListSecurityAlertsAsync`
  issues two separate calls through `IConnection.Get` (Octokit's generic
  HTTP layer) — `/repos/{owner}/{repo}/dependabot/alerts?state=open` and
  `/repos/{owner}/{repo}/code-scanning/alerts?state=open`. Each is wrapped
  in its own try/catch (mirroring the TS per-scanner graceful degradation)
  so a repo with Dependabot enabled but code-scanning disabled still
  returns the Dependabot alerts + an empty `codeScanning` array. Response
  shape is the `{dependabot, codeScanning}` keyed object the deployed
  `FetchUntriagedItemsActivity` already parses. Respects the `?type=`
  filter (`dependabot` / `codeql` / `all`).

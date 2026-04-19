# Finding 010: `POST /api/engine/create-issue` stub

**Scope**: engine
**Severity**: P0 (cutover-blocking — security alert → issue conversion dead)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 2h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-github-routes.ts:378-442` (9e9a57c~1)
- Contract: `POST /api/engine/create-issue` body `{repository, title, body?, labels?, assignees?}` → `client.rest.issues.create(...)` → 201 `{number, htmlUrl, title}`.

```typescript
// packages/api/src/routes/engine/engine-github-routes.ts:417-431 (9e9a57c~1)
const { data } = await client.rest.issues.create(createOpts);
fastify.log.info({ repository, issueNumber: data.number, title }, 'Issue created');
return reply.status(201).send({
  number: data.number,
  htmlUrl: data.html_url,
  title: data.title,
});
```

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:87-88`
- DTO: `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:8`

```csharp
// Dtos/Engine/EngineDtos.cs:8
public record CreateIssueRequest(string Repo, string Title, string? Body, string[]? Labels);

// EngineEndpoints.cs:87-88
public static Task<IResult> CreateIssue(CreateIssueRequest req) =>
    Task.FromResult(Results.Ok(new { message = "Issue created (stub)", title = req.Title }));
```

DTO drops the `assignees` field entirely and uses `Repo` instead of `Repository` (same drift as 008, 009).

### Deployed caller

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/ApplyTriageResultActivity.cs:99-107
var createResult = await httpClient.PostAsJsonAsync(
    $"{baseUrl}/api/engine/create-issue",
    new
    {
        repository = repo,
        title = issueTitle,
        body = issueBody,
        labels = new[] { "auto-security", $"cve-{alert.Id}" }
    });
```

The activity sends `repository` (not `repo`). `req.Repo` stays null on the C# side.

## 3. The gap

- TS did: created a GitHub issue, returned 201 with issue number for the caller to track.
- C# does: returns 200 `{message, title}`. No issue is created. No issue number to track.

For the autonomous security triage workflow that converts a Dependabot alert into an issue:

- TS: GitHub issue created with `auto-security`, `cve-XXXX` labels. Number surfaced back. Workflow can now open a PR referencing it.
- C#: no issue. Triage "succeeds" but the alert never becomes actionable work. Subsequent alerts keep pouring in unaddressed.

Response code drift: TS returns 201, C# returns 200.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`. Also `docs/stories/epic-3/story-3-9/3-9-security-scanning-gate-implementation.md` for the security triage use case.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub) + DTO drift (missing `Assignees`, field name `Repo`).
- **What's needed to finish**:
  1. Rewrite `CreateIssueRequest` as `(string Repository, string Title, string? Body, string[]? Labels, string[]? Assignees)`.
  2. Parse `owner/repo` from `Repository`.
  3. Call Octokit `Issues.Create(...)`.
  4. Return 201 with `{number, htmlUrl, title}`.
- **Is it "just a stub" or is scope missing?** Both: DTO drift + missing GitHub client integration.
- **Blockers**: shared GitHub client (findings 005-011).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:8`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:87-88`
- Tests to add:
  - `CreateIssue_BindsRepositoryField`
  - `CreateIssue_IncludesAssignees_WhenProvided`
  - `CreateIssue_Returns201_WithNumberAndHtmlUrl`
  - `CreateIssue_AcceptsLabelsArray`
- Estimated effort: 2h — DTO + handler 30m, Octokit service reuse from 008/009 30m, tests 1h.

## References

- TS source: `packages/api/src/routes/engine/engine-github-routes.ts:378-442`
- Deployed caller: `apps/tamma-elsa/src/Tamma.Activities/ADL/ApplyTriageResultActivity.cs:99-107`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:87-88`, `Dtos/Engine/EngineDtos.cs:8`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- Related findings: 005-011

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (partial — graceful degradation; full impl deferred)
- **Commit**: ff581af
- **Notes**: Endpoint reworked to (a) bind the correct query / body shape
  (renames `Repo`→`Repository`; restored missing fields like `Assignees`,
  `BranchName`, `WorkflowFile`, `Inputs`); (b) parse `owner/repo`,
  validate with 400 on bad format / missing required fields; (c) delegate
  to the new `IGitHubEngineCallbackService`. The default
  `NullGitHubEngineCallbackService` short-circuits to a 503
  `github_client_not_configured` (matches the TS contract for the
  unwired-reader path) so the deployed Elsa activities see the documented
  soft-fail instead of a bogus 200 with a stub body. Real Octokit-backed
  implementation lands when the GitHub App client wires up
  (cross-ref github audit scope + finding 021). The repo-config endpoint
  preserves the TS graceful-degradation 200 `{}` so the conventions
  injection path keeps working on un-configured installations.

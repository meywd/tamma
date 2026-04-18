# Finding 008: `POST /api/engine/issue-comment` stub

**Scope**: engine
**Severity**: P0 (cutover-blocking — workflows "succeed" but never effect GitHub state)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 2h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-github-routes.ts:226-275` (9e9a57c~1)
- Contract: `POST /api/engine/issue-comment` body `{repository: "owner/repo", issueNumber, body}` → `client.rest.issues.createComment(...)` → `{id, htmlUrl}`.

```typescript
// packages/api/src/routes/engine/engine-github-routes.ts:254-265 (9e9a57c~1)
const { data } = await client.rest.issues.createComment({
  owner: parsed.owner,
  repo: parsed.repo,
  issue_number: issueNumber,
  body,
});
fastify.log.info({ repository, issueNumber, commentId: data.id }, 'Issue comment created');
return reply.send({ id: data.id, htmlUrl: data.html_url });
```

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:78-79`
- DTO: `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:6`

```csharp
// Dtos/Engine/EngineDtos.cs:6
public record IssueCommentRequest(string Repo, int IssueNumber, string Body);

// EngineEndpoints.cs:78-79
public static Task<IResult> PostIssueComment(IssueCommentRequest req) =>
    Task.FromResult(Results.Ok(new { message = "Comment posted (stub)", repo = req.Repo, issueNumber = req.IssueNumber }));
```

### Deployed callers

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/UpdateIssueStatusActivity.cs:83-91
var commentPayload = new { repository = repo, issueNumber = issueNum, body = commentBody };
var response = await httpClient.PostAsJsonAsync(
    $"{baseUrl}/api/engine/issue-comment", commentPayload);
response.EnsureSuccessStatusCode();
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/ApplyTriageResultActivity.cs:91-94
await httpClient.PostAsJsonAsync(
    $"{baseUrl}/api/engine/issue-comment",
    new { repository = repo, issueNumber = item.Number, body = decision.Comment });
```

Note the field name mismatch: activities send `repository` (spelled out) but the C# DTO has `Repo`. Model binding tolerates case-insensitivity but not `repository` → `Repo`. The field is silently unbound; `req.Repo` is null.

## 3. The gap

- TS did: real comment posted via Octokit, returned `{id, htmlUrl}` for the caller to link to.
- C# does: echoes back `{message, repo, issueNumber}` where `repo` is null (property-name mismatch) and no comment is created on GitHub.

For a triage decision that posts a comment explaining the label added:

- TS: comment appears on the issue. Reviewer sees the reasoning.
- C#: no comment ever appears. The workflow step logs "success" but the audit trail on GitHub is silent.

The worst failure mode: workflows reporting success while taking zero effect on the external world. Dashboard shows green; GitHub shows nothing. Debugging this as a user is near-impossible — the only way to know the comment was never posted is to cross-check GitHub by hand.

Error paths:

- TS: 400 on invalid body, 502 on Octokit error, 503 when Octokit not configured.
- C#: 200 always.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`. Also implicit in `docs/stories/epic-7/` (ADL autonomous loop) — many workflow steps post comments as the user-visible audit trail.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub) + DTO field-name drift.
- **What's needed to finish**:
  1. Rename `Repo` → `Repository` on the DTO to match the deployed activity payloads.
  2. Bind + validate `{repository, issueNumber, body}` (all required).
  3. Parse `owner/repo` and call `Octokit.IIssueCommentsClient.Create`.
  4. Return `{id, htmlUrl}`.
  5. 503 when no GitHub client configured.
- **Is it "just a stub" or is scope missing?** Both: the DTO is subtly wrong, and the GitHub-client integration is missing.
- **Blockers**: GitHub-client blocker shared with 005-007, 009-011.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:6` — rename field to `Repository`.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:78-79`
- Files to create (probably shared service):
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubIssueCommentService.cs`
  - `OctokitIssueCommentService.cs`
- Tests to add:
  - `PostIssueComment_AcceptsRepositoryField` — DTO binds `repository` (not `repo`).
  - `PostIssueComment_CallsOctokit_WithCorrectOwnerAndRepo`
  - `PostIssueComment_ReturnsIdAndHtmlUrl`
  - `PostIssueComment_ValidatesBody_Required`
- Estimated effort: 2h
  - DTO + handler: 30m
  - Octokit service: 30m
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/engine/engine-github-routes.ts:226-275`
- Deployed callers: `apps/tamma-elsa/src/Tamma.Activities/ADL/UpdateIssueStatusActivity.cs:83-91`, `ApplyTriageResultActivity.cs:91-94`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:78-79`, `Dtos/Engine/EngineDtos.cs:6`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- Related findings: 005, 006, 007, 009, 010, 011 (shared GitHub client blocker)

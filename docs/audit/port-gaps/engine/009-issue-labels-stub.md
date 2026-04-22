# Finding 009: `POST` and `DELETE /api/engine/issue-labels` stubs

**Scope**: engine
**Severity**: P0 (cutover-blocking — autonomous labelling / triage dead)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 3h

## 1. What's in TS

- File: `packages/api/src/routes/engine/engine-github-routes.ts:278-375` (9e9a57c~1)
- Two endpoints:
  - `POST /api/engine/issue-labels` body `{repository, issueNumber, labels[]}` → `client.rest.issues.addLabels(...)` → `{labels: string[]}`
  - `DELETE /api/engine/issue-labels/:repo/:issueNumber/:label` → `client.rest.issues.removeLabel(...)` → `{removed: true, label}`

```typescript
// packages/api/src/routes/engine/engine-github-routes.ts:301-313 (9e9a57c~1) — POST
const { data } = await client.rest.issues.addLabels({
  owner: parsed.owner,
  repo: parsed.repo,
  issue_number: issueNumber,
  labels,
});
fastify.log.info({ repository, issueNumber, labels }, 'Labels added to issue');
return reply.send({ labels: data.map((l) => l.name) });
```

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:81-85`
- DTO: `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:7`

```csharp
// Dtos/Engine/EngineDtos.cs:7
public record IssueLabelRequest(string Repo, int IssueNumber, string[] Labels);

// EngineEndpoints.cs:81-85
public static Task<IResult> PostIssueLabels(IssueLabelRequest req) =>
    Task.FromResult(Results.Ok(new { message = "Labels added (stub)", labels = req.Labels }));

public static Task<IResult> DeleteIssueLabel(string repo, int issueNumber, string label) =>
    Task.FromResult(Results.Ok(new { message = $"Label '{label}' removed (stub)" }));
```

Same field-name drift as finding 008: DTO is `Repo`, activities send `repository`.

### Deployed callers

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/UpdateIssueStatusActivity.cs:95-107
if (addLabels is not null && addLabels.Any())
{
    var labelPayload = new { repository = repo, issueNumber = issueNum, labels = addLabels };
    await httpClient.PostAsJsonAsync($"{baseUrl}/api/engine/issue-labels", labelPayload);
}
// ...
await httpClient.DeleteAsync(
    $"{baseUrl}/api/engine/issue-labels/{Uri.EscapeDataString(repo)}/{issueNum}/{Uri.EscapeDataString(label)}");
```

```csharp
// apps/tamma-elsa/src/Tamma.Activities/ADL/ApplyTriageResultActivity.cs:83-85
await httpClient.PostAsJsonAsync(
    $"{baseUrl}/api/engine/issue-labels",
    new { repository = repo, issueNumber = item.Number, labels = decision.Labels });
```

## 3. The gap

- TS did: labels actually added/removed on GitHub.
- C# does: labels silently dropped. Payload is echoed back ("labels = req.Labels"), giving the appearance of success.

For the triage loop that classifies a CVE issue and tags it `auto:security`, `priority:high`:

- TS: labels appear on the issue. Filtering/selection queries downstream see them.
- C#: no labels. Triage "succeeds" but the issue remains untagged — next iteration sees it as untriaged again and re-triages. Infinite loop risk if not otherwise deduped.

The DELETE endpoint has no DTO (route params only), so parameter binding works. But it also does nothing.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`. The triage loop is part of ADL (Epic 7).
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented (stub) + DTO field-name drift.
- **What's needed to finish**:
  1. Rename `Repo` → `Repository` on `IssueLabelRequest`.
  2. Wire Octokit `issues.addLabels` / `issues.removeLabel`.
  3. Return `{labels: string[]}` / `{removed: true, label}`.
  4. 503 when no GitHub client; 502 on API error.
  5. Validate labels non-empty on POST.
- **Is it "just a stub" or is scope missing?** Both — DTO drift + GitHub client missing.
- **Blockers**: shared GitHub client blocker (findings 005-011). Consumer loops depend on idempotency: the re-triage loop described above will spam the same issue repeatedly until labelling works.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs:7`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:81-85`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubIssueLabelsService.cs` (probably merged with IssueCommentService into a single `IGitHubIssuesService`).
- Tests to add:
  - `PostIssueLabels_BindsRepositoryField`
  - `PostIssueLabels_CallsOctokit_AddLabels`
  - `PostIssueLabels_Returns_AppliedLabelNames`
  - `PostIssueLabels_ValidatesLabelsNonEmpty` — 400.
  - `DeleteIssueLabel_CallsOctokit_RemoveLabel`
  - `DeleteIssueLabel_Returns_RemovedTrue`
- Estimated effort: 3h
  - POST + DELETE handlers: 1h
  - Octokit service: 1h
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/engine/engine-github-routes.ts:278-375`
- Deployed callers: `apps/tamma-elsa/src/Tamma.Activities/ADL/UpdateIssueStatusActivity.cs:95-107`, `ApplyTriageResultActivity.cs:83-85`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:81-85`, `Dtos/Engine/EngineDtos.cs:7`
- Story: `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md`
- Related findings: 005-011

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `2c2cdfa` (engine wiring); depends on `4e1e0e4` (Octokit client)
- **Notes**: `OctokitGitHubEngineCallbackService.AddIssueLabelsAsync` /
  `RemoveIssueLabelAsync` call `Octokit.Issue.Labels.AddToIssue` /
  `RemoveFromIssue` respectively. POST returns the applied label-name array
  (`{labels: string[]}`); DELETE returns `{removed: true, label}`. The
  re-triage infinite-loop risk flagged in the original gap analysis is
  closed — labels actually land on the issue now, so the next iteration
  of the triage loop sees them and skips re-triaging.

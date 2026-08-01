# Story 31-13: Full PR Operation Support

Status: drafted

## User Story

As the orchestrator, I want the git platform client to support every PR
operation a maintainer performs by hand — close, reopen, comment, review
comments, request reviewers, add/remove labels, draft↔ready — so workflows
never dead-end on a missing verb, and every one of those verbs is a
governed catalog action with a zone level.

## Why now (2026-08-01, product owner)

- `IGitPlatformClient` supports neither close nor reopen for PRs. 43-11's
  zone work found `git.pull-request.close` had to be RESERVED because
  nothing can perform it.
- Issue comment/label routes already exist on the engine surface but are
  uncatalogued; PR-side equivalents are missing entirely.
- 41-17 (code review / PR triage) needs review-comment posting; today
  review output cannot land ON the PR.

## Scope

1. `IGitPlatformClient`: add `ClosePullRequestAsync`, `ReopenPullRequestAsync`,
   `PostPullRequestCommentAsync`, `PostReviewCommentAsync` (file/line),
   `RequestReviewersAsync`, `AddPullRequestLabelsAsync` / `RemovePullRequestLabelAsync`,
   `SetDraftAsync(bool)`. Implement for the GitHub driver; other drivers per
   the 31-x compat matrix (throw `PLATFORM_NOT_SUPPORTED` where the platform
   lacks the verb, recorded per driver).
2. `GitMediationService` + engine routes for each, mirroring the existing
   issue-comment/label shape.
3. Catalog keys with zone levels (43-11 model):
   `git.pull-request.close` 35 (reversible — reopen exists),
   `git.pull-request.reopen` 35, `git.pull-request.comment` 35,
   `git.pull-request.review-comment` 40 (it is review output),
   `git.pull-request.request-reviewers` 35, `git.pull-request.label` 35,
   `git.pull-request.set-draft` 35.
4. Also catalog the ALREADY-LIVE uncatalogued issue routes:
   `git.issue.create` 35, `git.issue.comment` 35, `git.issue.label` 35
   (engine routes exist today with no catalog member — governance blind
   spot, not a new capability).
5. Bind and enforce each new route per 43-9's opt-in model.

## Out of scope

Delete PR (platform cannot), merge (exists), branch ops (exist).

## Acceptance criteria

1. Every Scope-1 method implemented for GitHub with an integration test.
2. Every new route carries `.Governs` + `.EnforcesGovernance()` and its
   catalog key; the ungoverned baseline does not grow.
3. The three live issue routes gain catalog keys; the drift sweep proves no
   engine git route is uncatalogued.
4. A workflow can close and reopen a PR end to end (structure + seam test).

## Effort

3-4 days (GitHub driver + mediation + catalog + tests; other drivers ride
the compat matrix).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-08-01 | 1.0.0   | Initial story creation | Claude |
